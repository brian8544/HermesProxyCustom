/*
 * Copyright (C) 2012-2020 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Reflection;
using System.Collections.Concurrent;

using Framework;
using Framework.Constants;
using Framework.Cryptography;
using Framework.IO;
using Framework.Networking;
using Framework.Logging;
using Framework.Realm;

using HermesProxy.World.Enums;
using HermesProxy.World.Server.Packets;
using static HermesProxy.World.Server.Packets.AuthResponse;
using System.Net;
using BNetServer;
using BNetServer.Services;
using Google.Protobuf;
using HermesProxy.Enums;
using HermesProxy.World.Client;
using HermesProxy.World.Objects;
using Framework.Util;
using System.Numerics;
using System.Text;
using System.Linq;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket : SocketBase, BnetServices.INetwork
    {
        static readonly string ClientConnectionInitialize = "WORLD OF WARCRAFT CONNECTION - CLIENT TO SERVER - V2";
        static readonly string ServerConnectionInitialize = "WORLD OF WARCRAFT CONNECTION - SERVER TO CLIENT - V2";

        static readonly byte[] AuthCheckSeed = { 0xC5, 0xC6, 0x98, 0x95, 0x76, 0x3F, 0x1D, 0xCD, 0xB6, 0xA1, 0x37, 0x28, 0xB3, 0x12, 0xFF, 0x8A };
        static readonly byte[] SessionKeySeed = { 0x58, 0xCB, 0xCF, 0x40, 0xFE, 0x2E, 0xCE, 0xA6, 0x5A, 0x90, 0xB8, 0x01, 0x68, 0x6C, 0x28, 0x0B };
        static readonly byte[] ContinuedSessionSeed = { 0x16, 0xAD, 0x0C, 0xD4, 0x46, 0xF9, 0x4F, 0xB2, 0xEF, 0x7D, 0xEA, 0x2A, 0x17, 0x66, 0x4D, 0x2F };
        static readonly byte[] EncryptionKeySeed = { 0xE9, 0x75, 0x3C, 0x50, 0x90, 0x93, 0x61, 0xDA, 0x3B, 0x07, 0xEE, 0xFA, 0xFF, 0x9D, 0x41, 0xB8 };

        static readonly int HeaderSize = 16;
        static readonly int WotlkHeaderSize = 6;

        SocketBuffer _headerBuffer;
        SocketBuffer _packetBuffer;

        ConnectionType _connectType;
        ulong _key;

        byte[] _serverChallenge;
        WorldCrypt _worldCrypt;
        byte[] _sessionKey;
        byte[] _encryptKey;
        ConnectToKey _instanceConnectKey;
        RealmId _realmId;

        ZLib.z_stream _compressionStream;
        ConcurrentDictionary<Opcode, PacketHandler> _clientPacketTable = new();
        GlobalSessionData _globalSession;
        System.Threading.Mutex _sendMutex = new System.Threading.Mutex();

        private BnetServices.ServiceManager _bnetRpc;
        private readonly bool _isWotlkFrontend;
        private LegacyWorldCrypt _wotlkHeaderCrypt;
        private bool _wotlkHeaderCryptInitialized;
        private readonly byte[] _wotlkServerChallenge = new byte[4];
        private uint _wotlkClientOpcode;
        private ushort _wotlkClientPacketSize;
        private bool _wotlkSentMovementBootstrap;
        private uint _wotlkMovementBootstrapCounter;
        private readonly HashSet<uint> _wotlkSyntheticMovementSpeedAckCounters = new();
        private uint _wotlkTimeSyncCounter;
        private ulong _wotlkLastLoginGuidLow;
        private long _wotlkLastLoginUnixMs;
        private bool _wotlkLogoutInProgress;
        private bool _wotlkAccountDataLoaded;
        private ulong _wotlkAccountDataLoadedLow;
        private ulong _wotlkAccountDataLoadedHigh;
        private int _wotlkLastClientPacketTick;
        private const uint WotlkGlobalAccountDataMask = 0x15u;
        private const int WotlkSyntheticWorldPortAckTimeoutMs = 30000;
        private const int WotlkClientSilenceDisconnectMs = 75000;

        public WorldSocket(Socket socket) : base(socket)
        {
            _connectType = ConnectionType.Realm;
            _serverChallenge = Array.Empty<byte>().GenerateRandomKey(16);
            _worldCrypt = new WorldCrypt();
            _isWotlkFrontend = Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340;

            _encryptKey = new byte[16];

            if (_isWotlkFrontend)
                _wotlkServerChallenge.GenerateRandomKey(_wotlkServerChallenge.Length).CopyTo(_wotlkServerChallenge, 0);

            _wotlkLastClientPacketTick = Environment.TickCount;

            _headerBuffer = new SocketBuffer(_isWotlkFrontend ? WotlkHeaderSize : HeaderSize);
            _packetBuffer = new SocketBuffer(0);

            InitializePacketHandlers();
        }

        public override void Dispose()
        {
            _serverChallenge = null;
            _sessionKey = null;
            _compressionStream = null;

            base.Dispose();
        }

        public GlobalSessionData GetSession()
        {
            return _globalSession;
        }

        public GlobalSessionData Session => _globalSession;

        public override void Accept()
        {
            if (_isWotlkFrontend)
            {
                AcceptWotlkFrontend();
                return;
            }

            string ip_address = GetRemoteIpAddress().ToString();

            _packetBuffer.Resize(ClientConnectionInitialize.Length + 1);

            AsyncReadWithCallback(InitializeHandler);

            ByteBuffer packet = new();
            packet.WriteString(ServerConnectionInitialize);
            packet.WriteString("\n");
            AsyncWrite(packet.GetData());
        }

        void InitializeHandler(SocketAsyncEventArgs args)
        {
            if (args.SocketError != SocketError.Success)
            {
                CloseSocket();
                return;
            }

            if (args.BytesTransferred > 0)
            {
                if (_packetBuffer.GetRemainingSpace() > 0)
                {
                    // need to receive the header
                    int readHeaderSize = Math.Min(args.BytesTransferred, _packetBuffer.GetRemainingSpace());
                    _packetBuffer.Write(args.Buffer, 0, readHeaderSize);

                    if (_packetBuffer.GetRemainingSpace() > 0)
                    {
                        // Couldn't receive the whole header this time.
                        AsyncReadWithCallback(InitializeHandler);
                        return;
                    }

                    ByteBuffer buffer = new(_packetBuffer.GetData());
                    string initializer = buffer.ReadString((uint)ClientConnectionInitialize.Length);
                    if (initializer != ClientConnectionInitialize)
                    {
                        CloseSocket();
                        return;
                    }

                    byte terminator = buffer.ReadUInt8();
                    if (terminator != '\n')
                    {
                        CloseSocket();
                        return;
                    }

                    // Initialize the zlib stream
                    _compressionStream = new ZLib.z_stream();

                    // Initialize the deflate algo...
                    var z_res1 = ZLib.deflateInit2(_compressionStream, 1, 8, -15, 8, 0);
                    if (z_res1 != 0)
                    {
                        CloseSocket();
                        Log.Print(LogType.Error, $"Can't initialize packet compression (zlib: deflateInit2_) Error code: {z_res1}");
                        return;
                    }

                    _packetBuffer.Resize(0);
                    _packetBuffer.Reset();
                    HandleSendAuthSession();
                    AsyncRead();
                    return;
                }
            }
        }

        public override void ReadHandler(SocketAsyncEventArgs args)
        {
            if (!IsOpen())
                return;

            int currentReadIndex = 0;
            while (currentReadIndex < args.BytesTransferred)
            {
                if (_headerBuffer.GetRemainingSpace() > 0)
                {
                    // need to receive the header
                    int readHeaderSize = Math.Min(args.BytesTransferred - currentReadIndex, _headerBuffer.GetRemainingSpace());
                    _headerBuffer.Write(args.Buffer, currentReadIndex, readHeaderSize);
                    currentReadIndex += readHeaderSize;

                    if (_headerBuffer.GetRemainingSpace() > 0)
                        break; // Couldn't receive the whole header this time.

                    // We just received nice new header
                    if (!ReadHeader())
                    {
                        CloseSocket();
                        return;
                    }
                }

                // We have full read header, now check the data payload
                if (_packetBuffer.GetRemainingSpace() > 0)
                {
                    // need more data in the payload
                    int readDataSize = Math.Min(args.BytesTransferred - currentReadIndex, _packetBuffer.GetRemainingSpace());
                    _packetBuffer.Write(args.Buffer, currentReadIndex, readDataSize);
                    currentReadIndex += readDataSize;

                    if (_packetBuffer.GetRemainingSpace() > 0)
                        break; // Couldn't receive the whole data this time.
                }

                // just received fresh new payload
                ReadDataHandlerResult result = ReadData();
                _headerBuffer.Reset();
                if (result != ReadDataHandlerResult.Ok)
                {
                    if (result != ReadDataHandlerResult.WaitingForQuery)
                        CloseSocket();

                    return;
                }
            }

            AsyncRead();
        }

        bool ReadHeader()
        {
            if (_isWotlkFrontend)
                return ReadWotlkHeader();

            PacketHeader header = new();
            header.Read(_headerBuffer.GetData());

            _packetBuffer.Resize(header.Size);
            return true;
        }

        ReadDataHandlerResult ReadData()
        {
            if (_isWotlkFrontend)
                return ReadWotlkData();

            PacketHeader header = new();
            header.Read(_headerBuffer.GetData());

            if (!_worldCrypt.Decrypt(_packetBuffer.GetData(), header.Tag))
            {
                Log.Print(LogType.Error, $"WorldSocket.ReadData(): client {GetRemoteIpAddress()} failed to decrypt packet (size: {header.Size})");
                return ReadDataHandlerResult.Error;
            }

            WorldPacket packet = new(_packetBuffer.GetData());
            _packetBuffer.Reset();

            Opcode opcode = packet.GetUniversalOpcode(true);

            Log.PrintNet(LogType.Debug, LogNetDir.C2P, $"Received opcode {opcode.ToString()} ({packet.GetOpcode()}).");

            if (opcode != Opcode.CMSG_HOTFIX_REQUEST && !header.IsValidSize())
            {
                Log.Print(LogType.Error, $"WorldSocket.ReadHeaderHandler(): client {GetRemoteIpAddress()} sent malformed packet (size: {header.Size})");
                return ReadDataHandlerResult.Error;
            }

            switch (opcode)
            {
                case Opcode.CMSG_PING:
                    Ping ping = new(packet);
                    ping.Read();
                    if (_connectType == ConnectionType.Realm && GetSession().WorldClient != null && GetSession().WorldClient.IsConnected() && GetSession().WorldClient.IsAuthenticated())
                        GetSession().WorldClient.SendPing(ping.Serial, ping.Latency);
                    else
                        HandlePing(ping);
                    break;
                case Opcode.CMSG_AUTH_SESSION:
                    AuthSession authSession = new(packet);
                    authSession.Read();
                    HandleAuthSession(authSession);
                    return ReadDataHandlerResult.WaitingForQuery;
                case Opcode.CMSG_AUTH_CONTINUED_SESSION:
                    AuthContinuedSession authContinuedSession = new(packet);
                    authContinuedSession.Read();
                    HandleAuthContinuedSession(authContinuedSession);
                    return ReadDataHandlerResult.WaitingForQuery;
                case Opcode.CMSG_KEEP_ALIVE:
                    break;
                case Opcode.CMSG_LOG_DISCONNECT:
                    uint reason = packet.ReadUInt32();
                    Log.Print(LogType.Server, $"Client disconnected with reason {reason}.");
                    if (_connectType == ConnectionType.Realm)
                    {
                        if (GetSession().AuthClient != null)
                            GetSession().AuthClient.Disconnect();
                        if (GetSession().WorldClient != null)
                            GetSession().WorldClient.Disconnect();
                    } 
                    if (GetSession().ModernSniff != null)
                    {
                        GetSession().ModernSniff.CloseFile();
                        GetSession().ModernSniff = null;
                    }

                    break;
                case Opcode.CMSG_ENABLE_NAGLE:
                    SetNoDelay(false);
                    break;
                case Opcode.CMSG_CONNECT_TO_FAILED:
                    ConnectToFailed connectToFailed = new(packet);
                    connectToFailed.Read();
                    HandleConnectToFailed(connectToFailed);
                    break;
                case Opcode.CMSG_ENTER_ENCRYPTED_MODE_ACK:
                    HandleEnterEncryptedModeAck();
                    break;
                case Opcode.CMSG_SERVER_TIME_OFFSET_REQUEST:
                    SendServerTimeOffset();
                    break;
                default:
                    HandlePacket(packet);
                    break;
            }

            return ReadDataHandlerResult.Ok;
        }

        public void HandlePacket(WorldPacket packet)
        {
            Opcode universalOpcode = packet.GetUniversalOpcode(isModern: true);
            var handler = GetHandler(universalOpcode);
            if (handler != null)
                handler.Invoke(this, packet);
            else
                Log.PrintNet(LogType.Warn, LogNetDir.C2P, $"No handler for opcode {universalOpcode} ({packet.GetOpcode()}) (Got unknown packet from ModernClient)");
        }

        private bool TryHandleWotlkMovementAck(WorldPacket packet, Opcode opcode, int payloadSize)
        {
            try
            {
                HandlePacket(packet);
                return true;
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Warn, $"[WotLK] Movement ACK parse failed for {opcode} (payload={payloadSize}): {ex.Message}");
                return false;
            }
        }

        private void SendPacketToServer(WorldPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
        {
            if (GetSession().WorldClient != null)
                GetSession().WorldClient.SendPacketToServer(packet, delayUntilOpcode);
            else
                Log.Print(LogType.Error, $"Attempt to send opcode {packet.GetUniversalOpcode(false)} ({packet.GetOpcode()}) while WorldClient is disconnected!");
        }

        public PacketHandler GetHandler(Opcode opcode)
        {
            return _clientPacketTable.LookupByKey(opcode);
        }

        // C<P S: Sends data to modern client
        public void SendPacket(ServerPacket packet)
        {
            if (!IsOpen())
            {
                Log.PrintNet(LogType.Error, LogNetDir.P2C, $"Can't send {packet.GetUniversalOpcode()}, socket is closed!");
                if (GetSession() != null)
                {
                    if (GetSession().RealmSocket == this)
                        GetSession().RealmSocket = null;
                    else if (GetSession().InstanceSocket == this)
                        GetSession().InstanceSocket = null;
                    GetSession().OnDisconnect();
                }
                return;
            }

            packet.WritePacketData();
            if (GetSession() != null)
                packet.LogPacket(ref GetSession().ModernSniff);

            if (_isWotlkFrontend)
            {
                SendWotlkPacket(packet);
                return;
            }

            _sendMutex.WaitOne();
            var data = packet.GetData();
            Opcode universalOpcode = packet.GetUniversalOpcode();
            ushort opcode = (ushort)packet.GetOpcode();

            Log.PrintNet(LogType.Debug, LogNetDir.P2C, $"Sending opcode {universalOpcode} ({(uint)opcode}).");

            ByteBuffer buffer = new();

            int packetSize = data.Length;
            if (packetSize > 0x400 && _worldCrypt.IsInitialized)
            {
                buffer.WriteInt32(packetSize + 2);
                buffer.WriteUInt32(ZLib.adler32(ZLib.adler32(0x9827D8F1, BitConverter.GetBytes(opcode), 2), data, (uint)packetSize));

                byte[] compressedData;
                uint compressedSize = CompressPacket(data, opcode, out compressedData);
                buffer.WriteUInt32(ZLib.adler32(0x9827D8F1, compressedData, compressedSize));
                buffer.WriteBytes(compressedData, compressedSize);

                packetSize = (int)(compressedSize + 12);
                opcode = (ushort)ModernVersion.GetCurrentOpcode(Opcode.SMSG_COMPRESSED_PACKET);
                System.Diagnostics.Trace.Assert(opcode != 0);

                data = buffer.GetData();
            }

            buffer = new ByteBuffer();
            buffer.WriteUInt16(opcode);
            buffer.WriteBytes(data);
            packetSize += 2 /*opcode*/;

            data = buffer.GetData();

            PacketHeader header = new();
            header.Size = packetSize;
            _worldCrypt.Encrypt(ref data, ref header.Tag);

            ByteBuffer byteBuffer = new();
            header.Write(byteBuffer);
            byteBuffer.WriteBytes(data);

            AsyncWrite(byteBuffer.GetData());
            _sendMutex.ReleaseMutex();
        }

        public uint CompressPacket(byte[] data, ushort opcode, out byte[] outData)
        {
            byte[] uncompressedData = BitConverter.GetBytes(opcode).Combine(data);

            uint bufferSize = ZLib.deflateBound(_compressionStream, (uint)data.Length);
            outData = new byte[bufferSize];

            _compressionStream.next_out = 0;
            _compressionStream.avail_out = bufferSize;
            _compressionStream.out_buf = outData;

            _compressionStream.next_in = 0;
            _compressionStream.avail_in = (uint)uncompressedData.Length;
            _compressionStream.in_buf = uncompressedData;

            int z_res = ZLib.deflate(_compressionStream, 2);
            if (z_res != 0)
            {
                Log.PrintNet(LogType.Error, LogNetDir.P2C, $"Can't compress packet data (zlib: deflate) Error code: {z_res} msg: {_compressionStream.msg}");
                return 0;
            }

            return bufferSize - _compressionStream.avail_out;
        }

        public override bool Update()
        {
            if (!base.Update())
                return false;

            CheckWotlkFrontendLiveness();
            return true;
        }

        private void CheckWotlkFrontendLiveness()
        {
            if (!_isWotlkFrontend || _globalSession == null || _globalSession.IsDisconnecting)
                return;

            GameSessionData gameState = _globalSession.GameState;
            if (gameState == null)
                return;

            if (gameState.HasPendingSyntheticWotlkWorldPortAck &&
                gameState.PendingSyntheticWotlkWorldPortStartTick != 0 &&
                unchecked(Environment.TickCount - gameState.PendingSyntheticWotlkWorldPortStartTick) > WotlkSyntheticWorldPortAckTimeoutMs)
            {
                Log.Print(LogType.Network, "WotLK client did not acknowledge synthetic far-teleport worldport; disconnecting backend so the character is logged out.");
                _globalSession.OnDisconnect();
                return;
            }

            if (gameState.IsWaitingForWotlkMovementTeleportAck &&
                gameState.PendingWotlkMovementTeleportStartTick != 0 &&
                unchecked(Environment.TickCount - gameState.PendingWotlkMovementTeleportStartTick) > WotlkSyntheticWorldPortAckTimeoutMs)
            {
                Log.Print(LogType.Network, "WotLK client did not acknowledge same-map teleport; disconnecting backend so the character is logged out.");
                _globalSession.OnDisconnect();
                return;
            }

            if (gameState.IsInWorld &&
                _globalSession.WorldClient != null &&
                _globalSession.WorldClient.IsConnected() &&
                unchecked(Environment.TickCount - _wotlkLastClientPacketTick) > WotlkClientSilenceDisconnectMs)
            {
                Log.Print(LogType.Network, "No packets received from WotLK client for too long while in world; assuming crashed/hung client and disconnecting backend so the character is logged out.");
                _globalSession.OnDisconnect();
            }
        }

        public override void OnClose()
        {
            GlobalSessionData session = GetSession();
            bool wasAttachedSessionSocket = false;

            if (session != null)
            {
                if (session.RealmSocket == this)
                {
                    session.RealmSocket = null;
                    wasAttachedSessionSocket = true;
                }

                if (session.InstanceSocket == this)
                {
                    session.InstanceSocket = null;
                    wasAttachedSessionSocket = true;
                }
            }

            base.OnClose();

            if (session != null && wasAttachedSessionSocket && !session.IsDisconnecting)
            {
                Log.Print(LogType.Network, "World client socket closed; disconnecting backend world session so the character is logged out.");
                session.OnDisconnect();
            }
        }

        void HandleSendAuthSession()
        {
            AuthChallenge challenge = new();
            challenge.Challenge = _serverChallenge;
            challenge.DosChallenge = new byte[32].GenerateRandomKey(32);
            challenge.DosZeroBits = 1;

            SendPacket(challenge);
        }

        void HandleAuthSession(AuthSession authSession)
        {
            _globalSession = BnetSessionTicketStorage.SessionsByName[authSession.RealmJoinTicket];
            _bnetRpc = new BnetServices.ServiceManager("WorldSocket", this, _globalSession);
            HandleAuthSessionCallback(authSession);
        }

        void HandleAuthSessionCallback(AuthSession authSession)
        {
            RealmBuildInfo buildInfo = GetSession().RealmManager.GetBuildInfo(GetSession().Build);
            if (buildInfo == null)
            {
                SendAuthResponseError(BattlenetRpcErrorCode.BadVersion);
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthSessionCallback: Missing auth seed for realm build {GetSession().Build} ({GetRemoteIpAddress()}).");
                CloseSocket();
                GetSession().OnDisconnect();
                return;
            }

            // For hook purposes, we get Remoteaddress at this point.
            var address = GetRemoteIpAddress();

            bool TrySeed(byte[] seed)
            {
                Sha256 digestKeyHash = new();
                digestKeyHash.Process(GetSession().SessionKey, GetSession().SessionKey.Length);
                digestKeyHash.Finish(seed);
                HmacSha256 hmac = new(digestKeyHash.Digest);
                hmac.Process(authSession.LocalChallenge, authSession.LocalChallenge.Count);
                hmac.Process(_serverChallenge, 16);
                hmac.Finish(AuthCheckSeed, 16);

                // Check that Key and account name are the same on client and server
                return hmac.Digest.Compare(authSession.Digest);
            }

            if (GetSession().OS != "Wn64" && GetSession().OS != "Mc64" && GetSession().OS != "MacA" /*TODO what is windows arm?*/)
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthSession: Unknown OS for account: {GetSession().GameAccountInfo.Id} ('{authSession.RealmJoinTicket}') address: {address}");
                CloseSocket();
                GetSession().OnDisconnect();
                return;
            }
            
            byte[]? platformSeed = buildInfo.BuildSeeds.GetValueOrDefault(GetSession().OS);
            if (platformSeed == null || !TrySeed(platformSeed))
            {
                Log.Print(LogType.Debug, $"WorldSocket.HandleAuthSession: Fallback to static seed");
                if (!TrySeed(buildInfo.FallbackStaticSeed))
                {
                    Log.Print(LogType.Error, $"WorldSocket.HandleAuthSession: Authentication failed for account: {GetSession().GameAccountInfo.Id} ('{authSession.RealmJoinTicket}') address: {address}");
                    CloseSocket();
                    GetSession().OnDisconnect();
                    return;
                }
            }

            Sha256 keyData = new();
            keyData.Finish(GetSession().SessionKey);

            HmacSha256 sessionKeyHmac = new(keyData.Digest);
            sessionKeyHmac.Process(_serverChallenge, 16);
            sessionKeyHmac.Process(authSession.LocalChallenge, authSession.LocalChallenge.Count);
            sessionKeyHmac.Finish(SessionKeySeed, 16);

            _sessionKey = new byte[40];
            var sessionKeyGenerator = new SessionKeyGenerator(sessionKeyHmac.Digest, 32);
            sessionKeyGenerator.Generate(_sessionKey, 40);

            HmacSha256 encryptKeyGen = new(_sessionKey);
            encryptKeyGen.Process(authSession.LocalChallenge, authSession.LocalChallenge.Count);
            encryptKeyGen.Process(_serverChallenge, 16);
            encryptKeyGen.Finish(EncryptionKeySeed, 16);

            // only first 16 bytes of the hmac are used
            Buffer.BlockCopy(encryptKeyGen.Digest, 0, _encryptKey, 0, 16);

            GetSession().SessionKey = _sessionKey;

            Log.Print(LogType.Server, $"WorldSocket:HandleAuthSession: Client '{authSession.RealmJoinTicket}' authenticated successfully from {address}.");

            _realmId = new RealmId((byte)authSession.RegionID, (byte)authSession.BattlegroupID, authSession.RealmID);
            GetSession().WorldClient = new Client.WorldClient();
            if (!GetSession().WorldClient.ConnectToWorldServer(GetSession().RealmManager.GetRealm(_realmId), GetSession()))
            {
                SendAuthResponseError(BattlenetRpcErrorCode.BadServer);
                Log.Print(LogType.Error, "The WorldClient failed to connect to the selected world server!");
                Session.AccountMetaDataMgr.InvalidateLastSelectedCharacter();
                CloseSocket();
                GetSession().OnDisconnect();
                return;
            }

            SendPacket(new EnterEncryptedMode(_encryptKey, true));
            AsyncRead();
        }

        public struct ConnectToKey
        {
            public ulong Raw
            {
                get { return ((ulong)AccountId | ((ulong)connectionType << 32) | (Key << 33)); }
                set
                {
                    AccountId = (uint)(value & 0xFFFFFFFF);
                    connectionType = (ConnectionType)((value >> 32) & 1);
                    Key = (value >> 33);
                }
            }

            public uint AccountId;
            public ConnectionType connectionType;
            public ulong Key;
        }

        void HandleAuthContinuedSession(AuthContinuedSession authSession)
        {
            ConnectToKey key = new();
            _key = key.Raw = authSession.Key;

            _connectType = key.connectionType;
            if (_connectType != ConnectionType.Instance)
            {
                SendAuthResponseError(BattlenetRpcErrorCode.Denied);
                CloseSocket();
                return;
            }

            HandleAuthContinuedSessionCallback(authSession);
        }

        void HandleAuthContinuedSessionCallback(AuthContinuedSession authSession)
        {
            ConnectToKey key = new();
            _key = key.Raw = authSession.Key;

            _globalSession = BnetSessionTicketStorage.SessionsByKey[_key];

            uint accountId = key.AccountId;
            string login = GetSession().AccountInfo.Login;
            _sessionKey = GetSession().SessionKey;

            HmacSha256 hmac = new(_sessionKey);
            hmac.Process(BitConverter.GetBytes(authSession.Key), 8);
            hmac.Process(authSession.LocalChallenge, authSession.LocalChallenge.Length);
            hmac.Process(_serverChallenge, 16);
            hmac.Finish(ContinuedSessionSeed, 16);

            if (!hmac.Digest.Compare(authSession.Digest))
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleAuthContinuedSession: Authentication failed for account: {accountId} ('{login}') address: {GetRemoteIpAddress()}");
                CloseSocket();
                return;
            }

            HmacSha256 encryptKeyGen = new(_sessionKey);
            encryptKeyGen.Process(authSession.LocalChallenge, authSession.LocalChallenge.Length);
            encryptKeyGen.Process(_serverChallenge, 16);
            encryptKeyGen.Finish(EncryptionKeySeed, 16);

            // only first 16 bytes of the hmac are used
            Buffer.BlockCopy(encryptKeyGen.Digest, 0, _encryptKey, 0, 16);

            SendPacket(new EnterEncryptedMode(_encryptKey, true));
            AsyncRead();
        }

        public void SendConnectToInstance(ConnectToSerial serial)
        {
            IPAddress externalIp = IPAddress.Parse(Framework.Settings.ExternalAddress);
            IPEndPoint instanceAddress = new IPEndPoint(externalIp, Framework.Settings.InstancePort);
            
            _instanceConnectKey.AccountId = GetSession().AccountInfo.Id;
            _instanceConnectKey.connectionType = ConnectionType.Instance;
            _instanceConnectKey.Key = RandomHelper.URand(0, 0x7FFFFFFF);

            BnetSessionTicketStorage.AddNewSessionByKey(_instanceConnectKey.Raw, GetSession());

            ConnectTo connectTo = new();
            connectTo.Key = _instanceConnectKey.Raw;
            connectTo.Serial = serial;
            connectTo.Payload.Port = (ushort)Framework.Settings.InstancePort;
            connectTo.Con = (byte)ConnectionType.Instance;

            if (instanceAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                connectTo.Payload.Where.IPv4 = instanceAddress.Address.GetAddressBytes();
                connectTo.Payload.Where.Type = ConnectTo.AddressType.IPv4;
            }
            else
            {
                connectTo.Payload.Where.IPv6 = instanceAddress.Address.GetAddressBytes();
                connectTo.Payload.Where.Type = ConnectTo.AddressType.IPv6;
            }

            SendPacket(connectTo);
        }
        public class CharacterLoginFailed : ServerPacket
        {
            public CharacterLoginFailed(LoginFailureReason code) : base(Opcode.SMSG_CHARACTER_LOGIN_FAILED)
            {
                Code = code;
            }

            public override void Write()
            {
                _worldPacket.WriteUInt8((byte)Code);
            }

            LoginFailureReason Code;
        }
        public void AbortLogin(LoginFailureReason reason)
        {
            SendPacket(new CharacterLoginFailed(reason));

        }
        void HandleConnectToFailed(ConnectToFailed connectToFailed)
        {
            switch (connectToFailed.Serial)
            {
                case ConnectToSerial.WorldAttempt1:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt2);
                    break;
                case ConnectToSerial.WorldAttempt2:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt3);
                    break;
                case ConnectToSerial.WorldAttempt3:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt4);
                    break;
                case ConnectToSerial.WorldAttempt4:
                    SendConnectToInstance(ConnectToSerial.WorldAttempt5);
                    break;
                case ConnectToSerial.WorldAttempt5:
                {
                    Log.Print(LogType.Error, "Failed to connect 5 times to world socket, aborting login");
                    AbortLogin(LoginFailureReason.NoWorld);
                    break;
                }
                default:
                    return;
            }
        }

        void HandleEnterEncryptedModeAck()
        {
            _worldCrypt.Initialize(_encryptKey);
            if (_connectType == ConnectionType.Realm)
            {
                SendAuthResponse(BattlenetRpcErrorCode.Ok, GetSession().WorldClient.GetQueuePosition());
                SendSetTimeZoneInformation();
                SendFeatureSystemStatusGlueScreen();
                SendClientCacheVersion(0);
                SendAvailableHotfixes();
                SendBnetConnectionState(1);
                GetSession().AccountDataMgr = new AccountDataManager(GetSession().Username, GetSession().RealmManager.GetRealm(_realmId).Name);
                GetSession().RealmSocket = this;
            }
            else
            {
                Log.Print(LogType.Server, "Client has connected to the instance server.");
                SendPacket(new ResumeComms(ConnectionType.Instance));
                GetSession().InstanceSocket = this;
            }
        }

        public void SendAuthResponseError(BattlenetRpcErrorCode code)
        {
            AuthResponse response = new();
            response.SuccessInfo = null;
            response.WaitInfo = null;
            response.Result = code;
            SendPacket(response);
        }

        public void SendAuthResponse(BattlenetRpcErrorCode code, uint queuePos = 0)
        {
            AuthResponse response = new();
            response.Result = code;

            if (code == BattlenetRpcErrorCode.Ok)
            {
                response.SuccessInfo = new AuthResponse.AuthSuccessInfo();
                response.SuccessInfo.ActiveExpansionLevel = (byte)(LegacyVersion.ExpansionVersion - 1);
                response.SuccessInfo.AccountExpansionLevel = (byte)0;
                response.SuccessInfo.VirtualRealmAddress = _realmId.GetAddress();
                response.SuccessInfo.Time = (uint)Time.UnixTime;

                var realm = GetSession().RealmManager.GetRealm(_realmId);

                // Send current home realm. Also there is no need to send it later in realm queries.
                response.SuccessInfo.VirtualRealms.Add(new VirtualRealmInfo(realm.Id.GetAddress(), true, false, realm.Name, realm.NormalizedName));

                List<RaceClassAvailability> availableRaces = new List<RaceClassAvailability>();
                RaceClassAvailability race = new RaceClassAvailability();

                race.RaceID = 1;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(2, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 2;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(7, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 3;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(2, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 4;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(11, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 5;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 6;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(7, 0, 0));
                race.Classes.Add(new ClassAvailability(11, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 7;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                race.Classes.Add(new ClassAvailability(9, 0, 0));
                availableRaces.Add(race);

                race = new RaceClassAvailability();
                race.RaceID = 8;
                race.Classes.Add(new ClassAvailability(1, 0, 0));
                race.Classes.Add(new ClassAvailability(4, 0, 0));
                race.Classes.Add(new ClassAvailability(3, 0, 0));
                race.Classes.Add(new ClassAvailability(5, 0, 0));
                race.Classes.Add(new ClassAvailability(7, 0, 0));
                race.Classes.Add(new ClassAvailability(8, 0, 0));
                availableRaces.Add(race);

                if (ModernVersion.ExpansionVersion >= 2 &&
                    LegacyVersion.ExpansionVersion >= 2)
                {
                    race = new RaceClassAvailability();
                    race.RaceID = 10;
                    race.Classes.Add(new ClassAvailability(3, 0, 0));
                    race.Classes.Add(new ClassAvailability(4, 0, 0));
                    race.Classes.Add(new ClassAvailability(5, 0, 0));
                    race.Classes.Add(new ClassAvailability(8, 0, 0));
                    race.Classes.Add(new ClassAvailability(9, 0, 0));
                    race.Classes.Add(new ClassAvailability(2, 0, 0));
                    availableRaces.Add(race);

                    race = new RaceClassAvailability();
                    race.RaceID = 11;
                    race.Classes.Add(new ClassAvailability(1, 0, 0));
                    race.Classes.Add(new ClassAvailability(2, 0, 0));
                    race.Classes.Add(new ClassAvailability(3, 0, 0));
                    race.Classes.Add(new ClassAvailability(5, 0, 0));
                    race.Classes.Add(new ClassAvailability(8, 0, 0));
                    race.Classes.Add(new ClassAvailability(7, 0, 0));
                    availableRaces.Add(race);
                }

                response.SuccessInfo.AvailableClasses = availableRaces;
            }

            if (queuePos != 0)
            {
                response.WaitInfo = new AuthWaitInfo();
                response.WaitInfo.WaitCount = queuePos;
            }

            SendPacket(response);
        }

        public void SendAuthWaitQue(uint position)
        {
            if (position != 0)
            {
                WaitQueueUpdate waitQueueUpdate = new();
                waitQueueUpdate.WaitInfo.WaitCount = position;
                waitQueueUpdate.WaitInfo.WaitTime = 0;
                waitQueueUpdate.WaitInfo.HasFCM = false;
                SendPacket(waitQueueUpdate);
            }
            else
                SendPacket(new WaitQueueFinish());
        }

        public void SendSetTimeZoneInformation()
        {
            // @todo: replace dummy values
            SetTimeZoneInformation packet = new();
            packet.ServerTimeTZ = "Europe/Paris";
            packet.GameTimeTZ = "Europe/Paris";

            SendPacket(packet);//enabled it
        }

        public void SendFeatureSystemStatusGlueScreen()
        {
            FeatureSystemStatusGlueScreen features = new();
            features.BpayStoreAvailable = false;
            features.BpayStoreDisabledByParentalControls = false;
            features.CharUndeleteEnabled = false;
            features.BpayStoreEnabled = false;
            features.MaxCharactersPerRealm = 10;
            features.MinimumExpansionLevel = 5;
            features.MaximumExpansionLevel = 8;
            features.Unk14 = true;

            var europaTicketConfig = new EuropaTicketConfig();
            europaTicketConfig.ThrottleState.MaxTries = 10;
            europaTicketConfig.ThrottleState.PerMilliseconds = 60000;
            europaTicketConfig.ThrottleState.TryCount = 1;
            europaTicketConfig.ThrottleState.LastResetTimeBeforeNow = 111111;
            europaTicketConfig.TicketsEnabled = true;
            europaTicketConfig.BugsEnabled = true;
            europaTicketConfig.ComplaintsEnabled = true;
            europaTicketConfig.SuggestionsEnabled = true;

            features.EuropaTicketSystemStatus = europaTicketConfig;

            SendPacket(features);
        }

        public void SendFeatureSystemStatus()
        {
            FeatureSystemStatus features = new();
            features.ComplaintStatus = 2;
            features.ScrollOfResurrectionRequestsRemaining = 1;
            features.ScrollOfResurrectionMaxRequestsPerDay = 1;
            features.CfgRealmID = 1;
            features.CfgRealmRecID = 1;
            features.TwitterPostThrottleLimit = 60;
            features.TwitterPostThrottleCooldown = 20;
            features.TokenPollTimeSeconds = 300;
            features.KioskSessionMinutes = 30;
            features.BpayStoreProductDeliveryDelay = 180;
            features.HiddenUIClubsPresenceUpdateTimer = 60000;
            features.VoiceEnabled = false;
            features.BrowserEnabled = false;

            features.EuropaTicketSystemStatus = new EuropaTicketConfig();
            features.EuropaTicketSystemStatus.ThrottleState.MaxTries = 10;
            features.EuropaTicketSystemStatus.ThrottleState.PerMilliseconds = 60000;
            features.EuropaTicketSystemStatus.ThrottleState.TryCount = 1;
            features.EuropaTicketSystemStatus.ThrottleState.LastResetTimeBeforeNow = 111111;

            features.TutorialsEnabled = true;
            features.Unk67 = true;
            features.QuestSessionEnabled = true;
            features.BattlegroundsEnabled = true;

            features.QuickJoinConfig.ToastDuration = 7;
            features.QuickJoinConfig.DelayDuration = 10;
            features.QuickJoinConfig.QueueMultiplier = 1;
            features.QuickJoinConfig.PlayerMultiplier = 1;
            features.QuickJoinConfig.PlayerFriendValue = 5;
            features.QuickJoinConfig.PlayerGuildValue = 1;
            features.QuickJoinConfig.ThrottleDecayTime = 60;
            features.QuickJoinConfig.ThrottlePrioritySpike = 20;
            features.QuickJoinConfig.ThrottlePvPPriorityNormal = 50;
            features.QuickJoinConfig.ThrottlePvPPriorityLow = 1;
            features.QuickJoinConfig.ThrottlePvPHonorThreshold = 10;
            features.QuickJoinConfig.ThrottleLfgListPriorityDefault = 50;
            features.QuickJoinConfig.ThrottleLfgListPriorityAbove = 100;
            features.QuickJoinConfig.ThrottleLfgListPriorityBelow = 50;
            features.QuickJoinConfig.ThrottleLfgListIlvlScalingAbove = 1;
            features.QuickJoinConfig.ThrottleLfgListIlvlScalingBelow = 1;
            features.QuickJoinConfig.ThrottleRfPriorityAbove = 100;
            features.QuickJoinConfig.ThrottleRfIlvlScalingAbove = 1;
            features.QuickJoinConfig.ThrottleDfMaxItemLevel = 850;
            features.QuickJoinConfig.ThrottleDfBestPriority = 80;

            features.Squelch.IsSquelched = false;
            features.Squelch.BnetAccountGuid = WowGuid128.Create(HighGuidType703.BNetAccount, GetSession().AccountInfo.Id);
            features.Squelch.GuildGuid = WowGuid128.Empty;

            features.EuropaTicketSystemStatus.TicketsEnabled = true;
            features.EuropaTicketSystemStatus.BugsEnabled = true;
            features.EuropaTicketSystemStatus.ComplaintsEnabled = true;
            features.EuropaTicketSystemStatus.SuggestionsEnabled = true;

            features.EuropaTicketSystemStatus.ThrottleState.MaxTries = 10;
            features.EuropaTicketSystemStatus.ThrottleState.PerMilliseconds = 60000;
            features.EuropaTicketSystemStatus.ThrottleState.TryCount = 1;
            features.EuropaTicketSystemStatus.ThrottleState.LastResetTimeBeforeNow = 10627480;
            SendPacket(features);
        }

        public void SendSeasonInfo()
        {
            SeasonInfo seasonInfo = new();
            if (LegacyVersion.ExpansionVersion > 1 &&
                ModernVersion.ExpansionVersion > 1)
            {
                seasonInfo.CurrentSeason = 2;
                seasonInfo.PreviousSeason = 1;
            }
            SendPacket(seasonInfo);
        }

        public void SendMotd()
        {
            MOTD motd = new();
            SendPacket(motd);
        }

        public void SendClientCacheVersion(uint version)
        {
            ClientCacheVersion cache = new();
            cache.CacheVersion = version;
            SendPacket(cache);
        }

        public void SendAvailableHotfixes()
        {
            AvailableHotfixes hotfixes = new AvailableHotfixes();
            hotfixes.VirtualRealmAddress = GetSession().RealmId.GetAddress();
            SendPacket(hotfixes);
        }

        public void SendBnetConnectionState(byte state)
        {
            ConnectionStatus bnetConnected = new();
            bnetConnected.State = state;
            SendPacket(bnetConnected);
        }

        public void SendServerTimeOffset()
        {
            ServerTimeOffset response = new();
            response.Time = Time.UnixTime;
            SendPacket(response);
        }

        void HandlePing(Ping ping)
        {
            SendPacket(new Pong(ping.Serial));
        }

        public void SendAccountDataTimes()
        {
            System.Diagnostics.Trace.Assert(_connectType == ConnectionType.Realm);

            if (GetSession() == null || GetSession().GameState == null)
            {
                Log.Print(LogType.Error, "WorldSocket.SendAccountDataTimes: session or game state is null.");
                return;
            }

            if (GetSession().AccountDataMgr == null)
            {
                Log.Print(LogType.Error, "WorldSocket.SendAccountDataTimes: AccountDataMgr is null.");
                return;
            }

            WowGuid128 guid = GetSession().GameState.CurrentPlayerGuid;
            GetSession().AccountDataMgr.LoadAllData(guid);

            AccountDataTimes accountData = new AccountDataTimes();
            accountData.PlayerGuid = guid;
            accountData.ServerTime = Time.UnixTime;

            int count = ModernVersion.GetAccountDataCount();
            accountData.AccountTimes = new long[count];
            for (int i = 0; i < count; i++)
                accountData.AccountTimes[i] = GetSession().AccountDataMgr.Data[i] != null ? GetSession().AccountDataMgr.Data[i].Timestamp : 0;

            SendPacket(accountData);
        }

        public void SendRpcMessage(uint serviceId, OriginalHash service, uint methodId, uint token, BattlenetRpcErrorCode status, IMessage? message)
        {
            var methodInfo = new MethodCall();
            methodInfo.SetServiceHash((uint)service);
            methodInfo.SetMethodId(methodId);
            methodInfo.Token = token;
            methodInfo.ObjectId = serviceId;

            byte[] bytes = message == null ? Array.Empty<byte>() : message.ToByteArray();
            BattlenetResponse response = new()
            {
                Method = methodInfo,
                Status = status,
                Data   = new ByteBuffer(bytes),
            };

            SendPacket(response);
        }

        public IPEndPoint GetRemoteIpEndPoint()
        {
            return GetRemoteIpAddress();
        }

        public void InitializePacketHandlers()
        {
            foreach (var methodInfo in typeof(WorldSocket).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                foreach (var msgAttr in methodInfo.GetCustomAttributes<PacketHandlerAttribute>())
                {
                    if (msgAttr == null)
                        continue;

                    if (msgAttr.Opcode == Opcode.MSG_NULL_ACTION)
                        continue;

                    if (_clientPacketTable.ContainsKey(msgAttr.Opcode))
                    {
                        Log.Print(LogType.Error, $"Tried to override OpcodeHandler of {_clientPacketTable[msgAttr.Opcode].ToString()} with {methodInfo.Name} (Opcode {msgAttr.Opcode})");
                        continue;
                    }

                    var parameters = methodInfo.GetParameters();
                    if (parameters.Length == 0)
                    {
                        Log.Print(LogType.Error, $"Method: {methodInfo.Name} Has no paramters");
                        continue;
                    }

                    if (parameters[0].ParameterType.BaseType != typeof(ClientPacket))
                    {
                        Log.Print(LogType.Error, $"Method: {methodInfo.Name} has wrong BaseType");
                        continue;
                    }

                    _clientPacketTable[msgAttr.Opcode] = new PacketHandler(methodInfo, parameters[0].ParameterType);
                }
            }
        }

        public class PacketHandler
        {
            public PacketHandler(MethodInfo info, Type type)
            {
                methodCaller = (Action<WorldSocket, ClientPacket>)GetType().GetMethod("CreateDelegate", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(type).Invoke(null, new object[] { info });
                packetType = type;
            }

            public void Invoke(WorldSocket session, WorldPacket packet)
            {
                if (packetType == null)
                    return;

                using var clientPacket = (ClientPacket)Activator.CreateInstance(packetType, packet);
                clientPacket.LogPacket(ref session.GetSession().ModernSniff);
                clientPacket.Read();
                methodCaller(session, clientPacket);
            }

            static Action<WorldSocket, ClientPacket> CreateDelegate<P1>(MethodInfo method) where P1 : ClientPacket
            {
                // create first delegate. It is not fine because its 
                // signature contains unknown types T and P1
                Action<WorldSocket, P1> d = (Action<WorldSocket, P1>)method.CreateDelegate(typeof(Action<WorldSocket, P1>));
                // create another delegate having necessary signature. 
                // It encapsulates first delegate with a closure
                return delegate (WorldSocket target, ClientPacket p) { d(target, (P1)p); };
            }

            Action<WorldSocket, ClientPacket> methodCaller;
            Type packetType;
        }
    }

    enum ReadDataHandlerResult
    {
        Ok = 0,
        Error = 1,
        WaitingForQuery = 2
    }    public partial class WorldSocket
    {
private int _wotlkUpdateObjectSampleCount;
private readonly Dictionary<uint, int> _wotlkItemQueryThrottle = new();
private const int WotlkItemQueryThrottleMs = 1500;

        private void AcceptWotlkFrontend()
        {
            Log.Print(LogType.Network, $"WotLK world client connected from {GetRemoteIpAddress()}");

            SendWotlkAuthChallenge();
            AsyncRead();
        }

        private bool ReadWotlkHeader()
        {
            byte[] header = _headerBuffer.GetData();

            if (_wotlkHeaderCryptInitialized)
                _wotlkHeaderCrypt.Decrypt(header, WotlkHeaderSize);

            _wotlkClientPacketSize = NetworkUtility.EndianConvert(BitConverter.ToUInt16(header, 0));
            _wotlkClientOpcode = BitConverter.ToUInt32(header, sizeof(ushort));

            if (_wotlkClientPacketSize < sizeof(uint) || _wotlkClientPacketSize > 0x7FFF)
            {
                Log.Print(LogType.Error, $"WorldSocket.ReadWotlkHeader(): malformed packet from {GetRemoteIpAddress()} (size: {_wotlkClientPacketSize}, opcode: {_wotlkClientOpcode})");
                return false;
            }

            _packetBuffer.Resize(_wotlkClientPacketSize - sizeof(uint));
            return true;
        }

        private ReadDataHandlerResult ReadWotlkData()
        {
            try
            {
                int payloadSize = _wotlkClientPacketSize - sizeof(uint);
                byte[] payload = _packetBuffer.GetData();
                _packetBuffer.Reset();

                ushort opcode16 = (ushort)_wotlkClientOpcode;
                byte[] packetData = new byte[payloadSize + sizeof(ushort)];
                packetData[0] = (byte)(opcode16 & 0xFF);
                packetData[1] = (byte)((opcode16 >> 8) & 0xFF);
                if (payloadSize > 0)
                    Buffer.BlockCopy(payload, 0, packetData, sizeof(ushort), payloadSize);

                WorldPacket packet = new(packetData);
                Opcode universalOpcode = packet.GetUniversalOpcode(isModern: true);
                _wotlkLastClientPacketTick = Environment.TickCount;

                if (universalOpcode != Opcode.CMSG_MOVE_TIME_SKIPPED)
                    if (!IsWotlkMoveMessageOpcode(universalOpcode))
                        Log.PrintNet(LogType.Debug, LogNetDir.C2P, $"[WotLK] Received opcode {universalOpcode} ({_wotlkClientOpcode}).");

                switch (universalOpcode)
                {
                    case Opcode.CMSG_PING:
                    {
                        Ping ping = new(packet);
                        ping.Read();
                        if (_globalSession?.WorldClient != null && _globalSession.WorldClient.IsConnected() && _globalSession.WorldClient.IsAuthenticated())
                            _globalSession.WorldClient.SendPing(ping.Serial, ping.Latency);
                        else
                            HandlePing(ping);
                        break;
                    }
                    case Opcode.CMSG_AUTH_SESSION:
                    {
                        if (!HandleWotlkAuthSession(packet))
                            return ReadDataHandlerResult.Error;

                        // WotLK auth/session setup is fully synchronous in Hermes.
                        // Returning WaitingForQuery here stops AsyncRead() and the client
                        // gets stuck on "Retrieving character list" because CMSG_CHAR_ENUM
                        // is never processed.
                        return ReadDataHandlerResult.Ok;
                    }
                    case Opcode.CMSG_ENUM_CHARACTERS:
                    {
                        if (!ForwardWotlkPayloadToLegacy(universalOpcode, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_UPDATE_ACCOUNT_DATA:
                    {
                        HandleWotlkUpdateAccountData(payload);
                        break;
                    }
                    case Opcode.CMSG_REQUEST_ACCOUNT_DATA:
                    {
                        HandleWotlkRequestAccountData(payload);
                        break;
                    }
                    case Opcode.CMSG_ITEM_QUERY_SINGLE:
                    {
                        if (!ForwardWotlkItemQuerySingle(payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_MESSAGECHAT:
                    {
                        if (!HandleWotlkMessageChat(payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_KEEP_ALIVE:
                        break;
                    case Opcode.CMSG_LOG_DISCONNECT:
                    {
                        if (packet.CanRead())
                        {
                            uint reason = packet.ReadUInt32();
                            Log.Print(LogType.Server, $"WotLK client disconnected with reason {reason}.");
                        }

                        if (_globalSession != null)
                            _globalSession.OnDisconnect();

                        break;
                    }
                    case Opcode.CMSG_CREATE_CHARACTER:
                    case Opcode.CMSG_CHAR_DELETE:
                    {
                        if (!ForwardWotlkPayloadToLegacy(universalOpcode, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_LOGOUT_REQUEST:
                    {
                        _wotlkLogoutInProgress = true;
                        if (!ForwardWotlkPayloadToLegacy(universalOpcode, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_LOGOUT_CANCEL:
                    {
                        _wotlkLogoutInProgress = false;
                        if (!ForwardWotlkPayloadToLegacy(universalOpcode, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_GROUP_ACCEPT:
                    case Opcode.CMSG_GROUP_DECLINE:
                    {
                        if (!ForwardWotlkPayloadToLegacy(universalOpcode, Array.Empty<byte>()))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_NAME_QUERY:
                    {
                        Opcode delay = _globalSession?.GameState?.IsInWorld == true ? Opcode.MSG_NULL_ACTION : Opcode.SMSG_LOGIN_VERIFY_WORLD;
                        if (!ForwardWotlkPayloadToLegacy(universalOpcode, payload, delay))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_REQUEST_PLAYED_TIME:
                    {
                        bool triggerScriptEvent = payloadSize > 0 && payload[0] != 0;
                        if (!HandleWotlkRequestPlayedTime(triggerScriptEvent))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_MOVE_TIME_SKIPPED:
                    {
                        // 3.3.5 client can spam this while loading; vanilla backend does not need it.
                        break;
                    }
                    case Opcode.CMSG_MOVE_WATER_WALK_ACK:
                    case Opcode.CMSG_MOVE_FEATHER_FALL_ACK:
                    case Opcode.CMSG_MOVE_HOVER_ACK:
                    case Opcode.CMSG_MOVE_SET_CAN_FLY_ACK:
                    case Opcode.CMSG_MOVE_FORCE_WALK_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_RUN_BACK_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_SWIM_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_SWIM_BACK_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_TURN_RATE_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_PITCH_RATE_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_FLIGHT_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_FLIGHT_BACK_SPEED_CHANGE_ACK:
                    case Opcode.CMSG_MOVE_FORCE_ROOT_ACK:
                    case Opcode.CMSG_MOVE_FORCE_UNROOT_ACK:
                    case Opcode.CMSG_MOVE_KNOCK_BACK_ACK:
                    case Opcode.CMSG_MOVE_GRAVITY_DISABLE_ACK:
                    case Opcode.CMSG_MOVE_GRAVITY_ENABLE_ACK:
                    {
                        if (!TryHandleWotlkMovementAck(packet, universalOpcode, payloadSize))
                            Log.Print(LogType.Warn, $"[WotLK] Dropped malformed movement ACK {universalOpcode}.");
                        break;
                    }
                    case Opcode.CMSG_MOVE_TELEPORT_ACK:
                    case Opcode.MSG_MOVE_TELEPORT_ACK:
                    {
                        // Do not raw-forward: modern payload uses packed guid and crashes 1.12
                        // backend parser for opcode 199. Route through movement handler.
                        HandlePacket(packet);
                        break;
                    }
                    case Opcode.CMSG_SET_ACTIVE_MOVER:
                    {
                        HandlePacket(packet);
                        SendWotlkMovementBootstrap(force: true);
                        break;
                    }
                    case Opcode.CMSG_READY_FOR_ACCOUNT_DATA_TIMES:
                    {
                        HandleWotlkReadyForAccountDataTimes();
                        break;
                    }
                    case Opcode.CMSG_CALENDAR_GET_CALENDAR:
                    {
                        SendWotlkCalendar();
                        break;
                    }
                    case Opcode.CMSG_CALENDAR_GET_NUM_PENDING:
                    {
                        SendWotlkCalendarNumPending();
                        break;
                    }
                    case Opcode.CMSG_REALM_SPLIT:
                    case Opcode.CMSG_SET_ACTIVE_VOICE_CHANNEL:
                    case Opcode.CMSG_GAME_OBJ_REPORT_USE:
                    case Opcode.CMSG_GET_ITEM_PURCHASE_DATA:
                    case Opcode.CMSG_DF_GET_JOIN_STATUS:
                    case Opcode.CMSG_LFG_JOIN:
                    case Opcode.CMSG_LFG_PLAYER_LOCK_INFO_REQUEST:
                    case Opcode.CMSG_GM_LAG_REPORT:
                    case Opcode.MSG_GUILD_BANK_MONEY_WITHDRAWN:
                    case Opcode.CMSG_VOICE_SESSION_ENABLE:
                    {
                        // Modern-only quality-of-life packets; safe to ignore for vanilla backend.
                        break;
                    }
                    case Opcode.CMSG_UI_TIME_REQUEST:
                    {
                        SendWotlkUiTimeResponse();
                        break;
                    }
                    case Opcode.CMSG_PLAYER_LOGIN:
                    {
                        if (!HandleWotlkPlayerLogin(packet, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_CAST_SPELL:
                    case Opcode.CMSG_SET_ACTION_BUTTON:
                    {
                        if (!TryHandleWotlkGameplayOpcode(packet, universalOpcode, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_USE_ITEM:
                    {
                        if (!HandleWotlkUseItem(payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    case Opcode.CMSG_ZONEUPDATE:
                    case Opcode.CMSG_AREA_TRIGGER:
                    case Opcode.CMSG_REPOP_REQUEST:
                    case Opcode.CMSG_RECLAIM_CORPSE:
                    case Opcode.CMSG_QUERY_CORPSE_LOCATION_FROM_CLIENT:
                    case Opcode.CMSG_CORPSE_MAP_POSITION_QUERY:
                    case Opcode.CMSG_CORPSE_QUERY:
                    case Opcode.MSG_CORPSE_QUERY:
                    {
                        // These packets need state-aware handling during WotLK frontend
                        // same-map teleports.  Do not raw-forward them from this WotLK
                        // dispatch path, otherwise stale source zone/area packets can reach
                        // the 1.12 backend while a teleport ACK is still pending.
                        HandlePacket(packet);
                        break;
                    }
                    case Opcode.CMSG_BANKER_ACTIVATE:
                    case Opcode.CMSG_BINDER_ACTIVATE:
                    case Opcode.CMSG_LIST_INVENTORY:
                    case Opcode.CMSG_SPIRIT_HEALER_ACTIVATE:
                    case Opcode.CMSG_TALK_TO_GOSSIP:
                    case Opcode.CMSG_GOSSIP_SELECT_OPTION:
                    case Opcode.CMSG_TRAINER_LIST:
                    case Opcode.CMSG_TRAINER_BUY_SPELL:
                    case Opcode.CMSG_BATTLEMASTER_HELLO:
                    case Opcode.CMSG_BATTLEMASTER_JOIN:
                    case Opcode.CMSG_BATTLEFIELD_LIST:
                    case Opcode.CMSG_BATTLEFIELD_PORT:
                    case Opcode.CMSG_REQUEST_BATTLEFIELD_STATUS:
                    case Opcode.CMSG_PVP_LOG_DATA:
                    case Opcode.CMSG_BATTLEFIELD_LEAVE:
                    case Opcode.CMSG_AREA_SPIRIT_HEALER_QUERY:
                    case Opcode.CMSG_AREA_SPIRIT_HEALER_QUEUE:
                    case Opcode.CMSG_QUEST_GIVER_STATUS_QUERY:
                    case Opcode.CMSG_QUEST_GIVER_STATUS_MULTIPLE_QUERY:
                    case Opcode.CMSG_QUEST_GIVER_HELLO:
                    case Opcode.CMSG_QUEST_GIVER_QUERY_QUEST:
                    case Opcode.CMSG_QUEST_GIVER_ACCEPT_QUEST:
                    case Opcode.CMSG_QUEST_GIVER_COMPLETE_QUEST:
                    case Opcode.CMSG_QUEST_GIVER_REQUEST_REWARD:
                    case Opcode.CMSG_QUEST_GIVER_CHOOSE_REWARD:
                    case Opcode.CMSG_QUEST_LOG_REMOVE_QUEST:
                    case Opcode.CMSG_QUEST_CONFIRM_ACCEPT:
                    case Opcode.CMSG_QUEST_POI_QUERY:
                    case Opcode.CMSG_PUSH_QUEST_TO_PARTY:
                    case Opcode.CMSG_QUEST_PUSH_RESULT:
                    case Opcode.CMSG_AUCTION_HELLO_REQUEST:
                    case Opcode.CMSG_AUCTION_LIST_BIDDED_ITEMS:
                    case Opcode.CMSG_AUCTION_LIST_OWNED_ITEMS:
                    case Opcode.CMSG_AUCTION_LIST_ITEMS:
                    case Opcode.CMSG_AUCTION_LIST_PENDING_SALES:
                    case Opcode.CMSG_AUCTION_SELL_ITEM:
                    case Opcode.CMSG_AUCTION_REMOVE_ITEM:
                    case Opcode.CMSG_AUCTION_PLACE_BID:
                    case Opcode.CMSG_INITIATE_TRADE:
                    case Opcode.CMSG_BEGIN_TRADE:
                    case Opcode.CMSG_BUSY_TRADE:
                    case Opcode.CMSG_UNACCEPT_TRADE:
                    case Opcode.CMSG_IGNORE_TRADE:
                    case Opcode.CMSG_SET_TRADE_ITEM:
                    case Opcode.CMSG_CLEAR_TRADE_ITEM:
                    case Opcode.CMSG_SET_TRADE_GOLD:
                    case Opcode.CMSG_ACCEPT_TRADE:
                    case Opcode.CMSG_BUY_ITEM:
                    case Opcode.CMSG_BUY_BACK_ITEM:
                    case Opcode.CMSG_SELL_ITEM:
                    case Opcode.CMSG_SET_PARTY_LEADER:
                    case Opcode.CMSG_SET_ASSISTANT_LEADER:
                    {
                        HandlePacket(packet);
                        break;
                    }
                    case Opcode.CMSG_CANCEL_TRADE:
                    {
                        if (_wotlkLogoutInProgress || GetSession().GameState.CurrentTrade == null)
                        {
                            Log.Print(LogType.Debug, "[WotLK] Ignoring CMSG_CANCEL_TRADE with no active trade/logout in progress.");
                            break;
                        }

                        HandlePacket(packet);
                        break;
                    }
                    case Opcode.CMSG_SWAP_INV_ITEM:
                    case Opcode.CMSG_SWAP_ITEM:
                    case Opcode.CMSG_AUTO_EQUIP_ITEM:
                    case Opcode.CMSG_AUTO_STORE_BAG_ITEM:
                    case Opcode.CMSG_AUTO_EQUIP_ITEM_SLOT:
                    {
                        if (!ForwardWotlkInventoryOpcodeRaw(universalOpcode, payload))
                            return ReadDataHandlerResult.Error;
                        break;
                    }
                    default:
                    {
                        if (!_wotlkHeaderCryptInitialized)
                        {
                            Log.Print(LogType.Error, $"WotLK client sent {universalOpcode} before authentication.");
                            return ReadDataHandlerResult.Error;
                        }

                        if (IsWotlkMoveMessageOpcode(universalOpcode))
                        {
                            HandlePacket(packet);
                        }
                        else if (!ForwardWotlkPayloadToLegacy(universalOpcode, payload))
                            HandlePacket(packet);
                        break;
                    }
                }

                return ReadDataHandlerResult.Ok;
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Error, $"WorldSocket.ReadWotlkData(): exception while processing packet: {ex}");
                return ReadDataHandlerResult.Error;
            }
        }

        private bool HandleWotlkAuthSession(WorldPacket packet)
        {
            uint build = packet.ReadUInt32();
            _ = packet.ReadUInt32(); // loginServerId
            string account = packet.ReadCString();
            _ = packet.ReadUInt32(); // loginServerType
            byte[] localChallenge = packet.ReadBytes(4);
            uint regionId = packet.ReadUInt32();
            uint battlegroupId = packet.ReadUInt32();
            uint realmId = packet.ReadUInt32();
            _ = packet.ReadUInt64(); // dosResponse
            byte[] digest = packet.ReadBytes(20);
            _ = packet.ReadToEnd(); // addonInfo

            if (!WotlkFrontendSessionStore.TryGet(account, out WotlkFrontendSession frontendSession))
            {
                string normalized = account.Trim().ToUpperInvariant();
                if (!WotlkFrontendSessionStore.TryGet(normalized, out frontendSession))
                {
                    Log.Print(LogType.Error, $"WorldSocket.HandleWotlkAuthSession: no matching frontend auth session for account '{account}'.");
                    SendWotlkAuthResponse(AuthResult.AUTH_UNKNOWN_ACCOUNT);
                    return false;
                }
            }

            if (build == (uint)Settings.ServerBuild)
            {
                Log.Print(LogType.Error,
                    $"WorldSocket.HandleWotlkAuthSession: detected backend world self-loop for '{account}'. " +
                    $"Received legacy build {build} on WotLK frontend socket. " +
                    $"Set WotlkWorldPort to a port different from backend realm/world port.");
                SendWotlkAuthResponse(AuthResult.AUTH_FAILED);
                return false;
            }

            if (build != (uint)Settings.ClientBuild)
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleWotlkAuthSession: build mismatch for account '{account}'. Client={build}, expected={(uint)Settings.ClientBuild}.");
                SendWotlkAuthResponse(AuthResult.AUTH_VERSION_MISMATCH);
                return false;
            }

            byte[] expectedDigest = HashAlgorithm.SHA1.Hash(
                Encoding.ASCII.GetBytes(account),
                BitConverter.GetBytes(0u),
                localChallenge,
                _wotlkServerChallenge,
                frontendSession.ClientSessionKey);

            if (!expectedDigest.Compare(digest))
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleWotlkAuthSession: digest mismatch for account '{account}'.");
                SendWotlkAuthResponse(AuthResult.AUTH_FAILED);
                return false;
            }

            _wotlkHeaderCrypt = new Client.WotlkWorldCrypt();
            _wotlkHeaderCrypt.Initialize(frontendSession.ClientSessionKey);
            _wotlkHeaderCryptInitialized = true;

            _globalSession = frontendSession.GlobalSession;
            _realmId = _globalSession.RealmId;
            _globalSession.SessionKey = (byte[])frontendSession.ClientSessionKey.Clone();
            _globalSession.GameState = GameSessionData.CreateNewGameSessionData(_globalSession);
            _globalSession.RealmSocket = this;
            _globalSession.InstanceSocket = this;
            _globalSession.GameState.IsConnectedToInstance = true;

            if (_globalSession.WorldClient != null)
                _globalSession.WorldClient.Disconnect();

            _globalSession.WorldClient = new Client.WorldClient();
            Realm? backendRealm = _globalSession.Realm;
            if (backendRealm == null || !_globalSession.WorldClient.ConnectToWorldServer(backendRealm, _globalSession))
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleWotlkAuthSession: failed to connect backend world for '{account}' ({regionId}-{battlegroupId}-{realmId}).");
                SendWotlkAuthResponse(AuthResult.AUTH_FAILED);
                _globalSession.OnDisconnect();
                return false;
            }

            uint queuePosition = _globalSession.WorldClient.GetQueuePosition();
            if (queuePosition > 0)
                SendWotlkAuthResponse(AuthResult.AUTH_WAIT_QUEUE, queuePosition);
            else
                SendWotlkAuthResponse(AuthResult.AUTH_OK);

            Log.Print(LogType.Server, $"WotLK world auth complete for account '{account}' from {GetRemoteIpAddress()}.");
            return true;
        }

        private void SendWotlkAuthChallenge()
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(1); // DosZeroBits
            payload.WriteBytes(_wotlkServerChallenge);
            payload.WriteBytes(Array.Empty<byte>().GenerateRandomKey(32));

            uint opcode = ModernVersion.GetCurrentOpcode(Opcode.SMSG_AUTH_CHALLENGE);
            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkAuthResponse(AuthResult result, uint queuePosition = 0)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8((byte)result);
            payload.WriteUInt32(0); // BillingTimeRemaining
            payload.WriteUInt8(0); // BillingPlanFlags
            payload.WriteUInt32(0); // BillingTimeRested
            payload.WriteUInt8((byte)Math.Max(0, ModernVersion.ExpansionVersion - 1)); // 0: Vanilla, 1: TBC, 2: WotLK

            if (result == AuthResult.AUTH_WAIT_QUEUE)
            {
                payload.WriteUInt32(queuePosition);
                payload.WriteUInt8(0); // realm has free migration
            }

            uint opcode = ModernVersion.GetCurrentOpcode(Opcode.SMSG_AUTH_RESPONSE);
            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkPacket(ServerPacket packet)
        {
            if (TrySendWotlkSpecialPacket(packet))
                return;

            Opcode universalOpcode = packet.GetUniversalOpcode();

            uint opcode = packet.GetOpcode();
            if (opcode == 0 && universalOpcode == Opcode.SMSG_MOVE_TELEPORT)
            {
                // 3.3.5 uses MSG_MOVE_TELEPORT (no SMSG_ alias). Fall back so teleport packets are not dropped.
                opcode = ModernVersion.GetCurrentOpcode(Opcode.MSG_MOVE_TELEPORT);
            }
            if (opcode == 0)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"[WotLK] Skipping packet with missing opcode mapping: {packet.GetType().Name} ({packet.GetUniversalOpcode()}).");
                return;
            }

            byte[] payload = packet.GetData();
            if (universalOpcode == Opcode.SMSG_UPDATE_OBJECT)
            {
                Log.Print(LogType.Debug, $"[WotLK] Sending SMSG_UPDATE_OBJECT payload size {payload.Length}.");
                if (_wotlkUpdateObjectSampleCount < 6)
                {
                    _wotlkUpdateObjectSampleCount++;
                    uint objCount = payload.Length >= 4 ? BitConverter.ToUInt32(payload, 0) : 0;
                    ushort marker = payload.Length >= 6 ? BitConverter.ToUInt16(payload, 4) : (ushort)0;
                    string head = payload.Length == 0
                        ? string.Empty
                        : BitConverter.ToString(payload, 0, Math.Min(payload.Length, 24));
                    Log.Print(LogType.Debug, $"[WotLK] UPDATE_OBJECT head={head} count={objCount} marker16@4={marker}");
                }
            }
            SendWotlkRawPacket(opcode, payload);
        }

        private bool HandleWotlkPlayerLogin(WorldPacket packet, byte[] payload)
        {
            if (payload.Length < sizeof(ulong))
            {
                Log.Print(LogType.Error, "WorldSocket.HandleWotlkPlayerLogin: malformed CMSG_PLAYER_LOGIN payload.");
                return false;
            }

            WowGuid64 loginGuid64 = packet.ReadGuid();
            WowGuid128 loginGuid128 = loginGuid64.To128(GetSession().GameState);
            ulong loginGuidLow = loginGuid64.GetLowValue();

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_wotlkLastLoginGuidLow == loginGuidLow && (nowMs - _wotlkLastLoginUnixMs) < 1500)
            {
                Log.Print(LogType.Debug, $"[WotLK] Suppressed duplicate CMSG_PLAYER_LOGIN for guid {loginGuidLow}.");
                return true;
            }
            _wotlkLastLoginGuidLow = loginGuidLow;
            _wotlkLastLoginUnixMs = nowMs;

            if (!GetSession().GameState.CachedPlayers.TryGetValue(loginGuid128, out var selectedChar))
            {
                Log.Print(LogType.Error, $"Player tried to log in with unknown char id: {loginGuid128}");
                return false;
            }

            var realm = GetSession().RealmManager.GetRealm(GetSession().RealmId);
            if (realm == null)
            {
                Log.Print(LogType.Error, $"Player tried to log in to unknown realm id: {GetSession().RealmId}");
                return false;
            }

            GetSession().AccountMetaDataMgr.SaveLastSelectedCharacter(realm.Name, selectedChar.Name, loginGuid128.Low, Time.UnixTime);

            if (GetSession().AuthClient != null)
                GetSession().AuthClient.Disconnect();

            GetSession().RealmSocket = this;
            GetSession().InstanceSocket = this;

            GetSession().GameState.IsConnectedToInstance = true;
            GetSession().GameState.IsFirstEnterWorld = true;
            GetSession().GameState.CurrentPlayerGuid = loginGuid128;
            GetSession().GameState.HasCurrentPlayerPosition = false;
            _wotlkSentMovementBootstrap = false;
            _wotlkMovementBootstrapCounter = 0;
            _wotlkSyntheticMovementSpeedAckCounters.Clear();
            _wotlkLogoutInProgress = false;
            Packets.UpdateObject.ResetLoginBuffer(GetSession().GameState);
            GetSession().GameState.CurrentPlayerInfo = GetSession().GameState.OwnCharacters.Single(x => x.CharacterGuid == loginGuid128);
            GetSession().GameState.CurrentPlayerStorage.LoadCurrentPlayer();
            return ForwardWotlkPayloadToLegacy(Opcode.CMSG_PLAYER_LOGIN, payload);
        }

        private bool HandleWotlkRequestPlayedTime(bool triggerScriptEvent)
        {
            if (_globalSession?.WorldClient == null || !_globalSession.WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, "WorldSocket.HandleWotlkRequestPlayedTime: world client is disconnected.");
                return false;
            }

            WorldPacket backendPacket = new(Opcode.CMSG_REQUEST_PLAYED_TIME);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                backendPacket.WriteBool(triggerScriptEvent);

            _globalSession.WorldClient.SendPacketToServer(backendPacket);
            GetSession().GameState.ShowPlayedTime = triggerScriptEvent;
            return true;
        }

        private bool ForwardWotlkItemQuerySingle(byte[] payload)
        {
            if (_globalSession?.WorldClient == null || !_globalSession.WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, "WorldSocket.ForwardWotlkItemQuerySingle: world client is disconnected.");
                return false;
            }

            if (payload.Length < sizeof(uint))
            {
                Log.Print(LogType.Error, $"WorldSocket.ForwardWotlkItemQuerySingle: malformed payload length {payload.Length}.");
                return false;
            }

            uint itemId = BitConverter.ToUInt32(payload, 0);
            int now = Environment.TickCount;
            if (_wotlkItemQueryThrottle.TryGetValue(itemId, out int lastTick) &&
                unchecked(now - lastTick) < WotlkItemQueryThrottleMs)
            {
                return true;
            }
            _wotlkItemQueryThrottle[itemId] = now;
            GetSession().GameState.PendingLegacyItemQueries.Enqueue(itemId);

            WorldPacket backendPacket = new(Opcode.CMSG_ITEM_QUERY_SINGLE);
            backendPacket.WriteUInt32(itemId);

            // Vanilla expects an additional item guid (unused for our flow).
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                backendPacket.WriteGuid(WowGuid64.Empty);

            _globalSession.WorldClient.SendPacketToServer(backendPacket);
            return true;
        }

        private bool HandleWotlkMessageChat(byte[] payload)
        {
            if (_globalSession?.WorldClient == null || !_globalSession.WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, "WorldSocket.HandleWotlkMessageChat: world client is disconnected.");
                return false;
            }

            if (payload.Length < 8)
            {
                Log.Print(LogType.Error, $"WorldSocket.HandleWotlkMessageChat: malformed payload length {payload.Length}.");
                return false;
            }

            uint modernOpcode = ModernVersion.GetCurrentOpcode(Opcode.CMSG_MESSAGECHAT);
            if (modernOpcode == 0)
            {
                Log.Print(LogType.Error, "WorldSocket.HandleWotlkMessageChat: missing modern opcode mapping for CMSG_MESSAGECHAT.");
                return false;
            }

            try
            {
                WorldPacket reader = new(modernOpcode, payload);
                uint wotlkChatType = reader.ReadUInt32();
                uint language = reader.ReadUInt32();

                if (!TryMapWotlkChatTypeToLegacy(wotlkChatType, out uint legacyChatType))
                {
                    Log.Print(LogType.Warn, $"WorldSocket.HandleWotlkMessageChat: unsupported WotLK chat type {wotlkChatType}, forwarding raw payload.");
                    return ForwardWotlkPayloadToLegacy(Opcode.CMSG_MESSAGECHAT, payload);
                }

                string targetOrChannel = string.Empty;
                if (legacyChatType == (uint)ChatMessageTypeVanilla.Whisper ||
                    legacyChatType == (uint)ChatMessageTypeVanilla.Channel)
                {
                    targetOrChannel = reader.ReadCString();
                }

                string message = reader.ReadCString();
                uint normalizedLanguage = NormalizeLegacyLanguageForWotlkMessage(legacyChatType, language);

                WorldPacket backendPacket = new(Opcode.CMSG_MESSAGECHAT);
                backendPacket.WriteUInt32(legacyChatType);
                backendPacket.WriteUInt32(normalizedLanguage);
                if (legacyChatType == (uint)ChatMessageTypeVanilla.Whisper ||
                    legacyChatType == (uint)ChatMessageTypeVanilla.Channel)
                {
                    backendPacket.WriteCString(targetOrChannel);
                }
                backendPacket.WriteCString(message);

                Log.Print(LogType.Debug, $"[WotLK] Chat translate type {wotlkChatType}->{legacyChatType}, language {language}->{normalizedLanguage}.");
                _globalSession.WorldClient.SendPacketToServer(backendPacket);
                return true;
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Warn, $"WorldSocket.HandleWotlkMessageChat: failed to parse/translate CMSG_MESSAGECHAT ({ex.Message}), forwarding raw payload.");
                return ForwardWotlkPayloadToLegacy(Opcode.CMSG_MESSAGECHAT, payload);
            }
        }

        private static bool TryMapWotlkChatTypeToLegacy(uint wotlkChatType, out uint legacyChatType)
        {
            switch ((ChatMessageTypeWotLK)wotlkChatType)
            {
                case ChatMessageTypeWotLK.System:
                    legacyChatType = (uint)ChatMessageTypeVanilla.System;
                    return true;
                case ChatMessageTypeWotLK.Say:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Say;
                    return true;
                case ChatMessageTypeWotLK.Party:
                case ChatMessageTypeWotLK.PartyLeader:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Party;
                    return true;
                case ChatMessageTypeWotLK.Raid:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Raid;
                    return true;
                case ChatMessageTypeWotLK.RaidLeader:
                    legacyChatType = (uint)ChatMessageTypeVanilla.RaidLeader;
                    return true;
                case ChatMessageTypeWotLK.RaidWarning:
                    legacyChatType = (uint)ChatMessageTypeVanilla.RaidWarning;
                    return true;
                case ChatMessageTypeWotLK.Guild:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Guild;
                    return true;
                case ChatMessageTypeWotLK.Officer:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Officer;
                    return true;
                case ChatMessageTypeWotLK.Yell:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Yell;
                    return true;
                case ChatMessageTypeWotLK.Whisper:
                case ChatMessageTypeWotLK.WhisperForeign:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Whisper;
                    return true;
                case ChatMessageTypeWotLK.WhisperInform:
                    legacyChatType = (uint)ChatMessageTypeVanilla.WhisperInform;
                    return true;
                case ChatMessageTypeWotLK.Emote:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Emote;
                    return true;
                case ChatMessageTypeWotLK.TextEmote:
                    legacyChatType = (uint)ChatMessageTypeVanilla.TextEmote;
                    return true;
                case ChatMessageTypeWotLK.Channel:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Channel;
                    return true;
                case ChatMessageTypeWotLK.Afk:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Afk;
                    return true;
                case ChatMessageTypeWotLK.Dnd:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Dnd;
                    return true;
                case ChatMessageTypeWotLK.BattlegroundNeutral:
                    legacyChatType = (uint)ChatMessageTypeVanilla.BattlegroundNeutral;
                    return true;
                case ChatMessageTypeWotLK.BattlegroundAlliance:
                    legacyChatType = (uint)ChatMessageTypeVanilla.BattlegroundAlliance;
                    return true;
                case ChatMessageTypeWotLK.BattlegroundHorde:
                    legacyChatType = (uint)ChatMessageTypeVanilla.BattlegroundHorde;
                    return true;
                case ChatMessageTypeWotLK.Battleground:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Battleground;
                    return true;
                case ChatMessageTypeWotLK.BattlegroundLeader:
                    legacyChatType = (uint)ChatMessageTypeVanilla.BattlegroundLeader;
                    return true;
                case ChatMessageTypeWotLK.Addon:
                    legacyChatType = (uint)ChatMessageTypeVanilla.Addon;
                    return true;
                default:
                    legacyChatType = 0;
                    return false;
            }
        }

        private uint NormalizeLegacyLanguageForWotlkMessage(uint chatType, uint requestedLanguage)
        {
            if (requestedLanguage == (uint)Language.Addon ||
                requestedLanguage == (uint)Language.AddonBfA ||
                requestedLanguage == (uint)Language.AddonLogged)
                return requestedLanguage;

            // Fresh 3.3.5 characters can send the currently selected Wrath UI
            // language before the 1.12 core has initialized/accepted that skill
            // for the character.  For normal speech, force the vanilla racial
            // language so MaNGOS does not reply "you don't know that language".
            if (chatType == (uint)ChatMessageTypeVanilla.Say ||
                chatType == (uint)ChatMessageTypeVanilla.Yell ||
                chatType == (uint)ChatMessageTypeVanilla.Party ||
                chatType == (uint)ChatMessageTypeVanilla.Raid ||
                chatType == (uint)ChatMessageTypeVanilla.RaidLeader ||
                chatType == (uint)ChatMessageTypeVanilla.RaidWarning ||
                chatType == (uint)ChatMessageTypeVanilla.Guild ||
                chatType == (uint)ChatMessageTypeVanilla.Officer ||
                chatType == (uint)ChatMessageTypeVanilla.Channel ||
                chatType == (uint)ChatMessageTypeVanilla.Battleground ||
                chatType == (uint)ChatMessageTypeVanilla.BattlegroundLeader ||
                chatType == (uint)ChatMessageTypeVanilla.BattlegroundAlliance ||
                chatType == (uint)ChatMessageTypeVanilla.BattlegroundHorde ||
                chatType == (uint)ChatMessageTypeVanilla.BattlegroundNeutral)
                return GetDefaultLegacyLanguageForPlayer();

            if (IsSupportedLegacyLanguage(requestedLanguage))
                return requestedLanguage;

            if (chatType == (uint)ChatMessageTypeVanilla.Emote ||
                chatType == (uint)ChatMessageTypeVanilla.TextEmote)
                return requestedLanguage;

            return GetDefaultLegacyLanguageForPlayer();
        }

        private static bool IsSupportedLegacyLanguage(uint language)
        {
            switch ((Language)language)
            {
                case Language.Universal:
                case Language.Orcish:
                case Language.Darnassian:
                case Language.Taurahe:
                case Language.Dwarvish:
                case Language.Common:
                case Language.Demonic:
                case Language.Titan:
                case Language.Thalassian:
                case Language.Draconic:
                case Language.Kalimag:
                case Language.Gnomish:
                case Language.Troll:
                case Language.Gutterspeak:
                    return true;
                default:
                    return false;
            }
        }

        private uint GetDefaultLegacyLanguageForPlayer()
        {
            Race race = GetSession().GameState.CurrentPlayerInfo != null
                ? GetSession().GameState.CurrentPlayerInfo.RaceId
                : Race.None;

            return GameData.IsHordeRace(race)
                ? (uint)Language.Orcish
                : (uint)Language.Common;
        }

        private bool ForwardWotlkPayloadToLegacy(Opcode opcode, byte[] payload, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
        {
            if (_globalSession?.WorldClient == null || !_globalSession.WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, $"WorldSocket.ForwardWotlkPayloadToLegacy: cannot forward {opcode}, world client is disconnected.");
                return false;
            }

            if (opcode is Opcode.CMSG_MOVE_TELEPORT_ACK or Opcode.MSG_MOVE_TELEPORT_ACK)
            {
                uint modernOpcode = ModernVersion.GetCurrentOpcode(Opcode.MSG_MOVE_TELEPORT_ACK);
                if (modernOpcode == 0)
                    modernOpcode = ModernVersion.GetCurrentOpcode(Opcode.CMSG_MOVE_TELEPORT_ACK);

                if (modernOpcode == 0)
                {
                    Log.Print(LogType.Error, "WorldSocket.ForwardWotlkPayloadToLegacy: missing modern opcode mapping for MSG_MOVE_TELEPORT_ACK.");
                    return false;
                }

                WorldPacket clientPacket = new(modernOpcode, payload);
                MoveTeleportAck teleportAck = new(clientPacket);
                teleportAck.Read();
                HandleMoveTeleportAck(teleportAck);
                return true;
            }

            uint legacyOpcode = LegacyVersion.GetCurrentOpcode(opcode);
            if (legacyOpcode == 0)
                return false;

            WorldPacket backendPacket = new(legacyOpcode);
            if (payload.Length > 0)
                backendPacket.WriteBytes(payload);

            _globalSession.WorldClient.SendPacketToServer(backendPacket, delayUntilOpcode);
            return true;
        }

        private bool TrySendWotlkSpecialPacket(ServerPacket packet)
        {
            if (packet is CorpseLocation typedCorpseLocation)
            {
                SendWotlkCorpseLocation(typedCorpseLocation);
                return true;
            }

            if (packet is ChatPkt chat)
            {
                SendWotlkChat(chat);
                return true;
            }

            if (packet is PartyUpdate partyUpdateByType)
            {
                SendWotlkGroupList(partyUpdateByType);
                return true;
            }

            if (packet is GroupNewLeader groupNewLeader)
            {
                SendWotlkGroupNewLeader(groupNewLeader);
                return true;
            }

            if (packet is LearnedSpells learnedSpells)
            {
                SendWotlkLearnedSpells(learnedSpells);
                return true;
            }

            if (packet is TradeStatusPkt tradeStatus)
            {
                SendWotlkTradeStatus(tradeStatus);
                return true;
            }

            if (packet is TradeUpdated tradeUpdated)
            {
                SendWotlkTradeStatusExtended(tradeUpdated);
                return true;
            }

            if (packet is SAttackStart attackStart)
            {
                SendWotlkAttackStart(attackStart);
                return true;
            }

            if (packet is SAttackStop attackStop)
            {
                SendWotlkAttackStop(attackStop);
                return true;
            }

            if (packet is AttackerStateUpdate attackerState)
            {
                SendWotlkAttackerStateUpdate(attackerState);
                return true;
            }

            if (packet is MoveSetFlag moveSetFlag)
            {
                SendWotlkMoveSetFlag(moveSetFlag);
                return true;
            }

            if (packet is MoveSplineSetFlag moveSplineSetFlag)
            {
                SendWotlkMoveSplineSetFlag(moveSplineSetFlag);
                return true;
            }

            if (packet is MailListResult mailList)
            {
                SendWotlkMailListResult(mailList);
                return true;
            }

            if (packet is MailCommandResult mailCommandResult)
            {
                SendWotlkMailCommandResult(mailCommandResult);
                return true;
            }

            if (packet is MailQueryNextTimeResult mailNextTimeByType)
            {
                SendWotlkMailQueryNextTimeResult(mailNextTimeByType);
                return true;
            }

            if (packet is AuctionWonNotification auctionWon)
            {
                SendWotlkAuctionBidderNotification(auctionWon.Info, bidAmount: 0, minIncrement: 0);
                return true;
            }

            if (packet is AuctionOutbidNotification auctionOutbid)
            {
                SendWotlkAuctionBidderNotification(auctionOutbid.Info, auctionOutbid.BidAmount, auctionOutbid.MinIncrement);
                return true;
            }

            if (packet is AuctionClosedNotification auctionClosed)
            {
                SendWotlkAuctionOwnerNotification(auctionClosed.Info, minIncrement: 0, WowGuid128.Empty, auctionClosed.ProceedsMailDelay);
                return true;
            }

            if (packet is AuctionOwnerBidNotification auctionOwnerBid)
            {
                SendWotlkAuctionOwnerNotification(auctionOwnerBid.Info, auctionOwnerBid.MinIncrement, auctionOwnerBid.Bidder, mailDelay: 0);
                return true;
            }

            if (packet is SpellStart spellStart)
            {
                SendWotlkSpellStart(spellStart);
                return true;
            }

            if (packet is SpellGo spellGo)
            {
                SendWotlkSpellGo(spellGo);
                return true;
            }

            if (packet is SpellPrepare)
            {
                // 3.3.5 has no SMSG_SPELL_PREPARE equivalent. Safe to skip; cast flow uses SPELL_START/GO.
                return true;
            }

            if (packet is SpellChannelStart spellChannelStart)
            {
                SendWotlkSpellChannelStart(spellChannelStart);
                return true;
            }

            if (packet is SpellChannelUpdate spellChannelUpdate)
            {
                SendWotlkSpellChannelUpdate(spellChannelUpdate);
                return true;
            }

            if (packet is AuraUpdate auraUpdate)
            {
                SendWotlkAuraUpdate(auraUpdate);
                return true;
            }

            if (packet is PowerUpdate powerUpdate)
            {
                SendWotlkPowerUpdate(powerUpdate);
                return true;
            }

            if (packet is StandStateUpdate standStateUpdate)
            {
                SendWotlkStandStateUpdate(standStateUpdate);
                return true;
            }

            if (packet is SellResponse sellResponse)
            {
                SendWotlkSellResponse(sellResponse);
                return true;
            }

            if (packet is ItemPushResult itemPushResult)
            {
                SendWotlkItemPushResult(itemPushResult);
                return true;
            }

            if (packet is SetFactionStanding setFactionStanding)
            {
                SendWotlkSetFactionStanding(setFactionStanding);
                return true;
            }

            if (packet is LogXPGain logXPGain)
            {
                SendWotlkLogXPGain(logXPGain);
                return true;
            }

            if (packet is LootResponse lootResponse)
            {
                SendWotlkLootResponse(lootResponse);
                return true;
            }

            if (packet is LootRemoved lootRemoved)
            {
                SendWotlkLootRemoved(lootRemoved);
                return true;
            }

            if (packet is LootMoneyNotify lootMoneyNotify)
            {
                SendWotlkLootMoneyNotify(lootMoneyNotify);
                return true;
            }

            if (packet is CoinRemoved coinRemoved)
            {
                SendWotlkLootClearMoney(coinRemoved);
                return true;
            }

            if (packet is LootReleaseResponse lootReleaseResponse)
            {
                SendWotlkLootReleaseResponse(lootReleaseResponse);
                return true;
            }

            if (packet is DuelRequested duelRequested)
            {
                SendWotlkDuelRequested(duelRequested);
                return true;
            }
            if (packet is ChannelNotifyJoined channelJoinedByType)
            {
                SendWotlkChannelNotifyJoined(channelJoinedByType);
                return true;
            }
            if (packet is ChannelNotifyLeft channelLeftByType)
            {
                SendWotlkChannelNotifyLeft(channelLeftByType);
                return true;
            }

            if (packet is GossipMessagePkt gossipMessage)
            {
                SendWotlkGossipMessage(gossipMessage);
                return true;
            }

            if (packet is BinderConfirm binderConfirm)
            {
                SendWotlkSingleGuidPacket(Opcode.SMSG_BINDER_CONFIRM, binderConfirm.Guid);
                return true;
            }

            if (packet is ShowBank showBank)
            {
                SendWotlkSingleGuidPacket(Opcode.SMSG_SHOW_BANK, showBank.Guid);
                return true;
            }

            if (packet is TrainerList trainerList)
            {
                SendWotlkTrainerList(trainerList);
                return true;
            }

            if (packet is SpiritHealerConfirm spiritHealerConfirm)
            {
                SendWotlkSingleGuidPacket(Opcode.SMSG_SPIRIT_HEALER_CONFIRM, spiritHealerConfirm.Guid);
                return true;
            }

            if (packet is ResurrectRequest resurrectRequest)
            {
                SendWotlkResurrectRequest(resurrectRequest);
                return true;
            }

            if (packet is VendorInventory vendorInventory)
            {
                SendWotlkVendorInventory(vendorInventory);
                return true;
            }

            if (packet is QuestGiverStatusPkt questGiverStatus)
            {
                SendWotlkQuestGiverStatus(questGiverStatus);
                return true;
            }

            if (packet is QuestGiverStatusMultiple questGiverStatusMultiple)
            {
                SendWotlkQuestGiverStatusMultiple(questGiverStatusMultiple);
                return true;
            }

            if (packet is QuestGiverQuestListMessage questListMessage)
            {
                SendWotlkQuestGiverQuestListMessage(questListMessage);
                return true;
            }

            if (packet is QuestGiverQuestDetails questDetails)
            {
                SendWotlkQuestGiverQuestDetails(questDetails);
                return true;
            }

            if (packet is QuestGiverRequestItems requestItems)
            {
                SendWotlkQuestGiverRequestItems(requestItems);
                return true;
            }

            if (packet is QuestGiverOfferRewardMessage offerReward)
            {
                SendWotlkQuestGiverOfferRewardMessage(offerReward);
                return true;
            }

            if (packet is QuestGiverQuestComplete questComplete)
            {
                SendWotlkQuestGiverQuestComplete(questComplete);
                return true;
            }

            if (packet is QuestGiverQuestFailed questFailed)
            {
                SendWotlkQuestGiverQuestFailed(questFailed);
                return true;
            }

            if (packet is QuestGiverInvalidQuest invalidQuest)
            {
                SendWotlkQuestGiverInvalidQuest(invalidQuest);
                return true;
            }

            switch (packet.GetUniversalOpcode())
            {
                case Opcode.SMSG_TRANSFER_PENDING:
                {
                    if (packet is TransferPending transferPending)
                    {
                        SendWotlkTransferPending(transferPending);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_NEW_WORLD:
                {
                    if (packet is NewWorld newWorld)
                    {
                        SendWotlkNewWorld(newWorld);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_ENUM_CHARACTERS_RESULT:
                {
                    if (packet is EnumCharactersResult charEnum)
                    {
                        SendWotlkEnumCharactersResult(charEnum);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_CREATE_CHAR:
                {
                    if (packet is CreateChar createChar)
                    {
                        SendWotlkCreateChar(createChar);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_DELETE_CHAR:
                {
                    if (packet is DeleteChar deleteChar)
                    {
                        SendWotlkDeleteChar(deleteChar);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_QUERY_PLAYER_NAME_RESPONSE:
                {
                    if (packet is QueryPlayerNameResponse nameResponse)
                    {
                        SendWotlkQueryPlayerNameResponse(nameResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_QUERY_TIME_RESPONSE:
                {
                    if (packet is QueryTimeResponse timeResponse)
                    {
                        SendWotlkQueryTimeResponse(timeResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_QUERY_CREATURE_RESPONSE:
                {
                    if (packet is QueryCreatureResponse creatureResponse)
                    {
                        SendWotlkQueryCreatureResponse(creatureResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_QUERY_GAME_OBJECT_RESPONSE:
                {
                    if (packet is QueryGameObjectResponse gameObjectResponse)
                    {
                        SendWotlkQueryGameObjectResponse(gameObjectResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_QUERY_NPC_TEXT_RESPONSE:
                {
                    if (packet is QueryNPCTextResponse npcTextResponse)
                    {
                        SendWotlkQueryNpcTextResponse(npcTextResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_QUERY_PET_NAME_RESPONSE:
                {
                    if (packet is QueryPetNameResponse petNameResponse)
                    {
                        SendWotlkQueryPetNameResponse(petNameResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_LOGIN_VERIFY_WORLD:
                {
                    if (packet is LoginVerifyWorld loginVerify)
                    {
                        SendWotlkLoginVerifyWorld(loginVerify);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_LOGOUT_RESPONSE:
                {
                    if (packet is LogoutResponse logoutResponse)
                    {
                        SendWotlkLogoutResponse(logoutResponse);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_PLAYED_TIME:
                {
                    if (packet is PlayedTime playedTime)
                    {
                        SendWotlkPlayedTime(playedTime);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_ACCOUNT_DATA_TIMES:
                {
                    if (packet is AccountDataTimes accountDataTimes)
                    {
                        SendWotlkAccountDataTimes(accountDataTimes);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_CORPSE_LOCATION:
                {
                    if (packet is CorpseLocation corpseLocation)
                    {
                        SendWotlkCorpseLocation(corpseLocation);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_FEATURE_SYSTEM_STATUS:
                {
                    if (packet is FeatureSystemStatus featureStatus)
                    {
                        SendWotlkFeatureSystemStatus(featureStatus);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_MOTD:
                {
                    if (packet is MOTD motd)
                    {
                        SendWotlkMotd(motd);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_CONTACT_LIST:
                {
                    if (packet is ContactList contacts)
                    {
                        SendWotlkContactList(contacts);
                        return true;
                    }
                    break;
                }
                case Opcode.SMSG_PARTY_INVITE:
                {
                    if (packet is PartyInvite partyInvite)
                    {
                        SendWotlkPartyInvite(partyInvite);
                        return true;
                    }
                    break;
                }
            }

            return false;
        }

        private bool HandleWotlkUseItem(byte[] payload)
        {
            if (GetSession().WorldClient == null || !GetSession().WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, "WorldSocket.HandleWotlkUseItem: world client is disconnected.");
                return false;
            }

            // uint8 bag, uint8 slot, uint8 castCount, uint32 spellId,
            // uint64 itemGuid, uint32 glyphIndex, uint8 castFlags, SpellCastTargets.
            // uint8 bag, uint8 slot, uint8 spell_index, SpellCastTargets.
            const int MinWotlkUseItemSize = 1 + 1 + 1 + 4 + 8 + 4 + 1;
            if (payload == null || payload.Length < MinWotlkUseItemSize)
            {
                Log.Print(LogType.Warn, $"[WotLK] Dropping malformed CMSG_USE_ITEM payload: size={payload?.Length ?? 0}.");
                return true;
            }

            try
            {
                uint modernOpcode = ModernVersion.GetCurrentOpcode(Opcode.CMSG_USE_ITEM);
                if (modernOpcode == 0)
                    modernOpcode = (uint)HermesProxy.World.Enums.V3_3_5_12340.Opcode.CMSG_USE_ITEM;

                WorldPacket data = new(modernOpcode, payload);

                byte bagIndex = data.ReadUInt8();
                byte slot = data.ReadUInt8();
                byte castCount = data.ReadUInt8();
                uint spellId = data.ReadUInt32();
                WowGuid128 itemGuid = MovementInfo.LegacyPackedGuidTo128(data.ReadGuid());
                uint glyphIndex = data.ReadUInt32();
                byte castFlags = data.ReadUInt8();

                SpellTargetData targets = new();
                if (data.GetCurrentStream().Position < data.GetSize())
                    targets.ReadLegacyWotlk(data);
                else
                    targets.Flags = SpellCastTargetFlags.None;

                byte spellIndex = spellId != 0
                    ? GetSession().GameState.GetItemSpellSlot(itemGuid, spellId)
                    : (byte)0;

                RegisterWotlkItemCast(spellId, itemGuid);

                WorldPacket legacy = new(Opcode.CMSG_USE_ITEM);
                legacy.WriteUInt8(bagIndex);
                legacy.WriteUInt8(slot);
                legacy.WriteUInt8(spellIndex);

                SpellCastTargetFlags targetFlags = NormalizeLegacySpellTargetFlags(targets);
                WriteSpellTargets(targets, targetFlags, legacy);

                Log.Print(LogType.Debug,
                    $"[WotLK] Translated CMSG_USE_ITEM for vanilla: bag={bagIndex}, slot={slot}, castCount={castCount}, spellId={spellId}, spellIndex={spellIndex}, glyphIndex={glyphIndex}, castFlags=0x{castFlags:X2}, targetFlags=0x{(uint)targetFlags:X}.");

                SendPacketToServer(legacy);
                return true;
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Warn, $"[WotLK] Dropping malformed CMSG_USE_ITEM after failed translation: {ex.Message}");
                return true;
            }
        }

        private void RegisterWotlkItemCast(uint spellId, WowGuid128 itemGuid)
        {
            if (spellId == 0)
                return;

            ClientCastRequest castRequest = new();
            castRequest.Timestamp = Environment.TickCount;
            castRequest.SpellId = spellId;
            castRequest.SpellXSpellVisualId = GameData.GetSpellVisual(spellId);
            castRequest.ItemGUID = itemGuid;

            ulong castCounter = (ulong)(castRequest.Timestamp & 0x7FFFFFFF);
            castRequest.ClientGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, spellId, castCounter);
            castRequest.ServerGUID = WowGuid128.Create(HighGuidType703.Cast, SpellCastSource.Normal, (uint)GetSession().GameState.CurrentMapId, spellId, 10000 + castCounter);

            ClientCastRequest current = GetSession().GameState.CurrentClientNormalCast;
            if (current == null || current.HasStarted || current.Timestamp + 10000 < castRequest.Timestamp)
            {
                GetSession().GameState.CurrentClientNormalCast = castRequest;
                return;
            }

            GetSession().GameState.PendingClientCasts.Add(castRequest);
        }

        private bool TryHandleWotlkGameplayOpcode(WorldPacket packet, Opcode opcode, byte[] payload)
        {
            try
            {
                HandlePacket(packet);
                return true;
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Warn, $"[WotLK] Gameplay parser fallback for {opcode}: {ex.Message}");

                if (WotlkMovementPacketCompat.IsWotlkFrontendBuild() && opcode == Opcode.CMSG_USE_ITEM)
                    return true;

                return ForwardWotlkPayloadToLegacy(opcode, payload);
            }
        }

        private bool TryHandleWotlkInventoryOpcode(WorldPacket packet, Opcode opcode, byte[] payload)
        {
            try
            {
                HandlePacket(packet);
                return true;
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Warn, $"[WotLK] Inventory parser fallback for {opcode}: {ex.Message}");
                return ForwardWotlkInventoryOpcodeRaw(opcode, payload);
            }
        }

        private bool ForwardWotlkInventoryOpcodeRaw(Opcode opcode, byte[] payload)
        {
            if (_globalSession?.WorldClient == null || !_globalSession.WorldClient.IsConnected())
            {
                Log.Print(LogType.Error, $"[WotLK] Cannot forward {opcode}: world client is disconnected.");
                return false;
            }

            switch (opcode)
            {
                case Opcode.CMSG_SWAP_INV_ITEM:
                {
                    if (payload.Length < 2)
                    {
                        Log.Print(LogType.Error, $"[WotLK] Malformed CMSG_SWAP_INV_ITEM payload length {payload.Length}.");
                        return false;
                    }

                    // 3.3.5 reads this packet as DestinationSlot, SourceSlot.
                    // MaNGOS 1.12 reads SourceSlot, DestinationSlot.
                    byte destinationSlot = ModernVersion.AdjustInventorySlot(payload[0]);
                    byte sourceSlot = ModernVersion.AdjustInventorySlot(payload[1]);

                    WorldPacket backend = new WorldPacket(Opcode.CMSG_SWAP_INV_ITEM);
                    backend.WriteUInt8(sourceSlot);
                    backend.WriteUInt8(destinationSlot);
                    _globalSession.WorldClient.SendPacketToServer(backend);
                    return true;
                }
                case Opcode.CMSG_SWAP_ITEM:
                {
                    if (payload.Length < 4)
                    {
                        Log.Print(LogType.Error, $"[WotLK] Malformed CMSG_SWAP_ITEM payload length {payload.Length}.");
                        return false;
                    }

                    int start = payload.Length - 4;
                    byte containerB = payload[start + 0];
                    byte slotB = payload[start + 1];
                    byte containerA = payload[start + 2];
                    byte slotA = payload[start + 3];

                    containerB = containerB != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(containerB) : containerB;
                    slotB = containerB == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(slotB) : slotB;
                    containerA = containerA != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(containerA) : containerA;
                    slotA = containerA == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(slotA) : slotA;

                    WorldPacket backend = new WorldPacket(Opcode.CMSG_SWAP_ITEM);
                    backend.WriteUInt8(containerB);
                    backend.WriteUInt8(slotB);
                    backend.WriteUInt8(containerA);
                    backend.WriteUInt8(slotA);
                    _globalSession.WorldClient.SendPacketToServer(backend);
                    return true;
                }
                case Opcode.CMSG_AUTO_EQUIP_ITEM:
                {
                    if (payload.Length < 2)
                    {
                        Log.Print(LogType.Error, $"[WotLK] Malformed CMSG_AUTO_EQUIP_ITEM payload length {payload.Length}.");
                        return false;
                    }

                    byte packSlot = payload[0];
                    byte slot = payload[1];

                    byte container = packSlot != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(packSlot) : packSlot;
                    byte adjustedSlot = packSlot == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(slot) : slot;

                    WorldPacket backend = new WorldPacket(Opcode.CMSG_AUTO_EQUIP_ITEM);
                    backend.WriteUInt8(container);
                    backend.WriteUInt8(adjustedSlot);
                    _globalSession.WorldClient.SendPacketToServer(backend);
                    return true;
                }
                case Opcode.CMSG_AUTO_STORE_BAG_ITEM:
                {
                    if (payload.Length < 3)
                    {
                        Log.Print(LogType.Error, $"[WotLK] Malformed CMSG_AUTO_STORE_BAG_ITEM payload length {payload.Length}.");
                        return false;
                    }

                    byte sourceBag = payload[0];
                    byte sourceSlot = payload[1];
                    byte destinationBag = payload[2];

                    byte adjustedSourceBag = sourceBag != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(sourceBag) : sourceBag;
                    byte adjustedSourceSlot = sourceBag == Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(sourceSlot) : sourceSlot;
                    byte adjustedDestinationBag = destinationBag != Enums.Classic.InventorySlots.Bag0 ? ModernVersion.AdjustInventorySlot(destinationBag) : destinationBag;

                    WorldPacket backend = new WorldPacket(Opcode.CMSG_AUTO_STORE_BAG_ITEM);
                    backend.WriteUInt8(adjustedSourceBag);
                    backend.WriteUInt8(adjustedSourceSlot);
                    backend.WriteUInt8(adjustedDestinationBag);
                    _globalSession.WorldClient.SendPacketToServer(backend);
                    return true;
                }
                case Opcode.CMSG_AUTO_EQUIP_ITEM_SLOT:
                {
                    // guid(8) + dst slot(1) in legacy/WotLK payload.
                    if (payload.Length < 9)
                    {
                        Log.Print(LogType.Error, $"[WotLK] Malformed CMSG_AUTO_EQUIP_ITEM_SLOT payload length {payload.Length}.");
                        return false;
                    }

                    ulong itemGuidLow = BitConverter.ToUInt64(payload, 0);
                    byte dstSlot = ModernVersion.AdjustInventorySlot(payload[8]);

                    WorldPacket backend = new WorldPacket(Opcode.CMSG_AUTO_EQUIP_ITEM_SLOT);
                    backend.WriteUInt64(itemGuidLow);
                    backend.WriteUInt8(dstSlot);
                    _globalSession.WorldClient.SendPacketToServer(backend);
                    return true;
                }
            }

            return false;
        }

        private void SendWotlkCreateChar(CreateChar createChar)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8(createChar.Code);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CREATE_CHAR), payload.GetData());
        }

        private void SendWotlkDuelRequested(DuelRequested duelRequested)
        {
            // Wrath expects two plain uint64 GUIDs: duel arbiter and requester.
            // Sending the modern packed-account payload suppresses the duel popup.
            ByteBuffer payload = new();
            payload.WriteUInt64(duelRequested.ArbiterGUID.To64().GetLowValue());
            payload.WriteUInt64(duelRequested.RequestedByGUID.To64().GetLowValue());
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_DUEL_REQUESTED), payload.GetData());
        }

        private void SendWotlkDeleteChar(DeleteChar deleteChar)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8(deleteChar.Code);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_DELETE_CHAR), payload.GetData());
        }

        private void SendWotlkLoginVerifyWorld(LoginVerifyWorld loginVerify)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(loginVerify.MapID);
            payload.WriteFloat(loginVerify.Pos.X);
            payload.WriteFloat(loginVerify.Pos.Y);
            payload.WriteFloat(loginVerify.Pos.Z);
            payload.WriteFloat(loginVerify.Pos.Orientation);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOGIN_VERIFY_WORLD), payload.GetData());

            SendWotlkMovementBootstrap();
        }

        internal void SendWotlkMovementBootstrap(bool force = false)
        {
            if (!_isWotlkFrontend || (_wotlkSentMovementBootstrap && !force))
                return;

            WowGuid128 playerGuid = GetSession()?.GameState?.CurrentPlayerGuid ?? WowGuid128.Empty;
            ulong lowGuid = playerGuid.To64().GetLowValue();
            if (lowGuid == 0)
                return;

            _wotlkSentMovementBootstrap = true;
            Log.Print(LogType.Debug, $"[WotLK] Sending movement bootstrap for player {lowGuid:X}: control, unroot, land-walk.");

            SendWotlkControlUpdate(lowGuid, true);
            SendWotlkMoveFlagPacket(Opcode.SMSG_MOVE_UNROOT, lowGuid, _wotlkMovementBootstrapCounter++);
            SendWotlkMoveFlagPacket(Opcode.SMSG_MOVE_SET_LAND_WALK, lowGuid, _wotlkMovementBootstrapCounter++);
            SendWotlkMovementSpeedBootstrap(lowGuid);
            SendWotlkControlUpdate(lowGuid, true);
        }

        internal void SendWotlkTimeSyncRequest(string reason)
        {
            if (!_isWotlkFrontend)
                return;

            TimeSyncRequest sync = new()
            {
                SequenceIndex = _wotlkTimeSyncCounter++
            };

            Log.Print(LogType.Debug, $"[WotLK] Sending SMSG_TIME_SYNC_REQUEST counter={sync.SequenceIndex} ({reason}).");
            SendPacket(sync);
        }

        private uint GetWotlkOpcode(Opcode opcode)
        {
            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
            {
                switch (opcode)
                {
                    case Opcode.SMSG_CONTROL_UPDATE:
                        return 0x0159;
                }
            }

            return ModernVersion.GetCurrentOpcode(opcode);
        }

        private void SendWotlkControlUpdate(ulong guid, bool hasControl)
        {
            uint opcode = GetWotlkOpcode(Opcode.SMSG_CONTROL_UPDATE);
            if (opcode == 0)
            {
                Log.Print(LogType.Warn, "[WotLK] Cannot send SMSG_CLIENT_CONTROL_UPDATE: missing opcode mapping.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, guid);
            payload.WriteUInt8(hasControl ? (byte)1 : (byte)0);
            Log.Print(LogType.Debug, $"[WotLK] Sending SMSG_CLIENT_CONTROL_UPDATE opcode=0x{opcode:X4}, guid={guid:X}, allowMovement={hasControl}.");
            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkMoveFlagPacket(Opcode opcode, ulong guid, uint counter)
        {
            uint modernOpcode = ModernVersion.GetCurrentOpcode(opcode);
            if (modernOpcode == 0)
            {
                Log.Print(LogType.Warn, $"[WotLK] Cannot send movement bootstrap packet {opcode}: missing opcode mapping.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, guid);
            payload.WriteUInt32(counter);
            SendWotlkRawPacket(modernOpcode, payload.GetData());
        }

        private void SendWotlkMovementSpeedBootstrap(ulong guid)
        {
            Log.Print(LogType.Debug, $"[WotLK] Sending movement speed bootstrap for player {guid:X}.");

            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_WALK_SPEED_CHANGE, guid, MovementInfo.DEFAULT_WALK_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_RUN_SPEED_CHANGE, guid, MovementInfo.DEFAULT_RUN_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_RUN_BACK_SPEED_CHANGE, guid, MovementInfo.DEFAULT_RUN_BACK_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_SWIM_SPEED_CHANGE, guid, MovementInfo.DEFAULT_SWIM_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE, guid, MovementInfo.DEFAULT_SWIM_BACK_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_FLIGHT_SPEED_CHANGE, guid, MovementInfo.DEFAULT_FLY_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE, guid, MovementInfo.DEFAULT_FLY_BACK_SPEED);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_TURN_RATE_CHANGE, guid, MovementInfo.DEFAULT_TURN_RATE);
            SendWotlkForceSpeedPacket(Opcode.SMSG_FORCE_PITCH_RATE_CHANGE, guid, MovementInfo.DEFAULT_PITCH_RATE);
        }

        private void SendWotlkForceSpeedPacket(Opcode opcode, ulong guid, float speed)
        {
            uint modernOpcode = ModernVersion.GetCurrentOpcode(opcode);
            if (modernOpcode == 0)
            {
                Log.Print(LogType.Warn, $"[WotLK] Cannot send movement speed bootstrap packet {opcode}: missing opcode mapping.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, guid);
            uint counter = _wotlkMovementBootstrapCounter++;
            _wotlkSyntheticMovementSpeedAckCounters.Add(counter);
            payload.WriteUInt32(counter);
            if (opcode == Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)
                payload.WriteUInt8(0);
            payload.WriteFloat(speed);
            SendWotlkRawPacket(modernOpcode, payload.GetData());
        }

        private void SendWotlkTransferPending(TransferPending transferPending)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(transferPending.MapID);

            if (transferPending.Ship != null)
            {
                payload.WriteUInt32(transferPending.Ship.Id);
                payload.WriteUInt32((uint)transferPending.Ship.OriginMapID);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_TRANSFER_PENDING), payload.GetData());
        }

        private void SendWotlkNewWorld(NewWorld newWorld)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(newWorld.MapID);
            payload.WriteFloat(newWorld.Position.X);
            payload.WriteFloat(newWorld.Position.Y);
            payload.WriteFloat(newWorld.Position.Z);
            payload.WriteFloat(newWorld.Orientation);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_NEW_WORLD), payload.GetData());
        }

        private void SendWotlkLogoutResponse(LogoutResponse logoutResponse)
        {
            ByteBuffer payload = new();
            payload.WriteInt32(logoutResponse.LogoutResult);
            payload.WriteUInt8(logoutResponse.Instant ? (byte)1 : (byte)0);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOGOUT_RESPONSE), payload.GetData());
        }

        private void SendWotlkPlayedTime(PlayedTime playedTime)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(playedTime.TotalTime);
            payload.WriteUInt32(playedTime.LevelTime);
            payload.WriteUInt8(playedTime.TriggerEvent ? (byte)1 : (byte)0);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_PLAYED_TIME), payload.GetData());
        }

        private void HandleWotlkReadyForAccountDataTimes()
        {
            if (GetSession()?.AccountDataMgr == null)
            {
                Log.Print(LogType.Warn, "[WotLK] Ignoring CMSG_READY_FOR_ACCOUNT_DATA_TIMES before account-data manager is ready.");
                return;
            }

            if (!TryEnsureWotlkAccountDataLoaded(WowGuid128.Empty))
                return;

            SendWotlkAccountDataTimes(WotlkGlobalAccountDataMask);
        }

        private void HandleWotlkUpdateAccountData(byte[] payload)
        {
            if (payload.Length < 12)
            {
                Log.Print(LogType.Warn, $"[WotLK] Dropped malformed CMSG_UPDATE_ACCOUNT_DATA payload size {payload.Length}.");
                return;
            }

            WorldPacket packet = new(ModernVersion.GetCurrentOpcode(Opcode.CMSG_UPDATE_ACCOUNT_DATA), payload);
            uint dataType = packet.ReadUInt32();
            uint timestamp = packet.ReadUInt32();
            uint uncompressedSize = packet.ReadUInt32();
            byte[] compressedData = uncompressedSize == 0 ? Array.Empty<byte>() : packet.ReadToEnd();

            if (!IsValidWotlkAccountDataType(dataType))
            {
                Log.Print(LogType.Warn, $"[WotLK] Ignoring invalid account-data update type {dataType}.");
                return;
            }

            if (!TryGetWotlkAccountDataGuid(dataType, out WowGuid128 guid) ||
                !TryEnsureWotlkAccountDataLoaded(guid))
                return;

            long storedTimestamp = uncompressedSize == 0 ? 0 : timestamp;
            GetSession().AccountDataMgr.SaveData(guid, storedTimestamp, dataType, uncompressedSize, compressedData);
            SendWotlkUpdateAccountDataComplete(dataType, 0);
        }

        private void HandleWotlkRequestAccountData(byte[] payload)
        {
            if (payload.Length < sizeof(uint))
            {
                Log.Print(LogType.Warn, $"[WotLK] Dropped malformed CMSG_REQUEST_ACCOUNT_DATA payload size {payload.Length}.");
                return;
            }

            WorldPacket packet = new(ModernVersion.GetCurrentOpcode(Opcode.CMSG_REQUEST_ACCOUNT_DATA), payload);
            uint dataType = packet.ReadUInt32();

            if (!IsValidWotlkAccountDataType(dataType))
            {
                Log.Print(LogType.Warn, $"[WotLK] Ignoring invalid account-data request type {dataType}.");
                return;
            }

            if (!TryGetWotlkAccountDataGuid(dataType, out WowGuid128 guid) ||
                !TryEnsureWotlkAccountDataLoaded(guid))
                return;

            AccountData data = GetSession().AccountDataMgr.Data[dataType];
            if (data == null)
            {
                data = CreateEmptyWotlkAccountData(guid, dataType);
                GetSession().AccountDataMgr.Data[dataType] = data;
            }

            data.Guid = guid;
            data.Type = dataType;
            data.CompressedData ??= Array.Empty<byte>();
            SendWotlkAccountData(data);
        }

        private bool IsValidWotlkAccountDataType(uint dataType)
        {
            return dataType < ModernVersion.GetAccountDataCount();
        }

        private bool TryGetWotlkAccountDataGuid(uint dataType, out WowGuid128 guid)
        {
            guid = WowGuid128.Empty;
            GlobalSessionData session = GetSession();

            if (session?.AccountDataMgr == null)
            {
                Log.Print(LogType.Warn, "[WotLK] Account-data packet arrived before account-data manager was ready.");
                return false;
            }

            if (!AccountDataManager.IsGlobalDataType(dataType))
            {
                if (session.GameState == null ||
                    session.GameState.CurrentPlayerGuid == null ||
                    session.GameState.CurrentPlayerGuid.IsEmpty())
                {
                    Log.Print(LogType.Warn, $"[WotLK] Ignoring per-character account-data type {dataType} before player login.");
                    return false;
                }

                guid = session.GameState.CurrentPlayerGuid;
            }

            return true;
        }

        private bool TryEnsureWotlkAccountDataLoaded(WowGuid128 guid)
        {
            GlobalSessionData session = GetSession();
            if (session?.AccountDataMgr == null)
                return false;

            int count = ModernVersion.GetAccountDataCount();
            bool loadedForGuid = _wotlkAccountDataLoaded &&
                                 _wotlkAccountDataLoadedLow == guid.GetLowValue() &&
                                 _wotlkAccountDataLoadedHigh == guid.GetHighValue();

            if (session.AccountDataMgr.Data == null ||
                session.AccountDataMgr.Data.Length != count ||
                !loadedForGuid)
            {
                session.AccountDataMgr.LoadAllData(guid);
                _wotlkAccountDataLoaded = true;
                _wotlkAccountDataLoadedLow = guid.GetLowValue();
                _wotlkAccountDataLoadedHigh = guid.GetHighValue();
            }

            return true;
        }

        private static AccountData CreateEmptyWotlkAccountData(WowGuid128 guid, uint dataType)
        {
            return new AccountData
            {
                Guid = guid,
                Timestamp = 0,
                Type = dataType,
                UncompressedSize = 0,
                CompressedData = Array.Empty<byte>()
            };
        }

        private void SendWotlkAccountDataTimes(AccountDataTimes accountDataTimes)
        {
            int count = Math.Min(accountDataTimes.AccountTimes.Length, ModernVersion.GetAccountDataCount());
            uint mask = GetWotlkAccountDataMask(count);

            SendWotlkAccountDataTimesPayload((uint)accountDataTimes.ServerTime, mask,
                i => accountDataTimes.AccountTimes[i]);
        }

        private void SendWotlkAccountDataTimes(uint mask)
        {
            SendWotlkAccountDataTimesPayload((uint)Time.UnixTime, mask, i =>
            {
                AccountData[] data = GetSession()?.AccountDataMgr?.Data;
                return data != null && i < data.Length && data[i] != null ? data[i].Timestamp : 0;
            });
        }

        private void SendWotlkAccountDataTimesPayload(uint serverTime, uint mask, Func<int, long> getTimestamp)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(serverTime);
            payload.WriteUInt8(1);

            int count = Math.Min(ModernVersion.GetAccountDataCount(), 32);
            mask &= GetWotlkAccountDataMask(count);

            payload.WriteUInt32(mask);
            for (int i = 0; i < count; ++i)
            {
                if ((mask & (1u << i)) != 0)
                    payload.WriteUInt32((uint)getTimestamp(i));
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_ACCOUNT_DATA_TIMES), payload.GetData());
        }

        private static uint GetWotlkAccountDataMask(int count)
        {
            count = Math.Min(count, 32);
            return count == 32 ? 0xFFFFFFFFu : (count == 0 ? 0u : ((1u << count) - 1u));
        }

        private void SendWotlkAccountData(AccountData data)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(data.Guid?.To64().GetLowValue() ?? 0);
            payload.WriteUInt32(data.Type);
            payload.WriteUInt32((uint)data.Timestamp);
            payload.WriteUInt32(data.UncompressedSize);

            if (data.CompressedData != null && data.CompressedData.Length != 0)
                payload.WriteBytes(data.CompressedData);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_UPDATE_ACCOUNT_DATA), payload.GetData());
        }

        private void SendWotlkUpdateAccountDataComplete(uint dataType, uint result)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(dataType);
            payload.WriteUInt32(result);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_UPDATE_ACCOUNT_DATA_COMPLETE), payload.GetData());
        }

        private void SendWotlkCorpseLocation(CorpseLocation corpseLocation)
        {
            // Wrath expects MSG_CORPSE_QUERY payload shape here.
            // Vanilla does not provide corpse low guid, so synthesize from player guid.
            ByteBuffer payload = new();
            payload.WriteBool(corpseLocation.Valid);

            if (corpseLocation.Valid)
            {
                payload.WriteInt32(corpseLocation.ActualMapID);
                payload.WriteFloat(corpseLocation.Position.X);
                payload.WriteFloat(corpseLocation.Position.Y);
                payload.WriteFloat(corpseLocation.Position.Z);
                payload.WriteInt32(corpseLocation.MapID);
                payload.WriteUInt32((uint)corpseLocation.Player.To64().GetLowValue());
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.MSG_CORPSE_QUERY), payload.GetData());
        }

        private void SendWotlkFeatureSystemStatus(FeatureSystemStatus featureStatus)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8(featureStatus.ComplaintStatus);
            payload.WriteUInt8(featureStatus.VoiceEnabled ? (byte)1 : (byte)0);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_FEATURE_SYSTEM_STATUS), payload.GetData());
        }

        private void SendWotlkMotd(MOTD motd)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32((uint)motd.Text.Count);
            foreach (string line in motd.Text)
                payload.WriteCString(line ?? string.Empty);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_MOTD), payload.GetData());
        }

        private void SendWotlkContactList(ContactList contacts)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32((uint)contacts.Flags);
            payload.WriteUInt32((uint)contacts.Contacts.Count);

            foreach (ContactInfo contact in contacts.Contacts)
            {
                payload.WriteUInt64(contact.Guid.To64().GetLowValue());
                payload.WriteUInt32((uint)contact.TypeFlags);
                payload.WriteCString(contact.Note ?? string.Empty);

                if (((uint)contact.TypeFlags & (uint)SocialFlag.Friend) != 0)
                {
                    payload.WriteUInt8((byte)contact.Status);
                    if (contact.Status != FriendStatus.Offline)
                    {
                        payload.WriteUInt32(contact.AreaID);
                        payload.WriteUInt32(contact.Level);
                        payload.WriteUInt32((uint)contact.ClassID);
                    }
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CONTACT_LIST), payload.GetData());
        }

        private void SendWotlkChat(ChatPkt chat)
        {
            ByteBuffer payload = new();
            ChatMessageTypeWotLK chatType = MapModernChatTypeToWotlk(chat.SlashCmd);
            uint language = chat._Language == (uint)Language.AddonBfA
                ? (uint)Language.Addon
                : chat._Language;
            language = NormalizeWotlkIncomingChatLanguage(chatType, language);

            payload.WriteUInt8((byte)chatType);
            payload.WriteUInt32(language);
            payload.WriteUInt64(chat.SenderGUID.To64().GetLowValue());
            payload.WriteUInt32(0); // 3.3.5 chat timestamp/flags field.

            switch (chatType)
            {
                case ChatMessageTypeWotLK.MonsterSay:
                case ChatMessageTypeWotLK.MonsterParty:
                case ChatMessageTypeWotLK.MonsterYell:
                case ChatMessageTypeWotLK.MonsterWhisper:
                case ChatMessageTypeWotLK.MonsterEmote:
                case ChatMessageTypeWotLK.RaidBossEmote:
                case ChatMessageTypeWotLK.RaidBossWhisper:
                case ChatMessageTypeWotLK.BattleNet:
                    payload.WriteUInt32((uint)(chat.SenderName ?? string.Empty).GetByteCount() + 1);
                    payload.WriteCString(chat.SenderName ?? string.Empty);
                    payload.WriteUInt64(chat.TargetGUID.To64().GetLowValue());
                    if (!chat.TargetGUID.IsEmpty() && !chat.TargetGUID.To64().IsPlayer())
                    {
                        payload.WriteUInt32((uint)(chat.TargetName ?? string.Empty).GetByteCount() + 1);
                        payload.WriteCString(chat.TargetName ?? string.Empty);
                    }
                    break;
                case ChatMessageTypeWotLK.WhisperForeign:
                    payload.WriteUInt32((uint)(chat.SenderName ?? string.Empty).GetByteCount() + 1);
                    payload.WriteCString(chat.SenderName ?? string.Empty);
                    payload.WriteUInt64(chat.TargetGUID.To64().GetLowValue());
                    break;
                case ChatMessageTypeWotLK.BattlegroundNeutral:
                case ChatMessageTypeWotLK.BattlegroundAlliance:
                case ChatMessageTypeWotLK.BattlegroundHorde:
                    payload.WriteUInt64(chat.TargetGUID.To64().GetLowValue());
                    if (!chat.TargetGUID.IsEmpty() && !chat.TargetGUID.To64().IsPlayer())
                    {
                        payload.WriteUInt32((uint)(chat.TargetName ?? string.Empty).GetByteCount() + 1);
                        payload.WriteCString(chat.TargetName ?? string.Empty);
                    }
                    break;
                case ChatMessageTypeWotLK.Achievement:
                case ChatMessageTypeWotLK.GuildAchievement:
                    payload.WriteUInt64(chat.TargetGUID.To64().GetLowValue());
                    break;
                default:
                    if (chatType == ChatMessageTypeWotLK.Channel)
                        payload.WriteCString(chat.Channel ?? string.Empty);
                    payload.WriteUInt64(chat.TargetGUID.To64().GetLowValue());
                    break;
            }

            payload.WriteUInt32((uint)(chat.ChatText ?? string.Empty).GetByteCount() + 1);
            payload.WriteCString(chat.ChatText ?? string.Empty);
            payload.WriteUInt8((byte)chat._ChatFlags);

            if (chatType == ChatMessageTypeWotLK.Achievement || chatType == ChatMessageTypeWotLK.GuildAchievement)
                payload.WriteUInt32(chat.AchievementID);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CHAT), payload.GetData());
        }

        private uint NormalizeWotlkIncomingChatLanguage(ChatMessageTypeWotLK chatType, uint language)
        {
            if (language == (uint)Language.Addon ||
                language == (uint)Language.Universal ||
                !IsPlayerSpeechChatType(chatType))
                return language;

            return (uint)Language.Universal;
        }

        private static bool IsPlayerSpeechChatType(ChatMessageTypeWotLK chatType)
        {
            switch (chatType)
            {
                case ChatMessageTypeWotLK.Say:
                case ChatMessageTypeWotLK.Party:
                case ChatMessageTypeWotLK.PartyLeader:
                case ChatMessageTypeWotLK.Raid:
                case ChatMessageTypeWotLK.RaidLeader:
                case ChatMessageTypeWotLK.Guild:
                case ChatMessageTypeWotLK.Officer:
                case ChatMessageTypeWotLK.Yell:
                case ChatMessageTypeWotLK.Whisper:
                case ChatMessageTypeWotLK.WhisperInform:
                case ChatMessageTypeWotLK.Channel:
                case ChatMessageTypeWotLK.Battleground:
                case ChatMessageTypeWotLK.BattlegroundLeader:
                    return true;
                default:
                    return false;
            }
        }

        private static ChatMessageTypeWotLK MapModernChatTypeToWotlk(ChatMessageTypeModern chatType)
        {
            return chatType switch
            {
                ChatMessageTypeModern.Battleground => ChatMessageTypeWotLK.Battleground,
                ChatMessageTypeModern.BattlegroundLeader => ChatMessageTypeWotLK.BattlegroundLeader,
                ChatMessageTypeModern.Restricted => ChatMessageTypeWotLK.Restricted,
                ChatMessageTypeModern.Achievement => ChatMessageTypeWotLK.Achievement,
                ChatMessageTypeModern.GuildAchievement => ChatMessageTypeWotLK.GuildAchievement,
                ChatMessageTypeModern.PartyLeader => ChatMessageTypeWotLK.PartyLeader,
                ChatMessageTypeModern.Addon => ChatMessageTypeWotLK.Addon,
                _ when Enum.TryParse(chatType.ToString(), out ChatMessageTypeWotLK mapped) => mapped,
                _ => ChatMessageTypeWotLK.System,
            };
        }

        private void SendWotlkPartyInvite(PartyInvite invite)
        {
            // 3.3.5 expects SMSG_PARTY_INVITE as:
            //   u8 status (0 = already in group, 1 = not in group)
            //   cstring inviterName
            //   optional compatibility tail (all zeros on emulators)
            ByteBuffer payload = new();
            payload.WriteUInt8(invite.CanAccept ? (byte)1 : (byte)0);
            payload.WriteCString(invite.InviterName ?? string.Empty);
            payload.WriteUInt32(0);
            payload.WriteUInt8(0);
            payload.WriteUInt32(0);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_PARTY_INVITE), payload.GetData());
        }

        private void SendWotlkGroupList(PartyUpdate party)
        {
            ByteBuffer payload = new();

            WowGuid128 selfGuid = GetSession()?.GameState?.CurrentPlayerGuid ?? WowGuid128.Empty;
            PartyPlayerInfo selfInfo = default;
            bool hasSelfInfo = false;
            List<PartyPlayerInfo> members = new();
            HashSet<WowGuid128> uniqueMembers = new();

            foreach (PartyPlayerInfo member in party.PlayerList)
            {
                if (member.GUID == WowGuid128.Empty)
                    continue;

                if (member.GUID == selfGuid)
                {
                    if (!hasSelfInfo)
                    {
                        selfInfo = member;
                        hasSelfInfo = true;
                    }

                    continue;
                }

                if (uniqueMembers.Add(member.GUID))
                    members.Add(member);
            }

            // In 3.3.5 this field is a bitmask (raid/lfg/destroyed flags), not GroupType.
            payload.WriteUInt8((byte)((ushort)party.PartyFlags & 0xFF));
            payload.WriteUInt8(hasSelfInfo ? selfInfo.Subgroup : (byte)0);
            payload.WriteUInt8(hasSelfInfo ? (byte)selfInfo.Flags : (byte)0);
            payload.WriteUInt8(hasSelfInfo ? selfInfo.RolesAssigned : (byte)0);

            bool hasLfgPayload = party.LfgInfos != null || party.PartyFlags.HasAnyFlag(GroupFlags.Lfg | GroupFlags.LfgRestricted);
            if (hasLfgPayload)
            {
                payload.WriteUInt8(0); // LFG state (0 = none, 2 = finished)
                payload.WriteUInt32(party.LfgInfos?.Slot ?? 0u);
            }

            payload.WriteUInt64(party.PartyGUID.To64().GetLowValue());
            payload.WriteUInt32((uint)Math.Max(0, party.SequenceNum));
            payload.WriteUInt32((uint)members.Count);

            foreach (PartyPlayerInfo member in members)
            {
                string memberName = member.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(memberName))
                    memberName = GetSession()?.GameState?.GetPlayerName(member.GUID) ?? string.Empty;

                payload.WriteCString(memberName);
                payload.WriteUInt64(member.GUID.To64().GetLowValue());
                payload.WriteUInt8((byte)member.Status);
                payload.WriteUInt8(member.Subgroup);
                payload.WriteUInt8((byte)member.Flags);
                payload.WriteUInt8(member.RolesAssigned);
            }

            WowGuid128 leaderGuid = party.LeaderGUID;
            if ((leaderGuid == null || leaderGuid.IsEmpty()) && members.Count > 0)
                leaderGuid = members[0].GUID;

            payload.WriteUInt64(leaderGuid.To64().GetLowValue());

            if (members.Count > 0)
            {
                LootMethod lootMethod = party.LootSettings?.Method ?? LootMethod.GroupLoot;
                payload.WriteUInt8((byte)lootMethod);
                payload.WriteUInt64(
                    lootMethod == LootMethod.MasterLoot && party.LootSettings != null
                        ? party.LootSettings.LootMaster.To64().GetLowValue()
                        : 0UL);
                payload.WriteUInt8(party.LootSettings?.Threshold ?? 2);

                byte dungeonDifficulty = party.DifficultySettings?.DungeonDifficultyID == DifficultyModern.Heroic ? (byte)1 : (byte)0;
                byte raidDifficulty = party.DifficultySettings?.RaidDifficultyID switch
                {
                    DifficultyModern.Raid25N => 1,
                    DifficultyModern.Raid10HC => 2,
                    DifficultyModern.Raid25HC => 3,
                    _ => 0,
                };

                payload.WriteUInt8(dungeonDifficulty);
                payload.WriteUInt8(raidDifficulty);
                payload.WriteUInt8((byte)(raidDifficulty >= 2 ? 1 : 0));
            }

            Log.Print(LogType.Debug, $"[WotLK] Repacked PartyUpdate -> SMSG_GROUP_LIST: flags={(ushort)party.PartyFlags}, members={members.Count}, hasSelf={hasSelfInfo}.");
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_GROUP_LIST), payload.GetData());
        }

        private void SendWotlkGroupNewLeader(GroupNewLeader groupNewLeader)
        {
            string leaderName = groupNewLeader.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(leaderName))
            {
                WowGuid128 leaderGuid = GetSession()?.GameState?.GetCurrentGroupLeader() ?? WowGuid128.Empty;
                if (leaderGuid != null && !leaderGuid.IsEmpty())
                    leaderName = GetSession()?.GameState?.GetPlayerName(leaderGuid) ?? string.Empty;
            }

            ByteBuffer payload = new();
            payload.WriteCString(leaderName);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_GROUP_NEW_LEADER), payload.GetData());
        }

        private void SendWotlkLearnedSpells(LearnedSpells learnedSpells)
        {
            uint opcode = ModernVersion.GetCurrentOpcode(Opcode.SMSG_LEARNED_SPELL);
            if (opcode == 0)
            {
                Log.Print(LogType.Warn, "[WotLK] Cannot send learned spells: missing SMSG_LEARNED_SPELL opcode mapping.");
                return;
            }

            foreach (uint spellId in learnedSpells.Spells)
            {
                ByteBuffer payload = new();
                payload.WriteUInt32(spellId);
                SendWotlkRawPacket(opcode, payload.GetData());
            }
        }

        internal void SendWotlkActionButtons(IReadOnlyList<int> buttons, byte reason = 1)
        {
            const int WotlkActionButtonCount = 144;

            ByteBuffer payload = new();
            payload.WriteUInt8(reason);

            if (reason != 2)
            {
                for (int i = 0; i < WotlkActionButtonCount; ++i)
                {
                    int packed = buttons != null && i < buttons.Count ? buttons[i] : 0;
                    payload.WriteInt32(packed);
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_UPDATE_ACTION_BUTTONS), payload.GetData());
        }

        private void SendWotlkTradeStatus(TradeStatusPkt tradeStatus)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32((uint)tradeStatus.Status);

            switch (tradeStatus.Status)
            {
                case TradeStatus.Proposed:
                    payload.WriteUInt64(GetWotlkGuidLow(tradeStatus.Partner));
                    break;
                case TradeStatus.Initiated:
                    payload.WriteUInt32(tradeStatus.Id);
                    break;
                case TradeStatus.Failed:
                    payload.WriteUInt32((uint)tradeStatus.BagResult);
                    payload.WriteUInt8(tradeStatus.FailureForYou ? (byte)1 : (byte)0);
                    payload.WriteUInt32(tradeStatus.ItemID);
                    break;
                case TradeStatus.WrongRealm:
                case TradeStatus.NotOnTaplist:
                    payload.WriteUInt8(tradeStatus.TradeSlot);
                    break;
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_TRADE_STATUS), payload.GetData());
        }

        private void SendWotlkTradeStatusExtended(TradeUpdated trade)
        {
            const int TradeSlotCount = 7;

            ByteBuffer payload = new();
            payload.WriteUInt8(trade.WhichPlayer);
            payload.WriteUInt32(trade.Id);
            payload.WriteUInt32(TradeSlotCount);
            payload.WriteUInt32(TradeSlotCount);
            payload.WriteUInt32((uint)Math.Min(trade.Gold, uint.MaxValue));
            payload.WriteUInt32(unchecked((uint)trade.ProposedEnchantment));

            for (byte slot = 0; slot < TradeSlotCount; ++slot)
                WriteWotlkTradeSlot(payload, slot, trade.Items.FirstOrDefault(item => item.Slot == slot));

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_TRADE_STATUS_EXTENDED), payload.GetData());
        }

        private static void WriteWotlkTradeSlot(ByteBuffer payload, byte slot, TradeUpdated.TradeItem item)
        {
            payload.WriteUInt8(slot);

            if (item == null || item.Item == null || item.Item.ItemID == 0)
            {
                for (int i = 0; i < 18; ++i)
                    payload.WriteUInt32(0);
                return;
            }

            uint itemId = item.Item.ItemID;
            TradeUpdated.UnwrappedTradeItem unwrapped = item.Unwrapped;

            payload.WriteUInt32(itemId);
            payload.WriteUInt32(ResolveItemDisplayId(itemId));
            payload.WriteUInt32(unchecked((uint)item.StackCount));
            payload.WriteUInt32(0); // wrapped flag is not preserved by the legacy parser
            payload.WriteUInt64(GetWotlkGuidLow(item.GiftCreator));
            payload.WriteUInt32(unchecked((uint)(unwrapped?.EnchantID ?? 0)));
            payload.WriteUInt32(0);
            payload.WriteUInt32(0);
            payload.WriteUInt32(0);
            payload.WriteUInt64(GetWotlkGuidLow(unwrapped?.Creator));
            payload.WriteUInt32(unchecked((uint)(unwrapped?.Charges ?? 0)));
            payload.WriteUInt32(item.Item.RandomPropertiesSeed);
            payload.WriteUInt32(item.Item.RandomPropertiesID);
            payload.WriteUInt32(unwrapped?.Lock == true ? 1u : 0u);
            payload.WriteUInt32(unwrapped?.MaxDurability ?? 0);
            payload.WriteUInt32(unwrapped?.Durability ?? 0);
        }

        private static ulong GetWotlkGuidLow(WowGuid128 guid)
        {
            return guid != null ? guid.To64().GetLowValue() : 0;
        }

        private void SendWotlkAttackStart(SAttackStart attackStart)
        {
            ByteBuffer payload = new();
            WritePackedGuid64(payload, attackStart.Attacker.To64().GetLowValue());
            WritePackedGuid64(payload, attackStart.Victim.To64().GetLowValue());
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_ATTACK_START), payload.GetData());
        }

        private void SendWotlkAttackStop(SAttackStop attackStop)
        {
            ByteBuffer payload = new();
            WritePackedGuid64(payload, attackStop.Attacker.To64().GetLowValue());
            WritePackedGuid64(payload, attackStop.Victim.To64().GetLowValue());
            payload.WriteUInt32(attackStop.NowDead ? 1u : 0u);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_ATTACK_STOP), payload.GetData());
        }

        private void SendWotlkAttackerStateUpdate(AttackerStateUpdate attack)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32((uint)attack.HitInfo);
            WritePackedGuid64(payload, attack.AttackerGUID.To64().GetLowValue());
            WritePackedGuid64(payload, attack.VictimGUID.To64().GetLowValue());
            payload.WriteInt32(attack.Damage);
            payload.WriteInt32(attack.OverDamage);

            payload.WriteUInt8((byte)attack.SubDmg.Count);
            foreach (SubDamage subDmg in attack.SubDmg)
            {
                payload.WriteUInt32(subDmg.SchoolMask);
                payload.WriteFloat(subDmg.FloatDamage);
                payload.WriteInt32(subDmg.IntDamage);
                if (attack.HitInfo.HasAnyFlag(HitInfo.PartialAbsorb | HitInfo.FullAbsorb))
                    payload.WriteInt32(subDmg.Absorbed);
                if (attack.HitInfo.HasAnyFlag(HitInfo.PartialResist | HitInfo.FullResist))
                    payload.WriteInt32(subDmg.Resisted);
            }

            payload.WriteUInt8(attack.VictimState);
            payload.WriteInt32(attack.AttackerState);
            payload.WriteUInt32(attack.MeleeSpellID);

            if (attack.HitInfo.HasAnyFlag(HitInfo.Block))
                payload.WriteInt32(attack.BlockAmount);

            if (attack.HitInfo.HasAnyFlag(HitInfo.RageGain))
                payload.WriteInt32(attack.RageGained);

            if (attack.HitInfo.HasAnyFlag(HitInfo.Unk0))
            {
                payload.WriteUInt32(attack.UnkState.State1);
                payload.WriteFloat(attack.UnkState.State2);
                payload.WriteFloat(attack.UnkState.State3);
                payload.WriteFloat(attack.UnkState.State4);
                payload.WriteFloat(attack.UnkState.State5);
                payload.WriteFloat(attack.UnkState.State6);
                payload.WriteFloat(attack.UnkState.State7);
                payload.WriteFloat(attack.UnkState.State8);
                payload.WriteFloat(attack.UnkState.State9);
                payload.WriteFloat(attack.UnkState.State10);
                payload.WriteFloat(attack.UnkState.State11);
                payload.WriteUInt32(attack.UnkState.State12);
                payload.WriteUInt32(0);
                payload.WriteUInt32(0);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_ATTACKER_STATE_UPDATE), payload.GetData());
        }

        private void SendWotlkQueryPlayerNameResponse(QueryPlayerNameResponse response)
        {
            ByteBuffer payload = new();
            WritePackedGuid64(payload, response.Player.To64().GetLowValue());
            payload.WriteUInt8(response.Result);

            if (response.Result == 0)
            {
                payload.WriteCString(response.Data.Name ?? string.Empty);
                payload.WriteCString(string.Empty); // realm name (cross-realm not used)
                payload.WriteUInt8((byte)response.Data.RaceID);
                payload.WriteUInt8((byte)response.Data.Sex);
                payload.WriteUInt8((byte)response.Data.ClassID);

                bool hasDeclined = false;
                for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                {
                    if (!string.IsNullOrEmpty(response.Data.DeclinedNames.name[i]))
                    {
                        hasDeclined = true;
                        break;
                    }
                }

                payload.WriteUInt8(hasDeclined ? (byte)1 : (byte)0);
                if (hasDeclined)
                {
                    for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                        payload.WriteCString(response.Data.DeclinedNames.name[i] ?? string.Empty);
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_PLAYER_NAME_RESPONSE), payload.GetData());
        }

        private void SendWotlkQueryTimeResponse(QueryTimeResponse response)
        {
            ByteBuffer payload = new();
            payload.WriteInt32((int)response.CurrentTime);
            payload.WriteInt32(0); // next daily reset time
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_TIME_RESPONSE), payload.GetData());
        }

        private void SendWotlkUiTimeResponse()
        {
            uint unixTime = (uint)Time.UnixTime;
            ByteBuffer payload = new();
            payload.WriteUInt32(unixTime);
            Log.Print(LogType.Debug, $"[WotLK] Answering CMSG_WORLD_STATE_UI_TIMER_UPDATE with SMSG_WORLD_STATE_UI_TIMER_UPDATE time={unixTime}.");
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_UI_TIME), payload.GetData());
        }

        private void SendWotlkCalendar()
        {
            uint unixTime = (uint)Time.UnixTime;
            ByteBuffer payload = new();

            payload.WriteUInt32(0); // invites
            payload.WriteUInt32(0); // events
            payload.WriteUInt32(unixTime); // server time
            payload.WritePackedTime(unixTime); // zone time
            payload.WriteUInt32(0); // active raid lockouts
            payload.WriteUInt32(1135814400u + 4u * Time.Hour); // default Wrath raid reset reference time
            payload.WriteUInt32(0); // raid reset definitions
            payload.WriteUInt32(0); // modified holidays

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CALENDAR_SEND_CALENDAR), payload.GetData());
        }

        private void SendWotlkCalendarNumPending()
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(0);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CALENDAR_SEND_NUM_PENDING), payload.GetData());
        }

        private void SendWotlkQueryCreatureResponse(QueryCreatureResponse response)
        {
            ByteBuffer payload = new();
            uint entry = response.CreatureID;

            if (!response.Allow)
            {
                payload.WriteUInt32(entry | 0x80000000u);
            }
            else
            {
                payload.WriteUInt32(entry);
                for (int i = 0; i < 4; ++i)
                    payload.WriteCString(response.Stats.Name[i] ?? string.Empty);

                payload.WriteCString(response.Stats.Title ?? string.Empty);
                payload.WriteCString(response.Stats.CursorName ?? string.Empty);

                payload.WriteUInt32(response.Stats.Flags[0]);
                payload.WriteInt32(response.Stats.Type);
                payload.WriteInt32(response.Stats.Family);
                payload.WriteInt32(response.Stats.Classification);

                payload.WriteUInt32(response.Stats.ProxyCreatureID[0]);
                payload.WriteUInt32(response.Stats.ProxyCreatureID[1]);

                for (int i = 0; i < 4; ++i)
                {
                    uint displayId = 0;
                    if (i < response.Stats.Display.CreatureDisplay.Count)
                        displayId = response.Stats.Display.CreatureDisplay[i].CreatureDisplayID;
                    payload.WriteUInt32(displayId);
                }

                payload.WriteFloat(response.Stats.HpMulti);
                payload.WriteFloat(response.Stats.EnergyMulti);
                payload.WriteBool(response.Stats.Leader);

                for (int i = 0; i < 6; ++i)
                {
                    uint questItem = i < response.Stats.QuestItems.Count ? response.Stats.QuestItems[i] : 0;
                    payload.WriteUInt32(questItem);
                }

                payload.WriteUInt32(response.Stats.MovementInfoID);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_CREATURE_RESPONSE), payload.GetData());
        }

        private void SendWotlkQueryGameObjectResponse(QueryGameObjectResponse response)
        {
            ByteBuffer payload = new();
            uint entry = response.GameObjectID;

            if (!response.Allow)
            {
                payload.WriteUInt32(entry | 0x80000000u);
            }
            else
            {
                payload.WriteUInt32(entry);
                payload.WriteUInt32(response.Stats.Type);
                payload.WriteUInt32(response.Stats.DisplayID);

                for (int i = 0; i < 4; ++i)
                    payload.WriteCString(response.Stats.Name[i] ?? string.Empty);

                payload.WriteCString(response.Stats.IconName ?? string.Empty);
                payload.WriteCString(response.Stats.CastBarCaption ?? string.Empty);
                payload.WriteCString(response.Stats.UnkString ?? string.Empty);

                // WotLK 3.3.5 expects 24 GO data dwords in query responses.
                // Classic-era sources may only provide the first few; remaining
                // entries are already zero-filled by the parser/cache layer.
                for (int i = 0; i < 24; ++i)
                    payload.WriteUInt32((uint)response.Stats.Data[i]);

                payload.WriteFloat(response.Stats.Size);

                for (int i = 0; i < 6; ++i)
                {
                    uint questItem = i < response.Stats.QuestItems.Count ? response.Stats.QuestItems[i] : 0;
                    payload.WriteUInt32(questItem);
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_GAME_OBJECT_RESPONSE), payload.GetData());
        }

        private void SendWotlkQueryNpcTextResponse(QueryNPCTextResponse response)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(response.TextID);

            for (int i = 0; i < 8; ++i)
            {
                string maleText = response.Allow ? response.MaleText[i] ?? string.Empty : string.Empty;
                string femaleText = response.Allow ? response.FemaleText[i] ?? string.Empty : string.Empty;

                if (string.IsNullOrEmpty(maleText) && !string.IsNullOrEmpty(femaleText))
                    maleText = femaleText;
                else if (!string.IsNullOrEmpty(maleText) && string.IsNullOrEmpty(femaleText))
                    femaleText = maleText;

                payload.WriteFloat(response.Allow ? response.Probabilities[i] : 0.0f);
                payload.WriteCString(maleText);
                payload.WriteCString(femaleText);
                payload.WriteUInt32(response.Allow ? response.Language[i] : 0u);

                for (int j = 0; j < 3; ++j)
                {
                    payload.WriteUInt32(response.Allow ? response.EmoteDelays[i, j] : 0u);
                    payload.WriteUInt32(response.Allow ? response.Emotes[i, j] : 0u);
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_NPC_TEXT_RESPONSE), payload.GetData());
        }

        private void SendWotlkQueryPetNameResponse(QueryPetNameResponse response)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32((uint)response.UnitGUID.GetEntry());

            if (!response.Allow)
            {
                payload.WriteCString(string.Empty);
                payload.WriteBytes(new byte[7]);
                SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_PET_NAME_RESPONSE), payload.GetData());
                return;
            }

            payload.WriteCString(response.Name ?? string.Empty);
            payload.WriteUInt32((uint)response.Timestamp);

            bool hasDeclined = response.HasDeclined;
            if (!hasDeclined)
            {
                for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                {
                    if (!string.IsNullOrEmpty(response.DeclinedNames.name[i]))
                    {
                        hasDeclined = true;
                        break;
                    }
                }
            }

            payload.WriteBool(hasDeclined);
            if (hasDeclined)
            {
                for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                    payload.WriteCString(response.DeclinedNames.name[i] ?? string.Empty);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUERY_PET_NAME_RESPONSE), payload.GetData());
        }

        private void SendWotlkMailQueryNextTimeResult(MailQueryNextTimeResult result)
        {
            ByteBuffer payload = new();
            payload.WriteFloat(result.NextMailTime);
            payload.WriteUInt32((uint)result.Mails.Count);

            foreach (MailQueryNextTimeResult.MailNextTimeEntry entry in result.Mails)
            {
                // WotLK expects a fixed 64-bit sender guid in MSG_QUERY_NEXT_MAIL_TIME.
                payload.WriteUInt64(entry.SenderGuid.To64().GetLowValue());
                payload.WriteUInt32((uint)entry.AltSenderID);
                payload.WriteUInt32((uint)(byte)entry.AltSenderType);
                payload.WriteUInt32((uint)entry.StationeryID);
                payload.WriteFloat(entry.TimeLeft);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.MSG_QUERY_NEXT_MAIL_TIME), payload.GetData());
        }

        private void SendWotlkMailListResult(MailListResult result)
        {
            ByteBuffer payload = new();
            int count = Math.Min(result.Mails.Count, byte.MaxValue);

            payload.WriteUInt32((uint)Math.Max(result.TotalNumRecords, count));
            payload.WriteUInt8((byte)count);

            for (int i = 0; i < count; ++i)
            {
                MailListEntry mail = result.Mails[i];
                ByteBuffer entry = new();

                entry.WriteInt32(mail.MailID);
                entry.WriteUInt8((byte)mail.SenderType);

                if (mail.SenderType == MailType.Normal)
                    entry.WriteUInt64(mail.SenderCharacter == null ? 0ul : mail.SenderCharacter.To64().GetLowValue());
                else
                    entry.WriteUInt32(mail.AltSenderID ?? 0);

                entry.WriteUInt32((uint)Math.Min(mail.Cod, uint.MaxValue));
                entry.WriteUInt32(0); // package/unused in 3.3.5
                entry.WriteInt32(mail.StationeryID);
                entry.WriteUInt32((uint)Math.Min(mail.SentMoney, uint.MaxValue));
                entry.WriteUInt32(mail.Flags);
                entry.WriteFloat(mail.DaysLeft);
                entry.WriteInt32(mail.MailTemplateID);
                entry.WriteCString(mail.Subject ?? string.Empty);
                entry.WriteCString(mail.Body ?? string.Empty);

                int attachmentCount = Math.Min(mail.Attachments.Count, byte.MaxValue);
                entry.WriteUInt8((byte)attachmentCount);
                for (int j = 0; j < attachmentCount; ++j)
                    WriteWotlkMailAttachment(entry, mail.Attachments[j]);

                byte[] entryData = entry.GetData();
                payload.WriteUInt16((ushort)Math.Min(entryData.Length + sizeof(ushort), ushort.MaxValue));
                payload.WriteBytes(entryData);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_MAIL_LIST_RESULT), payload.GetData());
        }

        private static void WriteWotlkMailAttachment(ByteBuffer payload, MailAttachedItem item)
        {
            payload.WriteUInt8(item.Position);
            payload.WriteInt32(item.AttachID);
            payload.WriteUInt32(item.Item?.ItemID ?? 0);

            for (byte slot = 0; slot < 7; ++slot)
            {
                ItemEnchantData enchant = null;
                foreach (ItemEnchantData candidate in item.Enchants)
                {
                    if (candidate.Slot == slot)
                    {
                        enchant = candidate;
                        break;
                    }
                }

                payload.WriteUInt32(enchant?.ID ?? 0);
                payload.WriteUInt32(enchant?.Expiration ?? 0);
                payload.WriteInt32(enchant?.Charges ?? 0);
            }

            payload.WriteInt32(unchecked((int)(item.Item?.RandomPropertiesID ?? 0)));
            payload.WriteUInt32(item.Item?.RandomPropertiesSeed ?? 0);
            payload.WriteUInt32(item.Count);
            payload.WriteUInt32(unchecked((uint)item.Charges));
            payload.WriteUInt32(item.MaxDurability);
            payload.WriteUInt32(item.Durability);
            payload.WriteUInt8((byte)(item.Unlocked ? 1 : 0));
        }

        private void SendWotlkMailCommandResult(MailCommandResult result)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(result.MailID);
            payload.WriteUInt32((uint)result.Command);
            payload.WriteUInt32((uint)result.ErrorCode);

            if (result.ErrorCode == MailErrorType.Equip)
            {
                payload.WriteUInt32((uint)result.BagResult);
            }
            else if (result.Command == MailActionType.AttachmentExpired)
            {
                payload.WriteUInt32(result.AttachID);
                payload.WriteUInt32(result.QtyInInventory);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_MAIL_COMMAND_RESULT), payload.GetData());
        }

        private void SendWotlkAuctionBidderNotification(AuctionBidderNotification info, ulong bidAmount, ulong minIncrement)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(info.Command);
            payload.WriteUInt32(info.AuctionID);
            payload.WriteUInt64(info.Bidder == null ? 0ul : info.Bidder.To64().GetLowValue());
            payload.WriteUInt32((uint)Math.Min(bidAmount, uint.MaxValue));
            payload.WriteUInt32((uint)Math.Min(minIncrement, uint.MaxValue));
            payload.WriteUInt32(info.Item?.ItemID ?? 0);
            payload.WriteUInt32(info.Item?.RandomPropertiesID ?? 0);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_AUCTION_BIDDER_NOTIFICATION), payload.GetData());
        }

        private void SendWotlkAuctionOwnerNotification(AuctionOwnerNotification info, ulong minIncrement, WowGuid128 bidder, float mailDelay)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(info.AuctionID);
            payload.WriteUInt32((uint)Math.Min(info.BidAmount, uint.MaxValue));
            payload.WriteUInt32((uint)Math.Min(minIncrement, uint.MaxValue));
            payload.WriteUInt64(bidder == null ? 0ul : bidder.To64().GetLowValue());
            payload.WriteUInt32(info.Item?.ItemID ?? 0);
            payload.WriteUInt32(info.Item?.RandomPropertiesID ?? 0);
            payload.WriteFloat(mailDelay);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_AUCTION_OWNER_NOTIFICATION), payload.GetData());
        }

        private void SendWotlkChannelNotifyJoined(ChannelNotifyJoined joined)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8((byte)ChatNotify.YouJoined);
            payload.WriteCString(joined.Channel ?? string.Empty);
            payload.WriteUInt8((byte)((uint)joined.ChannelFlags & 0xFFu));
            payload.WriteInt32(joined.ChatChannelID);
            payload.WriteInt32(0); // Unused/unknown value present in WotLK-era payload.

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CHANNEL_NOTIFY), payload.GetData());
        }

        private void SendWotlkChannelNotifyLeft(ChannelNotifyLeft left)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8((byte)ChatNotify.YouLeft);
            payload.WriteCString(left.Channel ?? string.Empty);
            payload.WriteInt32(left.ChatChannelID);
            payload.WriteUInt8(left.Suspended ? (byte)1 : (byte)0);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_CHANNEL_NOTIFY), payload.GetData());
        }

        private void SendWotlkEnumCharactersResult(EnumCharactersResult charEnum)
        {
            ByteBuffer payload = new();

            int count = Math.Min(charEnum.Characters.Count, byte.MaxValue);
            payload.WriteUInt8((byte)count);
            for (int i = 0; i < count; ++i)
            {
                var character = charEnum.Characters[i];
                var customizations = new List<ChrCustomizationChoice>();
                for (int customizationIndex = 0; customizationIndex < character.Customizations.Count; ++customizationIndex)
                {
                    ChrCustomizationChoice? customization = character.Customizations[customizationIndex];
                    if (customization != null)
                        customizations.Add(customization);
                }

                CharacterCustomizations.ConvertModernCustomizationsToLegacy(customizations, out byte skin, out byte face, out byte hairStyle, out byte hairColor, out byte facialHair);

                payload.WriteUInt64(character.Guid.To64().GetLowValue());
                payload.WriteCString(character.Name ?? string.Empty);
                payload.WriteUInt8((byte)character.RaceId);
                payload.WriteUInt8((byte)character.ClassId);
                payload.WriteUInt8((byte)character.SexId);
                payload.WriteUInt8(skin);
                payload.WriteUInt8(face);
                payload.WriteUInt8(hairStyle);
                payload.WriteUInt8(hairColor);
                payload.WriteUInt8(facialHair);
                payload.WriteUInt8(character.ExperienceLevel);
                payload.WriteUInt32(character.ZoneId);
                payload.WriteUInt32(character.MapId);
                payload.WriteFloat(character.PreloadPos.X);
                payload.WriteFloat(character.PreloadPos.Y);
                payload.WriteFloat(character.PreloadPos.Z);
                payload.WriteUInt32((uint)character.GuildGuid.GetCounter());
                payload.WriteUInt32((uint)character.Flags);
                payload.WriteUInt32(0); // character customize flags
                payload.WriteUInt8(character.FirstLogin ? (byte)1 : (byte)0);
                payload.WriteUInt32(character.PetCreatureDisplayId);
                payload.WriteUInt32(character.PetExperienceLevel);
                payload.WriteUInt32(character.PetCreatureFamilyId);

                for (int slot = 0; slot < character.VisualItems.Length; ++slot)
                {
                    payload.WriteUInt32(character.VisualItems[slot].DisplayId);
                    payload.WriteUInt8(character.VisualItems[slot].InvType);
                    payload.WriteUInt32(character.VisualItems[slot].DisplayEnchantId);
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_ENUM_CHARACTERS_RESULT), payload.GetData());
        }

        private void SendWotlkGossipMessage(GossipMessagePkt gossipMessage)
        {
            if (gossipMessage.GossipOptions.Count == 0 && gossipMessage.GossipQuests.Count > 0)
            {
                // Vanilla often sends a quest-only SMSG_GOSSIP_MESSAGE.  Wrath
                // handles quest-only NPC dialogs more reliably as the dedicated
                // quest-list packet, which also avoids relying on a 1.12 GossipText
                // id that may not exist in the 3.3.5 client DBC.
                QuestGiverQuestListMessage questList = new();
                questList.QuestGiverGUID = gossipMessage.GossipGUID;
                questList.Greeting = string.Empty;
                questList.GreetEmoteDelay = 0;
                questList.GreetEmoteType = 0;
                questList.QuestOptions.AddRange(gossipMessage.GossipQuests);
                SendWotlkQuestGiverQuestListMessage(questList);
                return;
            }

            ByteBuffer payload = new();
            // 3.3.5 SMSG_GOSSIP_MESSAGE starts with a raw 64-bit ObjectGuid.
            // A packed guid here misaligns menu id/text id/options, so the client
            // keeps re-sending CMSG_TALK_TO_GOSSIP without opening the gossip UI.
            payload.WriteUInt64(gossipMessage.GossipGUID.To64().GetLowValue());
            payload.WriteUInt32((uint)Math.Max(0, gossipMessage.GossipID));
            payload.WriteUInt32((uint)Math.Max(0, gossipMessage.TextID));

            int gossipOptionCount = Math.Min(gossipMessage.GossipOptions.Count, byte.MaxValue);
            payload.WriteUInt32((uint)gossipOptionCount);
            for (int i = 0; i < gossipOptionCount; ++i)
            {
                ClientGossipOption option = gossipMessage.GossipOptions[i];
                payload.WriteUInt32((uint)Math.Max(0, option.OptionIndex));
                payload.WriteUInt8(option.OptionIcon);
                payload.WriteUInt8(option.OptionFlags != 0 ? (byte)1 : (byte)0);
                payload.WriteUInt32((uint)Math.Max(0, option.OptionCost));
                payload.WriteCString(option.Text ?? string.Empty);
                payload.WriteCString(option.Confirm ?? string.Empty);
            }

            int questCount = Math.Min(gossipMessage.GossipQuests.Count, byte.MaxValue);
            payload.WriteUInt32((uint)questCount);
            for (int i = 0; i < questCount; ++i)
            {
                ClientGossipQuest quest = gossipMessage.GossipQuests[i];
                payload.WriteUInt32(quest.QuestID);
                payload.WriteUInt32((uint)Math.Max(0, quest.QuestType));
                payload.WriteInt32(quest.QuestLevel);
                payload.WriteUInt32(quest.QuestFlags);
                payload.WriteUInt8(quest.Repeatable ? (byte)1 : (byte)0);
                payload.WriteCString(quest.QuestTitle ?? string.Empty);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_GOSSIP_MESSAGE), payload.GetData());
        }

        private void SendWotlkSingleGuidPacket(Opcode opcode, WowGuid128 guid)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(guid.To64().GetLowValue());
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(opcode), payload.GetData());
        }

        private void SendWotlkTrainerList(TrainerList trainerList)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(trainerList.TrainerGUID.To64().GetLowValue());
            payload.WriteInt32(trainerList.TrainerType);

            int spellCount = Math.Max(0, trainerList.Spells.Count);
            payload.WriteInt32(spellCount);
            foreach (TrainerListSpell spell in trainerList.Spells)
            {
                payload.WriteInt32((int)spell.SpellID);
                payload.WriteUInt8((byte)spell.Usable);
                payload.WriteInt32((int)spell.MoneyCost);
                payload.WriteInt32(0); // profession dialog points; vanilla does not provide this separately
                payload.WriteInt32(0); // profession button points
                payload.WriteUInt8(spell.ReqLevel);
                payload.WriteInt32((int)spell.ReqSkillLine);
                payload.WriteInt32((int)spell.ReqSkillRank);

                for (int i = 0; i < 3; ++i)
                    payload.WriteInt32((int)spell.ReqAbility[i]);
            }

            payload.WriteCString(trainerList.Greeting ?? string.Empty);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_TRAINER_LIST), payload.GetData());
        }

        private void SendWotlkResurrectRequest(ResurrectRequest resurrectRequest)
        {
            ByteBuffer payload = new();
            string name = resurrectRequest.Name ?? string.Empty;

            payload.WriteUInt64(resurrectRequest.CasterGUID.To64().GetLowValue());
            payload.WriteUInt32((uint)Encoding.UTF8.GetByteCount(name) + 1);
            payload.WriteCString(name);
            payload.WriteUInt8(resurrectRequest.Sickness ? (byte)1 : (byte)0);

            if (!resurrectRequest.UseTimer)
                payload.WriteUInt32(0);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_RESURRECT_REQUEST), payload.GetData());
        }

        private void SendWotlkSpellStart(SpellStart spellStart)
        {
            WorldPacket payload = new();
            WriteWotlk335SpellCastData(payload, spellStart.Cast, false);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_SPELL_START), payload.GetData());
        }

        private void SendWotlkSpellGo(SpellGo spellGo)
        {
            WorldPacket payload = new();
            WriteWotlk335SpellCastData(payload, spellGo.Cast, true);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_SPELL_GO), payload.GetData());
        }

        private void SendWotlkSpellChannelStart(SpellChannelStart spellChannelStart)
        {
            uint opcode = ModernVersion.GetCurrentOpcode(Opcode.MSG_CHANNEL_START);
            if (opcode == 0)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, "[WotLK] Cannot send SpellChannelStart: missing MSG_CHANNEL_START mapping.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, spellChannelStart.CasterGUID.To64().GetLowValue());
            payload.WriteUInt32(spellChannelStart.SpellID);
            payload.WriteUInt32(spellChannelStart.Duration);

            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkSpellChannelUpdate(SpellChannelUpdate spellChannelUpdate)
        {
            uint opcode = ModernVersion.GetCurrentOpcode(Opcode.MSG_CHANNEL_UPDATE);
            if (opcode == 0)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, "[WotLK] Cannot send SpellChannelUpdate: missing MSG_CHANNEL_UPDATE mapping.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, spellChannelUpdate.CasterGUID.To64().GetLowValue());
            payload.WriteUInt32((uint)Math.Max(0, spellChannelUpdate.TimeRemaining));

            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkAuraUpdate(AuraUpdate auraUpdate)
        {
            ByteBuffer payload = new();
            WritePackedGuid64(payload, auraUpdate.UnitGUID.To64().GetLowValue());

            foreach (AuraInfo aura in auraUpdate.Auras)
            {
                payload.WriteUInt8(aura.Slot);

                AuraDataInfo data = aura.AuraData;
                if (data == null || data.SpellID == 0)
                {
                    payload.WriteUInt32(0);
                    continue;
                }

                payload.WriteUInt32(data.SpellID);

                uint activeFlags = data.ActiveFlags != 0 ? data.ActiveFlags : 1u;
                byte flags = 0;
                if ((activeFlags & 0x1) != 0)
                    flags |= (byte)AuraFlagsWotLK.EffectIndex0;
                if ((activeFlags & 0x2) != 0)
                    flags |= (byte)AuraFlagsWotLK.EffectIndex1;
                if ((activeFlags & 0x4) != 0)
                    flags |= (byte)AuraFlagsWotLK.EffectIndex2;
                if (data.Flags.HasAnyFlag(AuraFlagsModern.Positive))
                    flags |= (byte)AuraFlagsWotLK.Positive;
                if (data.Flags.HasAnyFlag(AuraFlagsModern.Negative))
                    flags |= (byte)AuraFlagsWotLK.Negative;
                if (data.Duration.HasValue || data.Remaining.HasValue)
                    flags |= (byte)AuraFlagsWotLK.Duration;

                bool hasCaster = data.CastUnit != null && !data.CastUnit.IsEmpty();
                if (!hasCaster)
                    flags |= (byte)AuraFlagsWotLK.NoCaster;

                payload.WriteUInt8(flags);
                payload.WriteUInt8((byte)Math.Max(1, Math.Min((int)byte.MaxValue, (int)data.CastLevel)));
                payload.WriteUInt8(data.Applications);

                if (hasCaster)
                    WritePackedGuid64(payload, data.CastUnit.To64().GetLowValue());

                if ((flags & (byte)AuraFlagsWotLK.Duration) != 0)
                {
                    int maxDuration = data.Duration ?? data.Remaining ?? 0;
                    int remaining = data.Remaining ?? data.Duration ?? maxDuration;
                    payload.WriteInt32(Math.Max(0, maxDuration));
                    payload.WriteInt32(Math.Max(0, remaining));
                }
            }

            Opcode opcode = auraUpdate.UpdateAll ? Opcode.SMSG_AURA_UPDATE_ALL : Opcode.SMSG_AURA_UPDATE;
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(opcode), payload.GetData());
        }

        private void SendWotlkPowerUpdate(PowerUpdate powerUpdate)
        {
            foreach (PowerUpdatePower power in powerUpdate.Powers)
            {
                ByteBuffer payload = new();
                WritePackedGuid64(payload, powerUpdate.Guid.To64().GetLowValue());
                payload.WriteUInt8(power.PowerType);
                payload.WriteUInt32((uint)Math.Max(0, power.Power));

                SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_POWER_UPDATE), payload.GetData());
            }
        }

        private void SendWotlkStandStateUpdate(StandStateUpdate standStateUpdate)
        {
            ByteBuffer payload = new();
            // 3.3.5/1.12 SMSG_STAND_STATE_UPDATE is just the stand-state byte.
            // Modern Hermes packets include AnimKitID before it, which makes Wrath
            // see stale/zero stand states when food/drink toggles sitting.
            payload.WriteUInt8(standStateUpdate.StandState);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_STAND_STATE_UPDATE), payload.GetData());
        }

        private void WriteWotlk335SpellCastData(WorldPacket payload, SpellCastData cast, bool isSpellGo)
        {
            WowGuid128 casterGuid128 = cast.CasterGUID != null && !cast.CasterGUID.IsEmpty()
                ? cast.CasterGUID
                : cast.CasterUnit;
            WowGuid64 casterGuid = casterGuid128.To64();
            WowGuid64 casterUnit = cast.CasterUnit.To64();

            // 3.3.5 SMSG_SPELL_START/SMSG_SPELL_GO are pre-CastGUID packets.
            // They use packed 64-bit caster GUIDs and a one-byte cast counter,
            // not the modern 128-bit CastID/visual payload used by newer clients.
            payload.WritePackedGuid(casterGuid);
            payload.WritePackedGuid(casterUnit);
            payload.WriteUInt8(0); // cast count; vanilla backends do not provide one
            payload.WriteUInt32((uint)Math.Max(0, cast.SpellID));

            uint castFlags = BuildWotlk335CastFlags(cast, isSpellGo);
            payload.WriteUInt32(castFlags);
            payload.WriteUInt32(isSpellGo ? Time.GetMSTime() : (uint)Math.Max(0, cast.CastTime));

            if (isSpellGo)
            {
                int hitCount = Math.Min(cast.HitTargets?.Count ?? 0, byte.MaxValue);
                payload.WriteUInt8((byte)hitCount);
                for (int i = 0; i < hitCount; ++i)
                    payload.WriteGuid(cast.HitTargets[i].To64());

                int missCount = Math.Min(Math.Min(cast.MissTargets?.Count ?? 0, cast.MissStatus?.Count ?? 0), byte.MaxValue);
                payload.WriteUInt8((byte)missCount);
                for (int i = 0; i < missCount; ++i)
                {
                    payload.WriteGuid(cast.MissTargets[i].To64());
                    payload.WriteUInt8((byte)cast.MissStatus[i].Reason);
                    if (cast.MissStatus[i].Reason == SpellMissInfo.Reflect)
                        payload.WriteUInt8((byte)cast.MissStatus[i].ReflectStatus);
                }
            }

            WriteWotlk335SpellTargets(payload, cast.Target);

            if ((castFlags & (uint)CastFlag.Projectile) != 0)
            {
                payload.WriteInt32(cast.AmmoDisplayId ?? 0);
                payload.WriteInt32(cast.AmmoInventoryType ?? 0);
            }
        }

        private static uint BuildWotlk335CastFlags(SpellCastData cast, bool isSpellGo)
        {
            uint flags = isSpellGo ? (uint)CastFlag.Unknown9 : (uint)CastFlag.HasTrajectory;

            uint original = cast.CastFlags;
            if ((original & (uint)CastFlag.PendingCast) != 0)
                flags |= (uint)CastFlag.PendingCast;
            if ((original & (uint)CastFlag.NoGcd) != 0)
                flags |= (uint)CastFlag.NoGcd;
            if ((original & (uint)CastFlag.Projectile) != 0 && (cast.AmmoDisplayId.HasValue || cast.AmmoInventoryType.HasValue))
                flags |= (uint)CastFlag.Projectile;

            // Do not pass through flags with optional payloads that Hermes does not
            // fully reconstruct from 1.12 packets. Passing those through makes the
            // 3.3.5 client read past the real spell payload and leaves spell visuals stuck.
            return flags;
        }

        private void WriteWotlk335SpellTargets(WorldPacket payload, SpellTargetData target)
        {
            SpellCastTargetFlags targetFlags = target?.Flags ?? SpellCastTargetFlags.None;
            payload.WriteUInt32((uint)targetFlags);

            if (target == null || targetFlags == SpellCastTargetFlags.None)
                return;

            if ((targetFlags & SpellCastTargetFlags.UnitMask) != 0 ||
                targetFlags.HasAnyFlag(SpellCastTargetFlags.CorpseEnemy | SpellCastTargetFlags.CorpseAlly | SpellCastTargetFlags.GameObject | SpellCastTargetFlags.GameobjectItem))
                payload.WritePackedGuid(target.Unit.To64());

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.Item | SpellCastTargetFlags.TradeItem))
                payload.WritePackedGuid(target.Item.To64());

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.SourceLocation))
            {
                payload.WritePackedGuid((target.SrcLocation?.Transport ?? WowGuid128.Empty).To64());
                if (target.SrcLocation != null)
                    payload.WriteVector3(target.SrcLocation.Location);
                else
                    payload.WriteVector3(new Framework.GameMath.Vector3());
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.DestLocation))
            {
                payload.WritePackedGuid((target.DstLocation?.Transport ?? WowGuid128.Empty).To64());
                if (target.DstLocation != null)
                    payload.WriteVector3(target.DstLocation.Location);
                else
                    payload.WriteVector3(new Framework.GameMath.Vector3());
            }

            if (targetFlags.HasAnyFlag(SpellCastTargetFlags.String))
                payload.WriteCString(target.Name ?? string.Empty);
        }

        private void SendWotlkSellResponse(SellResponse sellResponse)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(sellResponse.VendorGUID.To64().GetLowValue());
            payload.WriteUInt64(sellResponse.ItemGUID.To64().GetLowValue());
            payload.WriteUInt8(sellResponse.Reason);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_SELL_RESPONSE), payload.GetData());
        }

        private void SendWotlkLootResponse(LootResponse loot)
        {
            ByteBuffer payload = new();

            // 3.3.5 SMSG_LOOT_RESPONSE is still the classic-era flat layout:
            // loot guid, loot type, then either a failure byte or the loot view.
            // The generic Hermes packet is a newer bit-packed/128-bit structure;
            // sending it made the client close the loot window immediately and
            // spam CMSG_LOOT_RELEASE.
            payload.WriteUInt64(loot.Owner.To64().GetLowValue());
            payload.WriteUInt8((byte)loot.AcquireReason);

            if (loot.AcquireReason == LootType.None)
            {
                payload.WriteUInt8((byte)loot.FailureReason);
                SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOOT_RESPONSE), payload.GetData());
                return;
            }

            payload.WriteUInt32(loot.Coins);

            int itemCount = Math.Min(loot.Items.Count, byte.MaxValue);
            payload.WriteUInt8((byte)itemCount);
            for (int i = 0; i < itemCount; ++i)
            {
                LootItemData item = loot.Items[i];
                uint itemId = item.Loot?.ItemID ?? 0;

                payload.WriteUInt8(item.LootListID);
                payload.WriteUInt32(itemId);
                payload.WriteUInt32(item.Quantity);
                payload.WriteUInt32(ResolveItemDisplayId(itemId));
                payload.WriteUInt32(0); // random suffix/seed: do not leak vanilla ids into Wrath
                payload.WriteUInt32(0); // random property id
                payload.WriteUInt8(ToWotlkLootSlotType(item.UIType));
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOOT_RESPONSE), payload.GetData());
        }

        private void SendWotlkLootReleaseResponse(LootReleaseResponse loot)
        {
            ByteBuffer payload = new();
            WowGuid128 guid = loot.LootObj != null && !loot.LootObj.IsEmpty() ? loot.LootObj : loot.Owner;
            payload.WriteUInt64(guid.To64().GetLowValue());
            payload.WriteUInt8(1);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOOT_RELEASE), payload.GetData());
        }

        private void SendWotlkLootRemoved(LootRemoved loot)
        {
            ByteBuffer payload = new();
            payload.WriteUInt8(loot.LootListID);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOOT_REMOVED), payload.GetData());
        }

        private void SendWotlkLootMoneyNotify(LootMoneyNotify loot)
        {
            ByteBuffer payload = new();
            uint money = loot.Money > uint.MaxValue ? uint.MaxValue : (uint)loot.Money;
            payload.WriteUInt32(money);
            payload.WriteUInt8(loot.SoleLooter ? (byte)1 : (byte)0);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOOT_MONEY_NOTIFY), payload.GetData());
        }

        private void SendWotlkLootClearMoney(CoinRemoved coinRemoved)
        {
            ByteBuffer payload = new();
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOOT_CLEAR_MONEY), payload.GetData());
        }

        private void SendWotlkItemPushResult(ItemPushResult item)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(item.PlayerGUID.To64().GetLowValue());

            bool receivedFromNpc = item.Pushed || item.DisplayText == ItemPushResult.DisplayType.Received;
            bool showInChat = item.DisplayText != ItemPushResult.DisplayType.Hidden;

            payload.WriteUInt32(receivedFromNpc ? 1u : 0u);
            payload.WriteUInt32(item.Created ? 1u : 0u);
            payload.WriteUInt32(showInChat ? 1u : 0u);
            payload.WriteUInt8(item.Slot);
            payload.WriteInt32(item.SlotInBag);
            payload.WriteUInt32(item.Item?.ItemID ?? 0);
            payload.WriteUInt32(item.Item?.RandomPropertiesSeed ?? 0);
            payload.WriteUInt32(item.Item?.RandomPropertiesID ?? 0);
            payload.WriteUInt32(item.Quantity);
            payload.WriteUInt32(item.QuantityInInventory);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_ITEM_PUSH_RESULT), payload.GetData());
        }

        private void SendWotlkSetFactionStanding(SetFactionStanding standing)
        {
            ByteBuffer payload = new();
            payload.WriteFloat(0.0f); // RAF reputation bonus
            payload.WriteUInt8(standing.ShowVisual ? (byte)1 : (byte)0);

            int count = Math.Min(standing.Factions.Count, ushort.MaxValue);
            payload.WriteUInt32((uint)count);
            for (int i = 0; i < count; ++i)
            {
                FactionStandingData faction = standing.Factions[i];
                payload.WriteUInt32((uint)faction.Index);
                payload.WriteUInt32((uint)faction.Standing);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_SET_FACTION_STANDING), payload.GetData());
        }

        private void SendWotlkLogXPGain(LogXPGain log)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(log.Victim?.To64().GetLowValue() ?? 0);
            payload.WriteUInt32(log.Original < 0 ? 0u : (uint)log.Original);
            payload.WriteUInt8((byte)log.Reason);

            if (log.Reason == PlayerLogXPReason.Kill)
            {
                payload.WriteUInt32(log.Amount < 0 ? 0u : (uint)log.Amount);
                payload.WriteFloat(log.GroupBonus);
            }

            payload.WriteUInt8(log.RAFBonus);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_LOG_XP_GAIN), payload.GetData());
        }

        private static byte ToWotlkLootSlotType(LootSlotTypeModern slotType)
        {
            return slotType switch
            {
                LootSlotTypeModern.RollOngoing => 1,
                LootSlotTypeModern.Master => 2,
                LootSlotTypeModern.Locked => 3,
                LootSlotTypeModern.Owner => 4,
                _ => 0
            };
        }

        private void SendWotlkVendorInventory(VendorInventory vendorInventory)
        {
            ByteBuffer payload = new();
            // 3.3.5 SMSG_VENDOR_INVENTORY starts with a raw 64-bit vendor GUID.
            // A packed GUID makes the following item count land at the wrong offset,
            // so the vendor frame never opens and the client keeps re-requesting it.
            payload.WriteUInt64(vendorInventory.VendorGUID.To64().GetLowValue());

            int itemCount = Math.Min(vendorInventory.Items.Count, byte.MaxValue);
            payload.WriteUInt8((byte)itemCount);
            if (itemCount == 0)
            {
                payload.WriteUInt8(vendorInventory.Reason);
            }
            else
            {
                for (int i = 0; i < itemCount; ++i)
                {
                    VendorItem item = vendorInventory.Items[i];
                    uint itemId = item.Item?.ItemID ?? 0;
                    uint displayId = ResolveItemDisplayId(itemId);
                    uint price = item.Price > uint.MaxValue ? uint.MaxValue : (uint)item.Price;

                    payload.WriteUInt32((uint)Math.Max(1, item.Slot));
                    payload.WriteUInt32(itemId);
                    payload.WriteUInt32(displayId);
                    payload.WriteInt32(item.Quantity);
                    payload.WriteUInt32(price);
                    payload.WriteUInt32((uint)Math.Max(0, item.Durability));
                    payload.WriteUInt32(item.StackCount);
                    payload.WriteUInt32((uint)Math.Max(0, item.ExtendedCostID));
                }
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_VENDOR_INVENTORY), payload.GetData());
        }

        private void SendWotlkQuestGiverStatus(QuestGiverStatusPkt questStatus)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(questStatus.QuestGiver.Guid.To64().GetLowValue());
            payload.WriteUInt8(ToWotlkQuestGiverStatusByte(questStatus.QuestGiver.Status));
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_STATUS), payload.GetData());
        }

        private void SendWotlkQuestGiverStatusMultiple(QuestGiverStatusMultiple questStatusMultiple)
        {
            ByteBuffer payload = new();
            int count = Math.Min(questStatusMultiple.QuestGivers.Count, ushort.MaxValue);
            payload.WriteUInt32((uint)count);
            for (int i = 0; i < count; ++i)
            {
                QuestGiverInfo info = questStatusMultiple.QuestGivers[i];
                payload.WriteUInt64(info.Guid.To64().GetLowValue());
                payload.WriteUInt8(ToWotlkQuestGiverStatusByte(info.Status));
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_STATUS_MULTIPLE), payload.GetData());
        }

        private void SendWotlkQuestGiverQuestListMessage(QuestGiverQuestListMessage questList)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(questList.QuestGiverGUID.To64().GetLowValue());
            payload.WriteCString(questList.Greeting ?? string.Empty);
            payload.WriteUInt32(questList.GreetEmoteDelay);
            payload.WriteUInt32(questList.GreetEmoteType);

            int count = Math.Min(questList.QuestOptions.Count, byte.MaxValue);
            payload.WriteUInt8((byte)count);
            for (int i = 0; i < count; ++i)
            {
                ClientGossipQuest quest = questList.QuestOptions[i];
                payload.WriteUInt32(quest.QuestID);
                payload.WriteUInt32((uint)Math.Max(0, quest.QuestType));
                payload.WriteInt32(quest.QuestLevel);
                payload.WriteUInt32(quest.QuestFlags);
                payload.WriteUInt8(quest.Repeatable ? (byte)1 : (byte)0);
                payload.WriteCString(quest.QuestTitle ?? string.Empty);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_QUEST_LIST_MESSAGE), payload.GetData());
        }

        private void SendWotlkQuestGiverQuestDetails(QuestGiverQuestDetails details)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(details.QuestGiverGUID.To64().GetLowValue());
            payload.WriteUInt64(details.InformUnit.To64().GetLowValue());
            payload.WriteUInt32(details.QuestID);
            payload.WriteCString(details.QuestTitle ?? string.Empty);
            payload.WriteCString(details.DescriptionText ?? string.Empty);
            payload.WriteCString(details.LogDescription ?? string.Empty);
            payload.WriteUInt8(details.AutoLaunched ? (byte)1 : (byte)0);
            payload.WriteUInt32(details.QuestFlags[0]);
            payload.WriteUInt32(details.SuggestedPartyMembers);
            payload.WriteUInt8(0); // IsFinished

            if ((details.QuestFlags[0] & (uint)QuestFlags.HiddenRewards) != 0)
            {
                payload.WriteUInt32(0);
                payload.WriteUInt32(0);
                payload.WriteUInt32(0);
                payload.WriteUInt32(0);
            }
            else
            {
                List<(uint ItemId, uint Count)> choiceRewards = new();
                if (details.Rewards.ChoiceItems != null)
                {
                    int maxChoices = details.Rewards.ChoiceItems.Length;
                    if (details.Rewards.ChoiceItemCount > 0)
                        maxChoices = Math.Min(maxChoices, (int)details.Rewards.ChoiceItemCount);

                    for (int i = 0; i < maxChoices; ++i)
                    {
                        QuestChoiceItem choice = details.Rewards.ChoiceItems[i];
                        if (choice == null || choice.Item == null || choice.Item.ItemID == 0 || choice.Quantity == 0)
                            continue;

                        choiceRewards.Add((choice.Item.ItemID, choice.Quantity));
                    }
                }

                List<(uint ItemId, uint Count)> fixedRewards = new();
                int fixedMax = Math.Min(details.Rewards.ItemID.Length, details.Rewards.ItemQty.Length);
                if (details.Rewards.ItemCount > 0)
                    fixedMax = Math.Min(fixedMax, (int)details.Rewards.ItemCount);
                for (int i = 0; i < fixedMax; ++i)
                {
                    uint itemId = details.Rewards.ItemID[i];
                    uint itemCount = details.Rewards.ItemQty[i];
                    if (itemId == 0 || itemCount == 0)
                        continue;

                    fixedRewards.Add((itemId, itemCount));
                }

                payload.WriteUInt32((uint)choiceRewards.Count);
                foreach (var reward in choiceRewards)
                {
                    payload.WriteUInt32(reward.ItemId);
                    payload.WriteUInt32(reward.Count);
                    payload.WriteUInt32(ResolveItemDisplayId(reward.ItemId));
                }

                payload.WriteUInt32((uint)fixedRewards.Count);
                foreach (var reward in fixedRewards)
                {
                    payload.WriteUInt32(reward.ItemId);
                    payload.WriteUInt32(reward.Count);
                    payload.WriteUInt32(ResolveItemDisplayId(reward.ItemId));
                }

                payload.WriteUInt32(details.Rewards.Money);
                payload.WriteUInt32(details.Rewards.XP);
            }

            payload.WriteUInt32(details.Rewards.Honor);
            payload.WriteFloat(0.0f); // honor multiplier
            payload.WriteUInt32(details.Rewards.SpellCompletionID);
            payload.WriteInt32(0); // cast reward spell id
            payload.WriteUInt32(details.Rewards.Title);
            payload.WriteUInt32(details.Rewards.NumSkillUps);
            payload.WriteUInt32(0); // arena points
            payload.WriteUInt32(0); // unknown

            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                payload.WriteUInt32(details.Rewards.FactionID[i]);
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                payload.WriteInt32(details.Rewards.FactionValue[i]);
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                payload.WriteInt32(details.Rewards.FactionOverride[i]);

            uint emoteCount = 0;
            for (int i = 0; i < details.DescEmotes.Length; ++i)
            {
                if (details.DescEmotes[i].Type != 0 || details.DescEmotes[i].Delay != 0)
                    ++emoteCount;
            }

            payload.WriteUInt32(emoteCount);
            for (int i = 0; i < details.DescEmotes.Length; ++i)
            {
                if (details.DescEmotes[i].Type == 0 && details.DescEmotes[i].Delay == 0)
                    continue;

                payload.WriteUInt32(details.DescEmotes[i].Type);
                payload.WriteUInt32(details.DescEmotes[i].Delay);
            }

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_QUEST_DETAILS), payload.GetData());
        }

        private void SendWotlkQuestGiverRequestItems(QuestGiverRequestItems requestItems)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(requestItems.QuestGiverGUID.To64().GetLowValue());
            payload.WriteUInt32(requestItems.QuestID);
            payload.WriteCString(requestItems.QuestTitle ?? string.Empty);
            payload.WriteCString(requestItems.CompletionText ?? string.Empty);
            payload.WriteUInt32(requestItems.CompEmoteDelay);
            payload.WriteUInt32(requestItems.CompEmoteType);
            payload.WriteUInt32(requestItems.AutoLaunched ? 1u : 0u); // close on cancel
            payload.WriteUInt32(requestItems.QuestFlags[0]);
            payload.WriteUInt32(requestItems.SuggestPartyMembers);
            payload.WriteUInt32((uint)Math.Max(0, requestItems.MoneyToGet));

            payload.WriteUInt32((uint)requestItems.Collect.Count);
            for (int i = 0; i < requestItems.Collect.Count; ++i)
            {
                QuestObjectiveCollect collect = requestItems.Collect[i];
                payload.WriteUInt32(collect.ObjectID);
                payload.WriteUInt32(collect.Amount);
                payload.WriteUInt32(ResolveItemDisplayId(collect.ObjectID));
            }

            bool canComplete = requestItems.StatusFlags == 223 || (requestItems.StatusFlags & 0x04) != 0;
            payload.WriteUInt32(canComplete ? 3u : 0u);
            payload.WriteUInt32(0x04);
            payload.WriteUInt32(0x08);
            payload.WriteUInt32(0x10);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_REQUEST_ITEMS), payload.GetData());
        }

        private void SendWotlkQuestGiverOfferRewardMessage(QuestGiverOfferRewardMessage offerReward)
        {
            ByteBuffer payload = new();
            payload.WriteUInt64(offerReward.QuestData.QuestGiverGUID.To64().GetLowValue());
            payload.WriteUInt32(offerReward.QuestData.QuestID);
            payload.WriteCString(offerReward.QuestTitle ?? string.Empty);
            payload.WriteCString(offerReward.RewardText ?? string.Empty);
            payload.WriteUInt8(offerReward.QuestData.AutoLaunched ? (byte)1 : (byte)0);
            payload.WriteUInt32(offerReward.QuestData.QuestFlags[0]);
            payload.WriteUInt32(offerReward.QuestData.SuggestedPartyMembers);

            payload.WriteUInt32((uint)offerReward.QuestData.Emotes.Count);
            for (int i = 0; i < offerReward.QuestData.Emotes.Count; ++i)
            {
                payload.WriteUInt32(offerReward.QuestData.Emotes[i].Delay);
                payload.WriteUInt32(offerReward.QuestData.Emotes[i].Type);
            }

            List<(uint ItemId, uint Count)> choiceRewards = new();
            if (offerReward.QuestData.Rewards.ChoiceItems != null)
            {
                int maxChoices = offerReward.QuestData.Rewards.ChoiceItems.Length;
                if (offerReward.QuestData.Rewards.ChoiceItemCount > 0)
                    maxChoices = Math.Min(maxChoices, (int)offerReward.QuestData.Rewards.ChoiceItemCount);

                for (int i = 0; i < maxChoices; ++i)
                {
                    QuestChoiceItem choice = offerReward.QuestData.Rewards.ChoiceItems[i];
                    if (choice == null || choice.Item == null || choice.Item.ItemID == 0 || choice.Quantity == 0)
                        continue;

                    choiceRewards.Add((choice.Item.ItemID, choice.Quantity));
                }
            }

            List<(uint ItemId, uint Count)> fixedRewards = new();
            int fixedMax = Math.Min(offerReward.QuestData.Rewards.ItemID.Length, offerReward.QuestData.Rewards.ItemQty.Length);
            if (offerReward.QuestData.Rewards.ItemCount > 0)
                fixedMax = Math.Min(fixedMax, (int)offerReward.QuestData.Rewards.ItemCount);
            for (int i = 0; i < fixedMax; ++i)
            {
                uint itemId = offerReward.QuestData.Rewards.ItemID[i];
                uint itemCount = offerReward.QuestData.Rewards.ItemQty[i];
                if (itemId == 0 || itemCount == 0)
                    continue;

                fixedRewards.Add((itemId, itemCount));
            }

            payload.WriteUInt32((uint)choiceRewards.Count);
            foreach (var reward in choiceRewards)
            {
                payload.WriteUInt32(reward.ItemId);
                payload.WriteUInt32(reward.Count);
                payload.WriteUInt32(ResolveItemDisplayId(reward.ItemId));
            }

            payload.WriteUInt32((uint)fixedRewards.Count);
            foreach (var reward in fixedRewards)
            {
                payload.WriteUInt32(reward.ItemId);
                payload.WriteUInt32(reward.Count);
                payload.WriteUInt32(ResolveItemDisplayId(reward.ItemId));
            }

            payload.WriteUInt32(offerReward.QuestData.Rewards.Money);
            payload.WriteUInt32(offerReward.QuestData.Rewards.XP);
            payload.WriteUInt32(offerReward.QuestData.Rewards.Honor);
            payload.WriteFloat(0.0f); // honor multiplier
            payload.WriteUInt32(0x08); // unused by 3.3.5 client
            payload.WriteUInt32(offerReward.QuestData.Rewards.SpellCompletionID);
            payload.WriteInt32(0); // cast reward spell id
            payload.WriteUInt32(offerReward.QuestData.Rewards.Title);
            payload.WriteUInt32(offerReward.QuestData.Rewards.NumSkillUps);
            payload.WriteUInt32(0); // arena points
            payload.WriteUInt32(0); // unknown

            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                payload.WriteUInt32(offerReward.QuestData.Rewards.FactionID[i]);
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                payload.WriteInt32(offerReward.QuestData.Rewards.FactionValue[i]);
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                payload.WriteInt32(offerReward.QuestData.Rewards.FactionOverride[i]);

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_OFFER_REWARD_MESSAGE), payload.GetData());
        }

        private void SendWotlkQuestGiverQuestComplete(QuestGiverQuestComplete questComplete)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(questComplete.QuestID);
            payload.WriteUInt32(questComplete.XPReward);

            uint moneyReward = questComplete.MoneyReward <= 0
                ? 0u
                : (questComplete.MoneyReward > uint.MaxValue ? uint.MaxValue : (uint)questComplete.MoneyReward);
            payload.WriteUInt32(moneyReward);
            payload.WriteUInt32(0); // honor
            payload.WriteUInt32(questComplete.NumSkillUpsReward); // bonus talents
            payload.WriteUInt32(0); // arena points

            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_QUEST_COMPLETE), payload.GetData());
        }

        private void SendWotlkQuestGiverQuestFailed(QuestGiverQuestFailed questFailed)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32(questFailed.QuestID);
            payload.WriteUInt32((uint)questFailed.Reason);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_QUEST_FAILED), payload.GetData());
        }

        private void SendWotlkQuestGiverInvalidQuest(QuestGiverInvalidQuest invalidQuest)
        {
            ByteBuffer payload = new();
            payload.WriteUInt32((uint)invalidQuest.Reason);
            SendWotlkRawPacket(ModernVersion.GetCurrentOpcode(Opcode.SMSG_QUEST_GIVER_INVALID_QUEST), payload.GetData());
        }

        private static byte ToWotlkQuestGiverStatusByte(QuestGiverStatusModern status)
        {
            if (status.HasAnyFlag(QuestGiverStatusModern.Reward | QuestGiverStatusModern.RewardLegendary | QuestGiverStatusModern.RewardJourney | QuestGiverStatusModern.RewardCovenantCalling))
                return (byte)QuestGiverStatusWotLK.Reward;
            if (status.HasAnyFlag(QuestGiverStatusModern.Reward2 | QuestGiverStatusModern.Reward2Legendary | QuestGiverStatusModern.Reward2Journey | QuestGiverStatusModern.Reward2CovenantCalling))
                return (byte)QuestGiverStatusWotLK.Reward2;
            if (status.HasAnyFlag(QuestGiverStatusModern.Available | QuestGiverStatusModern.AvailableLegendaryQuest | QuestGiverStatusModern.AvailableJourney | QuestGiverStatusModern.AvailableCovenantCalling))
                return (byte)QuestGiverStatusWotLK.Available;
            if (status.HasFlag(QuestGiverStatusModern.AvailableRep))
                return (byte)QuestGiverStatusWotLK.AvailableRep;
            if (status.HasFlag(QuestGiverStatusModern.RewardRep))
                return (byte)QuestGiverStatusWotLK.RewardRep;
            if (status.HasAnyFlag(QuestGiverStatusModern.Incomplete | QuestGiverStatusModern.IncompleteJourney | QuestGiverStatusModern.IncompleteCovenantCalling))
                return (byte)QuestGiverStatusWotLK.Incomplete;
            if (status.HasFlag(QuestGiverStatusModern.LowLevelAvailableRep))
                return (byte)QuestGiverStatusWotLK.LowLevelAvailableRep;
            if (status.HasFlag(QuestGiverStatusModern.LowLevelRewardRep))
                return (byte)QuestGiverStatusWotLK.LowLevelRewardRep;
            if (status.HasFlag(QuestGiverStatusModern.LowLevelAvailable))
                return (byte)QuestGiverStatusWotLK.LowLevelAvailable;
            if (status.HasFlag(QuestGiverStatusModern.Unavailable))
                return (byte)QuestGiverStatusWotLK.Unavailable;

            return (byte)QuestGiverStatusWotLK.None;
        }

        private static uint ResolveItemDisplayId(uint itemId)
        {
            if (itemId == 0)
                return 0;

            ItemTemplate itemTemplate = GameData.GetItemTemplate(itemId);
            if (itemTemplate != null && itemTemplate.DisplayID != 0)
                return itemTemplate.DisplayID;

            return GameData.GetItemDisplayId(itemId);
        }

        private static void WritePackedGuid64(ByteBuffer payload, ulong guid)
        {
            byte mask = 0;
            List<byte> packed = new(8);
            for (byte i = 0; i < 8; ++i)
            {
                byte value = (byte)((guid >> (i * 8)) & 0xFF);
                if (value != 0)
                {
                    mask |= (byte)(1 << i);
                    packed.Add(value);
                }
            }

            payload.WriteUInt8(mask);
            foreach (byte value in packed)
                payload.WriteUInt8(value);
        }

        private static bool IsWotlkMoveMessageOpcode(Opcode opcode)
        {
            switch (opcode)
            {
                case Opcode.CMSG_MOVE_CHANGE_TRANSPORT:
                case Opcode.CMSG_MOVE_DISMISS_VEHICLE:
                case Opcode.CMSG_MOVE_FALL_LAND:
                case Opcode.MSG_MOVE_FALL_LAND:
                case Opcode.CMSG_MOVE_FALL_RESET:
                case Opcode.CMSG_MOVE_HEARTBEAT:
                case Opcode.MSG_MOVE_HEARTBEAT:
                case Opcode.CMSG_MOVE_JUMP:
                case Opcode.MSG_MOVE_JUMP:
                case Opcode.CMSG_MOVE_REMOVE_MOVEMENT_FORCES:
                case Opcode.CMSG_MOVE_SET_FACING:
                case Opcode.MSG_MOVE_SET_FACING:
                case Opcode.CMSG_MOVE_SET_FACING_HEARTBEAT:
                case Opcode.CMSG_MOVE_SET_FLY:
                case Opcode.CMSG_MOVE_SET_PITCH:
                case Opcode.MSG_MOVE_SET_PITCH:
                case Opcode.CMSG_MOVE_SET_RUN_MODE:
                case Opcode.MSG_MOVE_SET_RUN_MODE:
                case Opcode.CMSG_MOVE_SET_WALK_MODE:
                case Opcode.MSG_MOVE_SET_WALK_MODE:
                case Opcode.CMSG_MOVE_START_ASCEND:
                case Opcode.MSG_MOVE_START_ASCEND:
                case Opcode.CMSG_MOVE_START_BACKWARD:
                case Opcode.MSG_MOVE_START_BACKWARD:
                case Opcode.CMSG_MOVE_START_DESCEND:
                case Opcode.MSG_MOVE_START_DESCEND:
                case Opcode.CMSG_MOVE_START_FORWARD:
                case Opcode.MSG_MOVE_START_FORWARD:
                case Opcode.CMSG_MOVE_START_PITCH_DOWN:
                case Opcode.MSG_MOVE_START_PITCH_DOWN:
                case Opcode.CMSG_MOVE_START_PITCH_UP:
                case Opcode.MSG_MOVE_START_PITCH_UP:
                case Opcode.CMSG_MOVE_START_SWIM:
                case Opcode.MSG_MOVE_START_SWIM:
                case Opcode.CMSG_MOVE_START_TURN_LEFT:
                case Opcode.MSG_MOVE_START_TURN_LEFT:
                case Opcode.CMSG_MOVE_START_TURN_RIGHT:
                case Opcode.MSG_MOVE_START_TURN_RIGHT:
                case Opcode.CMSG_MOVE_START_STRAFE_LEFT:
                case Opcode.MSG_MOVE_START_STRAFE_LEFT:
                case Opcode.CMSG_MOVE_START_STRAFE_RIGHT:
                case Opcode.MSG_MOVE_START_STRAFE_RIGHT:
                case Opcode.CMSG_MOVE_STOP:
                case Opcode.MSG_MOVE_STOP:
                case Opcode.CMSG_MOVE_STOP_ASCEND:
                case Opcode.MSG_MOVE_STOP_ASCEND:
                case Opcode.CMSG_MOVE_STOP_PITCH:
                case Opcode.MSG_MOVE_STOP_PITCH:
                case Opcode.CMSG_MOVE_STOP_STRAFE:
                case Opcode.MSG_MOVE_STOP_STRAFE:
                case Opcode.CMSG_MOVE_STOP_SWIM:
                case Opcode.MSG_MOVE_STOP_SWIM:
                case Opcode.CMSG_MOVE_STOP_TURN:
                case Opcode.MSG_MOVE_STOP_TURN:
                case Opcode.CMSG_MOVE_DOUBLE_JUMP:
                    return true;
                default:
                    return false;
            }
        }

        private void SendWotlkMoveSetFlag(MoveSetFlag moveSetFlag)
        {
            Opcode universalOpcode = moveSetFlag.GetUniversalOpcode();
            if (universalOpcode == Opcode.MSG_NULL_ACTION)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"[WotLK] Skipping MoveSetFlag with missing universal opcode.");
                return;
            }

            if (universalOpcode == Opcode.SMSG_MOVE_ROOT &&
                moveSetFlag.MoverGUID == GetSession()?.GameState?.CurrentPlayerGuid)
            {
                universalOpcode = Opcode.SMSG_MOVE_UNROOT;
                Log.Print(LogType.Debug, "[WotLK] Rewriting self SMSG_MOVE_ROOT to SMSG_MOVE_UNROOT.");
            }

            uint opcode = ModernVersion.GetCurrentOpcode(universalOpcode);
            if (opcode == 0)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"[WotLK] Skipping MoveSetFlag with missing opcode mapping: {universalOpcode}.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, moveSetFlag.MoverGUID.To64().GetLowValue());
            payload.WriteUInt32(moveSetFlag.MoveCounter);
            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkMoveSplineSetFlag(MoveSplineSetFlag moveSplineSetFlag)
        {
            Opcode universalOpcode = moveSplineSetFlag.GetUniversalOpcode();
            if (universalOpcode == Opcode.MSG_NULL_ACTION)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"[WotLK] Skipping MoveSplineSetFlag with missing universal opcode.");
                return;
            }

            uint opcode = ModernVersion.GetCurrentOpcode(universalOpcode);
            if (opcode == 0)
            {
                Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"[WotLK] Skipping MoveSplineSetFlag with missing opcode mapping: {universalOpcode}.");
                return;
            }

            ByteBuffer payload = new();
            WritePackedGuid64(payload, moveSplineSetFlag.MoverGUID.To64().GetLowValue());
            SendWotlkRawPacket(opcode, payload.GetData());
        }

        private void SendWotlkRawPacket(uint opcode, byte[] payload)
        {
            if (!IsOpen())
                return;

            if (opcode == 0)
            {
                Log.Print(LogType.Warn, $"[WotLK] Refusing to send packet with opcode 0 and payload size {payload?.Length ?? 0}.");
                return;
            }

            _sendMutex.WaitOne();
            try
            {
                int packetSize = payload.Length + sizeof(ushort);
                byte[] header;
                if (packetSize > 0x7FFF)
                {
                    header = new byte[5];
                    header[0] = (byte)(0x80 | ((packetSize >> 16) & 0x7F));
                    header[1] = (byte)((packetSize >> 8) & 0xFF);
                    header[2] = (byte)(packetSize & 0xFF);
                    header[3] = (byte)(opcode & 0xFF);
                    header[4] = (byte)((opcode >> 8) & 0xFF);
                }
                else
                {
                    header = new byte[4];
                    header[0] = (byte)((packetSize >> 8) & 0xFF);
                    header[1] = (byte)(packetSize & 0xFF);
                    header[2] = (byte)(opcode & 0xFF);
                    header[3] = (byte)((opcode >> 8) & 0xFF);
                }

                if (_wotlkHeaderCryptInitialized)
                    _wotlkHeaderCrypt.Encrypt(header, header.Length);

                ByteBuffer buffer = new();
                buffer.WriteBytes(header);
                buffer.WriteBytes(payload);
                AsyncWrite(buffer.GetData());
            }
            finally
            {
                _sendMutex.ReleaseMutex();
            }
        }
    }
    public class WotlkAuthSocket : SocketBase
    {
private const byte CmdAuthLogonChallenge = 0x00;
        private const byte CmdAuthLogonProof = 0x01;
        private const byte CmdRealmList = 0x10;

        private const byte AuthLogonSuccess = 0x00;
        private const byte AuthLogonFailedUnknown = 0x04;
        private const byte AuthLogonFailedVersionInvalid = 0x09;

        // cMaNGOS SRP6 prime in little-endian byte order
        private static readonly byte[] SrpPrimeN = ParseBigEndianHexToLittleEndian("894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7");
        private static readonly BigInteger SrpN = SrpPrimeN.ToBigInteger();
        private static readonly BigInteger SrpG = new BigInteger(7);

        private static readonly byte[] VersionChallenge =
        {
            0xBA, 0xA3, 0x1E, 0x99, 0xA0, 0x0B, 0x21, 0x57,
            0xFC, 0x37, 0x3F, 0xB3, 0x69, 0xCD, 0xD2, 0xF1
        };

        private readonly List<byte> _incoming = new();
        private AuthStage _stage = AuthStage.ExpectChallenge;

        private string _username = string.Empty;
        private string _locale = string.Empty;
        private ushort _build;

        private byte[] _salt = Array.Empty<byte>();
        private BigInteger _verifier;
        private BigInteger _privateB;
        private BigInteger _publicB;
        private byte[] _sessionKeyBytes = Array.Empty<byte>();
        private BigInteger _sessionKey;
        private byte[] _m2 = Array.Empty<byte>();
        private WotlkFrontendSession? _session;
        private WotlkAuthAccountData? _authAccount;

        public WotlkAuthSocket(Socket socket) : base(socket)
        {
        }

        public override void Accept()
        {
            Log.Print(LogType.Network, $"WotLK auth client connected from {GetRemoteIpAddress()}");
            AsyncRead();
        }

        public override void ReadHandler(SocketAsyncEventArgs args)
        {
            for (int i = 0; i < args.BytesTransferred; i++)
                _incoming.Add(args.Buffer[i]);

            if (!ProcessIncoming())
            {
                CloseSocket();
                return;
            }

            AsyncRead();
        }

        private bool ProcessIncoming()
        {
            while (_incoming.Count > 0)
            {
                switch (_stage)
                {
                    case AuthStage.ExpectChallenge:
                    {
                        ParseResult result = TryHandleChallenge();
                        if (result == ParseResult.NeedMoreData)
                            return true;
                        if (result == ParseResult.Error)
                            return false;
                        break;
                    }
                    case AuthStage.ExpectProof:
                    {
                        ParseResult result = TryHandleProof();
                        if (result == ParseResult.NeedMoreData)
                            return true;
                        if (result == ParseResult.Error)
                            return false;
                        break;
                    }
                    case AuthStage.Authed:
                    {
                        ParseResult result = TryHandleRealmList();
                        if (result == ParseResult.NeedMoreData)
                            return true;
                        if (result == ParseResult.Error)
                            return false;
                        break;
                    }
                    default:
                        return false;
                }
            }

            return true;
        }

        private ParseResult TryHandleChallenge()
        {
            if (_incoming.Count < 4)
                return ParseResult.NeedMoreData;

            if (_incoming[0] != CmdAuthLogonChallenge)
            {
                Log.Print(LogType.Error, $"WotlkAuthSocket: Expected logon challenge, got 0x{_incoming[0]:X2}");
                return ParseResult.Error;
            }

            ushort bodySize = BitConverter.ToUInt16(_incoming.Skip(2).Take(2).ToArray(), 0);
            int packetSize = 4 + bodySize;
            if (_incoming.Count < packetSize)
                return ParseResult.NeedMoreData;

            byte[] body = _incoming.Skip(4).Take(bodySize).ToArray();
            _incoming.RemoveRange(0, packetSize);

            if (body.Length < 30)
            {
                Log.Print(LogType.Error, "WotlkAuthSocket: logon challenge body too small");
                return ParseResult.Error;
            }

            _build = BitConverter.ToUInt16(body, 7);
            if (_build != (ushort)Settings.ClientBuild)
            {
                Log.Print(LogType.Error, $"WotlkAuthSocket: client build mismatch. Client={_build}, expected={(ushort)Settings.ClientBuild}");
                SendChallengeFailure(AuthLogonFailedVersionInvalid);
                return ParseResult.Error;
            }

            int usernameLength = body[29];
            if (body.Length < 30 + usernameLength)
            {
                Log.Print(LogType.Error, "WotlkAuthSocket: malformed username length");
                return ParseResult.Error;
            }

            _username = Encoding.ASCII.GetString(body, 30, usernameLength).Trim().ToUpperInvariant();
            _locale = new string(Encoding.ASCII.GetString(body, 17, 4).Reverse().ToArray());

            _authAccount = WotlkAuthDataProvider.TryLoadAccount(_username);
            if (_authAccount != null)
            {
                InitializeSrpForAccount(_authAccount);
            }
            else
            {
                if (!TryResolveFrontendPassword(_username, out string frontendPassword))
                {
                    Log.Print(LogType.Error, $"WotlkAuthSocket: no auth source for '{_username}'. Configure GruntUsername/GruntPassword or WotlkAuthDbConnectionString.");
                    SendChallengeFailure(AuthLogonFailedUnknown);
                    return ParseResult.Error;
                }

                InitializeSrpForCredentials(_username, frontendPassword);
            }
            SendChallengeResponse();
            _stage = AuthStage.ExpectProof;
            return ParseResult.Handled;
        }

        private ParseResult TryHandleProof()
        {
            const int proofPacketSize = 1 + 32 + 20 + 20 + 1 + 1;
            if (_incoming.Count < proofPacketSize)
                return ParseResult.NeedMoreData;

            if (_incoming[0] != CmdAuthLogonProof)
            {
                Log.Print(LogType.Error, $"WotlkAuthSocket: Expected logon proof, got 0x{_incoming[0]:X2}");
                return ParseResult.Error;
            }

            byte[] aBytes = _incoming.Skip(1).Take(32).ToArray();
            byte[] clientM1 = _incoming.Skip(33).Take(20).ToArray();
            _incoming.RemoveRange(0, proofPacketSize);

            if (!TryValidateClientProof(_username, aBytes, clientM1))
            {
                SendProofFailure();
                Log.Print(LogType.Error, $"WotlkAuthSocket: invalid proof for account '{_username}'");
                return ParseResult.Error;
            }

            _session = InitializeBackendSession();
            if (_session == null)
            {
                SendProofFailure();
                Log.Print(LogType.Error, $"WotlkAuthSocket: backend auth failed for account '{_username}'");
                return ParseResult.Error;
            }

            _session.ClientSessionKey = _sessionKeyBytes.ToArray();
            WotlkFrontendSessionStore.Upsert(_session);

            SendProofResponse();
            _stage = AuthStage.Authed;
            Log.Print(LogType.Network, $"WotlkAuthSocket: authenticated '{_username}' for build {_build}");
            return ParseResult.Handled;
        }

        private ParseResult TryHandleRealmList()
        {
            if (_incoming.Count < 5)
                return ParseResult.NeedMoreData;

            if (_incoming[0] != CmdRealmList)
            {
                Log.Print(LogType.Error, $"WotlkAuthSocket: Expected realm list request, got 0x{_incoming[0]:X2}");
                return ParseResult.Error;
            }

            _incoming.RemoveRange(0, 5); // cmd + 4 bytes unused
            SendRealmList();
            return ParseResult.Handled;
        }

        private void InitializeSrpForAccount(WotlkAuthAccountData account)
        {
            _salt = account.Salt;
            _verifier = account.Verifier.ToBigInteger();

            _privateB = Array.Empty<byte>().GenerateRandomKey(19).ToBigInteger();
            BigInteger gPowB = SrpG.ModPow(_privateB, SrpN);
            _publicB = ((_verifier * 3) + gPowB) % SrpN;
        }

        private void InitializeSrpForCredentials(string usernameUpper, string password)
        {
            _salt = Array.Empty<byte>().GenerateRandomKey(32);
            string authString = $"{usernameUpper}:{password}";
            byte[] userPassHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(Encoding.ASCII.GetBytes(authString.ToUpperInvariant()));
            byte[] xHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(_salt, userPassHash);
            BigInteger x = xHash.ToBigInteger();
            _verifier = SrpG.ModPow(x, SrpN);

            _privateB = Array.Empty<byte>().GenerateRandomKey(19).ToBigInteger();
            BigInteger gPowB = SrpG.ModPow(_privateB, SrpN);
            _publicB = ((_verifier * 3) + gPowB) % SrpN;
        }

        private bool TryValidateClientProof(string usernameUpper, byte[] aBytes, byte[] clientM1)
        {
            BigInteger A = aBytes.ToBigInteger();
            if (A.IsZero || (A % SrpN).IsZero)
                return false;

            BigInteger u = Framework.Cryptography.HashAlgorithm.SHA1.Hash(A.ToCleanByteArray(), _publicB.ToCleanByteArray()).ToBigInteger();
            BigInteger S = (A * _verifier.ModPow(u, SrpN)).ModPow(_privateB, SrpN);

            _sessionKeyBytes = DeriveSessionKeyBytes(S);
            _sessionKey = _sessionKeyBytes.ToBigInteger();

            byte[] nHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(SrpN.ToCleanByteArray());
            byte[] gHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(SrpG.ToCleanByteArray());
            byte[] ngXor = new byte[20];
            for (int i = 0; i < 20; i++)
                ngXor[i] = (byte)(nHash[i] ^ gHash[i]);

            byte[] userHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(Encoding.ASCII.GetBytes(usernameUpper));
            byte[] expectedM1 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(
                ngXor,
                userHash,
                _salt,
                A.ToCleanByteArray(),
                _publicB.ToCleanByteArray(),
                _sessionKey.ToCleanByteArray());

            if (!expectedM1.Compare(clientM1))
                return false;

            _m2 = Framework.Cryptography.HashAlgorithm.SHA1.Hash(A.ToCleanByteArray(), expectedM1, _sessionKeyBytes);
            return true;
        }

        private static byte[] DeriveSessionKeyBytes(BigInteger sessionKey)
        {
            byte[] sData = sessionKey.ToCleanByteArray();
            if (sData.Length < 32)
            {
                byte[] padded = new byte[32];
                Buffer.BlockCopy(sData, 0, padded, 32 - sData.Length, sData.Length);
                sData = padded;
            }

            byte[] keyData = new byte[40];
            byte[] temp = new byte[16];

            for (int i = 0; i < 16; i++)
                temp[i] = sData[i * 2];
            byte[] evenHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(temp);
            for (int i = 0; i < 20; i++)
                keyData[i * 2] = evenHash[i];

            for (int i = 0; i < 16; i++)
                temp[i] = sData[i * 2 + 1];
            byte[] oddHash = Framework.Cryptography.HashAlgorithm.SHA1.Hash(temp);
            for (int i = 0; i < 20; i++)
                keyData[i * 2 + 1] = oddHash[i];

            return keyData;
        }

        private WotlkFrontendSession InitializeBackendSession()
        {
            if (!TryResolveBackendCredentials(out string backendUsername, out string backendPassword))
            {
                Log.Print(LogType.Error, "WotlkAuthSocket: missing GruntUsername/GruntPassword for backend auth.");
                return null;
            }

            var globalSession = new GlobalSessionData
            {
                Username = backendUsername,
                Locale = string.IsNullOrWhiteSpace(_locale) ? "enUS" : _locale,
                OS = "Win",
                Build = (uint)Settings.ClientBuild,
                AccountInfo = new BNetServer.Networking.AccountInfo(backendUsername)
            };
            globalSession.GameAccountInfo = globalSession.AccountInfo.GameAccounts[1];
            globalSession.AccountMetaDataMgr = new AccountMetaDataManager(backendUsername);
            globalSession.SessionKey = _sessionKeyBytes.ToArray();
            globalSession.AuthClient = new HermesProxy.Auth.AuthClient(globalSession);
            var authResult = globalSession.AuthClient.ConnectToAuthServer(backendUsername, backendPassword, globalSession.Locale);
            if (authResult != HermesProxy.Auth.AuthResult.SUCCESS)
            {
                Log.Print(LogType.Error, $"WotlkAuthSocket: backend auth failed for '{backendUsername}' ({authResult}).");
                return null;
            }

            globalSession.AuthClient.WaitOrRequestRealmList();
            Realm? selectedRealm = globalSession.RealmManager.GetRealms()
                .FirstOrDefault(r => !r.Flags.HasFlag(Framework.Constants.RealmFlags.Offline));
            if (selectedRealm == null)
            {
                Log.Print(LogType.Error, $"WotlkAuthSocket: backend realm list empty/offline for '{backendUsername}'.");
                return null;
            }

            globalSession.RealmId = selectedRealm.Id;
            globalSession.AccountDataMgr = new AccountDataManager(globalSession.Username, selectedRealm.Name);

            return new WotlkFrontendSession
            {
                Username = _username,
                ClientSessionKey = _sessionKeyBytes.ToArray(),
                GlobalSession = globalSession
            };
        }

        private static bool TryResolveBackendCredentials(out string username, out string password)
        {
            username = (Settings.GruntUsername ?? string.Empty).Trim().ToUpperInvariant();
            password = Settings.GruntPassword ?? string.Empty;
            return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password);
        }

        private static bool TryResolveFrontendPassword(string frontendUsernameUpper, out string password)
        {
            password = Settings.GruntPassword ?? string.Empty;
            string configuredUser = (Settings.GruntUsername ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(configuredUser) || string.IsNullOrEmpty(password))
                return false;

            return string.Equals(configuredUser, frontendUsernameUpper, StringComparison.OrdinalIgnoreCase);
        }

        private void SendChallengeResponse()
        {
            byte[] bBytes = PadLittleEndian(_publicB.ToCleanByteArray(), 32);
            byte[] nBytes = PadLittleEndian(SrpN.ToCleanByteArray(), 32);

            ByteBuffer response = new();
            response.WriteUInt8(CmdAuthLogonChallenge);
            response.WriteUInt8(0x00);
            response.WriteUInt8(AuthLogonSuccess);
            response.WriteBytes(bBytes);
            response.WriteUInt8(1);
            response.WriteUInt8(7);
            response.WriteUInt8(32);
            response.WriteBytes(nBytes);
            response.WriteBytes(_salt);
            response.WriteBytes(VersionChallenge);
            response.WriteUInt8(0); // no additional security flags

            AsyncWrite(response.GetData());
        }

        private void SendProofResponse()
        {
            ByteBuffer response = new();
            response.WriteUInt8(CmdAuthLogonProof);
            response.WriteUInt8(AuthLogonSuccess);
            response.WriteBytes(_m2);
            response.WriteUInt32(0); // account flags
            response.WriteUInt32(0); // survey id
            response.WriteUInt16(0); // login flags
            AsyncWrite(response.GetData());
        }

        private void SendRealmList()
        {
            if (_session == null)
            {
                SendProofFailure();
                return;
            }

            var realm = _session.GlobalSession.Realm;
            if (realm == null)
            {
                SendProofFailure();
                return;
            }

            string address = $"{Settings.ExternalAddress}:{Settings.WotlkWorldPort}";

            Framework.Constants.RealmFlags flags = realm.Flags;
            flags &= ~Framework.Constants.RealmFlags.Offline;
            flags |= Framework.Constants.RealmFlags.SpecifyBuild;

            ByteBuffer payload = new();
            payload.WriteUInt32(0);
            payload.WriteUInt16(1);
            payload.WriteUInt8(realm.Type);
            payload.WriteUInt8(0); // unlocked
            payload.WriteUInt8((byte)flags);
            payload.WriteCString(realm.Name);
            payload.WriteCString(address);
            payload.WriteFloat(realm.PopulationLevel);
            payload.WriteUInt8(realm.CharacterCount);
            payload.WriteUInt8(realm.Timezone);
            payload.WriteUInt8((byte)realm.Id.Index);
            payload.WriteUInt8(3);
            payload.WriteUInt8(3);
            payload.WriteUInt8(5);
            payload.WriteUInt16((ushort)Settings.ClientBuild);
            payload.WriteUInt16(0x0010);

            ByteBuffer response = new();
            response.WriteUInt8(CmdRealmList);
            response.WriteUInt16((ushort)payload.GetSize());
            response.WriteBytes(payload.GetData());
            AsyncWrite(response.GetData());
        }

        private void SendChallengeFailure(byte result)
        {
            ByteBuffer response = new();
            response.WriteUInt8(CmdAuthLogonChallenge);
            response.WriteUInt8(0x00);
            response.WriteUInt8(result);
            AsyncWrite(response.GetData());
        }

        private void SendProofFailure()
        {
            ByteBuffer response = new();
            response.WriteUInt8(CmdAuthLogonProof);
            response.WriteUInt8(AuthLogonFailedUnknown);
            response.WriteUInt8(0);
            response.WriteUInt8(0);
            AsyncWrite(response.GetData());
        }

        private static byte[] PadLittleEndian(byte[] value, int targetLength)
        {
            if (value.Length >= targetLength)
                return value.Take(targetLength).ToArray();

            byte[] result = new byte[targetLength];
            Buffer.BlockCopy(value, 0, result, 0, value.Length);
            return result;
        }

        private static byte[] ParseBigEndianHexToLittleEndian(string hex)
        {
            byte[] value = hex.ParseAsByteArray();
            Array.Reverse(value);
            return value;
        }

        private enum AuthStage
        {
            ExpectChallenge,
            ExpectProof,
            Authed
        }

        private enum ParseResult
        {
            NeedMoreData,
            Handled,
            Error
        }
    }

    internal sealed class WotlkAuthAccountData
    {
        public int Id { get; set; }
        public string UsernameUpper { get; set; } = string.Empty;
        public byte[] Verifier { get; set; } = Array.Empty<byte>();
        public byte[] Salt { get; set; } = Array.Empty<byte>();
    }

    internal static class WotlkAuthDataProvider
    {
        public static WotlkAuthAccountData? TryLoadAccount(string username)
        {
            _ = username;
            return null;
        }

        public static Realm? TryLoadRealm()
        {
            return null;
        }
    }

    public sealed class WotlkFrontendSession
    {
public string Username { get; set; } = string.Empty;
        public byte[] ClientSessionKey { get; set; } = Array.Empty<byte>();
        public GlobalSessionData GlobalSession { get; set; } = null!;
        public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;
    }
    public static class WotlkFrontendSessionStore
    {
private static readonly ConcurrentDictionary<string, WotlkFrontendSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

        public static void Upsert(WotlkFrontendSession session)
        {
            session.LastUpdatedUtc = DateTime.UtcNow;
            _sessions[session.Username] = session;
            CleanupExpired();
        }

        public static bool TryGet(string username, out WotlkFrontendSession session)
        {
            if (_sessions.TryGetValue(username, out session))
            {
                if (DateTime.UtcNow - session.LastUpdatedUtc <= SessionTtl)
                    return true;

                _sessions.TryRemove(username, out _);
            }

            session = null;
            return false;
        }

        private static void CleanupExpired()
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _sessions)
            {
                if (now - entry.Value.LastUpdatedUtc > SessionTtl)
                    _sessions.TryRemove(entry.Key, out _);
            }
        }
    }
}
