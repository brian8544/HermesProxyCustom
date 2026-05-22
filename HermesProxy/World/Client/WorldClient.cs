using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using HermesProxy.Enums;
using System.Numerics;
using Framework.Constants;
using Framework.Cryptography;
using Framework;
using Framework.IO;
using Framework.Logging;
using HermesProxy.World.Enums;
using System.Reflection;
using System.Threading.Tasks;
using Framework.Networking;
using HermesProxy.World.Server;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        Socket _clientSocket;
        bool? _isSuccessful;
        uint _queuePosition;
        string _username;
        Realm _realm;
        LegacyWorldCrypt _worldCrypt;
        Dictionary<Opcode, Action<WorldPacket>> _packetHandlers;
        GlobalSessionData _globalSession;
        System.Threading.Mutex _sendMutex = new System.Threading.Mutex();
        bool _expectingBackendDisconnectAfterLogout;
        static readonly TimeSpan BackendWorldAuthTimeout = TimeSpan.FromSeconds(15);
        const int BackendWorldAuthPollDelayMs = 10;
        readonly System.Threading.ManualResetEventSlim _authResultEvent = new(false);

        // packet order is not always the same as new client, sometimes we need to delay packet until another one
        Dictionary<Opcode, List<WorldPacket>> _delayedPacketsToServer;
        Dictionary<Opcode, List<ServerPacket>> _delayedPacketsToClient;

        public WorldClient()
        {
            InitializePacketHandlers();
        }

        public GlobalSessionData GetSession()
        {
            return _globalSession;
        }

        public GlobalSessionData Session => _globalSession;

        public bool ConnectToWorldServer(Realm realm, GlobalSessionData globalSession)
        {
            _worldCrypt = null;
            _realm = realm;
            _globalSession = globalSession;
            _username = globalSession.Username;
            _isSuccessful = null;
            _authResultEvent.Reset();
            _delayedPacketsToServer = new Dictionary<Opcode, List<WorldPacket>>();
            _delayedPacketsToClient = new Dictionary<Opcode, List<ServerPacket>>();
            _expectingBackendDisconnectAfterLogout = false;

            Log.Print(LogType.Network, "Connecting to world server...");
            try
            {
                var ip = NetworkUtils.ResolveOrDirectIPv4(realm.ExternalAddress);
                Log.Print(LogType.Network, $"World Server address {realm.ExternalAddress}:{realm.Port} resolved as {ip}:{realm.Port}");
                _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                // Connect to the specified host.
                var endPoint = new IPEndPoint(ip, realm.Port);
                _clientSocket.BeginConnect(endPoint, ConnectCallback, null);
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Error, $"Socket Error: {ex.Message}");
                SetAuthResult(false);
            }

            if (!_authResultEvent.Wait(BackendWorldAuthTimeout))
            {
                Log.Print(LogType.Error, $"WorldClient.ConnectToWorldServer(): timed out waiting for backend auth response after {BackendWorldAuthTimeout.TotalSeconds:0}s.");
                SetAuthResult(false);
                Disconnect();
            }

            return _isSuccessful == true;
        }

        private void SetAuthResult(bool value)
        {
            _isSuccessful = value;
            _authResultEvent.Set();
        }

        public bool IsAuthenticated()
        {
            return _isSuccessful == true;
        }

        private void InitializeEncryption(byte[] sessionKey)
        {
            switch (Settings.ServerBuild)
            {
                case ClientVersionBuild.V1_12_1_5875:
                case ClientVersionBuild.V1_12_2_6005:
                case ClientVersionBuild.V1_12_3_6141:
                    _worldCrypt = new VanillaWorldCrypt();
                    break;
                case ClientVersionBuild.V2_4_3_8606:
                    _worldCrypt = new TbcWorldCrypt();
                    break;
                case ClientVersionBuild.V3_3_5a_12340:
                    _worldCrypt = new WotlkWorldCrypt();
                    break;
            }

            if (_worldCrypt != null)
                _worldCrypt.Initialize(sessionKey);
        }

        public void Disconnect()
        {
            Socket socket = _clientSocket;
            _clientSocket = null;

            if (socket != null)
            {
                try
                {
                    if (socket.Connected)
                        socket.Shutdown(SocketShutdown.Both);
                }
                catch
                {
                    // The peer may already have disappeared. Closing below is enough
                    // to make the legacy world server release the character.
                }

                try
                {
                    socket.Close();
                }
                catch
                {
                    // Ignore close races during disconnect cleanup.
                }
            }

            if (GetSession()?.WorldClient == this)
                GetSession().WorldClient = null;
        }

        public void RequestLogoutBeforeDisconnect(string reason)
        {
            Socket socket = _clientSocket;
            if (socket == null || _isSuccessful != true)
                return;

            try
            {
                Log.Print(LogType.Network, $"Requesting legacy logout before backend socket close ({reason}).");
                SendPacket(new WorldPacket(Opcode.CMSG_LOGOUT_REQUEST));
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Debug, $"Unable to send legacy logout request during disconnect cleanup: {ex.Message}");
            }
        }

        public void ForceLogoutBeforeDisconnect(string reason)
        {
            Socket socket = _clientSocket;
            if (socket == null || _isSuccessful != true)
                return;

            try
            {
                Log.Print(LogType.Network, $"Forcing legacy logout before backend socket close ({reason}).");

                // Single-user WotLK frontend mode: if the frontend disappears, do not
                // keep the vanilla world session around waiting for normal logout state.
                // Send both logout stages, then close the backend socket so MaNGOS frees
                // the character instead of leaving it stuck as already logged in.
                SendPacket(new WorldPacket(Opcode.CMSG_LOGOUT_REQUEST));
                SendPacket(new WorldPacket(Opcode.CMSG_PLAYER_LOGOUT));
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Debug, $"Unable to send forced legacy logout packets during disconnect cleanup: {ex.Message}");
            }
        }

        public bool IsConnected()
        {
            return _clientSocket != null && _clientSocket.Connected;
        }

        public uint GetQueuePosition()
        {
            return _queuePosition;
        }

        private void ConnectCallback(IAsyncResult AR)
        {
            try
            {
                Log.Print(LogType.Network, "Connection established!");

                _clientSocket.EndConnect(AR);
                _clientSocket.ReceiveBufferSize = 65535;

                Task.Run(ReceiveLoop);
            }
            catch (Exception ex)
            {
                Log.Print(LogType.Error, $"Connect Error: {ex.Message}");
                if (_isSuccessful == null)
                    SetAuthResult(false);
            }
        }

        private async Task<bool> ReceiveBufferFully(ArraySegment<byte> bufferToFill)
        {
            int alreadyReceived = 0;
            while (alreadyReceived < bufferToFill.Count)
            {
                Socket socket = _clientSocket;
                if (socket == null)
                    return false;

                var tmpArrayBuffer = new ArraySegment<byte>(bufferToFill.Array!, alreadyReceived + bufferToFill.Offset, bufferToFill.Count - alreadyReceived);
                int receive = await socket.ReceiveAsync(tmpArrayBuffer, SocketFlags.None);
                if (receive == 0)
                    return false;
                alreadyReceived += receive;
            }

            return true;
        }

        private async Task ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    byte[] headerBuffer = new byte[LegacyServerPacketHeader.StructSize];
                    if (!await ReceiveBufferFully(headerBuffer))
                    {
                        Log.PrintNet(LogType.Error, LogNetDir.S2P, "Socket Closed By GameWorldServer (header)");
                        if (_isSuccessful == null)
                            SetAuthResult(false);
                        else if (GetSession().WorldClient == this)
                        {
                            if (ShouldKeepFrontendAliveAfterBackendDisconnect())
                                HandleExpectedBackendDisconnectAfterLogout();
                            else
                                GetSession().OnDisconnect();
                        }
                        return;
                    }

                    if (_worldCrypt != null)
                        _worldCrypt.Decrypt(headerBuffer, LegacyServerPacketHeader.StructSize);

                    LegacyServerPacketHeader header = new LegacyServerPacketHeader();
                    header.Read(headerBuffer);
                    ushort packetSize = header.Size;

                    if (packetSize != 0)
                    {
                        byte[] buffer = new byte[packetSize];

                        // copy the opcode into the new buffer
                        buffer[0] = headerBuffer[2];
                        buffer[1] = headerBuffer[3];

                        if (!await ReceiveBufferFully(new ArraySegment<byte>(buffer, 2, buffer.Length - 2)))
                        {
                            Log.PrintNet(LogType.Error, LogNetDir.S2P, "Socket Closed By GameWorldServer (payload)");
                            if (_isSuccessful == null)
                                SetAuthResult(false);
                            else if (GetSession().WorldClient == this)
                            {
                                if (ShouldKeepFrontendAliveAfterBackendDisconnect())
                                    HandleExpectedBackendDisconnectAfterLogout();
                                else
                                    GetSession().OnDisconnect();
                            }
                            return;
                        }

                        WorldPacket packet = new WorldPacket(buffer);
                        packet.SetReceiveTime(Environment.TickCount);
                        HandlePacket(packet);
                    }
                }
            }
            catch (SocketException se) when (
                se.SocketErrorCode == SocketError.Shutdown ||
                se.SocketErrorCode == SocketError.OperationAborted ||
                se.SocketErrorCode == SocketError.ConnectionReset ||
                se.SocketErrorCode == SocketError.ConnectionAborted ||
                se.SocketErrorCode == SocketError.NotConnected)
            {
                Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Backend socket closed ({se.SocketErrorCode}).");
                if (_isSuccessful == null)
                    SetAuthResult(false);
                else
                {
                    Disconnect();
                    if (ShouldKeepFrontendAliveAfterBackendDisconnect())
                        HandleExpectedBackendDisconnectAfterLogout();
                    else
                        GetSession().OnDisconnect();
                }
            }
            catch(Exception e)
            {
                Log.PrintNet(LogType.Error, LogNetDir.S2P, $"Packet Read Error: {e.Message}{Environment.NewLine}{e.StackTrace}");
                if (_isSuccessful == null)
                    SetAuthResult(false);
                else
                {
                    Disconnect();
                    if (ShouldKeepFrontendAliveAfterBackendDisconnect())
                        HandleExpectedBackendDisconnectAfterLogout();
                    else
                        GetSession().OnDisconnect();
                }
            }
        }

        // C P>S: Sends data to world server
        private void SendPacket(WorldPacket packet)
        {
            _sendMutex.WaitOne();
            try
            {
                ByteBuffer buffer = new ByteBuffer();
                LegacyClientPacketHeader header = new LegacyClientPacketHeader();

                header.Size = (ushort)(packet.GetSize() + sizeof(uint)); // size includes the opcode
                header.Opcode = packet.GetOpcode();
                header.Write(buffer);

                Log.PrintNet(LogType.Debug, LogNetDir.P2S, $"Sending opcode {LegacyVersion.GetUniversalOpcode(header.Opcode)} ({header.Opcode}) with size {header.Size}.");

                byte[] headerArray = buffer.GetData();
                if (_worldCrypt != null)
                    _worldCrypt.Encrypt(headerArray, LegacyClientPacketHeader.StructSize);
                buffer.Clear();
                buffer.WriteBytes(headerArray);

                buffer.WriteBytes(packet.GetData(), packet.GetSize());

                Socket socket = _clientSocket;
                if (socket == null)
                    throw new SocketException((int)SocketError.NotConnected);

                socket.Send(buffer.GetData(), SocketFlags.None);
            }
            catch (Exception ex)
            {
                Log.PrintNet(LogType.Error, LogNetDir.P2S, $"Packet Write Error: {ex.Message}");
                if (_isSuccessful == null)
                    SetAuthResult(false);
            }
            _sendMutex.ReleaseMutex();
        }

        public void SendPacketToClient(ServerPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
        {
            Opcode opcode = packet.GetUniversalOpcode();
            if (delayUntilOpcode != Opcode.MSG_NULL_ACTION)
            {
                if (_delayedPacketsToClient.ContainsKey(delayUntilOpcode))
                    _delayedPacketsToClient[delayUntilOpcode].Add(packet);
                else
                {
                    List<ServerPacket> packets = new List<ServerPacket>();
                    packets.Add(packet);
                    _delayedPacketsToClient.Add(delayUntilOpcode, packets);
                }
                return;
            }

            SendPacketToClientDirect(packet);
            SendDelayedPacketsToClientOnOpcode(opcode);
        }

        private void SendPacketToClientDirect(ServerPacket packet)
        {
            var pendingPackets = GetSession().GameState.PendingUninstancedPackets;
            if (packet.GetConnection() == ConnectionType.Realm)
            {
                GetSession().RealmSocket.SendPacket(packet);
            }
            else
            {
                if (GetSession().InstanceSocket == null &&
                   !GetSession().GameState.IsConnectedToInstance)
                {
                    lock (pendingPackets)
                    {
                        if (GetSession().InstanceSocket == null &&
                            !GetSession().GameState.IsConnectedToInstance)
                        {
                            pendingPackets.Enqueue(packet);
                            Log.PrintNet(LogType.Warn, LogNetDir.P2C, $"Can't send opcode {packet.GetUniversalOpcode()} ({packet.GetOpcode()}) before entering world! Queue");
                            return;
                        }
                    }
                }

                // block these packets until connected to instance
                while (GetSession().InstanceSocket == null)
                {
                    Log.PrintNet(LogType.Network, LogNetDir.P2C, $"Waiting to send {packet.GetUniversalOpcode()} ({packet.GetOpcode()}).");
                    System.Threading.Thread.Sleep(200);
                }

                var socket = GetSession().InstanceSocket;
                if (pendingPackets.Count > 0)
                {
                    lock (pendingPackets)
                    {
                        while (pendingPackets.TryDequeue(out var oldPacket))
                        {
                            socket.SendPacket(oldPacket);
                        }
                    }
                }

                socket.SendPacket(packet);
            }
        }

        public void SendPacketToServer(WorldPacket packet, Opcode delayUntilOpcode = Opcode.MSG_NULL_ACTION)
        {
            Opcode opcode = packet.GetUniversalOpcode(false);
            if (delayUntilOpcode != Opcode.MSG_NULL_ACTION)
            {
                if (_delayedPacketsToServer.ContainsKey(delayUntilOpcode))
                    _delayedPacketsToServer[delayUntilOpcode].Add(packet);
                else
                {
                    List<WorldPacket> packets = new List<WorldPacket>();
                    packets.Add(packet);
                    _delayedPacketsToServer.Add(delayUntilOpcode, packets);
                }
                return;
            }

            SendPacket(packet);
            SendDelayedPacketsToServerOnOpcode(opcode);
        }

        private void SendDelayedPacketsToServerOnOpcode(Opcode opcode)
        {
            if (_delayedPacketsToServer.ContainsKey(opcode))
            {
                List<WorldPacket> packets = _delayedPacketsToServer[opcode];
                for (int i = packets.Count - 1; i >= 0; i--)
                {
                    SendPacket(packets[i]);
                    packets.RemoveAt(i);
                }
            }
        }

        private void SendDelayedPacketsToClientOnOpcode(Opcode opcode)
        {
            if (_delayedPacketsToClient.ContainsKey(opcode))
            {
                List<ServerPacket> packets = _delayedPacketsToClient[opcode];
                for (int i = packets.Count - 1; i >= 0; i--)
                {
                    SendPacketToClientDirect(packets[i]);
                    packets.RemoveAt(i);
                }
            }
        }

        private void HandlePacket(WorldPacket packet)
        {
            Opcode universalOpcode = packet.GetUniversalOpcode(false);
            // Suppress extremely chatty movement spline updates in debug logs.
            if (universalOpcode != Opcode.SMSG_ON_MONSTER_MOVE)
                Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Received opcode {universalOpcode} ({packet.GetOpcode()}).");

            switch (universalOpcode)
            {
                case Opcode.SMSG_AUTH_CHALLENGE:
                    HandleAuthChallenge(packet);
                    break;
                case Opcode.SMSG_AUTH_RESPONSE:
                    HandleAuthResponse(packet);
                    break;
                case Opcode.SMSG_ADDON_INFO:
                    break; // don't need to handle
                default:
                    if (IsWotlkFrontendClient() && packet.GetOpcode() == 0x021E)
                    {
                        if (IsCurrentPlayerAtLegacyMaxLevel())
                            break;

                        if (TryForwardLegacyPayloadToWotlkClient(packet, Opcode.SMSG_SET_REST_START))
                            break;
                    }

                    if (_packetHandlers.ContainsKey(universalOpcode))
                    {
                        _packetHandlers[universalOpcode](packet);
                    }
                    else
                    {
                        if (IsWotlkFrontendClient() && ShouldDropUnhandledRawWotlkOpcode(universalOpcode))
                        {
                            Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Dropped unhandled opcode {universalOpcode} ({packet.GetOpcode()}).");
                            break;
                        }

                        if (IsWotlkFrontendClient() && TryForwardLegacyPayloadToWotlkClient(packet))
                        {
                            Log.PrintNet(LogType.Debug, LogNetDir.S2P, $"Raw-forwarded unhandled opcode {universalOpcode} ({packet.GetOpcode()}).");
                        }
                        else
                        {
                            Log.PrintNet(LogType.Warn, LogNetDir.S2P, $"No handler for opcode {universalOpcode} ({packet.GetOpcode()}) (Got unknown packet from WorldServer)");
                            if (_isSuccessful == null)
                                SetAuthResult(false);
                        }
                    }
                    break;
            }

            SendDelayedPacketsToServerOnOpcode(universalOpcode);
        }

        private void HandleAuthChallenge(WorldPacket packet)
        {
            if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
            {
                uint one = packet.ReadUInt32();
            }

            uint seed = packet.ReadUInt32();

            if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
            {
                BigInteger seed1 = packet.ReadBytes(16).ToBigInteger();
                BigInteger seed2 = packet.ReadBytes(16).ToBigInteger();
            }

            var rand = System.Security.Cryptography.RandomNumberGenerator.Create();
            byte[] bytes = new byte[4];
            rand.GetBytes(bytes);
            BigInteger ourSeed = bytes.ToBigInteger();

            SendAuthResponse((uint)ourSeed, seed);
        }

        public void SendAuthResponse(uint clientSeed, uint serverSeed)
        {
            uint zero = 0;
            byte[] sessionKey = GetSession().AuthClient != null
                ? GetSession().AuthClient.GetSessionKey()
                : GetSession().SessionKey;
            if (sessionKey == null || sessionKey.Length == 0)
            {
                Log.Print(LogType.Error, "WorldClient.SendAuthResponse(): missing session key.");
                SetAuthResult(false);
                return;
            }

            byte[] authResponse = HashAlgorithm.SHA1.Hash
            (
                Encoding.ASCII.GetBytes(_username.ToUpper()),
                BitConverter.GetBytes(zero),
                BitConverter.GetBytes(clientSeed),
                BitConverter.GetBytes(serverSeed),
                sessionKey
            );

            WorldPacket packet = new WorldPacket(Opcode.CMSG_AUTH_SESSION);
            packet.WriteUInt32((uint)Settings.ServerBuild);
            packet.WriteUInt32(_realm.Id.Index);
            packet.WriteBytes(_username.ToUpper().ToCString());

            if (Settings.ServerBuild >= ClientVersionBuild.V3_0_2_9056)
                packet.WriteUInt32(zero); // LoginServerType

            packet.WriteUInt32(clientSeed);

            if (Settings.ServerBuild >= ClientVersionBuild.V3_3_5a_12340)
            {
                packet.WriteUInt32(_realm.Id.Region);
                packet.WriteUInt32(_realm.Id.Site);
                packet.WriteUInt32(_realm.Id.Index);
            }

            if (Settings.ServerBuild >= ClientVersionBuild.V3_2_0_10192)
                packet.WriteUInt64(zero); // DosResponse

            packet.WriteBytes(authResponse);

            // packet.WriteUInt32(zero); // length of addon data
            byte[] addonBytes = new byte[] { 208, 1, 0, 0, 120, 156, 117, 207, 61, 14, 194, 48, 12, 5, 224, 114, 14, 184, 12, 97, 64, 149, 154, 133, 150, 25, 153, 196, 173, 172, 38, 78, 21, 82, 126, 58, 113, 66, 206, 68, 81, 133, 24, 98, 188, 126, 126, 79, 182, 114, 52, 77, 16, 237, 105, 59, 154, 68, 129, 143, 101, 177, 242, 183, 77, 85, 204, 163, 190, 166, 32, 37, 135, 45, 161, 179, 154, 152, 60, 12, 210, 18, 177, 37, 238, 230, 130, 87, 102, 187, 224, 207, 144, 170, 208, 9, 185, 197, 26, 188, 39, 9, 35, 180, 73, 188, 105, 175, 235, 49, 94, 241, 33, 227, 72, 206, 42, 224, 94, 212, 146, 47, 3, 154, 79, 237, 58, 183, 132, 190, 14, 166, 199, 180, 252, 146, 167, 53, 152, 24, 102, 121, 102, 114, 0, 178, 51, 196, 12, 26, 112, 200, 242, 27, 77, 4, 139, 117, 79, 206, 253, 99, 98, 140, 178, 145, 71, 13, 12, 29, 198, 159, 190, 1, 43, 0, 141, 195 };
            packet.WriteBytes(addonBytes);

            SendPacket(packet);

            InitializeEncryption(sessionKey);
        }

        private void HandleAuthResponse(WorldPacket packet)
        {
            AuthResult result = (AuthResult)packet.ReadUInt8();

            if (_isSuccessful == null)
            {
                uint billingTimeRemaining = packet.ReadUInt32();
                byte billingFlags = packet.ReadUInt8();
                uint billingTimeRested = packet.ReadUInt32();

                if (Settings.ServerBuild >= ClientVersionBuild.V2_0_1_6180)
                {
                    byte expansion = packet.ReadUInt8();
                }
            }

            if (result == AuthResult.AUTH_OK)
            {
                Log.Print(LogType.Network, "Authentication succeeded!");
                if (_queuePosition != 0 && GetSession().RealmSocket != null)
                {
                    _queuePosition = 0;
                    GetSession().RealmSocket.SendAuthWaitQue(_queuePosition);
                }
                SetAuthResult(true);
            }
            else if (result == AuthResult.AUTH_WAIT_QUEUE)
            {
                _queuePosition = packet.ReadUInt32();
                Log.Print(LogType.Network, $"Position in queue is {_queuePosition}.");
                if (_isSuccessful != null && GetSession().RealmSocket != null)
                    GetSession().RealmSocket.SendAuthWaitQue(_queuePosition);
                SetAuthResult(true);
            }
            else
            {
                Log.Print(LogType.Network, "Authentication failed!");
                SetAuthResult(false);
            }
        }

        public void SendPing(uint ping, uint latency)
        {
            if (!IsConnected() || _isSuccessful == false)
                return;

            WorldPacket packet = new WorldPacket(Opcode.CMSG_PING);
            packet.WriteUInt32(ping);
            packet.WriteUInt32(latency);
            SendPacket(packet);
        }

        public void InitializePacketHandlers()
        {
            _packetHandlers = new();

            foreach (var methodInfo in typeof(WorldClient).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
            {
                foreach (var msgAttr in methodInfo.GetCustomAttributes<PacketHandlerAttribute>())
                {
                    if (msgAttr == null)
                        continue;

                    if (msgAttr.Opcode == Opcode.MSG_NULL_ACTION)
                        continue;

                    if (_packetHandlers.ContainsKey(msgAttr.Opcode))
                    {
                        Log.Print(LogType.Error, $"Tried to override OpcodeHandler of {_packetHandlers[msgAttr.Opcode]} with {methodInfo.Name} (Opcode {msgAttr.Opcode})");
                        continue;
                    }

                    var parameters = methodInfo.GetParameters();
                    if (parameters.Length == 0)
                    {
                        Log.Print(LogType.Error, $"Method: {methodInfo.Name} Has no parameters");
                        continue;
                    }

                    if (parameters[0].ParameterType != typeof(WorldPacket))
                    {
                        Log.Print(LogType.Error, $"Method: {methodInfo.Name} has wrong BaseType");
                        continue;
                    }

                    var del = (Action<WorldPacket>)Delegate.CreateDelegate(typeof(Action<WorldPacket>), this, methodInfo);

                    _packetHandlers[msgAttr.Opcode] = del;
                }
            }

            if (Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
            {
                Opcode legacyRest = LegacyVersion.GetUniversalOpcode(542);
                Opcode legacyMonsterMove = LegacyVersion.GetUniversalOpcode(221);

                Log.Print(LogType.Debug, $"Legacy opcode 542 maps to {legacyRest}; handler registered: {_packetHandlers.ContainsKey(legacyRest)}");
                Log.Print(LogType.Debug, $"Legacy opcode 221 maps to {legacyMonsterMove}; handler registered: {_packetHandlers.ContainsKey(legacyMonsterMove)}");
            }
        }
    }    public partial class WorldClient
    {
private static readonly HashSet<Opcode> WotlkRawForwardDenyList = new()
        {
            // Optional/auxiliary responses that have version-fragile payloads between 1.12 and 3.3.5a.
            // Dropping these in WotLK-frontend mode is safer than blind passthrough.
            Opcode.SMSG_EXPECTED_SPAM_RECORDS,
            Opcode.SMSG_GM_TICKET_GET_TICKET,
            Opcode.SMSG_GM_TICKET_GET_TICKET_RESPONSE,
            // Cinematic/movie triggers and spell visual casts are frequent crash points while
            // update-object conversion is still being stabilized for 3.3.5a.
            Opcode.SMSG_TRIGGER_CINEMATIC,
            Opcode.SMSG_TRIGGER_MOVIE,
            Opcode.SMSG_SPELL_START,
            Opcode.SMSG_SPELL_GO,
        };

        private bool IsWotlkFrontendClient()
        {
            return Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340;
        }

        private bool ShouldKeepFrontendAliveAfterBackendDisconnect()
        {
            return IsWotlkFrontendClient() && _expectingBackendDisconnectAfterLogout;
        }

        private void HandleExpectedBackendDisconnectAfterLogout()
        {
            _expectingBackendDisconnectAfterLogout = false;
            if (GetSession().WorldClient == this)
                GetSession().WorldClient = null;

            if (GetSession().GameState != null)
                GetSession().GameState.IsConnectedToInstance = false;
        }

        private bool IsCurrentPlayerGuid(WowGuid128 guid)
        {
            WowGuid128 current = GetSession().GameState.CurrentPlayerGuid;
            if (guid.IsEmpty() || current.IsEmpty())
                return false;

            return guid.To64().GetLowValue() == current.To64().GetLowValue();
        }

        private bool IsCurrentPlayerAtLegacyMaxLevel()
        {
            if (_globalSession?.GameState == null)
                return false;

            uint maxLevel = LegacyVersion.GetMaxLevel();
            if (maxLevel == 0)
                return false;

            GameSessionData gameState = _globalSession.GameState;
            WowGuid128 currentPlayer = gameState.CurrentPlayerGuid;

            if (currentPlayer != WowGuid128.Empty)
            {
                int cachedUpdateLevel = gameState.GetLegacyFieldValueInt32(currentPlayer, UnitField.UNIT_FIELD_LEVEL);
                if (cachedUpdateLevel > 0)
                    return cachedUpdateLevel >= maxLevel;

                if (gameState.CachedPlayers.TryGetValue(currentPlayer, out PlayerCache playerCache) &&
                    playerCache.Level > 0)
                    return playerCache.Level >= maxLevel;
            }

            if (gameState.CurrentPlayerInfo != null && gameState.CurrentPlayerInfo.Level > 0)
                return gameState.CurrentPlayerInfo.Level >= maxLevel;

            return false;
        }

        private static bool ShouldDropUnhandledRawWotlkOpcode(Opcode opcode)
        {
            return WotlkRawForwardDenyList.Contains(opcode);
        }

        private bool TryForwardLegacyPayloadToWotlkClient(WorldPacket legacyPacket, Opcode forcedOpcode = Opcode.MSG_NULL_ACTION, byte[] payloadOverride = null)
        {
            if (!IsWotlkFrontendClient())
                return false;

            Opcode opcode = forcedOpcode == Opcode.MSG_NULL_ACTION
                ? legacyPacket.GetUniversalOpcode(false)
                : forcedOpcode;

            if (opcode == Opcode.MSG_NULL_ACTION || ModernVersion.GetCurrentOpcode(opcode) == 0)
                return false;

            byte[] payload = payloadOverride ?? ExtractLegacyPayload(legacyPacket);
            SendPacketToClient(new RawServerPacket(opcode, ConnectionType.Instance, payload));
            return true;
        }

        private static byte[] ExtractLegacyPayload(WorldPacket legacyPacket)
        {
            byte[] source = legacyPacket.GetData();
            if (source == null || source.Length == 0)
                return Array.Empty<byte>();

            int payloadOffset = 0;
            if (source.Length >= sizeof(ushort) &&
                BitConverter.ToUInt16(source, 0) == legacyPacket.GetOpcode())
            {
                payloadOffset = sizeof(ushort);
            }

            if (source.Length <= payloadOffset)
                return Array.Empty<byte>();

            int payloadSize = source.Length - payloadOffset;
            byte[] payload = new byte[payloadSize];
            Buffer.BlockCopy(source, payloadOffset, payload, 0, payloadSize);
            return payload;
        }

        private static byte[] PatchLoginSetTimeSpeedPayloadForWotlk(byte[] legacyPayload)
        {
            if (legacyPayload == null || legacyPayload.Length != sizeof(uint) + sizeof(float))
                return legacyPayload ?? Array.Empty<byte>();

            byte[] patched = new byte[sizeof(uint) + sizeof(float) + sizeof(uint)];
            Buffer.BlockCopy(legacyPayload, 0, patched, 0, legacyPayload.Length);
            // Wrath adds one extra u32 field after timescale; vanilla doesn't provide it.
            // Keep it zero-initialized.
            return patched;
        }
    }
}
