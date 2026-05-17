using Framework.Constants;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Server
{
    public partial class WorldSocket
    {
        // Handlers for CMSG opcodes coming from the modern client
        private bool IsWaitingForWotlkSameMapTeleport(GameSessionData gameState)
        {
            return Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                Framework.Settings.ServerBuild == ClientVersionBuild.V1_12_1_5875 &&
                (gameState.IsWaitingForWotlkMovementTeleportAck || gameState.PendingLegacyTeleportCounter.HasValue);
        }

        private void CompletePendingWotlkSameMapTeleportFromClient(string reason, uint clientMoveTime)
        {
            var gameState = GetSession().GameState;
            if (!IsWaitingForWotlkSameMapTeleport(gameState))
                return;

            WowGuid128 ackGuid = gameState.PendingWotlkMovementTeleportGuid != WowGuid128.Empty
                ? gameState.PendingWotlkMovementTeleportGuid
                : gameState.CurrentPlayerGuid;
            uint ackTime = gameState.PendingWotlkMovementTeleportMoveTime != 0
                ? gameState.PendingWotlkMovementTeleportMoveTime
                : clientMoveTime;
            uint counter = gameState.PendingLegacyTeleportCounter ?? 0;

            WorldPacket ack = new WorldPacket(Opcode.MSG_MOVE_TELEPORT_ACK);
            ack.WriteGuid(ackGuid.To64());
            ack.WriteUInt32(counter);
            ack.WriteUInt32(ackTime);

            Log.Print(LogType.Debug,
                $"[WotLK] translating client zone/movement confirmation to vanilla ACK: reason={reason}, guid={ackGuid.To64().GetLowValue():X}, counter={counter}, time={ackTime}.");

            gameState.IsWaitingForWotlkMovementTeleportAck = false;
            gameState.PendingWotlkMovementTeleportGuid = WowGuid128.Empty;
            gameState.PendingWotlkMovementTeleportMoveTime = 0;
            gameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position();
            gameState.PendingWotlkMovementTeleportStartTick = 0;
            gameState.PendingLegacyTeleportCounter = null;

            // Same-map movement teleports do not send a new self create block.
            // Mark the player object as present before destination updates arrive,
            // otherwise those updates get buffered forever as "pre-player-create".
            gameState.PlayerObjectSent = true;

            SendPacketToServer(ack);
            FlushPendingWotlkWorldPortUpdates(gameState);
        }

        private bool IsClientMovementNearPendingWotlkTeleport(MovementInfo moveInfo, out float distanceSq)
        {
            var gameState = GetSession().GameState;
            float dx = moveInfo.Position.X - gameState.PendingWotlkMovementTeleportDestination.X;
            float dy = moveInfo.Position.Y - gameState.PendingWotlkMovementTeleportDestination.Y;
            float dz = moveInfo.Position.Z - gameState.PendingWotlkMovementTeleportDestination.Z;
            distanceSq = dx * dx + dy * dy + dz * dz;

            // Large tolerance: the client may snap/fall slightly after MSG_MOVE_TELEPORT,
            // but movement still needs to be clearly from the destination, not stale
            // movement from the source zone.
            const float WotlkTeleportAcceptDistance = 500.0f;
            return distanceSq <= WotlkTeleportAcceptDistance * WotlkTeleportAcceptDistance;
        }

        [PacketHandler(Opcode.CMSG_MOVE_CHANGE_TRANSPORT)]
        [PacketHandler(Opcode.CMSG_MOVE_DISMISS_VEHICLE)]
        [PacketHandler(Opcode.CMSG_MOVE_FALL_LAND)]
        [PacketHandler(Opcode.MSG_MOVE_FALL_LAND)]
        [PacketHandler(Opcode.CMSG_MOVE_FALL_RESET)]
        [PacketHandler(Opcode.CMSG_MOVE_HEARTBEAT)]
        [PacketHandler(Opcode.MSG_MOVE_HEARTBEAT)]
        [PacketHandler(Opcode.CMSG_MOVE_JUMP)]
        [PacketHandler(Opcode.MSG_MOVE_JUMP)]
        [PacketHandler(Opcode.CMSG_MOVE_REMOVE_MOVEMENT_FORCES)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_FACING)]
        [PacketHandler(Opcode.MSG_MOVE_SET_FACING)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_FACING_HEARTBEAT)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_FLY)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_PITCH)]
        [PacketHandler(Opcode.MSG_MOVE_SET_PITCH)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_RUN_MODE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_MODE)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_WALK_MODE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_WALK_MODE)]
        [PacketHandler(Opcode.CMSG_MOVE_START_ASCEND)]
        [PacketHandler(Opcode.MSG_MOVE_START_ASCEND)]
        [PacketHandler(Opcode.CMSG_MOVE_START_BACKWARD)]
        [PacketHandler(Opcode.MSG_MOVE_START_BACKWARD)]
        [PacketHandler(Opcode.CMSG_MOVE_START_DESCEND)]
        [PacketHandler(Opcode.MSG_MOVE_START_DESCEND)]
        [PacketHandler(Opcode.CMSG_MOVE_START_FORWARD)]
        [PacketHandler(Opcode.MSG_MOVE_START_FORWARD)]
        [PacketHandler(Opcode.CMSG_MOVE_START_PITCH_DOWN)]
        [PacketHandler(Opcode.MSG_MOVE_START_PITCH_DOWN)]
        [PacketHandler(Opcode.CMSG_MOVE_START_PITCH_UP)]
        [PacketHandler(Opcode.MSG_MOVE_START_PITCH_UP)]
        [PacketHandler(Opcode.CMSG_MOVE_START_SWIM)]
        [PacketHandler(Opcode.MSG_MOVE_START_SWIM)]
        [PacketHandler(Opcode.CMSG_MOVE_START_TURN_LEFT)]
        [PacketHandler(Opcode.MSG_MOVE_START_TURN_LEFT)]
        [PacketHandler(Opcode.CMSG_MOVE_START_TURN_RIGHT)]
        [PacketHandler(Opcode.MSG_MOVE_START_TURN_RIGHT)]
        [PacketHandler(Opcode.CMSG_MOVE_START_STRAFE_LEFT)]
        [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_LEFT)]
        [PacketHandler(Opcode.CMSG_MOVE_START_STRAFE_RIGHT)]
        [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_RIGHT)]
        [PacketHandler(Opcode.CMSG_MOVE_STOP)]
        [PacketHandler(Opcode.MSG_MOVE_STOP)]
        [PacketHandler(Opcode.CMSG_MOVE_STOP_ASCEND)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_ASCEND)]
        [PacketHandler(Opcode.CMSG_MOVE_STOP_PITCH)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_PITCH)]
        [PacketHandler(Opcode.CMSG_MOVE_STOP_STRAFE)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_STRAFE)]
        [PacketHandler(Opcode.CMSG_MOVE_STOP_SWIM)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM)]
        [PacketHandler(Opcode.CMSG_MOVE_STOP_TURN)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_TURN)]
        [PacketHandler(Opcode.CMSG_MOVE_DOUBLE_JUMP)]
        void HandlePlayerMove(ClientPlayerMovement movement)
        {
            Opcode universalOpcode = movement.GetUniversalOpcode();
            string opcodeName = universalOpcode.ToString();
            opcodeName = opcodeName.Replace("CMSG", "MSG");
            uint opcode = Opcodes.GetOpcodeValueForVersion(opcodeName, Framework.Settings.ServerBuild);
            if (opcode == 0)
                opcode = Opcodes.GetOpcodeValueForVersion("MSG_MOVE_SET_FACING", Framework.Settings.ServerBuild);

            bool isCurrentPlayerMovement = movement.Guid.To64().GetLowValue() == GetSession().GameState.CurrentPlayerGuid.To64().GetLowValue();
            if (isCurrentPlayerMovement)
            {
                GetSession().GameState.CurrentPlayerPosition = new Framework.GameMath.Position(
                    movement.MoveInfo.Position.X,
                    movement.MoveInfo.Position.Y,
                    movement.MoveInfo.Position.Z,
                    movement.MoveInfo.Orientation);
                GetSession().GameState.HasCurrentPlayerPosition = true;

                // Wrath does not always send CMSG_MOVE_TELEPORT_ACK for the legacy
                // same-map server movement teleport.  Only use post-teleport movement
                // as the ACK signal when the movement position is near the teleport
                // destination; stale source-zone movement is suppressed so it cannot
                // corrupt the 1.12 server's area/zone state.
                if (IsWaitingForWotlkSameMapTeleport(GetSession().GameState))
                {
                    if (!IsClientMovementNearPendingWotlkTeleport(movement.MoveInfo, out float teleportDistanceSq))
                    {
                        Log.Print(LogType.Debug,
                            $"[WotLK] suppressing stale client movement before teleport ACK: opcode={universalOpcode}, pos=({movement.MoveInfo.Position.X:0.###},{movement.MoveInfo.Position.Y:0.###},{movement.MoveInfo.Position.Z:0.###}), dest=({GetSession().GameState.PendingWotlkMovementTeleportDestination.X:0.###},{GetSession().GameState.PendingWotlkMovementTeleportDestination.Y:0.###},{GetSession().GameState.PendingWotlkMovementTeleportDestination.Z:0.###}), distSq={teleportDistanceSq:0.###}.");
                        GetSession().GameState.CurrentPlayerPosition = GetSession().GameState.PendingWotlkMovementTeleportDestination;
                        GetSession().GameState.HasCurrentPlayerPosition = true;
                        return;
                    }

                    CompletePendingWotlkSameMapTeleportFromClient(universalOpcode.ToString(), movement.MoveInfo.MoveTime);
                }
            }

            WorldPacket packet = new WorldPacket(opcode);
            // Client->server MSG_MOVE_* payloads for legacy backends are MovementInfo-only.
            // Injecting a mover guid here shifts the backend parser and corrupts position data
            // (for example x=0 with y/z shifted, invalid zone updates, fatigue deaths).
            movement.MoveInfo.WriteMovementInfoLegacy(packet);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_MOVE_TELEPORT_ACK)]
        [PacketHandler(Opcode.MSG_MOVE_TELEPORT_ACK)]
        void HandleMoveTeleportAck(MoveTeleportAck teleport)
        {
            var gameState = GetSession().GameState;
            bool pendingWotlkSameMapTeleport =
                Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                (gameState.IsWaitingForWotlkMovementTeleportAck || gameState.PendingLegacyTeleportCounter.HasValue);

            WorldPacket packet = new WorldPacket(Opcode.MSG_MOVE_TELEPORT_ACK);
            // Classic/TBC-era cores (including 1.12 MaNGOS) parse opcode 199 as:
            // uint64 guid + uint32 counter + uint32 time.
            // Always send full guid here to avoid short packed payloads that trigger
            // ByteBufferException on backend teleport ACK parsing.
            WowGuid128 ackGuid = pendingWotlkSameMapTeleport && gameState.PendingWotlkMovementTeleportGuid != WowGuid128.Empty
                ? gameState.PendingWotlkMovementTeleportGuid
                : teleport.MoverGUID;
            uint ackTime = pendingWotlkSameMapTeleport && gameState.PendingWotlkMovementTeleportMoveTime != 0
                ? gameState.PendingWotlkMovementTeleportMoveTime
                : teleport.MoveTime;

            packet.WriteGuid(ackGuid.To64());
            uint counter = gameState.PendingLegacyTeleportCounter ?? teleport.MoveCounter;
            gameState.PendingLegacyTeleportCounter = null;
            packet.WriteUInt32(counter);
            packet.WriteUInt32(ackTime);

            if (packet.GetSize() != 16)
                Log.Print(LogType.Debug, $"[MoveTeleportAck] Unexpected payload size {packet.GetSize()} (expected 16).");
            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                Framework.Settings.ServerBuild == ClientVersionBuild.V1_12_1_5875)
                Log.Print(LogType.Debug, $"[WotLK] translating client teleport ACK to vanilla: guid={ackGuid.To64().GetLowValue():X}, counter={counter} (clientField={teleport.MoveCounter}), time={ackTime} (clientField={teleport.MoveTime}), pending={pendingWotlkSameMapTeleport}.");

            gameState.IsWaitingForWotlkMovementTeleportAck = false;
            gameState.PendingWotlkMovementTeleportGuid = WowGuid128.Empty;
            gameState.PendingWotlkMovementTeleportMoveTime = 0;
            gameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position();
            gameState.PendingWotlkMovementTeleportStartTick = 0;
            if (pendingWotlkSameMapTeleport)
                gameState.PlayerObjectSent = true;
            SendPacketToServer(packet);

            if (pendingWotlkSameMapTeleport)
                FlushPendingWotlkWorldPortUpdates(gameState);
        }

        [PacketHandler(Opcode.CMSG_WORLD_PORT_RESPONSE)]
        [PacketHandler(Opcode.MSG_MOVE_WORLDPORT_ACK)]
        void HandleWorldPortResponse(WorldPortResponse teleport)
        {
            var gameState = GetSession().GameState;
            bool wasWaitingForWorldPortAck = gameState.IsWaitingForWorldPortAck;
            gameState.IsWaitingForWorldPortAck = false;

            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                gameState.HasPendingSyntheticWotlkWorldPortAck)
            {
                WorldPacket ack = new WorldPacket(Opcode.MSG_MOVE_TELEPORT_ACK);
                ack.WriteGuid(gameState.PendingSyntheticWotlkWorldPortGuid.To64());
                ack.WriteUInt32(gameState.PendingSyntheticWotlkWorldPortCounter);
                ack.WriteUInt32(gameState.PendingSyntheticWotlkWorldPortMoveTime);

                Log.Print(LogType.Debug, $"[WotLK] Translating synthetic worldport ACK back to vanilla teleport ACK: guid={gameState.PendingSyntheticWotlkWorldPortGuid.To64().GetLowValue():X}, counter={gameState.PendingSyntheticWotlkWorldPortCounter}, time={gameState.PendingSyntheticWotlkWorldPortMoveTime}.");

                gameState.HasPendingSyntheticWotlkWorldPortAck = false;
                gameState.PendingSyntheticWotlkWorldPortStartTick = 0;
                gameState.PendingLegacyTeleportCounter = null;
                gameState.PlayerObjectSent = true;
                SendPacketToServer(ack);
                FlushPendingWotlkWorldPortUpdates(gameState);
                return;
            }

            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                Framework.Settings.ServerBuild == ClientVersionBuild.V1_12_1_5875 &&
                !wasWaitingForWorldPortAck)
            {
                // A 1.12 same-map teleport never expects WORLDPORT_ACK.  If the
                // Wrath client sends one without a pending real/synthetic worldport,
                // do not forward it and trip the backend "player is still in world" guard.
                Log.Print(LogType.Warn, "[WotLK] Dropping unexpected MSG_MOVE_WORLDPORT_ACK for vanilla backend; no pending worldport.");
                FlushPendingWotlkWorldPortUpdates(gameState);
                return;
            }

            WorldPacket packet = new WorldPacket(Opcode.MSG_MOVE_WORLDPORT_ACK);
            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
            {
                Log.Print(LogType.Debug, $"[WotLK] WORLDPORT-v14 forwarding real MSG_MOVE_WORLDPORT_ACK and releasing worldport wait: wasWaiting={wasWaitingForWorldPortAck}.");
                gameState.IsWaitingForWorldPortAck = false;
                gameState.IsWaitingForWotlkMovementTeleportAck = false;
                gameState.PendingWotlkMovementTeleportGuid = WowGuid128.Empty;
                gameState.PendingWotlkMovementTeleportMoveTime = 0;
                gameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position();
                gameState.PendingWotlkMovementTeleportStartTick = 0;
                gameState.PendingLegacyTeleportCounter = null;
            }
            SendPacketToServer(packet);
            FlushPendingWotlkWorldPortUpdates(gameState);
        }

        private void FlushPendingWotlkWorldPortUpdates(GameSessionData gameState)
        {
            if (Framework.Settings.ClientBuild != ClientVersionBuild.V3_3_5a_12340)
                return;

            if (gameState.PendingLoginUpdates.Count == 0 &&
                gameState.PendingLoginDestroys.Count == 0 &&
                gameState.PendingLoginOutOfRangeGuids.Count == 0)
                return;

            // After the Wrath client has acknowledged NEW_WORLD it is safe to release
            // same-map teleport updates even when a vanilla backend does not send a new
            // self create block for the player.
            gameState.PlayerObjectSent = true;

            UpdateObject updateObject = new UpdateObject(gameState);
            updateObject.ObjectUpdates.AddRange(gameState.PendingLoginUpdates);
            updateObject.DestroyedGuids.AddRange(gameState.PendingLoginDestroys);
            updateObject.OutOfRangeGuids.AddRange(gameState.PendingLoginOutOfRangeGuids);

            Log.Print(LogType.Debug, $"[WotLK] Flushing buffered worldport updates after ACK: updates={updateObject.ObjectUpdates.Count}, destroys={updateObject.DestroyedGuids.Count}, oor={updateObject.OutOfRangeGuids.Count}.");

            gameState.PendingLoginUpdates.Clear();
            gameState.PendingLoginDestroys.Clear();
            gameState.PendingLoginOutOfRangeGuids.Clear();

            SendPacket(updateObject);
        }

        private void FlushPendingWotlkSettledWorldPortUpdates(string reason)
        {
            var gameState = GetSession().GameState;
            if (Framework.Settings.ClientBuild != ClientVersionBuild.V3_3_5a_12340 ||
                !gameState.IsSettlingWotlkWorldPortObjectStream)
                return;

            int elapsed = gameState.WotlkWorldPortObjectStreamSettleStartTick == 0
                ? 0
                : Environment.TickCount - gameState.WotlkWorldPortObjectStreamSettleStartTick;

            Log.Print(LogType.Debug,
                $"[WotLK] WORLDPORT-v14 releasing held destination objects after client settle: reason={reason}, elapsedMs={elapsed}, updates={gameState.PendingLoginUpdates.Count}, destroys={gameState.PendingLoginDestroys.Count}, oor={gameState.PendingLoginOutOfRangeGuids.Count}.");

            gameState.IsSettlingWotlkWorldPortObjectStream = false;
            gameState.WotlkWorldPortObjectStreamSettleStartTick = 0;
            FlushPendingWotlkWorldPortUpdates(gameState);
        }

        [PacketHandler(Opcode.CMSG_MOVE_FORCE_FLIGHT_BACK_SPEED_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_FLIGHT_SPEED_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_PITCH_RATE_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_RUN_BACK_SPEED_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_SWIM_BACK_SPEED_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_SWIM_SPEED_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_TURN_RATE_CHANGE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_WALK_SPEED_CHANGE_ACK)]
        void HandleMoveForceSpeedChangeAck(MovementSpeedAck speed)
        {
            var opcode = speed.GetUniversalOpcode();
            if (opcode == Opcode.MSG_NULL_ACTION)
                return;

            if (LegacyVersion.GetCurrentOpcode(opcode) == 0)
                return;

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180)
                && opcode is Opcode.CMSG_MOVE_FORCE_FLIGHT_SPEED_CHANGE_ACK
                          or Opcode.CMSG_MOVE_FORCE_FLIGHT_BACK_SPEED_CHANGE_ACK)
                return; // This is probably an ack by our swim to fly speed change for vanilla

            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                Framework.Settings.ServerBuild == ClientVersionBuild.V1_12_1_5875 &&
                _wotlkSyntheticMovementSpeedAckCounters.Remove(speed.Ack.MoveCounter))
            {
                Log.Print(LogType.Debug, $"[WotLK] Dropping synthetic movement speed bootstrap ACK for vanilla: opcode={opcode}, counter={speed.Ack.MoveCounter}, clientSpeed={speed.Speed:0.###}.");
                return;
            }

            WorldPacket packet = new WorldPacket(opcode);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                packet.WritePackedGuid(speed.MoverGUID.To64());
            else
                packet.WriteGuid(speed.MoverGUID.To64());
            if (LegacyMovementAckHasCounter())
                packet.WriteUInt32(speed.Ack.MoveCounter);
            float ackSpeed = NormalizeWotlkVanillaSpeedAck(opcode, speed.Speed);
            speed.Ack.MoveInfo.WriteMovementInfoLegacy(packet);
            packet.WriteFloat(ackSpeed);
            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 &&
                Framework.Settings.ServerBuild == ClientVersionBuild.V1_12_1_5875)
                Log.Print(LogType.Debug, $"[WotLK] Translated {opcode} ACK for vanilla: speed={ackSpeed:0.###} (client={speed.Speed:0.###}), size={packet.GetSize()}.");
            SendPacketToServer(packet);
        }

        private static bool LegacyMovementAckHasCounter()
        {
            // 1.12 MaNGOS force/root/water-walk ACK handlers read a movement
            // counter before MovementInfo even though regular MSG_MOVE payloads
            // do not include a mover guid or counter.
            return true;
        }

        private static float NormalizeWotlkVanillaSpeedAck(Opcode opcode, float speed)
        {
            if (Framework.Settings.ClientBuild != ClientVersionBuild.V3_3_5a_12340 ||
                speed > 0.0f)
                return speed;

            string opcodeName = opcode.ToString();
            if (opcodeName.Contains("RUN_BACK_SPEED"))
                return MovementInfo.DEFAULT_RUN_BACK_SPEED;
            if (opcodeName.Contains("RUN_SPEED"))
                return MovementInfo.DEFAULT_RUN_SPEED;
            if (opcodeName.Contains("WALK_SPEED"))
                return MovementInfo.DEFAULT_WALK_SPEED;
            if (opcodeName.Contains("SWIM_BACK_SPEED"))
                return MovementInfo.DEFAULT_SWIM_BACK_SPEED;
            if (opcodeName.Contains("SWIM_SPEED"))
                return MovementInfo.DEFAULT_SWIM_SPEED;
            if (opcodeName.Contains("TURN_RATE"))
                return MovementInfo.DEFAULT_TURN_RATE;
            if (opcodeName.Contains("PITCH_RATE"))
                return MovementInfo.DEFAULT_PITCH_RATE;

            switch (opcode)
            {
                case Opcode.CMSG_MOVE_FORCE_WALK_SPEED_CHANGE_ACK:
                    return MovementInfo.DEFAULT_WALK_SPEED;
                case Opcode.CMSG_MOVE_FORCE_RUN_SPEED_CHANGE_ACK:
                    return MovementInfo.DEFAULT_RUN_SPEED;
                case Opcode.CMSG_MOVE_FORCE_RUN_BACK_SPEED_CHANGE_ACK:
                    return MovementInfo.DEFAULT_RUN_BACK_SPEED;
                case Opcode.CMSG_MOVE_FORCE_SWIM_SPEED_CHANGE_ACK:
                    return MovementInfo.DEFAULT_SWIM_SPEED;
                case Opcode.CMSG_MOVE_FORCE_SWIM_BACK_SPEED_CHANGE_ACK:
                    return MovementInfo.DEFAULT_SWIM_BACK_SPEED;
                case Opcode.CMSG_MOVE_FORCE_TURN_RATE_CHANGE_ACK:
                    return MovementInfo.DEFAULT_TURN_RATE;
                case Opcode.CMSG_MOVE_FORCE_PITCH_RATE_CHANGE_ACK:
                    return MovementInfo.DEFAULT_PITCH_RATE;
                default:
                    return speed;
            }
        }

        MovementFlagModern GetFlagForAckOpcode(Opcode opcode)
        {
            switch (opcode)
            {
                case Opcode.CMSG_MOVE_FEATHER_FALL_ACK:
                    return MovementFlagModern.CanSafeFall;
                case Opcode.CMSG_MOVE_HOVER_ACK:
                    return MovementFlagModern.Hover;
                case Opcode.CMSG_MOVE_SET_CAN_FLY_ACK:
                    return MovementFlagModern.CanFly;
                case Opcode.CMSG_MOVE_WATER_WALK_ACK:
                    return MovementFlagModern.Waterwalking;
            }
            return MovementFlagModern.None;
        }

        [PacketHandler(Opcode.CMSG_MOVE_FEATHER_FALL_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_HOVER_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_SET_CAN_FLY_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_WATER_WALK_ACK)]
        void HandleMoveForceAck1(MovementAckMessage movementAck)
        {
            uint legacyOpcode = LegacyVersion.GetCurrentOpcode(movementAck.GetUniversalOpcode());
            if (legacyOpcode == 0)
                return;

            WorldPacket packet = new WorldPacket(legacyOpcode);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                packet.WritePackedGuid(movementAck.MoverGUID.To64());
            else
                packet.WriteGuid(movementAck.MoverGUID.To64());
            if (LegacyMovementAckHasCounter())
                packet.WriteUInt32(movementAck.Ack.MoveCounter);
            movementAck.Ack.MoveInfo.WriteMovementInfoLegacy(packet);

            int appliedState = movementAck.HasAppliedState
                ? (movementAck.AppliedState != 0 ? 1 : 0)
                : (movementAck.Ack.MoveInfo.Flags.HasAnyFlag(GetFlagForAckOpcode(movementAck.GetUniversalOpcode())) ? 1 : 0);

            packet.WriteInt32(appliedState);
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_MOVE_FORCE_ROOT_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_FORCE_UNROOT_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_KNOCK_BACK_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_GRAVITY_DISABLE_ACK)]
        [PacketHandler(Opcode.CMSG_MOVE_GRAVITY_ENABLE_ACK)]
        void HandleMoveForceAck2(MovementAckMessage movementAck)
        {
            uint legacyOpcode = LegacyVersion.GetCurrentOpcode(movementAck.GetUniversalOpcode());
            if (legacyOpcode == 0)
                return;

            WorldPacket packet = new WorldPacket(legacyOpcode);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                packet.WritePackedGuid(movementAck.MoverGUID.To64());
            else
                packet.WriteGuid(movementAck.MoverGUID.To64());
            if (LegacyMovementAckHasCounter())
                packet.WriteUInt32(movementAck.Ack.MoveCounter);
            movementAck.Ack.MoveInfo.WriteMovementInfoLegacy(packet);

            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_SET_ACTIVE_MOVER)]
        void HandleMoveSetActiveMover(SetActiveMover move)
        {
            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
                Log.Print(LogType.Debug, $"[WotLK] Client active mover set to {move.MoverGUID.To64().GetLowValue():X}.");

            WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTIVE_MOVER);
            packet.WriteGuid(move.MoverGUID.To64());
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_MOVE_INIT_ACTIVE_MOVER_COMPLETE)]
        void HandleMoveInitActiveMoverComplete(InitActiveMoverComplete move)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_SET_ACTIVE_MOVER);
            packet.WriteGuid(GetSession().GameState.CurrentPlayerGuid.To64());
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_MOVE_SPLINE_DONE)]
        void HandleMoveSplineDone(MoveSplineDone movement)
        {
            WorldPacket packet = new WorldPacket(Opcode.CMSG_MOVE_SPLINE_DONE);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                packet.WritePackedGuid(movement.Guid.To64());
            movement.MoveInfo.WriteMovementInfoLegacy(packet);
            packet.WriteInt32(movement.SplineID);
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                packet.WriteFloat(0); // Spline Type
            SendPacketToServer(packet);
        }

        [PacketHandler(Opcode.CMSG_MOVE_TIME_SKIPPED)]
        void HandleMoveSplineDone(MoveTimeSkipped movement)
        {
            if (Framework.Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
                return;

            WorldPacket packet = new WorldPacket(Opcode.CMSG_MOVE_TIME_SKIPPED);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                packet.WritePackedGuid(movement.MoverGUID.To64());
            else
                packet.WriteGuid(movement.MoverGUID.To64());
            packet.WriteUInt32(movement.TimeSkipped);
            SendPacketToServer(packet);
        }
    }
}
