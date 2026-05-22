using Framework.GameMath;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        private const float WotlkSyntheticFarTeleportDistance = 533.3333f;

        private void RememberCurrentPlayerPosition(MovementInfo moveInfo)
        {
            GetSession().GameState.CurrentPlayerPosition = new Framework.GameMath.Position(
                moveInfo.Position.X,
                moveInfo.Position.Y,
                moveInfo.Position.Z,
                moveInfo.Orientation);
            GetSession().GameState.HasCurrentPlayerPosition = true;
        }

        private bool IsFarFromPosition(Position oldPosition, MovementInfo moveInfo, out float dx, out float dy, out float dz, out float distanceSq)
        {
            dx = moveInfo.Position.X - oldPosition.X;
            dy = moveInfo.Position.Y - oldPosition.Y;
            dz = moveInfo.Position.Z - oldPosition.Z;
            distanceSq = dx * dx + dy * dy;
            return distanceSq > (WotlkSyntheticFarTeleportDistance * WotlkSyntheticFarTeleportDistance);
        }

        private void ClearPendingWotlkSameMapMovementTeleport(string reason, bool clearBufferedUpdates)
        {
            var gameState = GetSession().GameState;
            if (!gameState.IsWaitingForWotlkMovementTeleportAck &&
                !gameState.PendingLegacyTeleportCounter.HasValue &&
                gameState.PendingWotlkMovementTeleportGuid == WowGuid128.Empty)
                return;

            Log.Print(LogType.Warn, $"clearing pending movement teleport state ({reason}).");

            gameState.IsWaitingForWotlkMovementTeleportAck = false;
            gameState.PendingLegacyTeleportCounter = null;
            gameState.PendingWotlkMovementTeleportGuid = WowGuid128.Empty;
            gameState.PendingWotlkMovementTeleportMoveTime = 0;
            gameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position();
            gameState.PendingWotlkMovementTeleportStartTick = 0;
            gameState.HasPendingWotlkFarServerTeleport = false;
            gameState.PendingWotlkFarServerTeleportGuid = WowGuid128.Empty;

            if (clearBufferedUpdates)
                UpdateObject.ResetLoginBuffer(gameState);
        }

        private bool RememberPendingWotlkFarServerTeleport(WowGuid128 guid, MovementInfo moveInfo)
        {
            if (!IsWotlkFrontendClient())
                return false;

            var gameState = GetSession().GameState;
            if (!gameState.HasCurrentPlayerPosition)
                return false;

            bool far = IsFarFromPosition(gameState.CurrentPlayerPosition, moveInfo, out float dx, out float dy, out float dz, out float distanceSq);
            if (!far)
                return false;

            gameState.HasPendingWotlkFarServerTeleport = true;
            gameState.PendingWotlkFarServerTeleportGuid = guid;
            gameState.PendingWotlkFarServerTeleportStartPosition = gameState.CurrentPlayerPosition;
            gameState.PendingWotlkFarServerTeleportDestination = new Position(
                moveInfo.Position.X,
                moveInfo.Position.Y,
                moveInfo.Position.Z,
                moveInfo.Orientation);

            ulong moverLow = guid.To64().GetLowValue();
            ulong currentLow = gameState.CurrentPlayerGuid.To64().GetLowValue();
            Log.Print(LogType.Debug,
                $"Detected far server MSG_MOVE_TELEPORT; deferring raw movement and forcing next teleport ACK into worldport path: " +
                $"mover={moverLow:X}, current={currentLow:X}, old=({gameState.CurrentPlayerPosition.X:0.###},{gameState.CurrentPlayerPosition.Y:0.###},{gameState.CurrentPlayerPosition.Z:0.###}), " +
                $"new=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}), " +
                $"delta=({dx:0.###},{dy:0.###},{dz:0.###}), distanceSq={distanceSq:0.###}, limitSq={(WotlkSyntheticFarTeleportDistance * WotlkSyntheticFarTeleportDistance):0.###}.");
            return true;
        }

        private bool ShouldPromoteLegacyTeleportToWotlkWorldPort(WowGuid128 guid, MovementInfo moveInfo)
        {
            if (!IsWotlkFrontendClient())
                return false;

            var gameState = GetSession().GameState;
            ulong moverLow = guid.To64().GetLowValue();
            ulong currentLow = gameState.CurrentPlayerGuid.To64().GetLowValue();

            if (gameState.HasPendingWotlkFarServerTeleport)
            {
                float destDx = moveInfo.Position.X - gameState.PendingWotlkFarServerTeleportDestination.X;
                float destDy = moveInfo.Position.Y - gameState.PendingWotlkFarServerTeleportDestination.Y;
                float destDz = moveInfo.Position.Z - gameState.PendingWotlkFarServerTeleportDestination.Z;
                float destDistanceSq = destDx * destDx + destDy * destDy + destDz * destDz;
                bool destinationMatches = destDistanceSq < 25.0f * 25.0f;
                bool selfOrUnknown = currentLow == 0 || moverLow == 0 || moverLow == currentLow;

                if (destinationMatches && selfOrUnknown)
                {
                    Position oldPosition = gameState.PendingWotlkFarServerTeleportStartPosition;
                    bool far = IsFarFromPosition(oldPosition, moveInfo, out float dx, out float dy, out float dz, out float distanceSq);
                    Log.Print(LogType.Debug,
                        $"Promoting teleport ACK because prior MSG_MOVE_TELEPORT was far: mover={moverLow:X}, current={currentLow:X}, " +
                        $"old=({oldPosition.X:0.###},{oldPosition.Y:0.###},{oldPosition.Z:0.###}), " +
                        $"new=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}), " +
                        $"delta=({dx:0.###},{dy:0.###},{dz:0.###}), distanceSq={distanceSq:0.###}, limitSq={(WotlkSyntheticFarTeleportDistance * WotlkSyntheticFarTeleportDistance):0.###}, promote={far}.");
                    return far;
                }

                Log.Print(LogType.Debug,
                    $"Pending far server teleport did not match this ACK: mover={moverLow:X}, current={currentLow:X}, destDistanceSq={destDistanceSq:0.###}, selfOrUnknown={selfOrUnknown}.");
            }

            // Vanilla MSG_MOVE_TELEPORT_ACK is a server-directed teleport for the local
            // mover.  In some crash/reconnect paths CurrentPlayerGuid can briefly be
            // stale or empty, so do not classify it as a harmless near-teleport just
            // because the guid guard is inconclusive.  Only reject it when we clearly
            // know it belongs to a different mover.
            if (currentLow != 0 && moverLow != 0 && moverLow != currentLow)
            {
                Log.Print(LogType.Debug, $"Not promoting legacy teleport for non-self mover: mover={moverLow:X}, current={currentLow:X}.");
                return false;
            }

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                // 1.12 backends report even continent-spanning .tele commands as a
                // server-side MSG_MOVE_TELEPORT_ACK without SMSG_TRANSFER_PENDING or
                // SMSG_NEW_WORLD.  The WotLK client is not stable if we translate that
                // into a plain movement update and then stream destination objects.  In
                // WotLK-frontend/vanilla-backend mode, treat every self teleport ACK as
                // a synthetic worldport.  Short-range teleports get a loading screen, but
                // far same-map teleports no longer poison the character for relog.
                if (gameState.HasCurrentPlayerPosition)
                {
                    bool far = IsFarFromPosition(gameState.CurrentPlayerPosition, moveInfo, out float dx, out float dy, out float dz, out float distanceSq);
                    Log.Print(LogType.Debug,
                        $"Forcing vanilla teleport ACK through NEW_WORLD: mover={moverLow:X}, current={currentLow:X}, " +
                        $"old=({gameState.CurrentPlayerPosition.X:0.###},{gameState.CurrentPlayerPosition.Y:0.###},{gameState.CurrentPlayerPosition.Z:0.###}), " +
                        $"new=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}), " +
                        $"delta=({dx:0.###},{dy:0.###},{dz:0.###}), distanceSq={distanceSq:0.###}, far={far}.");
                }
                else
                {
                    Log.Print(LogType.Debug,
                        $"Forcing vanilla teleport ACK through NEW_WORLD without prior position: mover={moverLow:X}, current={currentLow:X}, " +
                        $"new=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}).");
                }
                return true;
            }

            if (!gameState.HasCurrentPlayerPosition)
            {
                Log.Print(LogType.Debug, $"Cannot classify legacy teleport distance yet; missing current player position (mover={moverLow:X}, current={currentLow:X}).");
                return false;
            }

            bool promote = IsFarFromPosition(gameState.CurrentPlayerPosition, moveInfo, out float normalDx, out float normalDy, out float normalDz, out float normalDistanceSq);

            Log.Print(LogType.Debug,
                $"Legacy teleport distance classification: mover={moverLow:X}, current={currentLow:X}, " +
                $"old=({gameState.CurrentPlayerPosition.X:0.###},{gameState.CurrentPlayerPosition.Y:0.###},{gameState.CurrentPlayerPosition.Z:0.###}), " +
                $"new=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}), " +
                $"delta=({normalDx:0.###},{normalDy:0.###},{normalDz:0.###}), distanceSq={normalDistanceSq:0.###}, limitSq={(WotlkSyntheticFarTeleportDistance * WotlkSyntheticFarTeleportDistance):0.###}, promote={promote}.");

            return promote;
        }

        private void BeginSyntheticWotlkWorldPortForLegacyTeleport(WowGuid128 guid, MoveTeleport teleport, MovementInfo moveInfo)
        {
            var gameState = GetSession().GameState;
            gameState.HasPendingSyntheticWotlkWorldPortAck = true;
            gameState.HasPendingWotlkFarServerTeleport = false;
            gameState.PendingWotlkFarServerTeleportGuid = WowGuid128.Empty;
            gameState.PendingSyntheticWotlkWorldPortGuid = guid;
            gameState.PendingSyntheticWotlkWorldPortCounter = teleport.MoveCounter;
            gameState.PendingSyntheticWotlkWorldPortMoveTime = moveInfo.MoveTime;
            gameState.PendingSyntheticWotlkWorldPortStartTick = Environment.TickCount;
            gameState.IsWaitingForNewWorld = false;
            gameState.IsWaitingForWorldPortAck = true;
            gameState.PendingTransferMapId = gameState.CurrentMapId ?? 0;

            UpdateObject.ResetLoginBuffer(gameState);

            TransferPending pending = new()
            {
                MapID = gameState.PendingTransferMapId,
                OldMapPosition = gameState.HasCurrentPlayerPosition
                    ? gameState.CurrentPlayerPosition.ToVector3()
                    : moveInfo.Position
            };

            NewWorld newWorld = new()
            {
                MapID = gameState.PendingTransferMapId,
                Position = moveInfo.Position,
                Orientation = moveInfo.Orientation,
                Reason = 4
            };

            Log.Print(LogType.Debug, $"Promoting vanilla legacy teleport to WotLK NEW_WORLD: map={newWorld.MapID}, counter={teleport.MoveCounter}, old=({gameState.CurrentPlayerPosition.X:0.###},{gameState.CurrentPlayerPosition.Y:0.###},{gameState.CurrentPlayerPosition.Z:0.###}), new=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}).");
            SendPacketToClient(pending);
            SendPacketToClient(newWorld);
            RememberCurrentPlayerPosition(moveInfo);
        }

        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.MSG_MOVE_START_FORWARD)]
        [PacketHandler(Opcode.MSG_MOVE_START_BACKWARD)]
        [PacketHandler(Opcode.MSG_MOVE_STOP)]
        [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_LEFT)]
        [PacketHandler(Opcode.MSG_MOVE_START_STRAFE_RIGHT)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_STRAFE)]
        [PacketHandler(Opcode.MSG_MOVE_START_ASCEND)]
        [PacketHandler(Opcode.MSG_MOVE_START_DESCEND)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_ASCEND)]
        [PacketHandler(Opcode.MSG_MOVE_JUMP)]
        [PacketHandler(Opcode.MSG_MOVE_START_TURN_LEFT)]
        [PacketHandler(Opcode.MSG_MOVE_START_TURN_RIGHT)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_TURN)]
        [PacketHandler(Opcode.MSG_MOVE_START_PITCH_UP)]
        [PacketHandler(Opcode.MSG_MOVE_START_PITCH_DOWN)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_PITCH)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_MODE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_WALK_MODE)]
        [PacketHandler(Opcode.MSG_MOVE_TELEPORT)]
        [PacketHandler(Opcode.MSG_MOVE_SET_FACING)]
        [PacketHandler(Opcode.MSG_MOVE_SET_PITCH)]
        [PacketHandler(Opcode.MSG_MOVE_TOGGLE_COLLISION_CHEAT)]
        [PacketHandler(Opcode.MSG_MOVE_GRAVITY_CHNG)]
        [PacketHandler(Opcode.MSG_MOVE_ROOT)]
        [PacketHandler(Opcode.MSG_MOVE_UNROOT)]
        [PacketHandler(Opcode.MSG_MOVE_START_SWIM)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM)]
        [PacketHandler(Opcode.MSG_MOVE_START_SWIM_CHEAT)]
        [PacketHandler(Opcode.MSG_MOVE_STOP_SWIM_CHEAT)]
        [PacketHandler(Opcode.MSG_MOVE_HEARTBEAT)]
        [PacketHandler(Opcode.MSG_MOVE_FALL_LAND)]
        [PacketHandler(Opcode.MSG_MOVE_UPDATE_CAN_FLY)]
        [PacketHandler(Opcode.MSG_MOVE_UPDATE_CAN_TRANSITION_BETWEEN_SWIM_AND_FLY)]
        [PacketHandler(Opcode.MSG_MOVE_HOVER)]
        [PacketHandler(Opcode.MSG_MOVE_FEATHER_FALL)]
        [PacketHandler(Opcode.MSG_MOVE_WATER_WALK)]
        void HandleMovementMessages(WorldPacket packet)
        {
            Opcode outgoingOpcode = packet.GetUniversalOpcode(false);
            WowGuid128 moverGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
            MovementInfo moveInfo = new();
            moveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            moveInfo.Flags = (uint)(((MovementFlagWotLK)moveInfo.Flags).CastFlags<MovementFlagModern>());

            moveInfo.ValidateMovementInfo();

            if (IsWotlkFrontendClient() &&
                outgoingOpcode != Opcode.MSG_MOVE_SET_FACING)
            {
                ulong moverLow = moverGuid.To64().GetLowValue();
                ulong currentLow = GetSession().GameState.CurrentPlayerGuid.To64().GetLowValue();
                Log.Print(LogType.Debug, $"Backend movement {outgoingOpcode}: mover={moverLow:X}, current={currentLow:X}, flags=0x{moveInfo.Flags:X8}, extra=0x{moveInfo.FlagsExtra:X4}, pos=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}), o={moveInfo.Orientation:0.###}.");
            }

            if (IsWotlkFrontendClient() &&
                outgoingOpcode == Opcode.MSG_MOVE_TELEPORT)
            {
                // In 1.12 this server->client packet is only a precursor to the later
                // MSG_MOVE_TELEPORT_ACK.  Passing it through to a 3.3.5 client can leave
                // the client half-moved while the server later sends object updates for
                // the destination area.  Suppress it unconditionally in WotLK frontend
                // mode and let HandleMoveTeleportAck perform the single authoritative
                // TRANSFER_PENDING + NEW_WORLD handoff.
                bool rememberedAsFar = RememberPendingWotlkFarServerTeleport(moverGuid, moveInfo);
                if (!rememberedAsFar)
                {
                    var gameState = GetSession().GameState;
                    gameState.HasPendingWotlkFarServerTeleport = true;
                    gameState.PendingWotlkFarServerTeleportGuid = moverGuid;
                    gameState.PendingWotlkFarServerTeleportStartPosition = gameState.HasCurrentPlayerPosition
                        ? gameState.CurrentPlayerPosition
                        : new Position(moveInfo.Position.X, moveInfo.Position.Y, moveInfo.Position.Z, moveInfo.Orientation);
                    gameState.PendingWotlkFarServerTeleportDestination = new Position(
                        moveInfo.Position.X,
                        moveInfo.Position.Y,
                        moveInfo.Position.Z,
                        moveInfo.Orientation);

                    Log.Print(LogType.Debug,
                        $"Suppressing backend MSG_MOVE_TELEPORT and forcing next teleport ACK through NEW_WORLD: " +
                        $"pos=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}), map={gameState.CurrentMapId?.ToString() ?? "unknown"}.");
                }
                return;
            }

            if (IsCurrentPlayerGuid(moverGuid))
                RememberCurrentPlayerPosition(moveInfo);

            MoveUpdate moveUpdate = IsWotlkFrontendClient()
                ? new MoveUpdate(outgoingOpcode)
                : new MoveUpdate();
            moveUpdate.MoverGUID = moverGuid;
            moveUpdate.MoveInfo = moveInfo;
            SendPacketToClient(moveUpdate);
        }

        [PacketHandler(Opcode.MSG_MOVE_KNOCK_BACK)]
        void HandleMoveKnockBack(WorldPacket packet)
        {
            MoveUpdateKnockBack knockback = IsWotlkFrontendClient()
                ? new MoveUpdateKnockBack(packet.GetUniversalOpcode(false))
                : new MoveUpdateKnockBack();
            knockback.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            knockback.MoveInfo = new();
            knockback.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            knockback.MoveInfo.Flags = (uint)(((MovementFlagWotLK)knockback.MoveInfo.Flags).CastFlags<MovementFlagModern>());
            knockback.MoveInfo.JumpSinAngle = packet.ReadFloat();
            knockback.MoveInfo.JumpCosAngle = packet.ReadFloat();
            knockback.MoveInfo.JumpHorizontalSpeed = packet.ReadFloat();
            knockback.MoveInfo.JumpVerticalSpeed = packet.ReadFloat();
            knockback.MoveInfo.ValidateMovementInfo();
            SendPacketToClient(knockback);
        }

        [PacketHandler(Opcode.SMSG_MOVE_KNOCK_BACK)]
        void HandleMoveForceKnockBack(WorldPacket packet)
        {
            MoveKnockBack knockback = new MoveKnockBack();
            knockback.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            knockback.MoveCounter = packet.ReadUInt32();
            knockback.Direction = packet.ReadVector2();
            knockback.HorizontalSpeed = packet.ReadFloat();
            knockback.VerticalSpeed = packet.ReadFloat();
            SendPacketToClient(knockback);
        }

        [PacketHandler(Opcode.SMSG_CONTROL_UPDATE)]
        void HandleControlUpdate(WorldPacket packet)
        {
            ControlUpdate control = new ControlUpdate();
            control.Guid = packet.ReadPackedGuid().To128(GetSession().GameState);
            control.HasControl = packet.ReadBool();

            // In WotLK-frontend mode, losing control on the active player can leave
            // the client in a turn-only state (no forward movement opcodes emitted).
            // Keep authority pinned to true for the local player.
            if (IsWotlkFrontendClient() &&
                IsCurrentPlayerGuid(control.Guid) &&
                !control.HasControl)
            {
                Log.Print(LogType.Debug, "Overriding SMSG_CONTROL_UPDATE(false) to true for active player.");
                control.HasControl = true;
            }

            SendPacketToClient(control);
        }

        [PacketHandler(Opcode.MSG_MOVE_TELEPORT_ACK)]
        void HandleMoveTeleportAck(WorldPacket packet)
        {
            // Never raw-forward backend MSG_MOVE_TELEPORT_ACK to Wrath frontend.
            // Wrath expects this as a client->server ack and can crash or desync if
            // received as server payload. Translate to MoveTeleport instead.

            WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);

            if (GetSession().GameState.IsInTaxiFlight &&
                IsCurrentPlayerGuid(guid))
            {
                ControlUpdate control = new ControlUpdate();
                control.Guid = guid;
                control.HasControl = true;
                SendPacketToClient(control);
                GetSession().GameState.IsInTaxiFlight = false;
            }

            MoveTeleport teleport = new MoveTeleport();
            teleport.MoverGUID = guid;
            teleport.MoveCounter = packet.ReadUInt32();
            GetSession().GameState.PendingLegacyTeleportCounter = teleport.MoveCounter;
            MovementInfo moveInfo = new();
            moveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            moveInfo.Flags = (uint)(((MovementFlagWotLK)moveInfo.Flags).CastFlags<MovementFlagModern>());
            moveInfo.ValidateMovementInfo();
            teleport.Position = moveInfo.Position;
            teleport.Orientation = moveInfo.Orientation;
            teleport.TransportGUID = moveInfo.TransportGuid;
            if (moveInfo.TransportSeat > 0)
            {
                teleport.Vehicle = new();
                teleport.Vehicle.VehicleSeatIndex = moveInfo.TransportSeat;
            }

            if (IsWotlkFrontendClient())
            {
                ClearPendingWotlkSameMapMovementTeleport("new backend same-map teleport", true);

                // Vanilla same-map teleports are not worldports.  The 1.12 server
                // remains in-world and expects MSG_MOVE_TELEPORT_ACK, not
                // MSG_MOVE_WORLDPORT_ACK.  Wrath accepts MSG_MOVE_TELEPORT but often
                // does not emit CMSG_MOVE_TELEPORT_ACK for this legacy payload.  Send
                // the movement teleport, hold destination updates, then let the first
                // post-teleport client movement act as the safe ACK point.
                MoveUpdate teleportMove = new MoveUpdate(Opcode.MSG_MOVE_TELEPORT)
                {
                    MoverGUID = guid,
                    MoveInfo = moveInfo
                };

                GetSession().GameState.IsWaitingForWotlkMovementTeleportAck = true;
                GetSession().GameState.PendingWotlkMovementTeleportGuid = guid;
                GetSession().GameState.PendingWotlkMovementTeleportMoveTime = moveInfo.MoveTime;
                GetSession().GameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position(
                    moveInfo.Position.X,
                    moveInfo.Position.Y,
                    moveInfo.Position.Z,
                    moveInfo.Orientation);
                GetSession().GameState.PendingWotlkMovementTeleportStartTick = Environment.TickCount;
                GetSession().GameState.IsWaitingForWorldPortAck = false;
                GetSession().GameState.HasPendingSyntheticWotlkWorldPortAck = false;
                GetSession().GameState.PendingSyntheticWotlkWorldPortStartTick = 0;
                UpdateObject.ResetLoginBuffer(GetSession().GameState);

                Log.Print(LogType.Debug,
                    $"sending MSG_MOVE_TELEPORT and waiting for zone/movement ACK: guid={guid.To64().GetLowValue():X}, counter={teleport.MoveCounter}, pos=({moveInfo.Position.X:0.###},{moveInfo.Position.Y:0.###},{moveInfo.Position.Z:0.###}).");
                SendPacketToClient(teleportMove);
                RememberCurrentPlayerPosition(moveInfo);
                return;
            }

            SendPacketToClient(teleport);
        }

        private void AcknowledgeLegacyNearTeleport(WowGuid128 guid, uint moveCounter, uint moveTime)
        {
            if (!LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                return;

            if (IsWotlkFrontendClient())
            {
                // In Wrath frontend mode we must never complete a vanilla teleport by
                // silently ACKing it here.  The 3.3.5 client needs the synthetic
                // TRANSFER_PENDING/NEW_WORLD path first; otherwise it can receive
                // destination object updates while still in the previous world state.
                Log.Print(LogType.Error, $"BLOCKED unsafe legacy teleport auto-ACK: counter={moveCounter}, time={moveTime}. This should have gone through NEW_WORLD.");
                return;
            }

            WorldPacket ack = new WorldPacket(Opcode.MSG_MOVE_TELEPORT_ACK);
            ack.WriteGuid(guid.To64());
            ack.WriteUInt32(moveCounter);
            ack.WriteUInt32(moveTime);
            GetSession().GameState.PendingLegacyTeleportCounter = null;
            GetSession().GameState.HasPendingWotlkFarServerTeleport = false;
            GetSession().GameState.PendingWotlkFarServerTeleportGuid = WowGuid128.Empty;
            Log.Print(LogType.Debug, $"[Legacy] ACKing short-range teleport: counter={moveCounter}, time={moveTime}.");
            SendPacketToServer(ack);
        }

        [PacketHandler(Opcode.SMSG_TRANSFER_PENDING)]
        void HandleTransferPending(WorldPacket packet)
        {
            if (IsWotlkFrontendClient())
                ClearPendingWotlkSameMapMovementTeleport("real SMSG_TRANSFER_PENDING", true);

            if (GetSession().GameState.IsWaitingForWorldPortAck)
            {
                if (IsWotlkFrontendClient())
                {
                    Log.Print(LogType.Warn, "Received SMSG_TRANSFER_PENDING while still waiting for worldport ACK; clearing stale state and starting the new transfer.");
                    GetSession().GameState.IsWaitingForWorldPortAck = false;
                    GetSession().GameState.IsWaitingForNewWorld = false;
                    GetSession().GameState.HasPendingSyntheticWotlkWorldPortAck = false;
                    GetSession().GameState.PendingSyntheticWotlkWorldPortStartTick = 0;
                    GetSession().GameState.IsWaitingForWotlkMovementTeleportAck = false;
                    GetSession().GameState.PendingWotlkMovementTeleportGuid = WowGuid128.Empty;
                    GetSession().GameState.PendingWotlkMovementTeleportMoveTime = 0;
                    GetSession().GameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position();
                    GetSession().GameState.PendingWotlkMovementTeleportStartTick = 0;
                }
                else
                {
                    Log.Print(LogType.Error, "Skipping SMSG_TRANSFER_PENDING, client is already being teleported.");
                    return;
                }
            }

            TransferPending transfer = new TransferPending();
            transfer.MapID = GetSession().GameState.PendingTransferMapId = packet.ReadUInt32();
            transfer.OldMapPosition = GetSession().GameState.HasCurrentPlayerPosition
                ? GetSession().GameState.CurrentPlayerPosition.ToVector3()
                : Vector3.Zero;
            if (IsWotlkFrontendClient())
                UpdateObject.ResetLoginBuffer(GetSession().GameState);
            SendPacketToClient(transfer);
            GetSession().GameState.IsFirstEnterWorld = false;
            GetSession().GameState.IsWaitingForNewWorld = true;

            if (!IsWotlkFrontendClient())
            {
                SuspendToken suspend = new();
                suspend.SequenceIndex = 3;
                suspend.Reason = 1;
                SendPacketToClient(suspend);
            }
        }

        [PacketHandler(Opcode.SMSG_TRANSFER_ABORTED)]
        void HandleTransferAborted(WorldPacket packet)
        {
            TransferAborted transfer = new TransferAborted();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                transfer.MapID = packet.ReadUInt32();
            else
                transfer.MapID = GetSession().GameState.PendingTransferMapId;

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                transfer.Reason = (TransferAbortReasonModern)packet.ReadUInt8();
            else
            {
                TransferAbortReasonLegacy legacyReason = (TransferAbortReasonLegacy)packet.ReadUInt8();
                transfer.Reason = (TransferAbortReasonModern)Enum.Parse(typeof(TransferAbortReasonModern), legacyReason.ToString());
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                transfer.Arg = packet.ReadUInt8();

            SendPacketToClient(transfer);
            GetSession().GameState.IsWaitingForNewWorld = false;
            GetSession().GameState.IsWaitingForWorldPortAck = false;
            GetSession().GameState.HasPendingSyntheticWotlkWorldPortAck = false;
            GetSession().GameState.PendingSyntheticWotlkWorldPortStartTick = 0;
            GetSession().GameState.IsWaitingForWotlkMovementTeleportAck = false;
            GetSession().GameState.PendingWotlkMovementTeleportGuid = WowGuid128.Empty;
            GetSession().GameState.PendingWotlkMovementTeleportMoveTime = 0;
            GetSession().GameState.PendingWotlkMovementTeleportDestination = new Framework.GameMath.Position();
            GetSession().GameState.PendingWotlkMovementTeleportStartTick = 0;
        }

        [PacketHandler(Opcode.SMSG_NEW_WORLD)]
        void HandleNewWorld(WorldPacket packet)
        {
            if (IsWotlkFrontendClient())
                ClearPendingWotlkSameMapMovementTeleport("real SMSG_NEW_WORLD", true);

            NewWorld teleport = new NewWorld();
            GetSession().GameState.CurrentMapId = teleport.MapID = packet.ReadUInt32();
            teleport.Position = packet.ReadVector3();
            teleport.Orientation = packet.ReadFloat();
            teleport.Reason = 4;
            GetSession().GameState.CurrentPlayerPosition = new Framework.GameMath.Position(
                teleport.Position.X,
                teleport.Position.Y,
                teleport.Position.Z,
                teleport.Orientation);
            GetSession().GameState.HasCurrentPlayerPosition = true;
            if (IsWotlkFrontendClient())
                UpdateObject.ResetLoginBuffer(GetSession().GameState);
            GetSession().GameState.IsFirstEnterWorld = false;

            if (GetSession().GameState.IsWaitingForNewWorld)
            {
                GetSession().GameState.IsWaitingForNewWorld = false;
                GetSession().GameState.IsWaitingForWorldPortAck = true;
                SendPacketToClient(teleport);
                if (!IsWotlkFrontendClient() && teleport.MapID > 1)
                {
                    UpdateLastInstance instance = new();
                    instance.MapID = teleport.MapID;
                    SendPacketToClient(instance);

                    if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                        SendPacketToClient(new TimeSyncRequest());

                    ResumeToken resume = new();
                    resume.SequenceIndex = 3;
                    resume.Reason = 1;
                    SendPacketToClient(resume);
                }

                if (!IsWotlkFrontendClient())
                {
                    WorldServerInfo info = new();
                    if (teleport.MapID > 1)
                    {
                        info.DifficultyID = 1;
                        info.InstanceGroupSize = 5;
                    }
                    SendPacketToClient(info);
                }
            }
        }

        // for server controlled units
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLIGHT_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLIGHT_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_PITCH_RATE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_SWIM_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_SWIM_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_TURN_RATE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_BACK_SPEED)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_SPEED)]
        void HandleMoveSplineSetSpeed(WorldPacket packet)
        {
            MoveSplineSetSpeed speed = new MoveSplineSetSpeed(packet.GetUniversalOpcode(false));
            speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            speed.Speed = packet.ReadFloat();
            SendPacketToClient(speed);
        }

        // for own player
        [PacketHandler(Opcode.SMSG_FORCE_WALK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_RUN_BACK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_SWIM_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_SWIM_BACK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_TURN_RATE_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_FLIGHT_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_FLIGHT_BACK_SPEED_CHANGE)]
        [PacketHandler(Opcode.SMSG_FORCE_PITCH_RATE_CHANGE)]
        void HandleMoveForceSpeedChange(WorldPacket packet)
        { // for own player
            string opcodeName = packet.GetUniversalOpcode(false).ToString().Replace("SMSG_FORCE_", "SMSG_MOVE_SET_").Replace("_CHANGE", "");
            Opcode universalOpcode = Opcodes.GetUniversalOpcode(opcodeName);
            if (universalOpcode == Opcode.MSG_NULL_ACTION || ModernVersion.GetCurrentOpcode(universalOpcode) == 0)
            {
                // Some modern opcode maps (notably WotLK frontend mode) do not define
                // SMSG_MOVE_SET_* variants for these packets, but do define SMSG_FORCE_*.
                // Falling back prevents dropping speed updates and leaving player movement locked.
                universalOpcode = packet.GetUniversalOpcode(false);
            }

            MoveSetSpeed speed = new MoveSetSpeed(universalOpcode);
            speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            speed.MoveCounter = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) &&
                packet.GetUniversalOpcode(false) == Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)
            {
                packet.ReadUInt8(); // unk byte
            }

            speed.Speed = packet.ReadFloat();
            SendPacketToClient(speed);

            // Convenience in vanilla to use SwimSpeed as FlySpeed
            if (universalOpcode is Opcode.SMSG_MOVE_SET_SWIM_SPEED
                                or Opcode.SMSG_MOVE_SET_SWIM_BACK_SPEED &&
                LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var flyOpcode = (Opcode) Enum.Parse(typeof(Opcode), universalOpcode.ToString().Replace("SWIM", "FLIGHT"));
                MoveSetSpeed flySpeed = new MoveSetSpeed(flyOpcode);
                flySpeed.MoverGUID = speed.MoverGUID;
                flySpeed.MoveCounter = speed.MoveCounter;
                flySpeed.Speed = speed.Speed;
                SendPacketToClient(flySpeed);
            }
        }

        // for other players
        [PacketHandler(Opcode.MSG_MOVE_SET_FLIGHT_BACK_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_FLIGHT_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_PITCH_RATE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_BACK_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_RUN_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_SWIM_BACK_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_SWIM_SPEED)]
        [PacketHandler(Opcode.MSG_MOVE_SET_TURN_RATE)]
        [PacketHandler(Opcode.MSG_MOVE_SET_WALK_SPEED)]
        void HandleMoveUpdateSpeed(WorldPacket packet)
        { // for other players
            string opcodeName = packet.GetUniversalOpcode(false).ToString().Replace("MSG_MOVE_SET", "SMSG_MOVE_UPDATE");
            Opcode universalOpcode = Opcodes.GetUniversalOpcode(opcodeName);
            if (IsWotlkFrontendClient())
                universalOpcode = packet.GetUniversalOpcode(false);

            MoveUpdateSpeed speed = new MoveUpdateSpeed(universalOpcode);
            speed.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            speed.MoveInfo = new MovementInfo();
            speed.MoveInfo.ReadMovementInfoLegacy(packet, GetSession().GameState);
            var newFlags = ((MovementFlagWotLK)speed.MoveInfo.Flags).CastFlags<MovementFlagModern>();
            speed.MoveInfo.Flags = (uint)(newFlags);
            speed.MoveInfo.ValidateMovementInfo();
            speed.Speed = packet.ReadFloat();
            SendPacketToClient(speed);

            // Convenience in vanilla to use SwimSpeed as FlySpeed
            if (universalOpcode is Opcode.SMSG_MOVE_UPDATE_SWIM_SPEED
                                or Opcode.SMSG_MOVE_UPDATE_SWIM_BACK_SPEED &&
                LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var flyOpcode = (Opcode) Enum.Parse(typeof(Opcode), universalOpcode.ToString().Replace("SWIM", "FLIGHT"));
                MoveUpdateSpeed flySpeed = new MoveUpdateSpeed(flyOpcode);
                flySpeed.MoverGUID = speed.MoverGUID;
                flySpeed.MoveInfo = speed.MoveInfo;
                flySpeed.Speed = speed.Speed;
                SendPacketToClient(flySpeed);
            }
        }

        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_ROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_ENABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_DISABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FEATHER_FALL)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_NORMAL_FALL)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_HOVER)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNSET_HOVER)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WATER_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_LAND_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_START_SWIM)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_STOP_SWIM)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_RUN_MODE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_WALK_MODE)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_SET_FLYING)]
        [PacketHandler(Opcode.SMSG_MOVE_SPLINE_UNSET_FLYING)]
        void HandleSplineMovementMessages(WorldPacket packet)
        {
            MoveSplineSetFlag spline = new MoveSplineSetFlag(packet.GetUniversalOpcode(false));
            spline.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            SendPacketToClient(spline);
        }

        [PacketHandler(Opcode.SMSG_MOVE_ROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_UNROOT)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_WATER_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_LAND_WALK)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_HOVERING)]
        [PacketHandler(Opcode.SMSG_MOVE_UNSET_HOVERING)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_CAN_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_UNSET_CAN_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_ENABLE_TRANSITION_BETWEEN_SWIM_AND_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_DISABLE_TRANSITION_BETWEEN_SWIM_AND_FLY)]
        [PacketHandler(Opcode.SMSG_MOVE_DISABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_ENABLE_GRAVITY)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_FEATHER_FALL)]
        [PacketHandler(Opcode.SMSG_MOVE_SET_NORMAL_FALL)]
        void HandleMoveForceFlagChange(WorldPacket packet)
        {
            MoveSetFlag flag = new MoveSetFlag(packet.GetUniversalOpcode(false));
            flag.MoverGUID = packet.ReadPackedGuid().To128(GetSession().GameState);
            flag.MoveCounter = packet.ReadUInt32();
            SendPacketToClient(flag);
        }

        [PacketHandler(Opcode.SMSG_COMPRESSED_MOVES)]
        void HandleCompressedMoves(WorldPacket packet)
        {
            var uncompressedSize = packet.ReadInt32();

            WorldPacket pkt = packet.Inflate(uncompressedSize);

            while (pkt.CanRead())
            {
                var size = pkt.ReadUInt8();
                var opc = pkt.ReadUInt16();
                var data = pkt.ReadBytes((uint)(size - 2));

                var pkt2 = new WorldPacket(opc, data);
                pkt2.SetReceiveTime(pkt.GetReceivedTime());
                HandlePacket(pkt2);
            }
        }

        [PacketHandler(Opcode.SMSG_ON_MONSTER_MOVE)]
        [PacketHandler(Opcode.SMSG_MONSTER_MOVE_TRANSPORT)]
        void HandleMonsterMove(WorldPacket packet)
        {
            WowGuid128 guid = packet.ReadPackedGuid().To128(GetSession().GameState);
            ServerSideMovement moveSpline = new();

            if (packet.GetUniversalOpcode(false) == Opcode.SMSG_MONSTER_MOVE_TRANSPORT)
            {
                moveSpline.TransportGuid = packet.ReadPackedGuid().To128(GetSession().GameState);
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                    moveSpline.TransportSeat = packet.ReadInt8();
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767)) // no idea when this was added exactly
                packet.ReadBool(); // "Toggle AnimTierInTrans"

            moveSpline.StartPosition = packet.ReadVector3();
            moveSpline.SplineId = packet.ReadUInt32();
            SplineTypeLegacy type = (SplineTypeLegacy)packet.ReadUInt8();
            switch (type)
            {
                case SplineTypeLegacy.FacingSpot:
                {
                    moveSpline.SplineType = SplineTypeModern.FacingSpot;
                    moveSpline.FinalFacingSpot = packet.ReadVector3();
                    break;
                }
                case SplineTypeLegacy.FacingTarget:
                {
                    moveSpline.SplineType = SplineTypeModern.FacingTarget;
                    moveSpline.FinalFacingGuid = packet.ReadGuid().To128(GetSession().GameState);
                    break;
                }
                case SplineTypeLegacy.FacingAngle:
                {
                    moveSpline.SplineType = SplineTypeModern.FacingAngle;
                    moveSpline.FinalOrientation = packet.ReadFloat();
                    MovementInfo.ClampOrientation(ref moveSpline.FinalOrientation);
                    break;
                }
                case SplineTypeLegacy.Stop:
                {
                    moveSpline.SplineType = SplineTypeModern.None;
                    MonsterMove moveStop = new MonsterMove(guid, moveSpline);
                    SendPacketToClient(moveStop);
                    return;
                }
            }

            bool hasAnimTier;
            bool hasTrajectory;
            bool hasCatmullRom;
            bool hasTaxiFlightFlags;
            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var splineFlags = (SplineFlagVanilla)packet.ReadUInt32();
                hasAnimTier = false;
                hasTrajectory = false;
                hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagVanilla.Flying);
                hasTaxiFlightFlags = splineFlags == (SplineFlagVanilla.Runmode | SplineFlagVanilla.Flying);

                if (splineFlags == SplineFlagVanilla.Runmode) // Default spline flags used by Vanilla and TBC servers
                {
                    moveSpline.SplineFlags = SplineFlagModern.Unknown5;
                    UnitFlagsVanilla unitFlags = (UnitFlagsVanilla)GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
                    if (unitFlags.HasFlag(UnitFlagsVanilla.CanSwim))
                        moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
                    if (type == SplineTypeLegacy.Normal && !unitFlags.HasFlag(UnitFlagsVanilla.InCombat))
                        moveSpline.SplineFlags |= SplineFlagModern.Steering | SplineFlagModern.Unknown10;
                }
                else
                    moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }
            else if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                var splineFlags = (SplineFlagTBC)packet.ReadUInt32();
                hasAnimTier = false;
                hasTrajectory = false;
                hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagTBC.Flying);
                hasTaxiFlightFlags = splineFlags == (SplineFlagTBC.Runmode | SplineFlagTBC.Flying);

                if (splineFlags == SplineFlagTBC.Runmode) // Default spline flags used by Vanilla and TBC servers
                {
                    moveSpline.SplineFlags = SplineFlagModern.Unknown5;
                    UnitFlags unitFlags = (UnitFlags)GetSession().GameState.GetLegacyFieldValueUInt32(guid, UnitField.UNIT_FIELD_FLAGS);
                    if (unitFlags.HasFlag(UnitFlags.CanSwim))
                        moveSpline.SplineFlags |= SplineFlagModern.CanSwim;
                    if (type == SplineTypeLegacy.Normal && !unitFlags.HasFlag(UnitFlags.InCombat))
                        moveSpline.SplineFlags |= SplineFlagModern.Steering | SplineFlagModern.Unknown10;
                }
                else
                    moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }
            else
            {
                var splineFlags = (SplineFlagWotLK)packet.ReadUInt32();
                hasAnimTier = splineFlags.HasAnyFlag(SplineFlagWotLK.AnimationTier);
                hasTrajectory = splineFlags.HasAnyFlag(SplineFlagWotLK.Trajectory);
                hasCatmullRom = splineFlags.HasAnyFlag(SplineFlagWotLK.Flying | SplineFlagWotLK.CatmullRom);
                hasTaxiFlightFlags = splineFlags == (SplineFlagWotLK.WalkMode | SplineFlagWotLK.Flying);
                moveSpline.SplineFlags = splineFlags.CastFlags<SplineFlagModern>();
            }

            if (hasAnimTier)
            {
                packet.ReadUInt8(); // Animation State
                packet.ReadInt32(); // Async-time in ms
            }

            moveSpline.SplineTimeFull = packet.ReadUInt32();

            if (hasTrajectory)
            {
                packet.ReadFloat(); // Vertical Speed
                packet.ReadInt32(); // Async-time in ms
            }

            moveSpline.SplineCount = packet.ReadUInt32();

            if (hasCatmullRom)
            {
                for (var i = 0; i < moveSpline.SplineCount; i++)
                {
                    Vector3 vec = packet.ReadVector3();

                    if (moveSpline != null)
                        moveSpline.SplinePoints.Add(vec);
                }
                moveSpline.SplineFlags |= SplineFlagModern.UncompressedPath;
            }
            else
            {
                moveSpline.EndPosition = packet.ReadVector3();

                Vector3 mid = (moveSpline.StartPosition + moveSpline.EndPosition) * 0.5f;

                for (var i = 1; i < moveSpline.SplineCount; i++)
                {
                    var vec = packet.ReadPackedVector3();

                    if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                        vec = mid - vec;
                    else
                        vec = moveSpline.EndPosition - vec;

                    moveSpline.SplinePoints.Add(vec);
                }
            }

            bool isTaxiFlight = (hasTaxiFlightFlags &&
                                (GetSession().GameState.IsWaitingForTaxiStart ||
                                 Math.Abs(packet.GetReceivedTime() - GetSession().GameState.CurrentPlayerCreateTime) <= 1000) &&
                                 IsCurrentPlayerGuid(guid));

            if (isTaxiFlight)
            {
                // Exact sequence of packets from sniff.
                // Client instantly teleports to destination if anything is left out.

                ServerSideMovement stopSpline = new();
                stopSpline.StartPosition = moveSpline.StartPosition;
                stopSpline.SplineId = moveSpline.SplineId - 2;
                MonsterMove moveStop = new MonsterMove(guid, stopSpline);
                SendPacketToClient(moveStop);

                ControlUpdate update = new();
                update.Guid = guid;
                update.HasControl = false;
                SendPacketToClient(update);

                stopSpline.SplineId = moveSpline.SplineId - 1;
                moveStop = new MonsterMove(guid, stopSpline);
                SendPacketToClient(moveStop);

                update = new();
                update.Guid = guid;
                update.HasControl = false;
                SendPacketToClient(update);

                moveSpline.SplineFlags = SplineFlagModern.Flying |
                                         SplineFlagModern.CatmullRom |
                                         SplineFlagModern.CanSwim |
                                         SplineFlagModern.UncompressedPath |
                                         SplineFlagModern.Unknown5 |
                                         SplineFlagModern.Steering |
                                         SplineFlagModern.Unknown10;

                if (!hasCatmullRom && moveSpline.EndPosition != Vector3.Zero)
                    moveSpline.SplinePoints.Add(moveSpline.EndPosition);
            }

            MonsterMove monsterMove = new MonsterMove(guid, moveSpline);
            SendPacketToClient(monsterMove);

            if (isTaxiFlight)
            {
                if (GetSession().GameState.IsWaitingForTaxiStart)
                {
                    ActivateTaxiReplyPkt taxi = new();
                    taxi.Reply = ActivateTaxiReply.Ok;
                    SendPacketToClient(taxi);
                    GetSession().GameState.IsWaitingForTaxiStart = false;
                }
                GetSession().GameState.IsInTaxiFlight = true;
            }
        }
    }
}
