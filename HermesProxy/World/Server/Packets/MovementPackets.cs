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


using Framework.Constants;
using Framework;
using Framework.GameMath;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using System;
using System.Collections.Generic;

namespace HermesProxy.World.Server.Packets
{
    internal static class WotlkMovementPacketCompat
    {
        internal static bool IsWotlkFrontendBuild()
        {
            return Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340;
        }

        internal static WowGuid128 ReadPackedMoverGuid(WorldPacket packet)
        {
            if (!IsWotlkFrontendBuild())
                return packet.ReadPackedGuid128();

            return MovementInfo.LegacyPackedGuidTo128(packet.ReadPackedGuid());
        }
    }

    public class ClientPlayerMovement : ClientPacket
    {
        public ClientPlayerMovement(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            Guid = WotlkMovementPacketCompat.ReadPackedMoverGuid(_worldPacket);
            MoveInfo = new MovementInfo();

            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
                MoveInfo.ReadMovementInfoWotlk(_worldPacket);
            else
                MoveInfo.ReadMovementInfoModern(_worldPacket);
        }

        public WowGuid128 Guid;
        public MovementInfo MoveInfo;
    }
    public class MoveUpdate : ServerPacket
    {
        public MoveUpdate() : this(Opcode.SMSG_MOVE_UPDATE) { }
        public MoveUpdate(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                MoveInfo.WriteMovementInfoWotlk(_worldPacket);
                return;
            }

            MoveInfo.WriteMovementInfoModern(_worldPacket, MoverGUID);
        }

        public WowGuid128 MoverGUID;
        public MovementInfo MoveInfo;
    }

    public class MonsterMove : ServerPacket
    {
        public MonsterMove(WowGuid128 guid, ServerSideMovement moveSpline) : base(Opcode.SMSG_ON_MONSTER_MOVE, ConnectionType.Instance)
        {
            if (moveSpline.SplineFlags.HasFlag(SplineFlagModern.UncompressedPath))
            {
                if (!moveSpline.SplineFlags.HasFlag(SplineFlagModern.Cyclic))
                {
                    foreach (var point in moveSpline.SplinePoints)
                        Points.Add(point);

                    if (moveSpline.EndPosition != Vector3.Zero)
                        Points.Add(moveSpline.EndPosition);
                }
                else
                {
                    if (moveSpline.EndPosition != Vector3.Zero)
                        Points.Add(moveSpline.EndPosition);

                    foreach (var point in moveSpline.SplinePoints)
                        Points.Add(point);
                }
            }
            else if (moveSpline.EndPosition != Vector3.Zero)
            {
                Points.Add(moveSpline.EndPosition);

                if (moveSpline.SplinePoints.Count > 0)
                {
                    Vector3 middle = (moveSpline.StartPosition + moveSpline.EndPosition) / 2.0f;

                    // first and last points already appended
                    for (int i = 0; i < moveSpline.SplinePoints.Count; ++i)
                        PackedDeltas.Add(middle - moveSpline.SplinePoints[i]);
                }
            }
            MoverGUID = guid;
            MoveSpline = moveSpline;
        }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                WriteWotlk();
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteVector3(MoveSpline.StartPosition);

            _worldPacket.WriteUInt32(MoveSpline.SplineId);
            _worldPacket.WriteVector3(Vector3.Zero); // Destination
            _worldPacket.WriteBit(false); // CrzTeleport
            _worldPacket.WriteBits(Points.Count == 0 ? 2 : 0, 3); // StopDistanceTolerance

            _worldPacket.WriteUInt32((uint)MoveSpline.SplineFlags);
            _worldPacket.WriteInt32(0); // Elapsed
            _worldPacket.WriteUInt32(MoveSpline.SplineTimeFull);
            _worldPacket.WriteUInt32(0); // FadeObjectTime
            _worldPacket.WriteUInt8(MoveSpline.SplineMode);
            _worldPacket.WritePackedGuid128(MoveSpline.TransportGuid != null ? MoveSpline.TransportGuid : WowGuid128.Empty);
            _worldPacket.WriteInt8(MoveSpline.TransportSeat);
            _worldPacket.WriteBits((byte)MoveSpline.SplineType, 2);
            _worldPacket.WriteBits(Points.Count, 16);
            _worldPacket.WriteBit(false); // VehicleExitVoluntary ;
            _worldPacket.WriteBit(false); // Interpolate
            _worldPacket.WriteBits(PackedDeltas.Count, 16);
            _worldPacket.WriteBit(false); // SplineFilter.HasValue
            _worldPacket.WriteBit(false); // SpellEffectExtraData.HasValue
            _worldPacket.WriteBit(false); // JumpExtraData.HasValue
            _worldPacket.FlushBits();

            //if (SplineFilter.HasValue)
            //    SplineFilter.Value.Write(data);

            switch (MoveSpline.SplineType)
            {
                case SplineTypeModern.FacingSpot:
                    _worldPacket.WriteVector3(MoveSpline.FinalFacingSpot);
                    break;
                case SplineTypeModern.FacingTarget:
                    _worldPacket.WriteFloat(MoveSpline.FinalOrientation);
                    _worldPacket.WritePackedGuid128(MoveSpline.FinalFacingGuid);
                    break;
                case SplineTypeModern.FacingAngle:
                    _worldPacket.WriteFloat(MoveSpline.FinalOrientation);
                    break;
            }

            foreach (Vector3 pos in Points)
                _worldPacket.WriteVector3(pos);

            foreach (Vector3 pos in PackedDeltas)
                _worldPacket.WritePackXYZ(pos);

            /*
            if (SpellEffectExtraData.HasValue)
                SpellEffectExtraData.Value.Write(data);

            if (JumpExtraData.HasValue)
                JumpExtraData.Value.Write(data);
            */
        }

        private void WriteWotlk()
        {
            _worldPacket.WritePackedGuid(MoverGUID.To64());
            _worldPacket.WriteUInt8(0); // toggles MOVEMENTFLAG2_UNK7 in 3.3.5 clients
            _worldPacket.WriteVector3(MoveSpline.StartPosition);
            _worldPacket.WriteUInt32(MoveSpline.SplineId);

            if (Points.Count == 0 && PackedDeltas.Count == 0 && MoveSpline.SplineType == SplineTypeModern.None)
            {
                _worldPacket.WriteUInt8((byte)SplineTypeLegacy.Stop);
                return;
            }

            switch (MoveSpline.SplineType)
            {
                case SplineTypeModern.FacingSpot:
                    _worldPacket.WriteUInt8((byte)SplineTypeLegacy.FacingSpot);
                    _worldPacket.WriteVector3(MoveSpline.FinalFacingSpot);
                    break;
                case SplineTypeModern.FacingTarget:
                    _worldPacket.WriteUInt8((byte)SplineTypeLegacy.FacingTarget);
                    _worldPacket.WriteGuid(MoveSpline.FinalFacingGuid.To64());
                    break;
                case SplineTypeModern.FacingAngle:
                    _worldPacket.WriteUInt8((byte)SplineTypeLegacy.FacingAngle);
                    _worldPacket.WriteFloat(MoveSpline.FinalOrientation);
                    break;
                default:
                    _worldPacket.WriteUInt8((byte)SplineTypeLegacy.Normal);
                    break;
            }

            SplineFlagWotLK splineFlags = BuildWotlkMonsterMoveFlags(MoveSpline.SplineFlags);

            _worldPacket.WriteUInt32((uint)splineFlags);
            _worldPacket.WriteUInt32(MoveSpline.SplineTimeFull);

            if (splineFlags.HasAnyFlag(SplineFlagWotLK.CatmullRom))
            {
                _worldPacket.WriteUInt32((uint)Points.Count);
                foreach (Vector3 pos in Points)
                    _worldPacket.WriteVector3(pos);
                return;
            }

            uint pointCount = (uint)(Points.Count + PackedDeltas.Count);
            _worldPacket.WriteUInt32(pointCount);

            Vector3 destination = Points.Count > 0
                ? Points[0]
                : MoveSpline.EndPosition;
            _worldPacket.WriteVector3(destination);

            foreach (Vector3 pos in PackedDeltas)
                _worldPacket.WritePackXYZ(pos);
        }

        private static SplineFlagWotLK BuildWotlkMonsterMoveFlags(SplineFlagModern modernFlags)
        {
            SplineFlagWotLK flags = SplineFlagWotLK.None;

            if (modernFlags.HasAnyFlag(SplineFlagModern.Falling))
                flags |= SplineFlagWotLK.Falling;
            if (modernFlags.HasAnyFlag(SplineFlagModern.Flying))
                flags |= SplineFlagWotLK.Flying;
            if (modernFlags.HasAnyFlag(SplineFlagModern.CatmullRom | SplineFlagModern.UncompressedPath))
                flags |= SplineFlagWotLK.CatmullRom;
            if (modernFlags.HasAnyFlag(SplineFlagModern.Cyclic))
                flags |= SplineFlagWotLK.Cyclic;
            if (modernFlags.HasAnyFlag(SplineFlagModern.TransportEnter))
                flags |= SplineFlagWotLK.Transport;
            if (modernFlags.HasAnyFlag(SplineFlagModern.TransportExit))
                flags |= SplineFlagWotLK.TransportExit;

            if (flags.HasAnyFlag(SplineFlagWotLK.Cyclic | SplineFlagWotLK.Flying))
                flags |= SplineFlagWotLK.EnterCycle;

            return flags;
        }

        public WowGuid128 MoverGUID;
        public ServerSideMovement MoveSpline;
        public List<Vector3> Points = new();
        public List<Vector3> PackedDeltas = new();
    }

    class MoveTeleportAck : ClientPacket
    {
        public MoveTeleportAck(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            MoverGUID = WotlkMovementPacketCompat.ReadPackedMoverGuid(_worldPacket);
            MoveCounter = _worldPacket.ReadUInt32();
            MoveTime = _worldPacket.ReadUInt32();
        }

        public WowGuid128 MoverGUID;
        public uint MoveCounter;
        public uint MoveTime;
    }

    public class MoveTeleport : ServerPacket
    {
        public MoveTeleport() : base(GetMoveTeleportOpcode(), ConnectionType.Instance) { }

        private static Opcode GetMoveTeleportOpcode()
        {
            // 3.3.5 uses MSG_MOVE_TELEPORT (no SMSG alias), while newer builds
            // may expose SMSG_MOVE_TELEPORT. Select whichever exists.
            return ModernVersion.GetCurrentOpcode(Opcode.SMSG_MOVE_TELEPORT) != 0
                ? Opcode.SMSG_MOVE_TELEPORT
                : Opcode.MSG_MOVE_TELEPORT;
        }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                // 3.3.5 MSG_MOVE_TELEPORT_Server is not the modern teleport
                // packet shape. It is the normal movement-msg body:
                // PackedGuid + MovementInfo. Reusing the Legion/Classic-era
                // packed128/vector/bit payload makes the Wrath client ignore or
                // misapply the correction and produces rubber-banding.
                MovementInfo moveInfo = MoveInfo ?? new MovementInfo
                {
                    MoveTime = MoveCounter,
                    Position = Position,
                    Orientation = Orientation,
                    TransportGuid = TransportGUID,
                    TransportSeat = Vehicle != null ? Vehicle.VehicleSeatIndex : (sbyte)-1,
                    WalkSpeed = MovementInfo.DEFAULT_WALK_SPEED,
                    RunSpeed = MovementInfo.DEFAULT_RUN_SPEED,
                    RunBackSpeed = MovementInfo.DEFAULT_RUN_BACK_SPEED,
                    SwimSpeed = MovementInfo.DEFAULT_SWIM_SPEED,
                    SwimBackSpeed = MovementInfo.DEFAULT_SWIM_BACK_SPEED,
                    FlightSpeed = MovementInfo.DEFAULT_FLY_SPEED,
                    FlightBackSpeed = MovementInfo.DEFAULT_FLY_BACK_SPEED,
                    TurnRate = MovementInfo.DEFAULT_TURN_RATE,
                    PitchRate = MovementInfo.DEFAULT_PITCH_RATE
                };

                _worldPacket.WritePackedGuid(MoverGUID.To64());
                moveInfo.WriteMovementInfoWotlk(_worldPacket);
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteUInt32(MoveCounter);
            _worldPacket.WriteVector3(Position);
            _worldPacket.WriteFloat(Orientation);
            _worldPacket.WriteUInt8(PreloadWorld);

            _worldPacket.WriteBit(TransportGUID != null);
            _worldPacket.WriteBit(Vehicle != null);
            _worldPacket.FlushBits();

            if (Vehicle != null)
            {
                _worldPacket.WriteInt8(Vehicle.VehicleSeatIndex);
                _worldPacket.WriteBit(Vehicle.VehicleExitVoluntary);
                _worldPacket.WriteBit(Vehicle.VehicleExitTeleport);
                _worldPacket.FlushBits();
            }

            if (TransportGUID != null)
                _worldPacket.WritePackedGuid128(TransportGUID);
        }

        public MovementInfo MoveInfo;
        public Vector3 Position;
        public VehicleTeleport Vehicle;
        public uint MoveCounter;
        public WowGuid128 MoverGUID;
        public WowGuid128 TransportGUID;
        public float Orientation;
        public byte PreloadWorld;
    }

    public class VehicleTeleport
    {
        public sbyte VehicleSeatIndex;
        public bool VehicleExitVoluntary;
        public bool VehicleExitTeleport;
    }

    public class TransferPending : ServerPacket
    {
        public TransferPending() : base(Opcode.SMSG_TRANSFER_PENDING) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(MapID);
            _worldPacket.WriteVector3(OldMapPosition);
            _worldPacket.WriteBit(Ship != null);
            _worldPacket.WriteBit(TransferSpellID.HasValue);

            if (Ship != null)
            {
                _worldPacket.WriteUInt32(Ship.Id);
                _worldPacket.WriteInt32(Ship.OriginMapID);
            }

            if (TransferSpellID.HasValue)
                _worldPacket.WriteInt32(TransferSpellID.Value);

            _worldPacket.FlushBits();
        }

        public uint MapID;
        public Vector3 OldMapPosition;
        public ShipTransferPending Ship;
        public int? TransferSpellID;

        public class ShipTransferPending
        {
            public uint Id;              // gameobject_template.entry of the transport the player is teleporting on
            public int OriginMapID;     // Map id the player is currently on (before teleport)
        }
    }

    public class TransferAborted : ServerPacket
    {
        public TransferAborted() : base(Opcode.SMSG_TRANSFER_ABORTED) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(MapID);
            _worldPacket.WriteUInt8(Arg);
            _worldPacket.WriteInt32(MapDifficultyXConditionID);
            _worldPacket.WriteBits(Reason, 6);
            _worldPacket.FlushBits();
        }

        public uint MapID;
        public byte Arg;
        public int MapDifficultyXConditionID = -6;
        public TransferAbortReasonModern Reason;
    }

    public class NewWorld : ServerPacket
    {
        public NewWorld() : base(Opcode.SMSG_NEW_WORLD) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(MapID);
            _worldPacket.WriteVector3(Position);
            _worldPacket.WriteFloat(Orientation);
            _worldPacket.WriteUInt32(Reason);
            _worldPacket.WriteVector3(MovementOffset);
        }

        public uint MapID;
        public uint Reason;
        public Vector3 Position = new();
        public float Orientation;
        public Vector3 MovementOffset;    // Adjusts all pending movement events by this offset
    }

    public class WorldPortResponse : ClientPacket
    {
        public WorldPortResponse(WorldPacket packet) : base(packet) { }

        public override void Read() { }
    }

    // for server controlled units
    public class MoveSplineSetSpeed : ServerPacket
    {
        public MoveSplineSetSpeed(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                _worldPacket.WriteFloat(Speed);
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteFloat(Speed);
        }

        public WowGuid128 MoverGUID;
        public float Speed = 1.0f;
    }

    // for own player
    public class MoveSetSpeed : ServerPacket
    {
        public MoveSetSpeed(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                _worldPacket.WriteUInt32(MoveCounter);
                if (GetUniversalOpcode() == Opcode.SMSG_FORCE_RUN_SPEED_CHANGE)
                    _worldPacket.WriteUInt8(0);
                _worldPacket.WriteFloat(Speed);
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteUInt32(MoveCounter);
            _worldPacket.WriteFloat(Speed);
        }

        public WowGuid128 MoverGUID;
        public uint MoveCounter = 0;
        public float Speed = 1.0f;
    }

    public class MovementSpeedAck : ClientPacket
    {
        public MovementSpeedAck(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            MoverGUID = WotlkMovementPacketCompat.ReadPackedMoverGuid(_worldPacket);
            Ack.Read(_worldPacket);
            Speed = _worldPacket.ReadFloat();
        }

        public WowGuid128 MoverGUID;
        public MovementAck Ack;
        public float Speed;
    }

    public struct MovementAck
    {
        public void Read(WorldPacket data)
        {
            MoveInfo = new();

            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                MoveCounter = data.ReadUInt32();
                MoveInfo.ReadMovementInfoWotlk(data);
                return;
            }

            MoveInfo.ReadMovementInfoModern(data);
            MoveCounter = data.ReadUInt32();
        }

        public MovementInfo MoveInfo;
        public uint MoveCounter;
    }

    // for other players
    public class MoveUpdateSpeed : ServerPacket
    {
        public MoveUpdateSpeed(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                MoveInfo.WriteMovementInfoWotlk(_worldPacket);
                _worldPacket.WriteFloat(Speed);
                return;
            }

            MoveInfo.WriteMovementInfoModern(_worldPacket, MoverGUID);
            _worldPacket.WriteFloat(Speed);
        }

        public WowGuid128 MoverGUID;
        public MovementInfo MoveInfo;
        public float Speed = 1.0f;
    }

    public class MoveSplineSetFlag : ServerPacket
    {
        public MoveSplineSetFlag(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
        }

        public WowGuid128 MoverGUID;
    }

    public class MoveSetFlag : ServerPacket
    {
        public MoveSetFlag(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                _worldPacket.WriteUInt32(MoveCounter);
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteUInt32(MoveCounter);
        }

        public WowGuid128 MoverGUID;
        public uint MoveCounter = 0;
    }

    public class MovementAckMessage : ClientPacket
    {
        public MovementAckMessage(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            Opcode opcode = GetUniversalOpcode();
            MoverGUID = WotlkMovementPacketCompat.ReadPackedMoverGuid(_worldPacket);
            Ack.Read(_worldPacket);

            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild() && HasWotlkAppliedState(opcode))
            {
                if (GetRemainingBytes(_worldPacket) >= sizeof(int))
                {
                    AppliedState = _worldPacket.ReadInt32();
                    HasAppliedState = true;
                }
            }
        }

        private static bool HasWotlkAppliedState(Opcode opcode)
        {
            return opcode is Opcode.CMSG_MOVE_WATER_WALK_ACK
                or Opcode.CMSG_MOVE_FEATHER_FALL_ACK
                or Opcode.CMSG_MOVE_HOVER_ACK
                or Opcode.CMSG_MOVE_SET_CAN_FLY_ACK;
        }

        private static long GetRemainingBytes(WorldPacket packet)
        {
            var stream = packet.GetCurrentStream();
            return stream.Length - stream.Position;
        }

        public WowGuid128 MoverGUID;
        public MovementAck Ack;
        public bool HasAppliedState;
        public int AppliedState;
    }

    class MoveSetCollisionHeight : ServerPacket
    {
        public MoveSetCollisionHeight() : base(Opcode.SMSG_MOVE_SET_COLLISION_HEIGHT) { }

        public override void Write()
        {
            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteUInt32(SequenceIndex);
            _worldPacket.WriteFloat(Height);
            _worldPacket.WriteFloat(Scale);
            _worldPacket.WriteByteEnum(Reason);
            _worldPacket.WriteUInt32(MountDisplayID);
            _worldPacket.WriteInt32(ScaleDuration);
        }
        
        public WowGuid128 MoverGUID;
        public uint SequenceIndex = 1;
        public float Height = 1.0f;
        public float Scale = 1.0f;
        public UpdateCollisionHeightReason Reason;
        public uint MountDisplayID;
        public int ScaleDuration = 2000; // time it takes for "scale"-animation

        public enum UpdateCollisionHeightReason : byte
        {
            Scale = 0,
            Mount = 1,
            Force = 2,
        }
    }

    class MoveKnockBack : ServerPacket
    {
        public MoveKnockBack() : base(Opcode.SMSG_MOVE_KNOCK_BACK, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                _worldPacket.WriteUInt32(MoveCounter);
                _worldPacket.WriteVector2(Direction);
                _worldPacket.WriteFloat(HorizontalSpeed);
                _worldPacket.WriteFloat(VerticalSpeed);
                return;
            }

            _worldPacket.WritePackedGuid128(MoverGUID);
            _worldPacket.WriteUInt32(MoveCounter);
            _worldPacket.WriteVector2(Direction);
            _worldPacket.WriteFloat(HorizontalSpeed);
            _worldPacket.WriteFloat(VerticalSpeed);
        }

        public WowGuid128 MoverGUID;
        public uint MoveCounter;
        public Vector2 Direction;
        public float HorizontalSpeed;
        public float VerticalSpeed;
    }

    public class MoveUpdateKnockBack : ServerPacket
    {
        public MoveUpdateKnockBack() : this(Opcode.SMSG_MOVE_UPDATE_KNOCK_BACK) { }
        public MoveUpdateKnockBack(Opcode opcode) : base(opcode, ConnectionType.Instance) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(MoverGUID.To64());
                MoveInfo.WriteMovementInfoWotlk(_worldPacket);
                _worldPacket.WriteFloat(MoveInfo.JumpSinAngle);
                _worldPacket.WriteFloat(MoveInfo.JumpCosAngle);
                _worldPacket.WriteFloat(MoveInfo.JumpHorizontalSpeed);
                _worldPacket.WriteFloat(MoveInfo.JumpVerticalSpeed);
                return;
            }

            MoveInfo.WriteMovementInfoModern(_worldPacket, MoverGUID);
        }

        public WowGuid128 MoverGUID;
        public MovementInfo MoveInfo;
    }

    class SuspendToken : ServerPacket
    {
        public SuspendToken() : base(Opcode.SMSG_SUSPEND_TOKEN, ConnectionType.Instance) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(SequenceIndex);
            _worldPacket.WriteBits(Reason, 2);
            _worldPacket.FlushBits();
        }

        public uint SequenceIndex = 1;
        public uint Reason = 1;
    }

    class ResumeToken : ServerPacket
    {
        public ResumeToken() : base(Opcode.SMSG_RESUME_TOKEN, ConnectionType.Instance) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(SequenceIndex);
            _worldPacket.WriteBits(Reason, 2);
            _worldPacket.FlushBits();
        }

        public uint SequenceIndex = 1;
        public uint Reason = 1;
    }

    public class ControlUpdate : ServerPacket
    {
        public ControlUpdate() : base(Opcode.SMSG_CONTROL_UPDATE) { }

        public override void Write()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
            {
                _worldPacket.WritePackedGuid(Guid.To64());
                _worldPacket.WriteUInt8(HasControl ? (byte)1 : (byte)0);
                return;
            }

            _worldPacket.WritePackedGuid128(Guid);
            _worldPacket.WriteBit(HasControl);
            _worldPacket.FlushBits();
        }

        public WowGuid128 Guid;
        public bool HasControl;
    }

    public class SetActiveMover : ClientPacket
    {
        public SetActiveMover(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
                MoverGUID = MovementInfo.LegacyPackedGuidTo128(_worldPacket.ReadGuid());
            else
                MoverGUID = _worldPacket.ReadPackedGuid128();
        }

        public WowGuid128 MoverGUID;
    }

    public class InitActiveMoverComplete : ClientPacket
    {
        public InitActiveMoverComplete(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            Ticks = _worldPacket.ReadUInt32();
        }

        public uint Ticks;
    }

    class MoveSplineDone : ClientPacket
    {
        public MoveSplineDone(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            Guid = WotlkMovementPacketCompat.ReadPackedMoverGuid(_worldPacket);
            MoveInfo = new();

            if (WotlkMovementPacketCompat.IsWotlkFrontendBuild())
                MoveInfo.ReadMovementInfoWotlk(_worldPacket);
            else
                MoveInfo.ReadMovementInfoModern(_worldPacket);

            SplineID = _worldPacket.ReadInt32();
        }

        public WowGuid128 Guid;
        public MovementInfo MoveInfo;
        public int SplineID;
    }
    class MoveTimeSkipped : ClientPacket
    {
        public MoveTimeSkipped(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            MoverGUID = WotlkMovementPacketCompat.ReadPackedMoverGuid(_worldPacket);
            TimeSkipped = _worldPacket.ReadUInt32();
        }

        public WowGuid128 MoverGUID;
        public uint TimeSkipped;
    }
}
