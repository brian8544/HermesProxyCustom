using Framework.Constants;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        [PacketHandler(Opcode.CMSG_ATTACK_SWING)]
        void HandleAttackSwing(AttackSwing attack)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_ATTACK_SWING);
            packet.WriteGuid(attack.Victim.To64());
            SendPacketToServer(packet);

            // 3.3.5 frontend can miss attack-start visuals when bridging to 1.12.
            // Emit local start immediately; backend SMSG_ATTACK_START/STOP will still reconcile.
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                SAttackStart localStart = new();
                localStart.Attacker = GetSession().GameState.CurrentPlayerGuid;
                localStart.Victim = attack.Victim;
                SendPacket(localStart);
            }
        }
        [PacketHandler(Opcode.CMSG_ATTACK_STOP)]
        void HandleAttackSwing(AttackStop attack)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_ATTACK_STOP);
            SendPacketToServer(packet);

            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                SAttackStop localStop = new();
                localStop.Attacker = GetSession().GameState.CurrentPlayerGuid;
                localStop.Victim = WowGuid128.Empty;
                localStop.NowDead = false;
                SendPacket(localStop);
            }
        }
        [PacketHandler(Opcode.CMSG_SET_SHEATHED)]
        void HandleSetSheathed(SetSheathed sheath)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_SHEATHED);
            packet.WriteInt32(sheath.SheathState);
            SendPacketToServer(packet);
        }
    }
}
