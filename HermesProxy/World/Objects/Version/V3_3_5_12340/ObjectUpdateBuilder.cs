using Framework.GameMath;
using Framework.IO;
using Framework.Util;
using HermesProxy.Enums;
using HermesProxy.World.Enums.V3_3_5_12340;
using BccUpdateFields = HermesProxy.World.Enums.V2_5_2_39570;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HermesProxy.World.Objects.Version.V3_3_5_12340
{
    public class ObjectUpdateBuilder
    {
        public ObjectUpdateBuilder(ObjectUpdate updateData, GameSessionData gameState)
        {
            m_alreadyWritten = false;
            m_updateData = updateData;
            m_gameState = gameState;
            bool isWotlkTarget = ModernVersion.GetUpdateFieldsDefiningBuild() == ClientVersionBuild.V3_3_5a_12340;

            Enums.ObjectType objectType = updateData.Guid.GetObjectType();
            if (updateData.CreateData != null)
            {
                objectType = updateData.CreateData.ObjectType;
                if (!isWotlkTarget && updateData.CreateData.ThisIsYou)
                    objectType = Enums.ObjectType.ActivePlayer;
            }
            if (!isWotlkTarget &&
                objectType == Enums.ObjectType.Player &&
                m_gameState.CurrentPlayerGuid == updateData.Guid)
                objectType = Enums.ObjectType.ActivePlayer;
            m_objectType = ObjectTypeConverter.ConvertToBCC(objectType);
            m_objectTypeMask = Enums.ObjectTypeMask.Object;

            uint fieldsSize;
            uint dynamicFieldsSize;
            switch (m_objectType)
            {
                case Enums.ObjectTypeBCC.Item:
                    fieldsSize = (uint)BccUpdateFields.ItemField.ITEM_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.ItemDynamicField.ITEM_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Item;
                    break;
                case Enums.ObjectTypeBCC.Container:
                    fieldsSize = (uint)BccUpdateFields.ContainerField.CONTAINER_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.ContainerDynamicField.CONTAINER_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Item;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Container;
                    break;
                case Enums.ObjectTypeBCC.Unit:
                    fieldsSize = (uint)BccUpdateFields.UnitField.UNIT_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.UnitDynamicField.UNIT_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Unit;
                    break;
                case Enums.ObjectTypeBCC.Player:
                    fieldsSize = (uint)BccUpdateFields.PlayerField.PLAYER_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.PlayerDynamicField.PLAYER_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Unit;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Player;
                    break;
                case Enums.ObjectTypeBCC.ActivePlayer:
                    fieldsSize = (uint)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.ActivePlayerDynamicField.ACTIVE_PLAYER_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Unit;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Player;
                    m_objectTypeMask |= Enums.ObjectTypeMask.ActivePlayer;
                    break;
                case Enums.ObjectTypeBCC.GameObject:
                    fieldsSize = (uint)BccUpdateFields.GameObjectField.GAMEOBJECT_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.GameObjectDynamicField.GAMEOBJECT_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.GameObject;
                    break;
                case Enums.ObjectTypeBCC.DynamicObject:
                    fieldsSize = (uint)BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.DynamicObjectDynamicField.DYNAMICOBJECT_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.DynamicObject;
                    break;
                case Enums.ObjectTypeBCC.Corpse:
                    fieldsSize = (uint)BccUpdateFields.CorpseField.CORPSE_END;
                    dynamicFieldsSize = (uint)BccUpdateFields.CorpseDynamicField.CORPSE_DYNAMIC_END;
                    m_objectTypeMask |= Enums.ObjectTypeMask.Corpse;
                    break;
                default:
                    throw new ArgumentOutOfRangeException("Unsupported object type!");
            }

            if (isWotlkTarget)
            {
                switch (m_objectType)
                {
                    case Enums.ObjectTypeBCC.Item:
                        fieldsSize = (uint)ItemField.ITEM_END;
                        dynamicFieldsSize = 0;
                        break;
                    case Enums.ObjectTypeBCC.Container:
                        fieldsSize = (uint)ContainerField.CONTAINER_END;
                        dynamicFieldsSize = 0;
                        break;
                    case Enums.ObjectTypeBCC.Unit:
                        fieldsSize = (uint)UnitField.UNIT_END;
                        dynamicFieldsSize = 0;
                        break;
                    case Enums.ObjectTypeBCC.Player:
                    case Enums.ObjectTypeBCC.ActivePlayer:
                        fieldsSize = (uint)PlayerField.PLAYER_END;
                        dynamicFieldsSize = 0;
                        break;
                    case Enums.ObjectTypeBCC.GameObject:
                        fieldsSize = (uint)GameObjectField.GAMEOBJECT_END;
                        dynamicFieldsSize = 0;
                        break;
                    case Enums.ObjectTypeBCC.DynamicObject:
                        fieldsSize = (uint)DynamicObjectField.DYNAMICOBJECT_END;
                        dynamicFieldsSize = 0;
                        break;
                    case Enums.ObjectTypeBCC.Corpse:
                        fieldsSize = (uint)CorpseField.CORPSE_END;
                        dynamicFieldsSize = 0;
                        break;
                }
            }

            m_dynamicFields = new(dynamicFieldsSize, m_updateData.Type);

            m_gameState.ObjectCacheMutex.WaitOne();
            if (m_updateData.CreateData == null &&
                m_gameState.ObjectCacheModern.TryGetValue(updateData.Guid, out m_fields) &&
                m_fields != null)
            {
                m_fields.m_updateMask.Clear();
            }
            else
            {
                m_fields = new UpdateFieldsArray(fieldsSize);
                m_gameState.ObjectCacheModern.Remove(updateData.Guid);
                m_gameState.ObjectCacheModern.Add(updateData.Guid, m_fields);
            }
            m_gameState.ObjectCacheMutex.ReleaseMutex();
        }

        protected bool m_alreadyWritten;
        protected ObjectUpdate m_updateData;
        protected UpdateFieldsArray m_fields;
        protected DynamicUpdateFieldsArray m_dynamicFields;
        protected Enums.ObjectTypeBCC m_objectType;
        protected Enums.ObjectTypeMask m_objectTypeMask;
        protected CreateObjectBits m_createBits;
        protected GameSessionData m_gameState;

        public void WriteToPacket(WorldPacket packet)
        {
            packet.WriteUInt8(GetSerializedUpdateType());
            if (IsWotlkTargetBuild())
                packet.WritePackedGuid(m_updateData.Guid.To64());
            else
                packet.WritePackedGuid128(m_updateData.Guid);

            if (m_updateData.Type != Enums.UpdateTypeModern.Values)
            {
                if (IsWotlkTargetBuild())
                {
                    packet.WriteUInt8((byte)GetLegacyObjectType());
                    BuildMovementUpdateWotlk(packet);
                }
                else
                {
                    packet.WriteUInt8((byte)m_objectType);
                    packet.WriteInt32((int)m_objectTypeMask); //< HeirFlags

                    SetCreateObjectBits();
                    BuildMovementUpdate(packet);
                }
            }

            BuildValuesUpdate(packet);
            if (!IsWotlkTargetBuild())
                BuildDynamicValuesUpdate(packet);
        }

        private byte GetSerializedUpdateType()
        {
            if (ModernVersion.GetUpdateFieldsDefiningBuild() != ClientVersionBuild.V3_3_5a_12340)
                return (byte)m_updateData.Type;

            return m_updateData.Type switch
            {
                Enums.UpdateTypeModern.Values => 0,
                Enums.UpdateTypeModern.CreateObject1 => 2,
                Enums.UpdateTypeModern.CreateObject2 => 3,
                _ => (byte)m_updateData.Type
            };
        }

        private bool IsWotlkTargetBuild()
        {
            return ModernVersion.GetUpdateFieldsDefiningBuild() == ClientVersionBuild.V3_3_5a_12340;
        }

        private uint GetWotlkObjectTypeMask()
        {
            const uint typeMaskObject = 0x01;
            const uint typeMaskItem = 0x02;
            const uint typeMaskContainer = 0x04;
            const uint typeMaskUnit = 0x08;
            const uint typeMaskPlayer = 0x10;
            const uint typeMaskGameObject = 0x20;
            const uint typeMaskDynamicObject = 0x40;
            const uint typeMaskCorpse = 0x80;

            return m_objectType switch
            {
                Enums.ObjectTypeBCC.Item => typeMaskObject | typeMaskItem,
                Enums.ObjectTypeBCC.Container => typeMaskObject | typeMaskItem | typeMaskContainer,
                Enums.ObjectTypeBCC.Unit => typeMaskObject | typeMaskUnit,
                Enums.ObjectTypeBCC.Player => typeMaskObject | typeMaskUnit | typeMaskPlayer,
                Enums.ObjectTypeBCC.ActivePlayer => typeMaskObject | typeMaskUnit | typeMaskPlayer,
                Enums.ObjectTypeBCC.GameObject => typeMaskObject | typeMaskGameObject,
                Enums.ObjectTypeBCC.DynamicObject => typeMaskObject | typeMaskDynamicObject,
                Enums.ObjectTypeBCC.Corpse => typeMaskObject | typeMaskCorpse,
                _ => typeMaskObject
            };
        }

        private HermesProxy.World.Enums.ObjectTypeLegacy GetLegacyObjectType()
        {
            HermesProxy.World.Enums.ObjectType type = m_updateData.CreateData?.ObjectType ?? m_updateData.Guid.GetObjectType();
            if (type == HermesProxy.World.Enums.ObjectType.ActivePlayer)
                type = HermesProxy.World.Enums.ObjectType.Player;
            if (m_updateData.CreateData?.ThisIsYou == true)
                type = HermesProxy.World.Enums.ObjectType.Player;

            return ObjectTypeConverter.ConvertToLegacy(type);
        }

        private void BuildMovementUpdateWotlk(WorldPacket data)
        {
            MovementInfo moveInfo = m_updateData.CreateData?.MoveInfo;
            HermesProxy.World.Enums.UpdateFlag updateFlags = HermesProxy.World.Enums.UpdateFlag.None;

            if (m_updateData.CreateData?.ThisIsYou == true)
                updateFlags |= HermesProxy.World.Enums.UpdateFlag.Self;

            if (moveInfo != null)
            {
                if (m_objectTypeMask.HasAnyFlag(Enums.ObjectTypeMask.Unit))
                {
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.Living;
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.StationaryObject;
                }
                else
                {
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.StationaryObject;
                }

                if (m_updateData.CreateData.AutoAttackVictim != null)
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.AttackingTarget;

                if (moveInfo.TransportPathTimer != 0)
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.Transport;

                if (moveInfo.VehicleId != 0)
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.Vehicle;

                if (m_objectType == Enums.ObjectTypeBCC.GameObject && moveInfo.Rotation.GetPackedRotation() != 0)
                    updateFlags |= HermesProxy.World.Enums.UpdateFlag.GORotation;
            }

            data.WriteUInt16((ushort)updateFlags);

            if ((updateFlags & HermesProxy.World.Enums.UpdateFlag.Living) != 0)
            {
                uint movementFlags = (uint)(((HermesProxy.World.Enums.MovementFlagModern)moveInfo.Flags).CastFlags<HermesProxy.World.Enums.MovementFlagWotLK>());
                movementFlags &= ~(uint)HermesProxy.World.Enums.MovementFlagWotLK.SplineEnabled;

                data.WriteUInt32(movementFlags);
                data.WriteUInt16((ushort)moveInfo.FlagsExtra);
                data.WriteUInt32(moveInfo.MoveTime);
                data.WriteVector3(moveInfo.Position);
                data.WriteFloat(moveInfo.Orientation);

                if ((movementFlags & (uint)HermesProxy.World.Enums.MovementFlagWotLK.OnTransport) != 0 && moveInfo.TransportGuid != null)
                {
                    data.WritePackedGuid(moveInfo.TransportGuid.To64());
                    data.WriteVector3(moveInfo.TransportOffset);
                    data.WriteFloat(moveInfo.TransportOrientation);
                    data.WriteUInt32(moveInfo.TransportTime);
                    data.WriteInt8(moveInfo.TransportSeat);

                    if (((ushort)moveInfo.FlagsExtra & (ushort)HermesProxy.World.Enums.MovementFlagExtra.InterpolateMove) != 0)
                        data.WriteUInt32(moveInfo.TransportTime2);
                }

                bool hasPitch =
                    (movementFlags & ((uint)HermesProxy.World.Enums.MovementFlagWotLK.Swimming | (uint)HermesProxy.World.Enums.MovementFlagWotLK.Flying)) != 0 ||
                    (((ushort)moveInfo.FlagsExtra & (ushort)HermesProxy.World.Enums.MovementFlagExtra.AlwaysAllowPitching) != 0);
                if (hasPitch)
                    data.WriteFloat(moveInfo.SwimPitch);

                data.WriteUInt32(moveInfo.FallTime);
                if ((movementFlags & (uint)HermesProxy.World.Enums.MovementFlagWotLK.Falling) != 0)
                {
                    data.WriteFloat(moveInfo.JumpVerticalSpeed);
                    data.WriteFloat(moveInfo.JumpSinAngle);
                    data.WriteFloat(moveInfo.JumpCosAngle);
                    data.WriteFloat(moveInfo.JumpHorizontalSpeed);
                }

                if ((movementFlags & (uint)HermesProxy.World.Enums.MovementFlagWotLK.SplineElevation) != 0)
                    data.WriteFloat(moveInfo.SplineElevation);

                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.WalkSpeed, MovementInfo.DEFAULT_WALK_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.RunSpeed, MovementInfo.DEFAULT_RUN_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.RunBackSpeed, MovementInfo.DEFAULT_RUN_BACK_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.SwimSpeed, MovementInfo.DEFAULT_SWIM_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.SwimBackSpeed, MovementInfo.DEFAULT_SWIM_BACK_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.FlightSpeed, MovementInfo.DEFAULT_FLY_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.FlightBackSpeed, MovementInfo.DEFAULT_FLY_BACK_SPEED));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.TurnRate, MovementInfo.DEFAULT_TURN_RATE));
                data.WriteFloat(GetMovementSpeedOrDefault(moveInfo.PitchRate, MovementInfo.DEFAULT_PITCH_RATE));
            }
            else if ((updateFlags & HermesProxy.World.Enums.UpdateFlag.StationaryObject) != 0)
            {
                data.WriteFloat(moveInfo.Position.X);
                data.WriteFloat(moveInfo.Position.Y);
                data.WriteFloat(moveInfo.Position.Z);
                data.WriteFloat(moveInfo.Orientation);
            }

            if ((updateFlags & HermesProxy.World.Enums.UpdateFlag.AttackingTarget) != 0)
                data.WritePackedGuid(m_updateData.CreateData.AutoAttackVictim.To64());

            if ((updateFlags & HermesProxy.World.Enums.UpdateFlag.Transport) != 0)
                data.WriteUInt32(moveInfo.TransportPathTimer);

            if ((updateFlags & HermesProxy.World.Enums.UpdateFlag.Vehicle) != 0)
            {
                data.WriteUInt32(moveInfo.VehicleId);
                data.WriteFloat(moveInfo.VehicleOrientation);
            }

            if ((updateFlags & HermesProxy.World.Enums.UpdateFlag.GORotation) != 0)
                data.WriteInt64(moveInfo.Rotation.GetPackedRotation());
        }

        public void SetCreateObjectBits()
        {
            m_createBits.Clear();
            m_createBits.PlayHoverAnim = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && m_updateData.CreateData.MoveInfo.Hover;
            m_createBits.MovementUpdate = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && m_objectTypeMask.HasAnyFlag(Enums.ObjectTypeMask.Unit);
            m_createBits.MovementTransport = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && m_updateData.CreateData.MoveInfo.TransportGuid != null && m_objectType == Enums.ObjectTypeBCC.GameObject;
            m_createBits.Stationary = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && !m_objectTypeMask.HasAnyFlag(Enums.ObjectTypeMask.Unit);
            m_createBits.ServerTime = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && m_updateData.Guid.GetHighType() == Enums.HighGuidType.Transport;
            m_createBits.CombatVictim = m_updateData.CreateData != null && m_updateData.CreateData.AutoAttackVictim != null;
            m_createBits.Vehicle = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && m_updateData.CreateData.MoveInfo.VehicleId != 0;
            m_createBits.Rotation = m_updateData.CreateData != null & m_updateData.CreateData.MoveInfo != null && m_objectType == Enums.ObjectTypeBCC.GameObject;
            m_createBits.ThisIsYou = m_createBits.ActivePlayer = m_objectType == Enums.ObjectTypeBCC.ActivePlayer;
        }

        public void BuildValuesUpdate(WorldPacket packet)
        {
            if (IsWotlkTargetBuild())
                WriteValuesToArrayWotlkMinimal();
            else
                WriteValuesToArray();
            m_fields.WriteToPacket(packet);
        }

        private static float GetMovementSpeedOrDefault(float speed, float defaultSpeed)
        {
            return speed > 0.0f ? speed : defaultSpeed;
        }

        private static uint ClampToUInt32(ulong value)
        {
            return value > uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static short ClampToInt16(int value)
        {
            if (value < short.MinValue)
                return short.MinValue;
            if (value > short.MaxValue)
                return short.MaxValue;

            return (short)value;
        }

        private HermesProxy.World.Enums.Class GetUnitClassForPowerMapping(UnitData unitData)
        {
            if (unitData.ClassId != null)
                return (HermesProxy.World.Enums.Class)unitData.ClassId.Value;
            if (unitData.PlayerClassId != null)
                return (HermesProxy.World.Enums.Class)unitData.PlayerClassId.Value;

            return m_gameState.GetUnitClass(m_updateData.Guid);
        }

        private bool ShouldUseClassPowerSlots(UnitData unitData)
        {
            return unitData.ClassId != null ||
                unitData.PlayerClassId != null ||
                m_objectTypeMask.HasAnyFlag(Enums.ObjectTypeMask.Player);
        }

        private static readonly HermesProxy.World.Enums.PowerType[] WotlkFixedPowerTypes =
        {
            HermesProxy.World.Enums.PowerType.Mana,
            HermesProxy.World.Enums.PowerType.Rage,
            HermesProxy.World.Enums.PowerType.Focus,
            HermesProxy.World.Enums.PowerType.Energy,
            HermesProxy.World.Enums.PowerType.Happiness,
            HermesProxy.World.Enums.PowerType.Rune,
            HermesProxy.World.Enums.PowerType.RunicPower
        };

        private void WriteWotlkUnitPowers(UnitData unitData)
        {
            bool useClassSlots = ShouldUseClassPowerSlots(unitData);
            bool wroteAnyPower = false;
            bool wroteAnyMaxPower = false;

            if (useClassSlots)
            {
                HermesProxy.World.Enums.Class classId = GetUnitClassForPowerMapping(unitData);
                foreach (HermesProxy.World.Enums.PowerType powerType in WotlkFixedPowerTypes)
                {
                    sbyte sourceSlot = ClassPowerTypes.GetPowerSlotForClass(classId, powerType);
                    if (sourceSlot < 0 || sourceSlot >= unitData.Power.Length)
                        continue;

                    int wotlkPowerIndex = (int)powerType;
                    if (unitData.Power[sourceSlot] != null)
                    {
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_POWER1 + wotlkPowerIndex, unitData.Power[sourceSlot].Value);
                        wroteAnyPower = true;
                    }

                    if (unitData.MaxPower[sourceSlot] != null)
                    {
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_MAXPOWER1 + wotlkPowerIndex, unitData.MaxPower[sourceSlot].Value);
                        wroteAnyMaxPower = true;
                    }
                }
            }
            else if (m_updateData.Guid.GetHighType() == Enums.HighGuidType.Pet)
            {
                foreach (HermesProxy.World.Enums.PowerType powerType in WotlkFixedPowerTypes)
                {
                    sbyte sourceSlot = ClassPowerTypes.GetPowerSlotForPet(powerType);
                    if (sourceSlot < 0 || sourceSlot >= unitData.Power.Length)
                        continue;

                    int wotlkPowerIndex = (int)powerType;
                    if (unitData.Power[sourceSlot] != null)
                    {
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_POWER1 + wotlkPowerIndex, unitData.Power[sourceSlot].Value);
                        wroteAnyPower = true;
                    }

                    if (unitData.MaxPower[sourceSlot] != null)
                    {
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_MAXPOWER1 + wotlkPowerIndex, unitData.MaxPower[sourceSlot].Value);
                        wroteAnyMaxPower = true;
                    }
                }
            }

            if (!wroteAnyPower)
            {
                for (int i = 0; i < 7; i++)
                    if (unitData.Power[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_POWER1 + i, unitData.Power[i].Value);
            }

            if (!wroteAnyMaxPower)
            {
                for (int i = 0; i < 7; i++)
                    if (unitData.MaxPower[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_MAXPOWER1 + i, unitData.MaxPower[i].Value);
            }
        }

        public void BuildDynamicValuesUpdate(WorldPacket packet)
        {
            m_dynamicFields.WriteToPacket(packet);
        }

        private void WriteValuesToArrayWotlkMinimal()
        {
            if (m_alreadyWritten)
                return;

            ObjectData objectData = m_updateData.ObjectData;
            ItemData itemData = m_updateData.ItemData;
            ContainerData containerData = m_updateData.ContainerData;
            UnitData unitData = m_updateData.UnitData;
            PlayerData playerData = m_updateData.PlayerData;
            ActivePlayerData activeData = m_updateData.ActivePlayerData;
            GameObjectData goData = m_updateData.GameObjectData;
            DynamicObjectData dynData = m_updateData.DynamicObjectData;
            CorpseData corpseData = m_updateData.CorpseData;

            bool vanillaBackend = LegacyVersion.GetUpdateFieldsDefiningBuild() == ClientVersionBuild.V1_12_1_5875;

            ulong guid64 = m_updateData.Guid.To64().GetLowValue();
            m_fields.SetUpdateField<ulong>((int)ObjectField.OBJECT_FIELD_GUID, guid64);

            // Wrath expects OBJECT_FIELD_TYPE to be legacy masks:
            // object=0x1, item=0x2, container=0x4, unit=0x8, player=0x10, gameobject=0x20, dynobj=0x40, corpse=0x80.
            // Use object class mapping directly instead of internal enum bit layout.
            uint typeMask = GetWotlkObjectTypeMask();
            m_fields.SetUpdateField<uint>((int)ObjectField.OBJECT_FIELD_TYPE, typeMask);

            if (objectData?.EntryID != null)
                m_fields.SetUpdateField<int>((int)ObjectField.OBJECT_FIELD_ENTRY, (int)objectData.EntryID);
            if (objectData?.Scale != null)
                m_fields.SetUpdateField<float>((int)ObjectField.OBJECT_FIELD_SCALE_X, (float)objectData.Scale);

            if (itemData != null)
            {
                void SetItemGuid(int idx, WowGuid128 g)
                {
                    if (g != null)
                        m_fields.SetUpdateField<ulong>(idx, g.To64().GetLowValue());
                }

                SetItemGuid((int)ItemField.ITEM_FIELD_OWNER, itemData.Owner);
                SetItemGuid((int)ItemField.ITEM_FIELD_CONTAINED, itemData.ContainedIn);
                SetItemGuid((int)ItemField.ITEM_FIELD_CREATOR, itemData.Creator);
                SetItemGuid((int)ItemField.ITEM_FIELD_GIFTCREATOR, itemData.GiftCreator);

                if (itemData.StackCount != null)
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_STACK_COUNT, (uint)itemData.StackCount);
                if (itemData.Duration != null)
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_DURATION, (uint)itemData.Duration);

                for (int i = 0; i < 5; i++)
                {
                    if (itemData.SpellCharges[i] != null)
                        m_fields.SetUpdateField<int>((int)ItemField.ITEM_FIELD_SPELL_CHARGES + i, (int)itemData.SpellCharges[i]);
                }

                if (itemData.Flags != null)
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_FLAGS, (uint)itemData.Flags);

                // WotLK 3.3.5 has 12 fixed enchantment slots, each 3 update fields:
                // ID, duration, and packed charges/inactive state.  Vanilla
                // enchantment ids do not safely line up with the 3.3.5 client
                // DBCs; weapon slots were still producing bogus tooltip stats.
                // Explicitly clear them for a 1.12 backend instead of forwarding
                // ids that only make sense to the vanilla client.
                if (vanillaBackend)
                {
                    for (int i = 0; i < 12; i++)
                    {
                        int startIndex = (int)ItemField.ITEM_FIELD_ENCHANTMENT_1_1 + i * 3;
                        m_fields.SetUpdateField<int>(startIndex, 0);
                        m_fields.SetUpdateField<uint>(startIndex + 1, 0);
                        m_fields.SetUpdateField<uint>(startIndex + 2, 0);
                    }
                }
                else
                {
                    for (int i = 0; i < 12; i++)
                    {
                        if (itemData.Enchantment[i] == null)
                            continue;

                        int startIndex = (int)ItemField.ITEM_FIELD_ENCHANTMENT_1_1 + i * 3;
                        if (itemData.Enchantment[i].ID != null)
                            m_fields.SetUpdateField<int>(startIndex, (int)itemData.Enchantment[i].ID);
                        if (itemData.Enchantment[i].Duration != null)
                            m_fields.SetUpdateField<uint>(startIndex + 1, (uint)itemData.Enchantment[i].Duration);
                        if (itemData.Enchantment[i].Charges != null)
                            m_fields.SetUpdateField<ushort>(startIndex + 2, (ushort)itemData.Enchantment[i].Charges, 0);
                        if (itemData.Enchantment[i].Inactive != null)
                            m_fields.SetUpdateField<ushort>(startIndex + 2, (ushort)itemData.Enchantment[i].Inactive, 1);
                    }
                }

                if (vanillaBackend)
                {
                    // 1.12 random-property/suffix ids are not guaranteed to exist in
                    // the 3.3.5 client DBCs.  Bad ids make the Wrath tooltip path
                    // synthesize absurd stats and can crash GameTooltip:SetInventoryItem.
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_PROPERTY_SEED, 0);
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_RANDOM_PROPERTIES_ID, 0);
                }
                else
                {
                    if (itemData.PropertySeed != null)
                        m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_PROPERTY_SEED, (uint)itemData.PropertySeed);
                    if (itemData.RandomProperty != null)
                        m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_RANDOM_PROPERTIES_ID, (uint)itemData.RandomProperty);
                }
                if (itemData.Durability != null)
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_DURABILITY, (uint)itemData.Durability);
                if (itemData.MaxDurability != null)
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_MAXDURABILITY, (uint)itemData.MaxDurability);
                if (itemData.CreatePlayedTime != null)
                    m_fields.SetUpdateField<uint>((int)ItemField.ITEM_FIELD_CREATE_PLAYED_TIME, (uint)itemData.CreatePlayedTime);
            }

            if (containerData != null)
            {
                if (containerData.NumSlots != null)
                    m_fields.SetUpdateField<uint>((int)ContainerField.CONTAINER_FIELD_NUM_SLOTS, (uint)containerData.NumSlots);

                for (int i = 0; i < 36; i++)
                {
                    if (containerData.Slots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)ContainerField.CONTAINER_FIELD_SLOT_1 + i * 2, containerData.Slots[i].To64().GetLowValue());
                }
            }

            if (unitData != null)
            {
                void SetGuid(int idx, WowGuid128 g)
                {
                    if (g != null)
                        m_fields.SetUpdateField<ulong>(idx, g.To64().GetLowValue());
                }

                SetGuid((int)UnitField.UNIT_FIELD_CHARM, unitData.Charm);
                SetGuid((int)UnitField.UNIT_FIELD_SUMMON, unitData.Summon);
                SetGuid((int)UnitField.UNIT_FIELD_CHARMEDBY, unitData.CharmedBy);
                SetGuid((int)UnitField.UNIT_FIELD_SUMMONEDBY, unitData.SummonedBy);
                SetGuid((int)UnitField.UNIT_FIELD_CREATEDBY, unitData.CreatedBy);
                SetGuid((int)UnitField.UNIT_FIELD_TARGET, unitData.Target);
                SetGuid((int)UnitField.UNIT_FIELD_CHANNEL_OBJECT, unitData.ChannelObject);

                if (unitData.ChannelData != null)
                    m_fields.SetUpdateField<int>((int)UnitField.UNIT_CHANNEL_SPELL, unitData.ChannelData.SpellID);

                if (unitData.RaceId != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.RaceId, 0);
                if (unitData.ClassId != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.ClassId, 1);
                if (unitData.SexId != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.SexId, 2);
                if (unitData.DisplayPower != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.DisplayPower, 3);

                if (unitData.Health != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_HEALTH, (int)unitData.Health);
                WriteWotlkUnitPowers(unitData);

                if (unitData.MaxHealth != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_MAXHEALTH, (int)unitData.MaxHealth);

                if (unitData.Level != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_LEVEL, (int)unitData.Level);
                if (unitData.FactionTemplate != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_FACTIONTEMPLATE, (int)unitData.FactionTemplate);
                if (unitData.Flags != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_FLAGS, (uint)unitData.Flags);
                if (unitData.Flags2 != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_FLAGS_2, (uint)unitData.Flags2);
                if (unitData.AuraState != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_AURASTATE, (uint)unitData.AuraState);

                if (unitData.AttackRoundBaseTime[0] != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_BASEATTACKTIME, (uint)unitData.AttackRoundBaseTime[0]);
                if (unitData.AttackRoundBaseTime[1] != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_UNK63, (uint)unitData.AttackRoundBaseTime[1]);
                if (unitData.RangedAttackRoundBaseTime != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_RANGEDATTACKTIME, (uint)unitData.RangedAttackRoundBaseTime);

                if (unitData.BoundingRadius != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_BOUNDINGRADIUS, (float)unitData.BoundingRadius);
                if (unitData.CombatReach != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_COMBATREACH, (float)unitData.CombatReach);
                if (unitData.DisplayID != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_DISPLAYID, (int)unitData.DisplayID);
                if (unitData.NativeDisplayID != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_NATIVEDISPLAYID, (int)unitData.NativeDisplayID);
                if (unitData.MountDisplayID != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_MOUNTDISPLAYID, (int)unitData.MountDisplayID);

                if (unitData.StandState != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.StandState, 0);
                if (unitData.PetLoyaltyIndex != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.PetLoyaltyIndex, 1);
                if (unitData.VisFlags != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.VisFlags, 2);
                if (unitData.AnimTier != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.AnimTier, 3);

                if (unitData.PetNumber != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_PETNUMBER, (uint)unitData.PetNumber);
                if (unitData.PetNameTimestamp != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_FIELD_PET_NAME_TIMESTAMP, (uint)unitData.PetNameTimestamp);

                if (objectData?.DynamicFlags != null)
                    m_fields.SetUpdateField<uint>((int)UnitField.UNIT_DYNAMIC_FLAGS, (uint)objectData.DynamicFlags);

                if (unitData.ModCastSpeed != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_MOD_CAST_SPEED, (float)unitData.ModCastSpeed);
                if (unitData.CreatedBySpell != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_CREATED_BY_SPELL, (int)unitData.CreatedBySpell);
                if (unitData.NpcFlags[0] != null) m_fields.SetUpdateField<uint>((int)UnitField.UNIT_NPC_FLAGS, (uint)unitData.NpcFlags[0]);
                if (unitData.EmoteState != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_NPC_EMOTESTATE, (int)unitData.EmoteState);

                for (int i = 0; i < 3; i++)
                {
                    if (unitData.VirtualItems[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_VIRTUAL_ITEM_SLOT_ID + i, unitData.VirtualItems[i].ItemID);
                }

                if (unitData.MinDamage != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MINDAMAGE, (float)unitData.MinDamage);
                if (unitData.MaxDamage != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MAXDAMAGE, (float)unitData.MaxDamage);
                if (unitData.MinOffHandDamage != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MINOFFHANDDAMAGE, (float)unitData.MinOffHandDamage);
                if (unitData.MaxOffHandDamage != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MAXOFFHANDDAMAGE, (float)unitData.MaxOffHandDamage);

                for (int i = 0; i < 5; i++)
                {
                    if (unitData.Stats[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_STAT0 + i, (int)unitData.Stats[i]);
                    if (unitData.StatPosBuff[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_POSSTAT0 + i, (int)unitData.StatPosBuff[i]);
                    if (unitData.StatNegBuff[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_NEGSTAT0 + i, (int)unitData.StatNegBuff[i]);
                }

                for (int i = 0; i < 7; i++)
                {
                    if (unitData.Resistances[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_RESISTANCES + i, (int)unitData.Resistances[i]);
                    if (unitData.ResistanceBuffModsPositive[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_RESISTANCEBUFFMODSPOSITIVE + i, (int)unitData.ResistanceBuffModsPositive[i]);
                    if (unitData.ResistanceBuffModsNegative[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_RESISTANCEBUFFMODSNEGATIVE + i, (int)unitData.ResistanceBuffModsNegative[i]);
                }

                if (unitData.BaseMana != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_BASE_MANA, (int)unitData.BaseMana);
                if (unitData.BaseHealth != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_BASE_HEALTH, (int)unitData.BaseHealth);

                if (unitData.SheatheState != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.SheatheState, 0);
                if (unitData.PvpFlags != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.PvpFlags, 1);
                if (unitData.PetFlags != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.PetFlags, 2);
                if (unitData.ShapeshiftForm != null) m_fields.SetUpdateField<byte>((int)UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.ShapeshiftForm, 3);

                if (unitData.AttackPower != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_ATTACK_POWER, (int)unitData.AttackPower);
                if (unitData.AttackPowerModPos != null) m_fields.SetUpdateField<ushort>((int)UnitField.UNIT_FIELD_ATTACK_POWER_MODS, unchecked((ushort)ClampToInt16((int)unitData.AttackPowerModPos)), 0);
                if (unitData.AttackPowerModNeg != null) m_fields.SetUpdateField<ushort>((int)UnitField.UNIT_FIELD_ATTACK_POWER_MODS, unchecked((ushort)ClampToInt16((int)unitData.AttackPowerModNeg)), 1);
                if (unitData.AttackPowerMultiplier != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_ATTACK_POWER_MULTIPLIER, (float)unitData.AttackPowerMultiplier);

                if (unitData.RangedAttackPower != null) m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_RANGED_ATTACK_POWER, (int)unitData.RangedAttackPower);
                if (unitData.RangedAttackPowerModPos != null) m_fields.SetUpdateField<ushort>((int)UnitField.UNIT_FIELD_RANGED_ATTACK_POWER_MODS, unchecked((ushort)ClampToInt16((int)unitData.RangedAttackPowerModPos)), 0);
                if (unitData.RangedAttackPowerModNeg != null) m_fields.SetUpdateField<ushort>((int)UnitField.UNIT_FIELD_RANGED_ATTACK_POWER_MODS, unchecked((ushort)ClampToInt16((int)unitData.RangedAttackPowerModNeg)), 1);
                if (unitData.RangedAttackPowerMultiplier != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_RANGED_ATTACK_POWER_MULTIPLIER, (float)unitData.RangedAttackPowerMultiplier);

                if (unitData.MinRangedDamage != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MINRANGEDDAMAGE, (float)unitData.MinRangedDamage);
                if (unitData.MaxRangedDamage != null) m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MAXRANGEDDAMAGE, (float)unitData.MaxRangedDamage);

                for (int i = 0; i < 7; i++)
                {
                    if (unitData.PowerCostModifier[i] != null)
                        m_fields.SetUpdateField<int>((int)UnitField.UNIT_FIELD_POWER_COST_MODIFIER + i, (int)unitData.PowerCostModifier[i]);
                    if (unitData.PowerCostMultiplier[i] != null)
                        m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_POWER_COST_MULTIPLIER1 + i, (float)unitData.PowerCostMultiplier[i]);
                }

                if (unitData.MaxHealthModifier != null)
                    m_fields.SetUpdateField<float>((int)UnitField.UNIT_FIELD_MAXHEALTHMODIFIER, (float)unitData.MaxHealthModifier);
            }

            if (playerData != null)
            {
                if (playerData.PlayerFlags != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FLAGS, (uint)playerData.PlayerFlags);
                if (playerData.GuildRankID != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_GUILDRANK, (uint)playerData.GuildRankID);
                if (playerData.DuelTeam != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_DUEL_TEAM, (uint)playerData.DuelTeam);
                if (playerData.GuildTimeStamp != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_GUILD_TIMESTAMP, (int)playerData.GuildTimeStamp);
                if (playerData.ChosenTitle != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_CHOSEN_TITLE, (int)playerData.ChosenTitle);
                if (playerData.FakeInebriation != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FAKE_INEBRIATION, (int)playerData.FakeInebriation);

                // 3.3.5 quest-log entries are 5 update fields each:
                // questId, stateFlags, objective counters low, objective counters high, endTime.
                // The vanilla backend sends 3 fields per entry; UpdateHandler expands those
                // into QuestLog objects.  Without writing these Wrath fields, the server
                // accepts the quest but the 3.3.5 client keeps an empty quest log.
                for (int i = 0; i < 25; i++)
                {
                    if (playerData.QuestLog[i] == null)
                        continue;

                    int startIndex = (int)PlayerField.PLAYER_QUEST_LOG_1_1 + i * 5;

                    bool hasQuestId = playerData.QuestLog[i].QuestID != null;

                    if (hasQuestId)
                        m_fields.SetUpdateField<int>(startIndex, (int)playerData.QuestLog[i].QuestID);

                    uint stateFlags = playerData.QuestLog[i].StateFlags ?? 0;
                    uint objectiveCountersLow = 0;
                    uint objectiveCountersHigh = 0;
                    bool hasLowCounters = false;
                    bool hasHighCounters = false;

                    for (int j = 0; j < 4; j++)
                    {
                        if (playerData.QuestLog[i].ObjectiveProgress[j] == null)
                            continue;

                        uint value = (uint)Math.Min((int)ushort.MaxValue, Math.Max(0, (int)playerData.QuestLog[i].ObjectiveProgress[j].Value));
                        if (j < 2)
                        {
                            objectiveCountersLow |= value << (j * 16);
                            hasLowCounters = true;
                        }
                        else
                        {
                            objectiveCountersHigh |= value << ((j - 2) * 16);
                            hasHighCounters = true;
                        }
                    }

                    // The vanilla backend may update only the quest-id field on
                    // accept.  Wrath's quest log is much less tolerant: it expects
                    // the row to be coherent, so emit the companion fields as
                    // zeroes when a quest slot appears.
                    if (hasQuestId || playerData.QuestLog[i].StateFlags != null)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + 1, stateFlags);
                        if (hasQuestId)
                            m_fields.m_updateMask.SetBit(startIndex + 1);
                    }
                    if (hasQuestId || hasLowCounters)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + 2, objectiveCountersLow);
                        if (hasQuestId)
                            m_fields.m_updateMask.SetBit(startIndex + 2);
                    }
                    if (hasQuestId || hasHighCounters)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + 3, objectiveCountersHigh);
                        if (hasQuestId)
                            m_fields.m_updateMask.SetBit(startIndex + 3);
                    }
                    if (hasQuestId || playerData.QuestLog[i].EndTime != null)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + 4, playerData.QuestLog[i].EndTime ?? 0);
                        if (hasQuestId)
                            m_fields.m_updateMask.SetBit(startIndex + 4);
                    }
                }

                for (int i = 0; i < 19; i++)
                {
                    if (playerData.VisibleItems[i] == null)
                        continue;

                    int startIndex = (int)PlayerField.PLAYER_VISIBLE_ITEM_1_ENTRYID + i * 2;
                    m_fields.SetUpdateField<int>(startIndex, playerData.VisibleItems[i].ItemID);
                    // Do not forward vanilla enchant visuals into the Wrath visible
                    // item enchantment field; the ids are from different tables and
                    // mainhand weapons were still showing corrupt tooltip/equipment data.
                    m_fields.SetUpdateField<uint>(startIndex + 1, vanillaBackend ? 0u : playerData.VisibleItems[i].ItemVisual);
                }
            }

            if (activeData != null)
            {
                for (int i = 0; i < 23; i++)
                {
                    if (activeData.InvSlots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_FIELD_INV_SLOT_HEAD + i * 2, activeData.InvSlots[i].To64().GetLowValue());
                }
                for (int i = 0; i < 16; i++)
                {
                    if (activeData.PackSlots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_FIELD_PACK_SLOT_1 + i * 2, activeData.PackSlots[i].To64().GetLowValue());
                }
                for (int i = 0; i < 24; i++)
                {
                    if (activeData.BankSlots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_FIELD_BANK_SLOT_1 + i * 2, activeData.BankSlots[i].To64().GetLowValue());
                }
                for (int i = 0; i < 6; i++)
                {
                    if (activeData.BankBagSlots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_FIELD_BANKBAG_SLOT_1 + i * 2, activeData.BankBagSlots[i].To64().GetLowValue());
                }
                for (int i = 0; i < 12; i++)
                {
                    if (activeData.BuyBackSlots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_FIELD_VENDORBUYBACK_SLOT_1 + i * 2, activeData.BuyBackSlots[i].To64().GetLowValue());
                }
                for (int i = 0; i < 32; i++)
                {
                    if (activeData.KeyringSlots[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_FIELD_KEYRING_SLOT_1 + i * 2, activeData.KeyringSlots[i].To64().GetLowValue());
                }

                if (activeData.XP != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_XP, (int)activeData.XP);
                if (activeData.NextLevelXP != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_NEXT_LEVEL_XP, (int)activeData.NextLevelXP);
                if (activeData.Coinage != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_COINAGE, ClampToUInt32((ulong)activeData.Coinage));
                if (activeData.CharacterPoints != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_CHARACTER_POINTS1, (int)activeData.CharacterPoints);
                if (activeData.MaxTalentTiers != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_CHARACTER_POINTS2, (int)activeData.MaxTalentTiers);
                if (activeData.TrackCreatureMask != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_TRACK_CREATURES, (uint)activeData.TrackCreatureMask);
                if (activeData.TrackResourceMask[0] != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_TRACK_RESOURCES, (uint)activeData.TrackResourceMask[0]);

                for (int i = 0; i < 128; i++)
                {
                    int startIndex = (int)PlayerField.PLAYER_SKILL_INFO_1_1 + i * 3;
                    if (activeData.Skill.SkillLineID[i] != null || activeData.Skill.SkillStep[i] != null)
                    {
                        uint lineId = activeData.Skill.SkillLineID[i] ?? 0;
                        uint step = activeData.Skill.SkillStep[i] ?? 0;
                        m_fields.SetUpdateField<uint>(startIndex, lineId | (step << 16));
                    }
                    if (activeData.Skill.SkillRank[i] != null || activeData.Skill.SkillMaxRank[i] != null)
                    {
                        uint rank = activeData.Skill.SkillRank[i] ?? 0;
                        uint maxRank = activeData.Skill.SkillMaxRank[i] ?? 0;
                        m_fields.SetUpdateField<uint>(startIndex + 1, rank | (maxRank << 16));
                    }
                    if (activeData.Skill.SkillTempBonus[i] != null || activeData.Skill.SkillPermBonus[i] != null)
                    {
                        uint tempBonus = (ushort)(activeData.Skill.SkillTempBonus[i] ?? 0);
                        uint permBonus = activeData.Skill.SkillPermBonus[i] ?? 0;
                        m_fields.SetUpdateField<uint>(startIndex + 2, tempBonus | (permBonus << 16));
                    }
                }

                // WotLK uses PLAYER_EXPLORED_ZONES_1..128 as uint32 values.
                // ActivePlayerData stores them packed as uint64 pairs.
                for (int i = 0; i < 64; i++)
                {
                    if (activeData.ExploredZones[i] != null)
                        m_fields.SetUpdateField<ulong>((int)PlayerField.PLAYER_EXPLORED_ZONES_1 + i * 2, (ulong)activeData.ExploredZones[i]);
                }

                if (activeData.BlockPercentage != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_BLOCK_PERCENTAGE, (float)activeData.BlockPercentage);
                if (activeData.DodgePercentage != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_DODGE_PERCENTAGE, (float)activeData.DodgePercentage);
                if (activeData.ParryPercentage != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_PARRY_PERCENTAGE, (float)activeData.ParryPercentage);
                if (activeData.MainhandExpertise != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_EXPERTISE, (float)activeData.MainhandExpertise);
                if (activeData.OffhandExpertise != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_OFFHAND_EXPERTISE, (float)activeData.OffhandExpertise);
                if (activeData.CritPercentage != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_CRIT_PERCENTAGE, (float)activeData.CritPercentage);
                if (activeData.RangedCritPercentage != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_RANGED_CRIT_PERCENTAGE, (float)activeData.RangedCritPercentage);
                if (activeData.OffhandCritPercentage != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_OFFHAND_CRIT_PERCENTAGE, (float)activeData.OffhandCritPercentage);
                for (int i = 0; i < 7; i++)
                {
                    if (activeData.SpellCritPercentage[i] != null)
                        m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_SPELL_CRIT_PERCENTAGE1 + i, (float)activeData.SpellCritPercentage[i]);
                    if (activeData.ModDamageDonePos[i] != null)
                        m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_MOD_DAMAGE_DONE_POS + i, (int)activeData.ModDamageDonePos[i]);
                    if (activeData.ModDamageDoneNeg[i] != null)
                        m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_MOD_DAMAGE_DONE_NEG + i, (int)activeData.ModDamageDoneNeg[i]);
                    if (activeData.ModDamageDonePercent[i] != null)
                        m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_FIELD_MOD_DAMAGE_DONE_PCT1 + i, (float)activeData.ModDamageDonePercent[i]);
                }

                if (activeData.ShieldBlock != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_SHIELD_BLOCK, (int)activeData.ShieldBlock);
                if (activeData.ModHealingDonePos != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_MOD_HEALING_DONE_POS, (int)activeData.ModHealingDonePos);
                if (activeData.ModHealingPercent != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_FIELD_MOD_HEALING_PCT, (float)activeData.ModHealingPercent);
                if (activeData.ModHealingDonePercent != null) m_fields.SetUpdateField<float>((int)PlayerField.PLAYER_FIELD_MOD_HEALING_DONE_PCT, (float)activeData.ModHealingDonePercent);
                if (activeData.ModTargetResistance != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_MOD_TARGET_RESISTANCE, (int)activeData.ModTargetResistance);
                if (activeData.ModTargetPhysicalResistance != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_MOD_TARGET_PHYSICAL_RESISTANCE, (int)activeData.ModTargetPhysicalResistance);

                if (activeData.LocalFlags != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_BYTES, (uint)activeData.LocalFlags);
                if (activeData.MultiActionBars != null) m_fields.SetUpdateField<byte>((int)PlayerField.PLAYER_FIELD_BYTES, (byte)activeData.MultiActionBars, 2);
                if (activeData.LifetimeMaxRank != null) m_fields.SetUpdateField<byte>((int)PlayerField.PLAYER_FIELD_BYTES, (byte)activeData.LifetimeMaxRank, 3);
                if (activeData.AmmoID != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_AMMO_ID, (uint)activeData.AmmoID);
                if (activeData.PvpMedals != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_PVP_MEDALS, (uint)activeData.PvpMedals);

                for (int i = 0; i < 12; i++)
                {
                    if (activeData.BuybackPrice[i] != null)
                        m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_BUYBACK_PRICE_1 + i, (uint)activeData.BuybackPrice[i]);
                    if (activeData.BuybackTimestamp[i] != null)
                        m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_BUYBACK_TIMESTAMP_1 + i, (uint)activeData.BuybackTimestamp[i]);
                }

                if (activeData.TodayHonorableKills != null) m_fields.SetUpdateField<ushort>((int)PlayerField.PLAYER_FIELD_KILLS, (ushort)activeData.TodayHonorableKills, 0);
                if (activeData.TodayDishonorableKills != null) m_fields.SetUpdateField<ushort>((int)PlayerField.PLAYER_FIELD_KILLS, (ushort)activeData.TodayDishonorableKills, 1);
                if (activeData.YesterdayContribution != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_YESTERDAY_CONTRIBUTION, (uint)activeData.YesterdayContribution);
                if (activeData.LifetimeHonorableKills != null) m_fields.SetUpdateField<uint>((int)PlayerField.PLAYER_FIELD_LIFETIME_HONORABLE_KILLS, (uint)activeData.LifetimeHonorableKills);

                for (int i = 0; i < 25; i++)
                {
                    if (activeData.CombatRatings[i] != null)
                        m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_COMBAT_RATING_1 + i, (int)activeData.CombatRatings[i]);
                }

                if (activeData.MaxLevel != null) m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_MAX_LEVEL, (int)activeData.MaxLevel);

                int watchedFaction = m_updateData.Guid == m_gameState.CurrentPlayerGuid ? -1 : (activeData.WatchedFactionIndex ?? -1);
                m_fields.SetUpdateField<int>((int)PlayerField.PLAYER_FIELD_WATCHED_FACTION_INDEX, watchedFaction);
            }

            if (goData != null)
            {
                if (goData.CreatedBy != null) m_fields.SetUpdateField<ulong>((int)GameObjectField.GAMEOBJECT_FIELD_CREATED_BY, goData.CreatedBy.To64().GetLowValue());
                if (goData.DisplayID != null) m_fields.SetUpdateField<int>((int)GameObjectField.GAMEOBJECT_DISPLAYID, (int)goData.DisplayID);
                if (goData.Flags != null) m_fields.SetUpdateField<uint>((int)GameObjectField.GAMEOBJECT_FLAGS, (uint)goData.Flags);
                if (goData.ParentRotation[0] != null) m_fields.SetUpdateField<float>((int)GameObjectField.GAMEOBJECT_PARENTROTATION + 0, (float)goData.ParentRotation[0]);
                if (goData.ParentRotation[1] != null) m_fields.SetUpdateField<float>((int)GameObjectField.GAMEOBJECT_PARENTROTATION + 1, (float)goData.ParentRotation[1]);
                if (goData.ParentRotation[2] != null) m_fields.SetUpdateField<float>((int)GameObjectField.GAMEOBJECT_PARENTROTATION + 2, (float)goData.ParentRotation[2]);
                if (goData.ParentRotation[3] != null) m_fields.SetUpdateField<float>((int)GameObjectField.GAMEOBJECT_PARENTROTATION + 3, (float)goData.ParentRotation[3]);
                if (goData.FactionTemplate != null) m_fields.SetUpdateField<int>((int)GameObjectField.GAMEOBJECT_FACTION, (int)goData.FactionTemplate);
                if (goData.Level != null) m_fields.SetUpdateField<int>((int)GameObjectField.GAMEOBJECT_LEVEL, (int)goData.Level);
                if (goData.State != null) m_fields.SetUpdateField<byte>((int)GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.State, 0);
                if (goData.TypeID != null) m_fields.SetUpdateField<byte>((int)GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.TypeID, 1);
                if (goData.ArtKit != null) m_fields.SetUpdateField<byte>((int)GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.ArtKit, 2);
                if (goData.PercentHealth != null) m_fields.SetUpdateField<byte>((int)GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.PercentHealth, 3);
            }

            if (dynData != null)
            {
                if (dynData.Caster != null) m_fields.SetUpdateField<ulong>((int)DynamicObjectField.DYNAMICOBJECT_CASTER, dynData.Caster.To64().GetLowValue());
                if (dynData.Type != null) m_fields.SetUpdateField<byte>((int)DynamicObjectField.DYNAMICOBJECT_BYTES, (byte)dynData.Type, 0);
                if (dynData.SpellXSpellVisualID != null) m_fields.SetUpdateField<byte>((int)DynamicObjectField.DYNAMICOBJECT_BYTES, (byte)dynData.SpellXSpellVisualID, 1);
                if (dynData.SpellID != null) m_fields.SetUpdateField<int>((int)DynamicObjectField.DYNAMICOBJECT_SPELLID, (int)dynData.SpellID);
                if (dynData.Radius != null) m_fields.SetUpdateField<float>((int)DynamicObjectField.DYNAMICOBJECT_RADIUS, (float)dynData.Radius);
                if (dynData.CastTime != null) m_fields.SetUpdateField<uint>((int)DynamicObjectField.DYNAMICOBJECT_CASTTIME, (uint)dynData.CastTime);
            }

            if (corpseData != null)
            {
                if (corpseData.Owner != null) m_fields.SetUpdateField<ulong>((int)CorpseField.CORPSE_FIELD_OWNER, corpseData.Owner.To64().GetLowValue());
                if (corpseData.PartyGUID != null) m_fields.SetUpdateField<ulong>((int)CorpseField.CORPSE_FIELD_PARTY, corpseData.PartyGUID.To64().GetLowValue());
                if (corpseData.DisplayID != null) m_fields.SetUpdateField<uint>((int)CorpseField.CORPSE_FIELD_DISPLAY_ID, (uint)corpseData.DisplayID);
                for (int i = 0; i < 19; i++)
                {
                    if (corpseData.Items[i] != null)
                        m_fields.SetUpdateField<uint>((int)CorpseField.CORPSE_FIELD_ITEM + i, (uint)corpseData.Items[i]);
                }
                if (corpseData.RaceId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_1, (byte)corpseData.RaceId, 1);
                if (corpseData.SexId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_1, (byte)corpseData.SexId, 2);
                if (corpseData.SkinId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_1, (byte)corpseData.SkinId, 3);
                if (corpseData.FaceId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_2, (byte)corpseData.FaceId, 0);
                if (corpseData.HairStyleId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_2, (byte)corpseData.HairStyleId, 1);
                if (corpseData.HairColorId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_2, (byte)corpseData.HairColorId, 2);
                if (corpseData.FacialHairId != null) m_fields.SetUpdateField<byte>((int)CorpseField.CORPSE_FIELD_BYTES_2, (byte)corpseData.FacialHairId, 3);
                if (corpseData.GuildGUID != null) m_fields.SetUpdateField<uint>((int)CorpseField.CORPSE_FIELD_GUILD, (uint)corpseData.GuildGUID.GetCounter());
                if (corpseData.Flags != null) m_fields.SetUpdateField<uint>((int)CorpseField.CORPSE_FIELD_FLAGS, (uint)corpseData.Flags);
                if (corpseData.DynamicFlags != null) m_fields.SetUpdateField<uint>((int)CorpseField.CORPSE_FIELD_DYNAMIC_FLAGS, (uint)corpseData.DynamicFlags);
            }

            m_alreadyWritten = true;
        }

        public void BuildMovementUpdate(WorldPacket data)
        {
            int PauseTimesCount = 0;

            data.WriteBit(m_createBits.NoBirthAnim);
            data.WriteBit(m_createBits.EnablePortals);
            data.WriteBit(m_createBits.PlayHoverAnim);
            data.WriteBit(m_createBits.MovementUpdate);
            data.WriteBit(m_createBits.MovementTransport);
            data.WriteBit(m_createBits.Stationary);
            data.WriteBit(m_createBits.CombatVictim);
            data.WriteBit(m_createBits.ServerTime);
            data.WriteBit(m_createBits.Vehicle);
            data.WriteBit(m_createBits.AnimKit);
            data.WriteBit(m_createBits.Rotation);
            data.WriteBit(m_createBits.AreaTrigger);
            data.WriteBit(m_createBits.GameObject);
            data.WriteBit(m_createBits.SmoothPhasing);
            data.WriteBit(m_createBits.ThisIsYou);
            data.WriteBit(m_createBits.SceneObject);
            data.WriteBit(m_createBits.ActivePlayer);
            data.WriteBit(m_createBits.Conversation);
            data.FlushBits();

            if (m_createBits.MovementUpdate)
            {
                MovementInfo moveInfo = m_updateData.CreateData.MoveInfo;
                bool hasSpline = m_updateData.CreateData.MoveSpline != null;
                moveInfo.WriteMovementInfoModern(data, m_updateData.Guid);

                data.WriteFloat(moveInfo.WalkSpeed);
                data.WriteFloat(moveInfo.RunSpeed);
                data.WriteFloat(moveInfo.RunBackSpeed);
                data.WriteFloat(moveInfo.SwimSpeed);
                data.WriteFloat(moveInfo.SwimBackSpeed);
                data.WriteFloat(moveInfo.FlightSpeed);
                data.WriteFloat(moveInfo.FlightBackSpeed);
                data.WriteFloat(moveInfo.TurnRate);
                data.WriteFloat(moveInfo.PitchRate);

                //MovementForces movementForces = unit.GetMovementForces();
                //if (movementForces != null)
                //{
                //    data.WriteInt32(movementForces.GetForces().Count);
                //    data.WriteFloat(movementForces.GetModMagnitude());          // MovementForcesModMagnitude
                //}
                //else
                //{
                    data.WriteUInt32(0);
                    data.WriteFloat(1.0f);                                       // MovementForcesModMagnitude
                //}

                data.WriteBit(hasSpline);
                data.FlushBits();

                //if (movementForces != null)
                //    foreach (MovementForce force in movementForces.GetForces())
                //        MovementExtensions.WriteMovementForceWithDirection(force, data, unit);

                // HasMovementSpline - marks that spline data is present in packet
                if (hasSpline)
                    WriteCreateObjectSplineDataBlock(m_updateData.CreateData.MoveSpline, data);
            }

            data.WriteInt32(PauseTimesCount);

            if (m_createBits.Stationary)
            {
                data.WriteFloat(m_updateData.CreateData.MoveInfo.Position.X);
                data.WriteFloat(m_updateData.CreateData.MoveInfo.Position.Y);
                data.WriteFloat(m_updateData.CreateData.MoveInfo.Position.Z);
                data.WriteFloat(m_updateData.CreateData.MoveInfo.Orientation);
            }

            if (m_createBits.CombatVictim)
                data.WritePackedGuid128(m_updateData.CreateData.AutoAttackVictim); // CombatVictim

            if (m_createBits.ServerTime)
            {
                /** @TODO Use IsTransport() to also handle type 11 (TRANSPORT)
                    Currently grid objects are not updated if there are no nearby players,
                    this causes clients to receive different PathProgress
                    resulting in players seeing the object in a different position
                */
                if (m_updateData.CreateData.MoveInfo.TransportPathTimer != 0) // ServerTime
                    data.WriteUInt32(m_updateData.CreateData.MoveInfo.TransportPathTimer);
                else
                    data.WriteUInt32((uint)Time.UnixTime);
            }

            if (m_createBits.Vehicle)
            {
                data.WriteUInt32(m_updateData.CreateData.MoveInfo.VehicleId); // RecID
                data.WriteFloat(m_updateData.CreateData.MoveInfo.VehicleOrientation); // InitialRawFacing
            }

            if (m_createBits.AnimKit)
            {
                data.WriteUInt16(0); // AiID
                data.WriteUInt16(0); // MovementID
                data.WriteUInt16(0); // MeleeID
            }

            if (m_createBits.Rotation)
                data.WriteInt64(m_updateData.CreateData.MoveInfo.Rotation.GetPackedRotation()); // Rotation

            for (int i = 0; i < PauseTimesCount; ++i)
                data.WriteUInt32(0);

            if (m_createBits.MovementTransport)
                m_updateData.CreateData.MoveInfo.WriteTransportInfoModern(data);

            /*
            if (m_createBits.AreaTrigger)
            {
                AreaTrigger areaTrigger = ToAreaTrigger();
                AreaTriggerMiscTemplate areaTriggerMiscTemplate = areaTrigger.GetMiscTemplate();
                AreaTriggerTemplate areaTriggerTemplate = areaTrigger.GetTemplate();

                data.WriteUInt32(areaTrigger.GetTimeSinceCreated());

                data.WriteVector3(areaTrigger.GetRollPitchYaw());

                bool hasAbsoluteOrientation = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasAbsoluteOrientation);
                bool hasDynamicShape = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasDynamicShape);
                bool hasAttached = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasAttached);
                bool hasFaceMovementDir = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasFaceMovementDir);
                bool hasFollowsTerrain = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasFollowsTerrain);
                bool hasUnk1 = areaTriggerTemplate.HasFlag(AreaTriggerFlags.Unk1);
                bool hasTargetRollPitchYaw = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasTargetRollPitchYaw);
                bool hasScaleCurveID = areaTriggerMiscTemplate.ScaleCurveId != 0;
                bool hasMorphCurveID = areaTriggerMiscTemplate.MorphCurveId != 0;
                bool hasFacingCurveID = areaTriggerMiscTemplate.FacingCurveId != 0;
                bool hasMoveCurveID = areaTriggerMiscTemplate.MoveCurveId != 0;
                bool hasAnimation = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasAnimID);
                bool hasUnk3 = areaTriggerTemplate.HasFlag(AreaTriggerFlags.Unk3);
                bool hasAnimKitID = areaTriggerTemplate.HasFlag(AreaTriggerFlags.HasAnimKitID);
                bool hasAnimProgress = false;
                bool hasAreaTriggerSphere = areaTriggerTemplate.IsSphere();
                bool hasAreaTriggerBox = areaTriggerTemplate.IsBox();
                bool hasAreaTriggerPolygon = areaTriggerTemplate.IsPolygon();
                bool hasAreaTriggerCylinder = areaTriggerTemplate.IsCylinder();
                bool hasAreaTriggerSpline = areaTrigger.HasSplines();
                bool hasOrbit = areaTrigger.HasOrbit();
                bool hasMovementScript = false;

                data.WriteBit(hasAbsoluteOrientation);
                data.WriteBit(hasDynamicShape);
                data.WriteBit(hasAttached);
                data.WriteBit(hasFaceMovementDir);
                data.WriteBit(hasFollowsTerrain);
                data.WriteBit(hasUnk1);
                data.WriteBit(hasTargetRollPitchYaw);
                data.WriteBit(hasScaleCurveID);
                data.WriteBit(hasMorphCurveID);
                data.WriteBit(hasFacingCurveID);
                data.WriteBit(hasMoveCurveID);
                data.WriteBit(hasAnimation);
                data.WriteBit(hasAnimKitID);
                data.WriteBit(hasUnk3);
                data.WriteBit(hasAnimProgress);
                data.WriteBit(hasAreaTriggerSphere);
                data.WriteBit(hasAreaTriggerBox);
                data.WriteBit(hasAreaTriggerPolygon);
                data.WriteBit(hasAreaTriggerCylinder);
                data.WriteBit(hasAreaTriggerSpline);
                data.WriteBit(hasOrbit);
                data.WriteBit(hasMovementScript);

                if (hasUnk3)
                    data.WriteBit(false);

                data.FlushBits();

                if (hasAreaTriggerSpline)
                {
                    data.WriteUInt32(areaTrigger.GetTimeToTarget());
                    data.WriteUInt32(areaTrigger.GetElapsedTimeForMovement());

                    MovementExtensions.WriteCreateObjectAreaTriggerSpline(areaTrigger.GetSpline(), data);
                }

                if (hasTargetRollPitchYaw)
                    data.WriteVector3(areaTrigger.GetTargetRollPitchYaw());

                if (hasScaleCurveID)
                    data.WriteUInt32(areaTriggerMiscTemplate.ScaleCurveId);

                if (hasMorphCurveID)
                    data.WriteUInt32(areaTriggerMiscTemplate.MorphCurveId);

                if (hasFacingCurveID)
                    data.WriteUInt32(areaTriggerMiscTemplate.FacingCurveId);

                if (hasMoveCurveID)
                    data.WriteUInt32(areaTriggerMiscTemplate.MoveCurveId);

                if (hasAnimation)
                    data.WriteUInt32(areaTriggerMiscTemplate.AnimId);

                if (hasAnimKitID)
                    data.WriteUInt32(areaTriggerMiscTemplate.AnimKitId);

                if (hasAnimProgress)
                    data.WriteUInt32(0);

                if (hasAreaTriggerSphere)
                {
                    data.WriteFloat(areaTriggerTemplate.SphereDatas.Radius);
                    data.WriteFloat(areaTriggerTemplate.SphereDatas.RadiusTarget);
                }

                if (hasAreaTriggerBox)
                {
                    unsafe
                    {
                        data.WriteFloat(areaTriggerTemplate.BoxDatas.Extents[0]);
                        data.WriteFloat(areaTriggerTemplate.BoxDatas.Extents[1]);
                        data.WriteFloat(areaTriggerTemplate.BoxDatas.Extents[2]);

                        data.WriteFloat(areaTriggerTemplate.BoxDatas.ExtentsTarget[0]);
                        data.WriteFloat(areaTriggerTemplate.BoxDatas.ExtentsTarget[1]);
                        data.WriteFloat(areaTriggerTemplate.BoxDatas.ExtentsTarget[2]);
                    }
                }

                if (hasAreaTriggerPolygon)
                {
                    data.WriteInt32(areaTriggerTemplate.PolygonVertices.Count);
                    data.WriteInt32(areaTriggerTemplate.PolygonVerticesTarget.Count);
                    data.WriteFloat(areaTriggerTemplate.PolygonDatas.Height);
                    data.WriteFloat(areaTriggerTemplate.PolygonDatas.HeightTarget);

                    foreach (var vertice in areaTriggerTemplate.PolygonVertices)
                        data.WriteVector2(vertice);

                    foreach (var vertice in areaTriggerTemplate.PolygonVerticesTarget)
                        data.WriteVector2(vertice);
                }

                if (hasAreaTriggerCylinder)
                {
                    data.WriteFloat(areaTriggerTemplate.CylinderDatas.Radius);
                    data.WriteFloat(areaTriggerTemplate.CylinderDatas.RadiusTarget);
                    data.WriteFloat(areaTriggerTemplate.CylinderDatas.Height);
                    data.WriteFloat(areaTriggerTemplate.CylinderDatas.HeightTarget);
                    data.WriteFloat(areaTriggerTemplate.CylinderDatas.LocationZOffset);
                    data.WriteFloat(areaTriggerTemplate.CylinderDatas.LocationZOffsetTarget);
                }

                //if (hasMovementScript)
                //    *data << *areaTrigger->GetMovementScript(); // AreaTriggerMovementScriptInfo

                if (hasOrbit)
                    areaTrigger.GetCircularMovementInfo().Value.Write(data);
            }
            */

            if (m_createBits.GameObject)
            {
                bool bit8 = false;
                uint Int1 = 0;

                data.WriteUInt32(0); // WorldEffectID

                data.WriteBit(bit8);
                data.FlushBits();
                if (bit8)
                    data.WriteUInt32(Int1);
            }

            //if (m_createBits.SmoothPhasing)
            //{
            //    data.WriteBit(ReplaceActive);
            //    data.WriteBit(StopAnimKits);
            //    data.WriteBit(HasReplaceObjectt);
            //    data.FlushBits();
            //    if (HasReplaceObject)
            //        *data << ObjectGuid(ReplaceObject);
            //}

            //if (m_createBits.SceneObject)
            //{
            //    data.WriteBit(HasLocalScriptData);
            //    data.WriteBit(HasPetBattleFullUpdate);
            //    data.FlushBits();

            //    if (HasLocalScriptData)
            //    {
            //        data.WriteBits(Data.length(), 7);
            //        data.FlushBits();
            //        data.WriteString(Data);
            //    }

            //    if (HasPetBattleFullUpdate)
            //    {
            //        for (std::size_t i = 0; i < 2; ++i)
            //        {
            //            *data << ObjectGuid(Players[i].CharacterID);
            //            *data << int32(Players[i].TrapAbilityID);
            //            *data << int32(Players[i].TrapStatus);
            //            *data << uint16(Players[i].RoundTimeSecs);
            //            *data << int8(Players[i].FrontPet);
            //            *data << uint8(Players[i].InputFlags);

            //            data.WriteBits(Players[i].Pets.size(), 2);
            //            data.FlushBits();
            //            for (std::size_t j = 0; j < Players[i].Pets.size(); ++j)
            //            {
            //                *data << ObjectGuid(Players[i].Pets[j].BattlePetGUID);
            //                *data << int32(Players[i].Pets[j].SpeciesID);
            //                *data << int32(Players[i].Pets[j].DisplayID);
            //                *data << int32(Players[i].Pets[j].CollarID);
            //                *data << int16(Players[i].Pets[j].Level);
            //                *data << int16(Players[i].Pets[j].Xp);
            //                *data << int32(Players[i].Pets[j].CurHealth);
            //                *data << int32(Players[i].Pets[j].MaxHealth);
            //                *data << int32(Players[i].Pets[j].Power);
            //                *data << int32(Players[i].Pets[j].Speed);
            //                *data << int32(Players[i].Pets[j].NpcTeamMemberID);
            //                *data << uint16(Players[i].Pets[j].BreedQuality);
            //                *data << uint16(Players[i].Pets[j].StatusFlags);
            //                *data << int8(Players[i].Pets[j].Slot);

            //                *data << uint32(Players[i].Pets[j].Abilities.size());
            //                *data << uint32(Players[i].Pets[j].Auras.size());
            //                *data << uint32(Players[i].Pets[j].States.size());
            //                for (std::size_t k = 0; k < Players[i].Pets[j].Abilities.size(); ++k)
            //                {
            //                    *data << int32(Players[i].Pets[j].Abilities[k].AbilityID);
            //                    *data << int16(Players[i].Pets[j].Abilities[k].CooldownRemaining);
            //                    *data << int16(Players[i].Pets[j].Abilities[k].LockdownRemaining);
            //                    *data << int8(Players[i].Pets[j].Abilities[k].AbilityIndex);
            //                    *data << uint8(Players[i].Pets[j].Abilities[k].Pboid);
            //                }

            //                for (std::size_t k = 0; k < Players[i].Pets[j].Auras.size(); ++k)
            //                {
            //                    *data << int32(Players[i].Pets[j].Auras[k].AbilityID);
            //                    *data << uint32(Players[i].Pets[j].Auras[k].InstanceID);
            //                    *data << int32(Players[i].Pets[j].Auras[k].RoundsRemaining);
            //                    *data << int32(Players[i].Pets[j].Auras[k].CurrentRound);
            //                    *data << uint8(Players[i].Pets[j].Auras[k].CasterPBOID);
            //                }

            //                for (std::size_t k = 0; k < Players[i].Pets[j].States.size(); ++k)
            //                {
            //                    *data << uint32(Players[i].Pets[j].States[k].StateID);
            //                    *data << int32(Players[i].Pets[j].States[k].StateValue);
            //                }

            //                data.WriteBits(Players[i].Pets[j].CustomName.length(), 7);
            //                data.FlushBits();
            //                data.WriteString(Players[i].Pets[j].CustomName);
            //            }
            //        }

            //        for (std::size_t i = 0; i < 3; ++i)
            //        {
            //            *data << uint32(Enviros[j].Auras.size());
            //            *data << uint32(Enviros[j].States.size());
            //            for (std::size_t j = 0; j < Enviros[j].Auras.size(); ++j)
            //            {
            //                *data << int32(Enviros[j].Auras[j].AbilityID);
            //                *data << uint32(Enviros[j].Auras[j].InstanceID);
            //                *data << int32(Enviros[j].Auras[j].RoundsRemaining);
            //                *data << int32(Enviros[j].Auras[j].CurrentRound);
            //                *data << uint8(Enviros[j].Auras[j].CasterPBOID);
            //            }

            //            for (std::size_t j = 0; j < Enviros[j].States.size(); ++j)
            //            {
            //                *data << uint32(Enviros[i].States[j].StateID);
            //                *data << int32(Enviros[i].States[j].StateValue);
            //            }
            //        }

            //        *data << uint16(WaitingForFrontPetsMaxSecs);
            //        *data << uint16(PvpMaxRoundTime);
            //        *data << int32(CurRound);
            //        *data << uint32(NpcCreatureID);
            //        *data << uint32(NpcDisplayID);
            //        *data << int8(CurPetBattleState);
            //        *data << uint8(ForfeitPenalty);
            //        *data << ObjectGuid(InitialWildPetGUID);
            //        data.WriteBit(IsPVP);
            //        data.WriteBit(CanAwardXP);
            //        data.FlushBits();
            //    }
            //}

            if (m_createBits.ActivePlayer)
            {
                bool hasSceneInstanceIDs = false;
                bool hasRuneState = false;
                bool hasActionButtons = m_gameState.ActionButtons.Count != 0;

                data.WriteBit(hasSceneInstanceIDs);
                data.WriteBit(hasRuneState);
                data.WriteBit(hasActionButtons);
                data.FlushBits();

                if (hasSceneInstanceIDs)
                {
                    var sceneInstanceIDs = 0;
                    data.WriteInt32(sceneInstanceIDs);
                    for (var i = 0; i < sceneInstanceIDs; ++i)
                        data.WriteInt32(0); // SceneInstanceIDs
                }

                if (hasRuneState)
                {
                    byte RechargingRuneMask = 0;
                    byte UsableRuneMask = 0;
                    data.WriteUInt8(RechargingRuneMask);
                    data.WriteUInt8(UsableRuneMask);

                    uint runeCount = 0;
                    data.WriteUInt32(runeCount);
                    for (var i = 0; i < runeCount; ++i)
                        data.WriteUInt8(0); // RuneCooldown
                }

                if (hasActionButtons)
                {
                    for (int i = 0; i < 132; i++)
                        data.WriteInt32(m_gameState.ActionButtons[i]);
                }
            }

            /*
            if (m_createBits.Conversation)
            {
                Conversation self = ToConversation();
                if (data.WriteBit(self.GetTextureKitId() != 0))
                    data.WriteUInt32(self.GetTextureKitId());
                data.FlushBits();
            }
            */
        }

        public static void WriteCreateObjectSplineDataBlock(ServerSideMovement moveSpline, WorldPacket data)
        {
            data.WriteUInt32(moveSpline.SplineId);                                          // ID

            if (!moveSpline.SplineFlags.HasAnyFlag(Enums.SplineFlagModern.Cyclic))          // Destination
                data.WriteVector3(moveSpline.EndPosition);
            else
                data.WriteVector3(Vector3.Zero);

            bool hasSplineMove = data.WriteBit(moveSpline.SplineCount != 0);
            data.FlushBits();

            if (hasSplineMove)
            {
                data.WriteUInt32((uint)moveSpline.SplineFlags);                             // SplineFlags
                data.WriteUInt32(moveSpline.SplineTime);                                    // Elapsed
                data.WriteUInt32(moveSpline.SplineTimeFull);                                // Duration
                data.WriteFloat(1.0f);                                                      // DurationModifier
                data.WriteFloat(1.0f);                                                      // NextDurationModifier
                data.WriteBits((byte)moveSpline.SplineType, 2);                             // Face
                bool hasFadeObjectTime = data.WriteBit(false);
                data.WriteBits(moveSpline.SplineCount, 16);
                data.WriteBit(false);                                                       // HasSplineFilter
                data.WriteBit(false);                                                       // HasSpellEffectExtraData
                data.WriteBit(false);                                                       // HasJumpExtraData
                data.WriteBit(false);                                                       // HasAnimationTierTransition
                data.FlushBits();

                //if (HasSplineFilterKey)
                //{
                //    data << uint32(FilterKeysCount);
                //    for (var i = 0; i < FilterKeysCount; ++i)
                //    {
                //        data << float(In);
                //        data << float(Out);
                //    }

                //    data.WriteBits(FilterFlags, 2);
                //    data.FlushBits();
                //}

                switch (moveSpline.SplineType)
                {
                    case Enums.SplineTypeModern.FacingSpot:
                        data.WriteVector3(moveSpline.FinalFacingSpot);  // FaceSpot
                        break;
                    case Enums.SplineTypeModern.FacingTarget:
                        data.WritePackedGuid128(moveSpline.FinalFacingGuid); // FaceGUID
                        break;
                    case Enums.SplineTypeModern.FacingAngle:
                        data.WriteFloat(moveSpline.FinalOrientation);   // FaceDirection
                        break;
                }

                if (hasFadeObjectTime)
                    data.WriteInt32(0); // FadeObjectTime

                foreach (var vec in moveSpline.SplinePoints)
                    data.WriteVector3(vec);

                /*
                if (moveSpline.spell_effect_extra.HasValue)
                {
                    data.WritePackedGuid(moveSpline.spell_effect_extra.Value.Target);
                    data.WriteUInt32(moveSpline.spell_effect_extra.Value.SpellVisualId);
                    data.WriteUInt32(moveSpline.spell_effect_extra.Value.ProgressCurveId);
                    data.WriteUInt32(moveSpline.spell_effect_extra.Value.ParabolicCurveId);
                }
                
                if (moveSpline.splineflags.HasFlag(SplineFlag.Parabolic))
                {
                    data.WriteFloat(moveSpline.vertical_acceleration);
                    data.WriteInt32(moveSpline.effect_start_time);
                    data.WriteUInt32(0);                                                  // Duration (override)
                }

                if (moveSpline.anim_tier.HasValue)
                {
                    data.WriteUInt32(moveSpline.anim_tier.Value.TierTransitionId);
                    data.WriteInt32(moveSpline.effect_start_time);
                    data.WriteUInt32(0);
                    data.WriteUInt8(moveSpline.anim_tier.Value.AnimTier);
                }*/
            }
        }

        public void WriteValuesToArray()
        {
            if (m_alreadyWritten)
                return;

            ObjectData objectData = m_updateData.ObjectData;
            if (objectData.Guid != null)
                m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ObjectField.OBJECT_FIELD_GUID, objectData.Guid);
            if (objectData.EntryID != null)
                m_fields.SetUpdateField<int>(BccUpdateFields.ObjectField.OBJECT_FIELD_ENTRY, (int)objectData.EntryID);
            if (objectData.DynamicFlags != null)
                m_fields.SetUpdateField<uint>(BccUpdateFields.ObjectField.OBJECT_DYNAMIC_FLAGS, (uint)objectData.DynamicFlags);
            if (objectData.Scale != null)
                m_fields.SetUpdateField<float>(BccUpdateFields.ObjectField.OBJECT_FIELD_SCALE_X, (float)objectData.Scale);

            ItemData itemData = m_updateData.ItemData;
            if (itemData != null)
            {
                if (itemData.Owner != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ItemField.ITEM_FIELD_OWNER, itemData.Owner);
                if (itemData.ContainedIn != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ItemField.ITEM_FIELD_CONTAINED, itemData.ContainedIn);
                if (itemData.Creator != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ItemField.ITEM_FIELD_CREATOR, itemData.Creator);
                if (itemData.GiftCreator != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ItemField.ITEM_FIELD_GIFTCREATOR, itemData.GiftCreator);
                if (itemData.StackCount != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_STACK_COUNT, (uint)itemData.StackCount);
                if (itemData.Duration != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_DURATION, (uint)itemData.Duration);
                for (int i = 0; i < 5; i++)
                {
                    int startIndex = (int)BccUpdateFields.ItemField.ITEM_FIELD_SPELL_CHARGES;
                    if (itemData.SpellCharges[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)itemData.SpellCharges[i]);
                }
                if (itemData.Flags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_FLAGS, (uint)itemData.Flags);
                for (int i = 0; i < 13; i++)
                {
                    int startIndex = (int)BccUpdateFields.ItemField.ITEM_FIELD_ENCHANTMENT;
                    int sizePerEntry = 3;
                    if (itemData.Enchantment[i] != null)
                    {
                        if (itemData.Enchantment[i].ID != null)
                            m_fields.SetUpdateField<int>(startIndex + i * sizePerEntry, (int)itemData.Enchantment[i].ID);
                        if (itemData.Enchantment[i].Duration != null)
                            m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 1, (uint)itemData.Enchantment[i].Duration);
                        if (itemData.Enchantment[i].Charges != null)
                            m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 2, (ushort)itemData.Enchantment[i].Charges, 0);
                        if (itemData.Enchantment[i].Inactive != null)
                            m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 2, (ushort)itemData.Enchantment[i].Inactive, 1);
                    }
                }
                if (itemData.PropertySeed != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_PROPERTY_SEED, (uint)itemData.PropertySeed);
                if (itemData.RandomProperty != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_RANDOM_PROPERTIES_ID, (uint)itemData.RandomProperty);
                if (itemData.Durability != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_DURABILITY, (uint)itemData.Durability);
                if (itemData.MaxDurability != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_MAXDURABILITY, (uint)itemData.MaxDurability);
                if (itemData.CreatePlayedTime != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_CREATE_PLAYED_TIME, (uint)itemData.CreatePlayedTime);
                if (itemData.ModifiersMask != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_MODIFIERS_MASK, (uint)itemData.ModifiersMask);
                if (itemData.Context != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ItemField.ITEM_FIELD_CONTEXT, (int)itemData.Context);
                if (itemData.ArtifactXP != null)
                    m_fields.SetUpdateField<ulong>(BccUpdateFields.ItemField.ITEM_FIELD_ARTIFACT_XP, (ulong)itemData.ArtifactXP);
                if (itemData.ItemAppearanceModID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ItemField.ITEM_FIELD_APPEARANCE_MOD_ID, (uint)itemData.ItemAppearanceModID);

                // Dynamic Fields
                if (itemData.HasGemsUpdate)
                {
                    uint[] fields = new uint[30];
                    uint[] gems = m_gameState.GetGemsForItem(m_updateData.Guid);
                    fields[0] = (uint)gems[0];
                    fields[10] = (uint)gems[1];
                    fields[20] = (uint)gems[2];
                    m_dynamicFields.SetUpdateField((int)BccUpdateFields.ItemDynamicField.ITEM_DYNAMIC_FIELD_GEMS, fields, DynamicFieldChangeType.ValueAndSizeChanged);
                }
            }

            ContainerData containerData = m_updateData.ContainerData;
            if (containerData != null)
            {
                for (int i = 0; i < 36; i++)
                {
                    int startIndex = (int)BccUpdateFields.ContainerField.CONTAINER_FIELD_SLOT_1;
                    int sizePerEntry = 4;
                    if (containerData.Slots[i] != null)
                    {
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, containerData.Slots[i]);
                    }
                }
                if (containerData.NumSlots != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ContainerField.CONTAINER_FIELD_NUM_SLOTS, (uint)containerData.NumSlots);
            }

            UnitData unitData = m_updateData.UnitData;
            if (unitData != null)
            {
                if (unitData.Charm != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_CHARM, unitData.Charm);
                if (unitData.Summon != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_SUMMON, unitData.Summon);
                if (unitData.Critter != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_CRITTER, unitData.Critter);
                if (unitData.CharmedBy != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_CHARMEDBY, unitData.CharmedBy);
                if (unitData.SummonedBy != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_SUMMONEDBY, unitData.SummonedBy);
                if (unitData.CreatedBy != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_CREATEDBY, unitData.CreatedBy);
                if (unitData.DemonCreator != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_DEMON_CREATOR, unitData.DemonCreator);
                if (unitData.LookAtControllerTarget != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_LOOK_AT_CONTROLLER_TARGET, unitData.LookAtControllerTarget);
                if (unitData.Target != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_TARGET, unitData.Target);
                if (unitData.BattlePetCompanionGUID != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_BATTLE_PET_COMPANION_GUID, unitData.BattlePetCompanionGUID);
                if (unitData.BattlePetDBID != null)
                    m_fields.SetUpdateField<ulong>(BccUpdateFields.UnitField.UNIT_FIELD_BATTLE_PET_DB_ID, (ulong)unitData.BattlePetDBID);
                if (unitData.ChannelData != null)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_CHANNEL_DATA;
                    m_fields.SetUpdateField<int>(startIndex, (int)unitData.ChannelData.SpellID);
                    m_fields.SetUpdateField<int>(startIndex + 1, (int)unitData.ChannelData.SpellXSpellVisualID);
                }
                if (unitData.SummonedByHomeRealm != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_SUMMONED_BY_HOME_REALM, (uint)unitData.SummonedByHomeRealm);
                if (unitData.RaceId != null || unitData.ClassId != null || unitData.PlayerClassId != null || unitData.SexId != null)
                {
                    if (unitData.RaceId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.RaceId, 0);
                    if (unitData.ClassId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.ClassId, 1);
                    if (unitData.PlayerClassId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.PlayerClassId, 2);
                    if (unitData.SexId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_0, (byte)unitData.SexId, 3);
                }
                if (unitData.DisplayPower != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_DISPLAY_POWER, (uint)unitData.DisplayPower);
                if (unitData.OverrideDisplayPowerID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_OVERRIDE_DISPLAY_POWER_ID, (uint)unitData.OverrideDisplayPowerID);
                if (unitData.Health != null)
                    m_fields.SetUpdateField<ulong>(BccUpdateFields.UnitField.UNIT_FIELD_HEALTH, (ulong)unitData.Health);
                for (int i = 0; i < 6; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_POWER;
                    if (unitData.Power[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.Power[i]);
                }
                if (unitData.MaxHealth != null)
                    m_fields.SetUpdateField<ulong>(BccUpdateFields.UnitField.UNIT_FIELD_MAXHEALTH, (ulong)unitData.MaxHealth);
                for (int i = 0; i < 6; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_MAXPOWER;
                    if (unitData.MaxPower[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.MaxPower[i]);
                }
                for (int i = 0; i < 6; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_MOD_POWER_REGEN;
                    if (unitData.ModPowerRegen[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)unitData.ModPowerRegen[i]);
                }
                if (unitData.Level != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_LEVEL, (int)unitData.Level);
                if (unitData.EffectiveLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_EFFECTIVE_LEVEL, (int)unitData.EffectiveLevel);
                if (unitData.ContentTuningID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_CONTENT_TUNING_ID, (int)unitData.ContentTuningID);
                if (unitData.ScalingLevelMin != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALING_LEVEL_MIN, (int)unitData.ScalingLevelMin);
                if (unitData.ScalingLevelMax != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALING_LEVEL_MAX, (int)unitData.ScalingLevelMax);
                if (unitData.ScalingLevelDelta != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALING_LEVEL_DELTA, (int)unitData.ScalingLevelDelta);
                if (unitData.ScalingFactionGroup != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALING_FACTION_GROUP, (int)unitData.ScalingFactionGroup);
                if (unitData.ScalingHealthItemLevelCurveID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALING_HEALTH_ITEM_LEVEL_CURVE_ID, (int)unitData.ScalingHealthItemLevelCurveID);
                if (unitData.ScalingDamageItemLevelCurveID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALING_DAMAGE_ITEM_LEVEL_CURVE_ID, (int)unitData.ScalingDamageItemLevelCurveID);
                if (unitData.FactionTemplate != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_FACTIONTEMPLATE, (int)unitData.FactionTemplate);
                for (int i = 0; i < 3; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_VIRTUAL_ITEM_SLOT_ID;
                    int sizePerEntry = 2;
                    if (unitData.VirtualItems[i] != null)
                    {
                        m_fields.SetUpdateField<int>(startIndex + i * sizePerEntry, (int)unitData.VirtualItems[i].ItemID);
                        m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 1, (ushort)unitData.VirtualItems[i].ItemAppearanceModID, 0);
                        m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 1, (ushort)unitData.VirtualItems[i].ItemVisual, 1);
                    }
                }
                if (unitData.Flags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_FLAGS, (uint)unitData.Flags);
                if (unitData.Flags2 != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_FLAGS_2, (uint)unitData.Flags2);
                if (unitData.Flags3 != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_FLAGS_3, (uint)unitData.Flags3);
                if (unitData.AuraState != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_AURASTATE, (uint)unitData.AuraState);
                for (int i = 0; i < 2; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_BASEATTACKTIME;
                    if (unitData.AttackRoundBaseTime[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)unitData.AttackRoundBaseTime[i]);
                }
                if (unitData.RangedAttackRoundBaseTime != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_RANGEDATTACKTIME, (uint)unitData.RangedAttackRoundBaseTime);
                if (unitData.BoundingRadius != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_BOUNDINGRADIUS, (float)unitData.BoundingRadius);
                if (unitData.CombatReach != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_COMBATREACH, (float)unitData.CombatReach);
                if (unitData.DisplayID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_DISPLAYID, (int)unitData.DisplayID);
                if (unitData.DisplayScale != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_DISPLAY_SCALE, (float)unitData.DisplayScale);
                if (unitData.NativeDisplayID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_NATIVEDISPLAYID, (int)unitData.NativeDisplayID);
                if (unitData.NativeXDisplayScale != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_NATIVE_X_DISPLAY_SCALE, (float)unitData.NativeXDisplayScale);
                if (unitData.MountDisplayID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_MOUNTDISPLAYID, (int)unitData.MountDisplayID);
                if (unitData.MinDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MINDAMAGE, (float)unitData.MinDamage);
                if (unitData.MaxDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MAXDAMAGE, (float)unitData.MaxDamage);
                if (unitData.MinOffHandDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MINOFFHANDDAMAGE, (float)unitData.MinOffHandDamage);
                if (unitData.MaxOffHandDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MAXOFFHANDDAMAGE, (float)unitData.MaxOffHandDamage);
                if (unitData.StandState != null || unitData.PetLoyaltyIndex != null || unitData.VisFlags != null || unitData.AnimTier != null)
                {
                    if (unitData.StandState != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.StandState, 0);
                    if (unitData.PetLoyaltyIndex != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.PetLoyaltyIndex, 1);
                    if (unitData.VisFlags != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.VisFlags, 2);
                    if (unitData.AnimTier != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_1, (byte)unitData.AnimTier, 3);
                }
                if (unitData.PetNumber != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_PETNUMBER, (uint)unitData.PetNumber);
                if (unitData.PetNameTimestamp != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_PET_NAME_TIMESTAMP, (uint)unitData.PetNameTimestamp);
                if (unitData.PetExperience != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_PETEXPERIENCE, (uint)unitData.PetExperience);
                if (unitData.PetNextLevelExperience != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_PETNEXTLEVELXP, (uint)unitData.PetNextLevelExperience);
                if (unitData.ModCastSpeed != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_MOD_CAST_SPEED, (float)unitData.ModCastSpeed);
                if (unitData.ModCastHaste != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_MOD_CAST_HASTE, (float)unitData.ModCastHaste);
                if (unitData.ModHaste != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MOD_HASTE, (float)unitData.ModHaste);
                if (unitData.ModRangedHaste != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MOD_RANGED_HASTE, (float)unitData.ModRangedHaste);
                if (unitData.ModHasteRegen != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MOD_HASTE_REGEN, (float)unitData.ModHasteRegen);
                if (unitData.ModTimeRate != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MOD_TIME_RATE, (float)unitData.ModTimeRate);
                if (unitData.CreatedBySpell != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_CREATED_BY_SPELL, (int)unitData.CreatedBySpell);
                for (int i = 0; i < 2; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_NPC_FLAGS;
                    if (unitData.NpcFlags[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)unitData.NpcFlags[i]);
                }
                if (unitData.EmoteState != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_NPC_EMOTESTATE, (int)unitData.EmoteState);
                if (unitData.TrainingPointsUsed != null && unitData.TrainingPointsTotal != null)
                {
                    m_fields.SetUpdateField<ushort>(BccUpdateFields.UnitField.UNIT_FIELD_TRAINING_POINTS_TOTAL, (ushort)unitData.TrainingPointsUsed, 0);
                    m_fields.SetUpdateField<ushort>(BccUpdateFields.UnitField.UNIT_FIELD_TRAINING_POINTS_TOTAL, (ushort)unitData.TrainingPointsTotal, 1);
                }
                for (int i = 0; i < 5; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_STAT;
                    if (unitData.Stats[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.Stats[i]);
                }
                for (int i = 0; i < 5; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_POSSTAT;
                    if (unitData.StatPosBuff[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.StatPosBuff[i]);
                }
                for (int i = 0; i < 5; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_NEGSTAT;
                    if (unitData.StatNegBuff[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.StatNegBuff[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_RESISTANCES;
                    if (unitData.Resistances[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.Resistances[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_RESISTANCEBUFFMODSPOSITIVE;
                    if (unitData.ResistanceBuffModsPositive[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.ResistanceBuffModsPositive[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_RESISTANCEBUFFMODSNEGATIVE;
                    if (unitData.ResistanceBuffModsNegative[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.ResistanceBuffModsNegative[i]);
                }
                if (unitData.BaseMana != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_BASE_MANA, (int)unitData.BaseMana);
                if (unitData.BaseHealth != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_BASE_HEALTH, (int)unitData.BaseHealth);
                if (unitData.SheatheState != null || unitData.PvpFlags != null || unitData.PetFlags != null || unitData.ShapeshiftForm != null)
                {
                    if (unitData.SheatheState != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.SheatheState, 0);
                    if (unitData.PvpFlags != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.PvpFlags, 1);
                    if (unitData.PetFlags != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.PetFlags, 2);
                    if (unitData.ShapeshiftForm != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.UnitField.UNIT_FIELD_BYTES_2, (byte)unitData.ShapeshiftForm, 3);
                }
                if (unitData.AttackPower != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_ATTACK_POWER, (int)unitData.AttackPower);
                if (unitData.AttackPowerModPos != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_ATTACK_POWER_MOD_POS, (int)unitData.AttackPowerModPos);
                if (unitData.AttackPowerModNeg != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_ATTACK_POWER_MOD_NEG, (int)unitData.AttackPowerModNeg);
                if (unitData.AttackPowerMultiplier != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_ATTACK_POWER_MULTIPLIER, (float)unitData.AttackPowerMultiplier);
                if (unitData.RangedAttackPower != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_RANGED_ATTACK_POWER, (int)unitData.RangedAttackPower);
                if (unitData.RangedAttackPowerModPos != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_RANGED_ATTACK_POWER_MOD_POS, (int)unitData.RangedAttackPowerModPos);
                if (unitData.RangedAttackPowerModNeg != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_RANGED_ATTACK_POWER_MOD_NEG, (int)unitData.RangedAttackPowerModNeg);
                if (unitData.RangedAttackPowerMultiplier != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_RANGED_ATTACK_POWER_MULTIPLIER, (float)unitData.RangedAttackPowerMultiplier);
                if (unitData.AttackSpeedAura != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_ATTACK_SPEED_AURA, (int)unitData.AttackSpeedAura);
                if (unitData.Lifesteal != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_LIFESTEAL, (float)unitData.Lifesteal);
                if (unitData.MinRangedDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MINRANGEDDAMAGE, (float)unitData.MinRangedDamage);
                if (unitData.MaxRangedDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MAXRANGEDDAMAGE, (float)unitData.MaxRangedDamage);
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_POWER_COST_MODIFIER;
                    if (unitData.PowerCostModifier[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)unitData.PowerCostModifier[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.UnitField.UNIT_FIELD_POWER_COST_MULTIPLIER;
                    if (unitData.PowerCostMultiplier[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)unitData.PowerCostMultiplier[i]);
                }
                if (unitData.MaxHealthModifier != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_MAXHEALTHMODIFIER, (float)unitData.MaxHealthModifier);
                if (unitData.HoverHeight != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.UnitField.UNIT_FIELD_HOVERHEIGHT, (float)unitData.HoverHeight);
                if (unitData.MinItemLevelCutoff != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_MIN_ITEM_LEVEL_CUTOFF, (int)unitData.MinItemLevelCutoff);
                if (unitData.MinItemLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_MIN_ITEM_LEVEL, (int)unitData.MinItemLevel);
                if (unitData.MaxItemLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_MAXITEMLEVEL, (int)unitData.MaxItemLevel);
                if (unitData.WildBattlePetLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_WILD_BATTLEPET_LEVEL, (int)unitData.WildBattlePetLevel);
                if (unitData.BattlePetCompanionNameTimestamp != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_BATTLEPET_COMPANION_NAME_TIMESTAMP, (uint)unitData.BattlePetCompanionNameTimestamp);
                if (unitData.InteractSpellID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_INTERACT_SPELLID, (int)unitData.InteractSpellID);
                if (unitData.StateSpellVisualID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_STATE_SPELL_VISUAL_ID, (uint)unitData.StateSpellVisualID);
                if (unitData.StateAnimID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_STATE_ANIM_ID, (uint)unitData.StateAnimID);
                if (unitData.StateAnimKitID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_STATE_ANIM_KIT_ID, (uint)unitData.StateAnimKitID);
                if (unitData.StateWorldEffectsID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.UnitField.UNIT_FIELD_STATE_WORLD_EFFECT_ID, (uint)unitData.StateWorldEffectsID);
                if (unitData.ScaleDuration != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_SCALE_DURATION, (int)unitData.ScaleDuration);
                if (unitData.LooksLikeMountID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_LOOKS_LIKE_MOUNT_ID, (int)unitData.LooksLikeMountID);
                if (unitData.LooksLikeCreatureID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_LOOKS_LIKE_CREATURE_ID, (int)unitData.LooksLikeCreatureID);
                if (unitData.LookAtControllerID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.UnitField.UNIT_FIELD_LOOK_AT_CONTROLLER_ID, (int)unitData.LookAtControllerID);
                if (unitData.GuildGUID != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitField.UNIT_FIELD_GUILD_GUID, unitData.GuildGUID);

                // Dynamic Fields
                if (unitData.ChannelObject != null)
                    m_dynamicFields.SetUpdateField<WowGuid128>(BccUpdateFields.UnitDynamicField.UNIT_DYNAMIC_FIELD_CHANNEL_OBJECTS, unitData.ChannelObject, DynamicFieldChangeType.ValueAndSizeChanged);
            }

            PlayerData playerData = m_updateData.PlayerData;
            if (playerData != null)
            {
                if (playerData.DuelArbiter != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.PlayerField.PLAYER_DUEL_ARBITER, playerData.DuelArbiter);
                if (playerData.WowAccount != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.PlayerField.PLAYER_WOW_ACCOUNT, playerData.WowAccount);
                if (playerData.LootTargetGUID != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.PlayerField.PLAYER_LOOT_TARGET_GUID, playerData.LootTargetGUID);
                if (playerData.PlayerFlags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_FLAGS, (uint)playerData.PlayerFlags);
                if (playerData.PlayerFlagsEx != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_FLAGS_EX, (uint)playerData.PlayerFlagsEx);
                if (playerData.GuildRankID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_GUILDRANK, (uint)playerData.GuildRankID);
                if (playerData.GuildDeleteDate != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_GUILDDELETE_DATE, (uint)playerData.GuildDeleteDate);
                if (playerData.GuildLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.PlayerField.PLAYER_GUILDLEVEL, (int)playerData.GuildLevel);
                if (playerData.PartyType != null || playerData.NumBankSlots != null || playerData.NativeSex != null || playerData.Inebriation != null)
                {
                    if (playerData.PartyType != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES, (byte)playerData.PartyType, 0);
                    if (playerData.NumBankSlots != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES, (byte)playerData.NumBankSlots, 1);
                    if (playerData.NativeSex != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES, (byte)playerData.NativeSex, 2);
                    if (playerData.Inebriation != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES, (byte)playerData.Inebriation, 3);
                }
                if (playerData.PvpTitle != null || playerData.ArenaFaction != null || playerData.PvPRank != null)
                {
                    if (playerData.PvpTitle != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES_2, (byte)playerData.PvpTitle, 0);
                    if (playerData.ArenaFaction != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES_2, (byte)playerData.ArenaFaction, 1);
                    if (playerData.PvPRank != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.PlayerField.PLAYER_BYTES_2, (byte)playerData.PvPRank, 2);
                }
                if (playerData.DuelTeam != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_DUEL_TEAM, (uint)playerData.DuelTeam);
                if (playerData.GuildTimeStamp != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.PlayerField.PLAYER_GUILD_TIMESTAMP, (int)playerData.GuildTimeStamp);
                for (int i = 0; i < 25; i++)
                {
                    int startIndex = (int)BccUpdateFields.PlayerField.PLAYER_QUEST_LOG;
                    int sizePerEntry = 16;
                    if (playerData.QuestLog[i] != null)
                    {
                        if (playerData.QuestLog[i].QuestID != null)
                            m_fields.SetUpdateField<int>(startIndex + i * sizePerEntry, (int)playerData.QuestLog[i].QuestID);
                        if (playerData.QuestLog[i].StateFlags != null)
                            m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 1, (uint)playerData.QuestLog[i].StateFlags);
                        for (int j = 0; j < 24; j++)
                        {
                            if (playerData.QuestLog[i].ObjectiveProgress[j] != null)
                                m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 2 + j / 2, (ushort)playerData.QuestLog[i].ObjectiveProgress[j], (byte)(j & 1));
                        }
                        if (playerData.QuestLog[i].EndTime != null)
                            m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 2 + 12, (uint)playerData.QuestLog[i].EndTime);
                        if (playerData.QuestLog[i].AcceptTime != null)
                            m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 3 + 12, (uint)playerData.QuestLog[i].AcceptTime); 
                    }
                }
                for (int i = 0; i < 19; i++)
                {
                    int startIndex = (int)BccUpdateFields.PlayerField.PLAYER_VISIBLE_ITEM;
                    int sizePerEntry = 2;
                    if (playerData.VisibleItems[i] != null)
                    {
                        m_fields.SetUpdateField<int>(startIndex + i * sizePerEntry, (int)playerData.VisibleItems[i].ItemID);
                        m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 1, (ushort)playerData.VisibleItems[i].ItemAppearanceModID, 0);
                        m_fields.SetUpdateField<ushort>(startIndex + i * sizePerEntry + 1, (ushort)playerData.VisibleItems[i].ItemVisual, 1);
                    }
                }
                if (playerData.ChosenTitle != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.PlayerField.PLAYER_CHOSEN_TITLE, (int)playerData.ChosenTitle);
                if (playerData.FakeInebriation != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.PlayerField.PLAYER_FAKE_INEBRIATION, (int)playerData.FakeInebriation);
                if (playerData.VirtualPlayerRealm != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_FIELD_VIRTUAL_PLAYER_REALM, (uint)playerData.VirtualPlayerRealm);
                if (playerData.CurrentSpecID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_FIELD_CURRENT_SPEC_ID, (uint)playerData.CurrentSpecID);
                if (playerData.TaxiMountAnimKitID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.PlayerField.PLAYER_FIELD_TAXI_MOUNT_ANIM_KIT_ID, (int)playerData.TaxiMountAnimKitID);
                for (int i = 0; i < 6; i++)
                {
                    int startIndex = (int)BccUpdateFields.PlayerField.PLAYER_FIELD_AVG_ITEM_LEVEL;
                    if (playerData.AvgItemLevel[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)playerData.AvgItemLevel[i]);
                }
                if (playerData.CurrentBattlePetBreedQuality != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.PlayerField.PLAYER_FIELD_CURRENT_BATTLE_PET_BREED_QUALITY, (uint)playerData.CurrentBattlePetBreedQuality);
                if (playerData.HonorLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.PlayerField.PLAYER_FIELD_HONOR_LEVEL, (int)playerData.HonorLevel);
                for (int i = 0; i < 36; i++)
                {
                    int startIndex = (int)BccUpdateFields.PlayerField.PLAYER_FIELD_CUSTOMIZATION_CHOICES;
                    int sizePerEntry = 2;
                    if (playerData.Customizations[i] != null)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry, (uint)playerData.Customizations[i].ChrCustomizationOptionID);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 1, (uint)playerData.Customizations[i].ChrCustomizationChoiceID);
                    }
                }
            }

            ActivePlayerData activeData = m_updateData.ActivePlayerData;
            if (activeData != null && m_objectType == Enums.ObjectTypeBCC.ActivePlayer)
            {
                for (int i = 0; i < 23; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_INV_SLOT_HEAD;
                    int sizePerEntry = 4;
                    if (activeData.InvSlots[i] != null)
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, activeData.InvSlots[i]);
                }
                for (int i = 0; i < 24; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_INV_SLOT_HEAD + Enums.Classic.InventorySlots.ItemStart * 4;
                    int sizePerEntry = 4;
                    if (activeData.PackSlots[i] != null)
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, activeData.PackSlots[i]);
                }
                for (int i = 0; i < 28; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_INV_SLOT_HEAD + Enums.Classic.InventorySlots.BankItemStart * 4;
                    int sizePerEntry = 4;
                    if (activeData.BankSlots[i] != null)
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, activeData.BankSlots[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_INV_SLOT_HEAD + Enums.Classic.InventorySlots.BankBagStart * 4;
                    int sizePerEntry = 4;
                    if (activeData.BankBagSlots[i] != null)
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, activeData.BankBagSlots[i]);
                }
                for (int i = 0; i < 12; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_INV_SLOT_HEAD + Enums.Classic.InventorySlots.BuyBackStart * 4;
                    int sizePerEntry = 4;
                    if (activeData.BuyBackSlots[i] != null)
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, activeData.BuyBackSlots[i]);
                }
                for (int i = 0; i < 32; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_INV_SLOT_HEAD + Enums.Classic.InventorySlots.KeyringStart * 4;
                    int sizePerEntry = 4;
                    if (activeData.KeyringSlots[i] != null)
                        m_fields.SetUpdateField<WowGuid128>(startIndex + i * sizePerEntry, activeData.KeyringSlots[i]);
                }
                if (activeData.FarsightObject != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_FARSIGHT, activeData.FarsightObject);
                if (activeData.ComboTarget != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_COMBO_TARGET, activeData.ComboTarget);
                if (activeData.SummonedBattlePetGUID != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SUMMONED_BATTLE_PET_ID, activeData.SummonedBattlePetGUID);
                for (int i = 0; i < 12; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_KNOWN_TITLES;
                    if (activeData.KnownTitles[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.KnownTitles[i]);
                }
                if (activeData.Coinage != null)
                    m_fields.SetUpdateField<ulong>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_COINAGE, (ulong)activeData.Coinage);
                if (activeData.XP != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_XP, (int)activeData.XP);
                if (activeData.NextLevelXP != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_NEXT_LEVEL_XP, (int)activeData.NextLevelXP);
                if (activeData.TrialXP != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_TRIAL_XP, (int)activeData.TrialXP);
                for (int i = 0; i < 256; i++)
                {
                    if (activeData.Skill.SkillLineID[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillLineID[i], (byte)(i & 1));
                    }
                    if (activeData.Skill.SkillStep[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID + 128;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillStep[i], (byte)(i & 1));
                    }
                    if (activeData.Skill.SkillRank[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID + 128 + 128;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillRank[i], (byte)(i & 1));
                    }
                    if (activeData.Skill.SkillStartingRank[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID + 128 + 128 + 128;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillStartingRank[i], (byte)(i & 1));
                    }
                    if (activeData.Skill.SkillMaxRank[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID + 128 + 128 + 128 + 128;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillMaxRank[i], (byte)(i & 1));
                    }
                    if (activeData.Skill.SkillTempBonus[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID + 128 + 128 + 128 + 128 + 128;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillTempBonus[i], (byte)(i & 1));
                    }
                    if (activeData.Skill.SkillPermBonus[i] != null)
                    {
                        int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SKILL_LINEID + 128 + 128 + 128 + 128 + 128 + 128;
                        m_fields.SetUpdateField<ushort>(startIndex + i / 2, (ushort)activeData.Skill.SkillPermBonus[i], (byte)(i & 1));
                    }
                }
                if (activeData.CharacterPoints != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_CHARACTER_POINTS, (int)activeData.CharacterPoints);
                if (activeData.MaxTalentTiers != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MAX_TALENT_TIERS, (int)activeData.MaxTalentTiers);
                if (activeData.TrackCreatureMask != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_TRACK_CREATURES, (uint)activeData.TrackCreatureMask);
                for (int i = 0; i < 2; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_TRACK_RESOURCES;
                    if (activeData.TrackResourceMask[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.TrackResourceMask[i]);
                }
                if (activeData.MainhandExpertise != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_EXPERTISE, (float)activeData.MainhandExpertise);
                if (activeData.OffhandExpertise != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_OFFHAND_EXPERTISE, (float)activeData.OffhandExpertise);
                if (activeData.RangedExpertise != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_RANGED_EXPERTISE, (float)activeData.RangedExpertise);
                if (activeData.CombatRatingExpertise != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_COMBAT_RATING_EXPERTISE, (float)activeData.CombatRatingExpertise);
                if (activeData.BlockPercentage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BLOCK_PERCENTAGE, (float)activeData.BlockPercentage);
                if (activeData.DodgePercentage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_DODGE_PERCENTAGE, (float)activeData.DodgePercentage);
                if (activeData.DodgePercentageFromAttribute != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_DODGE_PERCENTAGE_FROM_ATTRIBUTE, (float)activeData.DodgePercentageFromAttribute);
                if (activeData.ParryPercentage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PARRY_PERCENTAGE, (float)activeData.ParryPercentage);
                if (activeData.ParryPercentageFromAttribute != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PARRY_PERCENTAGE_FROM_ATTRIBUTE, (float)activeData.ParryPercentageFromAttribute);
                if (activeData.CritPercentage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_CRIT_PERCENTAGE, (float)activeData.CritPercentage);
                if (activeData.RangedCritPercentage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_RANGED_CRIT_PERCENTAGE, (float)activeData.RangedCritPercentage);
                if (activeData.OffhandCritPercentage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_OFFHAND_CRIT_PERCENTAGE, (float)activeData.OffhandCritPercentage);
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SPELL_CRIT_PERCENTAGE1;
                    if (activeData.SpellCritPercentage[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)activeData.SpellCritPercentage[i]);
                }
                if (activeData.ShieldBlock != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SHIELD_BLOCK, (int)activeData.ShieldBlock);
                if (activeData.Mastery != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MASTERY, (float)activeData.Mastery);
                if (activeData.Speed != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SPEED, (float)activeData.Speed);
                if (activeData.Avoidance != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_AVOIDANCE, (float)activeData.Avoidance);
                if (activeData.Sturdiness != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_STURDINESS, (float)activeData.Sturdiness);
                if (activeData.Versatility != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_VERSATILITY, (int)activeData.Versatility);
                if (activeData.VersatilityBonus != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_VERSATILITY_BONUS, (float)activeData.VersatilityBonus);
                if (activeData.PvpPowerDamage != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PVP_POWER_DAMAGE, (float)activeData.PvpPowerDamage);
                if (activeData.PvpPowerHealing != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PVP_POWER_HEALING, (float)activeData.PvpPowerHealing);
                for (int i = 0; i < 240; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_EXPLORED_ZONES;
                    if (activeData.ExploredZones[i] != null)
                        m_fields.SetUpdateField<ulong>(startIndex + i * 2, (ulong)activeData.ExploredZones[i]);
                }
                for (int i = 0; i < 2; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_REST_INFO;
                    int sizePerEntry = 2;
                    if (activeData.RestInfo[i] != null)
                    {
                        if (activeData.RestInfo[i].StateID != null)
                            m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry, (uint)activeData.RestInfo[i].StateID);
                        if (activeData.RestInfo[i].Threshold != null)
                            m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 1, (uint)activeData.RestInfo[i].Threshold);
                    }
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_DAMAGE_DONE_POS;
                    if (activeData.ModDamageDonePos[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)activeData.ModDamageDonePos[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_DAMAGE_DONE_NEG;
                    if (activeData.ModDamageDoneNeg[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)activeData.ModDamageDoneNeg[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_DAMAGE_DONE_PCT;
                    if (activeData.ModDamageDonePercent[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)activeData.ModDamageDonePercent[i]);
                }
                if (activeData.ModHealingDonePos != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_HEALING_DONE_POS, (int)activeData.ModHealingDonePos);
                if (activeData.ModHealingPercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_HEALING_PCT, (float)activeData.ModHealingPercent);
                if (activeData.ModHealingDonePercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_HEALING_DONE_PCT, (float)activeData.ModHealingDonePercent);
                if (activeData.ModPeriodicHealingDonePercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_PERIODIC_HEALING_DONE_PERCENT, (float)activeData.ModPeriodicHealingDonePercent);
                for (int i = 0; i < 3; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_WEAPON_DMG_MULTIPLIERS;
                    if (activeData.WeaponDmgMultipliers[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)activeData.WeaponDmgMultipliers[i]);
                }
                for (int i = 0; i < 3; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_WEAPON_ATK_SPEED_MULTIPLIERS;
                    if (activeData.WeaponAtkSpeedMultipliers[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)activeData.WeaponAtkSpeedMultipliers[i]);
                }
                if (activeData.ModSpellPowerPercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_SPELL_POWER_PCT, (float)activeData.ModSpellPowerPercent);
                if (activeData.ModResiliencePercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_RESILIENCE_PERCENT, (float)activeData.ModResiliencePercent);
                if (activeData.OverrideSpellPowerByAPPercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_OVERRIDE_SPELL_POWER_BY_AP_PCT, (float)activeData.OverrideSpellPowerByAPPercent);
                if (activeData.OverrideAPBySpellPowerPercent != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_OVERRIDE_AP_BY_SPELL_POWER_PERCENT, (float)activeData.OverrideAPBySpellPowerPercent);
                if (activeData.ModTargetResistance != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_TARGET_RESISTANCE, (int)activeData.ModTargetResistance);
                if (activeData.ModTargetPhysicalResistance != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_TARGET_PHYSICAL_RESISTANCE, (int)activeData.ModTargetPhysicalResistance);
                if (activeData.LocalFlags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_LOCAL_FLAGS, (uint)activeData.LocalFlags);
                if (activeData.GrantableLevels != null || activeData.MultiActionBars != null || activeData.LifetimeMaxRank != null || activeData.NumRespecs != null)
                {
                    if (activeData.GrantableLevels != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES, (byte)activeData.GrantableLevels, 0);
                    if (activeData.MultiActionBars != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES, (byte)activeData.MultiActionBars, 1);
                    if (activeData.LifetimeMaxRank != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES, (byte)activeData.LifetimeMaxRank, 2);
                    if (activeData.NumRespecs != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES, (byte)activeData.NumRespecs, 3);
                }
                if (activeData.AmmoID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_AMMO_ID, (uint)activeData.AmmoID);
                if (activeData.PvpMedals != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PVP_MEDALS, (uint)activeData.PvpMedals);
                for (int i = 0; i < 12; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BUYBACK_PRICE;
                    if (activeData.BuybackPrice[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.BuybackPrice[i]);
                }
                for (int i = 0; i < 12; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BUYBACK_TIMESTAMP;
                    if (activeData.BuybackTimestamp[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.BuybackTimestamp[i]);
                }
                if (activeData.TodayHonorableKills != null && activeData.YesterdayHonorableKills != null)
                {
                    m_fields.SetUpdateField<ushort>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_2, (ushort)activeData.TodayHonorableKills, 0);
                    m_fields.SetUpdateField<ushort>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_2, (ushort)activeData.YesterdayHonorableKills, 1);
                }
                if (activeData.LastWeekHonorableKills != null && activeData.ThisWeekHonorableKills != null)
                {
                    m_fields.SetUpdateField<ushort>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_3, (ushort)activeData.LastWeekHonorableKills, 0);
                    m_fields.SetUpdateField<ushort>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_3, (ushort)activeData.ThisWeekHonorableKills, 1);
                }
                if (activeData.ThisWeekContribution != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_THIS_WEEK_CONTRIBUTION, (uint)activeData.ThisWeekContribution);
                if (activeData.LifetimeHonorableKills != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_LIFETIME_HONORABLE_KILLS, (uint)activeData.LifetimeHonorableKills);
                if (activeData.YesterdayContribution != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_YESTERDAY_CONTRIBUTION, (uint)activeData.YesterdayContribution);
                if (activeData.LastWeekContribution != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_LAST_WEEK_CONTRIBUTION, (uint)activeData.LastWeekContribution);
                if (activeData.LastWeekRank != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_LAST_WEEK_RANK, (uint)activeData.LastWeekRank);
                if (activeData.WatchedFactionIndex != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_WATCHED_FACTION_INDEX, (int)activeData.WatchedFactionIndex);
                for (int i = 0; i < 32; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_COMBAT_RATING;
                    if (activeData.CombatRatings[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)activeData.CombatRatings[i]);
                }
                for (int i = 0; i < 6; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_ARENA_TEAM_INFO;
                    int sizePerEntry = 12;
                    if (activeData.PvpInfo[i] != null)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry, (uint)activeData.PvpInfo[i].WeeklyPlayed);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 1, (uint)activeData.PvpInfo[i].WeeklyWon);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 2, (uint)activeData.PvpInfo[i].SeasonPlayed);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 3, (uint)activeData.PvpInfo[i].SeasonWon);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 4, (uint)activeData.PvpInfo[i].Rating);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 5, (uint)activeData.PvpInfo[i].WeeklyBestRating);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 6, (uint)activeData.PvpInfo[i].SeasonBestRating);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 7, (uint)activeData.PvpInfo[i].PvpTierID);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 8, (uint)activeData.PvpInfo[i].WeeklyBestWinPvpTierID);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 9, (uint)activeData.PvpInfo[i].Field_28);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 10, (uint)activeData.PvpInfo[i].Field_2C);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 11, (uint)(activeData.PvpInfo[i].Disqualified ? 1 : 0));
                    }
                }
                if (activeData.MaxLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MAX_LEVEL, (int)activeData.MaxLevel);
                if (activeData.ScalingPlayerLevelDelta != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_SCALING_PLAYER_LEVEL_DELTA, (int)activeData.ScalingPlayerLevelDelta);
                if (activeData.MaxCreatureScalingLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MAX_CREATURE_SCALING_LEVEL, (int)activeData.MaxCreatureScalingLevel);
                for (int i = 0; i < 4; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_NO_REAGENT_COST;
                    if (activeData.NoReagentCostMask[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.NoReagentCostMask[i]);
                }
                if (activeData.PetSpellPower != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PET_SPELL_POWER, (int)activeData.PetSpellPower);
                for (int i = 0; i < 2; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PROFESSION_SKILL_LINE;
                    if (activeData.ProfessionSkillLine[i] != null)
                        m_fields.SetUpdateField<int>(startIndex + i, (int)activeData.ProfessionSkillLine[i]);
                }
                if (activeData.UiHitModifier != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_UI_HIT_MODIFIER, (float)activeData.UiHitModifier);
                if (activeData.UiSpellHitModifier != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_UI_SPELL_HIT_MODIFIER, (float)activeData.UiSpellHitModifier);
                if (activeData.HomeRealmTimeOffset != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_HOME_REALM_TIME_OFFSET, (int)activeData.HomeRealmTimeOffset);
                if (activeData.ModPetHaste != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_MOD_PET_HASTE, (float)activeData.ModPetHaste);
                if (activeData.LocalRegenFlags != null || activeData.AuraVision != null || activeData.NumBackpackSlots != null)
                {
                    if (activeData.LocalRegenFlags != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_4, (byte)activeData.LocalRegenFlags, 0);
                    if (activeData.AuraVision != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_4, (byte)activeData.AuraVision, 1);
                    if (activeData.NumBackpackSlots != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_4, (byte)activeData.NumBackpackSlots, 2);
                }
                if (activeData.OverrideSpellsID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_OVERRIDE_SPELLS_ID, (int)activeData.OverrideSpellsID);
                if (activeData.LfgBonusFactionID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_LFG_BONUS_FACTION_ID, (int)activeData.LfgBonusFactionID);
                if (activeData.LootSpecID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_LOOT_SPEC_ID, (uint)activeData.LootSpecID);
                if (activeData.OverrideZonePVPType != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_OVERRIDE_ZONE_PVP_TYPE, (uint)activeData.OverrideZonePVPType);
                for (int i = 0; i < 4; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BAG_SLOT_FLAGS;
                    if (activeData.BagSlotFlags[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.BagSlotFlags[i]);
                }
                for (int i = 0; i < 7; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BANK_BAG_SLOT_FLAGS;
                    if (activeData.BankBagSlotFlags[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)activeData.BankBagSlotFlags[i]);
                }
                for (int i = 0; i < 875; i++)
                {
                    int startIndex = (int)BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_QUEST_COMPLETED;
                    if (activeData.QuestCompleted[i] != null)
                        m_fields.SetUpdateField<ulong>(startIndex + i * 2, (ulong)activeData.QuestCompleted[i]);
                }
                if (activeData.Honor != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_HONOR, (int)activeData.Honor);
                if (activeData.HonorNextLevel != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_HONOR_NEXT_LEVEL, (int)activeData.HonorNextLevel);
                if (activeData.PvPTierMaxFromWins != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PVP_TIER_MAX_FROM_WINS, (uint)activeData.PvPTierMaxFromWins);
                if (activeData.PvPLastWeeksTierMaxFromWins != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_PVP_LAST_WEEKS_TIER_MAX_FROM_WINS, (uint)activeData.PvPLastWeeksTierMaxFromWins);
                if (activeData.InsertItemsLeftToRight != null || activeData.PvPRankProgress != null)
                {
                    if (activeData.InsertItemsLeftToRight != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_5, (byte)(activeData.InsertItemsLeftToRight == true ? 1 : 0), 0);
                    if (activeData.PvPRankProgress != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.ActivePlayerField.ACTIVE_PLAYER_FIELD_BYTES_5, (byte)activeData.PvPRankProgress, 1);
                }

                // Dynamic Fields
                if (activeData.SelfResSpells != null)
                {
                    uint[] fields = new uint[activeData.SelfResSpells.Count];
                    for (int i = 0; i < activeData.SelfResSpells.Count; i++)
                        fields[i] = activeData.SelfResSpells[i];
                    m_dynamicFields.SetUpdateField((int)BccUpdateFields.ActivePlayerDynamicField.ACTIVE_PLAYER_DYNAMIC_FIELD_SELF_RES_SPELLS, fields, DynamicFieldChangeType.ValueAndSizeChanged);
                }
                if (activeData.HasDailyQuestsUpdate)
                {
                    uint[] fields = new uint[m_gameState.DailyQuestsDone.Count];
                    int counter = 0;
                    foreach (var itr in m_gameState.DailyQuestsDone)
                        fields[counter++] = itr.Value;
                    m_dynamicFields.SetUpdateField((int)BccUpdateFields.ActivePlayerDynamicField.ACTIVE_PLAYER_DYNAMIC_FIELD_DAILY_QUESTS_COMPLETED, fields, DynamicFieldChangeType.ValueAndSizeChanged);
                }
            }

            GameObjectData goData = m_updateData.GameObjectData;
            if (goData != null)
            {
                if (goData.CreatedBy != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.GameObjectField.GAMEOBJECT_FIELD_CREATED_BY, goData.CreatedBy);
                if (goData.DisplayID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.GameObjectField.GAMEOBJECT_DISPLAYID, (int)goData.DisplayID);
                if (goData.Flags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.GameObjectField.GAMEOBJECT_FLAGS, (uint)goData.Flags);
                for (int i = 0; i < 4; i++)
                {
                    int startIndex = (int)BccUpdateFields.GameObjectField.GAMEOBJECT_PARENTROTATION;
                    if (goData.ParentRotation[i] != null)
                        m_fields.SetUpdateField<float>(startIndex + i, (float)(goData.ParentRotation[i]));
                }
                if (goData.FactionTemplate != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.GameObjectField.GAMEOBJECT_FACTION, (int)goData.FactionTemplate);
                if (goData.Level != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.GameObjectField.GAMEOBJECT_LEVEL, (int)goData.Level);
                if (goData.State != null || goData.TypeID != null || goData.ArtKit != null || goData.PercentHealth != null)
                {
                    if (goData.State != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.State, 0);
                    if (goData.TypeID != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.TypeID, 1);
                    if (goData.ArtKit != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.ArtKit, 2);
                    if (goData.PercentHealth != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.GameObjectField.GAMEOBJECT_BYTES_1, (byte)goData.PercentHealth, 3);
                }
                if (goData.SpellVisualID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.GameObjectField.GAMEOBJECT_SPELL_VISUAL_ID, (uint)goData.SpellVisualID);
                if (goData.StateSpellVisualID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.GameObjectField.GAMEOBJECT_STATE_SPELL_VISUAL_ID, (uint)goData.StateSpellVisualID);
                if (goData.StateAnimID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.GameObjectField.GAMEOBJECT_STATE_ANIM_ID, (uint)goData.StateAnimID);
                if (goData.StateAnimKitID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.GameObjectField.GAMEOBJECT_STATE_ANIM_KIT_ID, (uint)goData.StateAnimKitID);
                for (int i = 0; i < 4; i++)
                {
                    int startIndex = (int)BccUpdateFields.GameObjectField.GAMEOBJECT_STATE_WORLD_EFFECT_ID;
                    if (goData.StateWorldEffectIDs[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)(goData.StateWorldEffectIDs[i]));
                }
                if (goData.CustomParam != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.GameObjectField.GAMEOBJECT_FIELD_CUSTOM_PARAM, (uint)goData.CustomParam);
            }

            DynamicObjectData dynData = m_updateData.DynamicObjectData;
            if (dynData != null)
            {
                if (dynData.Caster != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_CASTER, dynData.Caster);
                if (dynData.Type != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_TYPE, (uint)dynData.Type);
                if (dynData.SpellXSpellVisualID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_SPELL_X_SPELL_VISUAL_ID, (int)dynData.SpellXSpellVisualID);
                if (dynData.SpellID != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_SPELLID, (int)dynData.SpellID);
                if (dynData.Radius != null)
                    m_fields.SetUpdateField<float>(BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_RADIUS, (float)dynData.Radius);
                if (dynData.CastTime != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.DynamicObjectField.DYNAMICOBJECT_CASTTIME, (uint)dynData.CastTime);
            }

            CorpseData corpseData = m_updateData.CorpseData;
            if (corpseData != null)
            {
                if (corpseData.Owner != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.CorpseField.CORPSE_FIELD_OWNER, corpseData.Owner);
                if (corpseData.PartyGUID != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.CorpseField.CORPSE_FIELD_PARTY_GUID, corpseData.PartyGUID);
                if (corpseData.GuildGUID != null)
                    m_fields.SetUpdateField<WowGuid128>(BccUpdateFields.CorpseField.CORPSE_FIELD_GUILD_GUID, corpseData.GuildGUID);
                if (corpseData.DisplayID != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.CorpseField.CORPSE_FIELD_DISPLAY_ID, (uint)corpseData.DisplayID);
                for (int i = 0; i < 19; i++)
                {
                    int startIndex = (int)BccUpdateFields.CorpseField.CORPSE_FIELD_ITEMS;
                    if (corpseData.Items[i] != null)
                        m_fields.SetUpdateField<uint>(startIndex + i, (uint)corpseData.Items[i]);
                }
                if (corpseData.RaceId != null || corpseData.SexId != null || corpseData.ClassId != null)
                {
                    if (corpseData.RaceId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.CorpseField.CORPSE_FIELD_BYTES_1, (byte)corpseData.RaceId, 0);
                    if (corpseData.SexId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.CorpseField.CORPSE_FIELD_BYTES_1, (byte)corpseData.SexId, 1);
                    if (corpseData.ClassId != null)
                        m_fields.SetUpdateField<byte>(BccUpdateFields.CorpseField.CORPSE_FIELD_BYTES_1, (byte)corpseData.ClassId, 2);
                }
                if (corpseData.Flags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.CorpseField.CORPSE_FIELD_FLAGS, (uint)corpseData.Flags);
                if (corpseData.DynamicFlags != null)
                    m_fields.SetUpdateField<uint>(BccUpdateFields.CorpseField.CORPSE_FIELD_DYNAMIC_FLAGS, (uint)corpseData.DynamicFlags);
                if (corpseData.FactionTemplate != null)
                    m_fields.SetUpdateField<int>(BccUpdateFields.CorpseField.CORPSE_FIELD_FACTION_TEMPLATE, (int)corpseData.FactionTemplate);
                for (int i = 0; i < 36; i++)
                {
                    int startIndex = (int)BccUpdateFields.CorpseField.CORPSE_FIELD_CUSTOMIZATION_CHOICES;
                    int sizePerEntry = 2;
                    if (corpseData.Customizations[i] != null)
                    {
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry, (uint)corpseData.Customizations[i].ChrCustomizationOptionID);
                        m_fields.SetUpdateField<uint>(startIndex + i * sizePerEntry + 1, (uint)corpseData.Customizations[i].ChrCustomizationChoiceID);
                    }
                }
            }

            m_alreadyWritten = true;
        }


    }
}
