using Framework.Cryptography;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HermesProxy.World.Client
{
    public interface LegacyWorldCrypt
    {
        public void Initialize(byte[] sessionKey);
        public void Decrypt(byte[] data, int len);
        public void Encrypt(byte[] data, int len);

    }
    public class VanillaWorldCrypt : LegacyWorldCrypt
    {
        public const uint CRYPTED_SEND_LEN = 6;
        public const uint CRYPTED_RECV_LEN = 4;

        public void Initialize(byte[] sessionKey)
        {
            SetKey(sessionKey);
            m_send_i = m_send_j = m_recv_i = m_recv_j = 0;
            m_isInitialized = true;
        }

        public void Decrypt(byte[] data, int len)
        {
            if (len < CRYPTED_RECV_LEN)
                return;

            for (byte t = 0; t < CRYPTED_RECV_LEN; t++)
            {
                m_recv_i %= (byte)m_key.Count();
                byte x = (byte)((data[t] - m_recv_j) ^ m_key[m_recv_i]);
                ++m_recv_i;
                m_recv_j = data[t];
                data[t] = x;
            }
        }

        public void Encrypt(byte[] data, int len)
        {
            if (!m_isInitialized)
                return;

            if (len < CRYPTED_SEND_LEN)
                return;

            for (byte t = 0; t < CRYPTED_SEND_LEN; t++)
            {
                m_send_i %= (byte)m_key.Count();
                byte x = (byte)((data[t] ^ m_key[m_send_i]) + m_send_j);
                ++m_send_i;
                data[t] = m_send_j = x;
            }
        }

        public void SetKey(byte[] key)
        {
            System.Diagnostics.Trace.Assert(key.Length != 0);

            m_key = key.ToArray();
        }

        byte[] m_key;
        byte m_send_i, m_send_j, m_recv_i, m_recv_j;
        bool m_isInitialized;
    }

    public class TbcWorldCrypt : LegacyWorldCrypt
    {
        public const uint CRYPTED_SEND_LEN = 6;
        public const uint CRYPTED_RECV_LEN = 4;

        public void Initialize(byte[] sessionKey)
        {
            byte[] recvSeed = new byte[16] { 0x38, 0xA7, 0x83, 0x15, 0xF8, 0x92, 0x25, 0x30, 0x71, 0x98, 0x67, 0xB1, 0x8C, 0x4, 0xE2, 0xAA };
            HmacHash recvHash = new HmacHash(recvSeed);
            recvHash.Finish(sessionKey, sessionKey.Count());
            m_key = recvHash.Digest.ToArray();

            m_send_i = m_send_j = m_recv_i = m_recv_j = 0;
            m_isInitialized = true;
        }

        public void Decrypt(byte[] data, int len)
        {
            if (len < CRYPTED_RECV_LEN)
                return;

            for (byte t = 0; t < CRYPTED_RECV_LEN; t++)
            {
                m_recv_i %= (byte)m_key.Count();
                byte x = (byte)((data[t] - m_recv_j) ^ m_key[m_recv_i]);
                ++m_recv_i;
                m_recv_j = data[t];
                data[t] = x;
            }
        }

        public void Encrypt(byte[] data, int len)
        {
            if (!m_isInitialized)
                return;

            if (len < CRYPTED_SEND_LEN)
                return;

            for (byte t = 0; t < CRYPTED_SEND_LEN; t++)
            {
                m_send_i %= (byte)m_key.Count();
                byte x = (byte)((data[t] ^ m_key[m_send_i]) + m_send_j);
                ++m_send_i;
                data[t] = m_send_j = x;
            }
        }

        byte[] m_key;
        byte m_send_i, m_send_j, m_recv_i, m_recv_j;
        bool m_isInitialized;
    }

    public class WotlkWorldCrypt : LegacyWorldCrypt
    {
        // TrinityCore WorldPacketCrypt.cpp
        static readonly byte[] ServerEncryptionKey =
        {
            0xCC, 0x98, 0xAE, 0x04, 0xE8, 0x97, 0xEA, 0xCA,
            0x12, 0xDD, 0xC0, 0x93, 0x42, 0x91, 0x53, 0x57
        };

        static readonly byte[] ServerDecryptionKey =
        {
            0xC2, 0xB3, 0x72, 0x3C, 0xC6, 0xAE, 0xD9, 0xB5,
            0x34, 0x3C, 0x53, 0xEE, 0x2F, 0x43, 0x67, 0xCE
        };

        public void Initialize(byte[] sessionKey)
        {
            if (sessionKey == null || sessionKey.Length == 0)
                throw new ArgumentException("Session key must not be empty", nameof(sessionKey));

            byte[] encryptKey = ComputeHmac(ServerEncryptionKey, sessionKey);
            byte[] decryptKey = ComputeHmac(ServerDecryptionKey, sessionKey);

            _serverEncrypt = new SARC4();
            _clientDecrypt = new SARC4();
            _serverEncrypt.PrepareKey(encryptKey);
            _clientDecrypt.PrepareKey(decryptKey);

            // ARC4-drop1024 sync
            byte[] drop = new byte[1024];
            _serverEncrypt.ProcessBuffer(drop, drop.Length);
            _clientDecrypt.ProcessBuffer(drop, drop.Length);

            _isInitialized = true;
        }

        public void Decrypt(byte[] data, int len)
        {
            if (!_isInitialized || data == null || len <= 0)
                return;

            _clientDecrypt.ProcessBuffer(data, len);
        }

        public void Encrypt(byte[] data, int len)
        {
            if (!_isInitialized || data == null || len <= 0)
                return;

            _serverEncrypt.ProcessBuffer(data, len);
        }

        private static byte[] ComputeHmac(byte[] seed, byte[] sessionKey)
        {
            HmacHash hash = new HmacHash(seed);
            hash.Finish(sessionKey, sessionKey.Length);
            return hash.Digest;
        }

        bool _isInitialized;
        SARC4 _serverEncrypt;
        SARC4 _clientDecrypt;
    }
}
