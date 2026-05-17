using Framework;
using Framework.Constants;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.SMSG_PET_SPELLS_MESSAGE)]
        void HandlePetSpellsMessage(WorldPacket packet)
        {
            WowGuid guid = packet.ReadGuid();
            GetSession().GameState.CurrentPetGuid = guid.To128(GetSession().GameState);
            GetSession().GameState.CurrentClientPetCast = null;

            // Equal to "Clear spells" pre cataclysm
            if (guid.IsEmpty())
            {
                if (IsWotlkFrontendClient() &&
                    TryForwardLegacyPayloadToWotlkClient(packet, Opcode.SMSG_PET_SPELLS_MESSAGE))
                    return;

                PetClearSpells clear = new();
                SendPacketToClient(clear);
                return;
            }

            PetSpells spells = new();
            spells.PetGUID = guid.To128(GetSession().GameState);
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                spells.CreatureFamily = packet.ReadUInt16();
            else
            {
                CreatureTemplate template = GameData.GetCreatureTemplate(guid.GetEntry());
                if (template != null && template.Family > 0)
                    spells.CreatureFamily = (ushort)Math.Min(template.Family, ushort.MaxValue);
            }

            spells.TimeLimit = packet.ReadUInt32();
            spells.ReactState = (ReactStates)packet.ReadUInt8();
            spells.CommandState = (CommandStates)packet.ReadUInt8();
            byte flagsLow = packet.ReadUInt8();
            spells.Flag = packet.ReadUInt8();
            spells.Flags = (ushort)(flagsLow | (spells.Flag << 8));

            const int maxCreatureSpells = 10;
            for (int i = 0; i < maxCreatureSpells; i++) // Read pet/vehicle spell ids
                spells.ActionButtons[i] = packet.ReadUInt32();

            byte spellCount = packet.ReadUInt8();
            for (int i = 0; i < spellCount; i++)
                spells.Actions.Add(packet.ReadUInt32());

            byte cdCount = packet.ReadUInt8();
            for (int i = 0; i < cdCount; i++)
            {
                PetSpellCooldown cooldown = new();

                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
                    cooldown.SpellID = packet.ReadUInt32();
                else
                    cooldown.SpellID = packet.ReadUInt16();

                cooldown.Category = packet.ReadUInt16();
                cooldown.Duration = packet.ReadUInt32();
                cooldown.CategoryDuration = packet.ReadUInt32();

                spells.Cooldowns.Add(cooldown);
            }

            if (TrySendWotlkPetSpells(spells))
                return;
            
            SendPacketToClient(spells);
        }

        private bool TrySendWotlkPetSpells(PetSpells spells)
        {
            if (!IsWotlkFrontendClient())
                return false;

            WorldPacket payload = new WorldPacket(Opcode.SMSG_PET_SPELLS_MESSAGE);
            payload.WriteGuid(spells.PetGUID.To64());
            payload.WriteUInt16(spells.CreatureFamily);
            payload.WriteUInt32(spells.TimeLimit);
            payload.WriteUInt8((byte)spells.ReactState);
            payload.WriteUInt8((byte)spells.CommandState);
            payload.WriteUInt16(spells.Flags);

            foreach (uint actionButton in spells.ActionButtons)
                payload.WriteUInt32(actionButton);

            byte actionCount = (byte)Math.Min(spells.Actions.Count, byte.MaxValue);
            payload.WriteUInt8(actionCount);
            for (int i = 0; i < actionCount; ++i)
                payload.WriteUInt32(spells.Actions[i]);

            byte cooldownCount = (byte)Math.Min(spells.Cooldowns.Count, byte.MaxValue);
            payload.WriteUInt8(cooldownCount);
            for (int i = 0; i < cooldownCount; ++i)
            {
                PetSpellCooldown cooldown = spells.Cooldowns[i];
                payload.WriteUInt32(cooldown.SpellID);
                payload.WriteUInt16(cooldown.Category);
                payload.WriteUInt32(cooldown.Duration);
                payload.WriteUInt32(cooldown.CategoryDuration);
            }

            SendPacketToClient(new RawServerPacket(Opcode.SMSG_PET_SPELLS_MESSAGE, ConnectionType.Instance, payload.GetData()));
            return true;
        }

        [PacketHandler(Opcode.SMSG_PET_ACTION_SOUND)]
        void HandlePetActionSound(WorldPacket packet)
        {
            PetActionSound sound = new PetActionSound();
            sound.UnitGUID = packet.ReadGuid().To128(GetSession().GameState);
            sound.Action = packet.ReadUInt32();
            SendPacketToClient(sound);
        }

        [PacketHandler(Opcode.SMSG_PET_BROKEN)]
        void HandlePetBroken(WorldPacket packet)
        {
            PrintNotification notify = new PrintNotification();
            notify.NotifyText = "Your pet has run away";
            SendPacketToClient(notify);
        }

        [PacketHandler(Opcode.SMSG_PET_UNLEARN_CONFIRM)]
        void HandlePetUnlearnConfirm(WorldPacket packet)
        {
            RespecWipeConfirm respec = new RespecWipeConfirm();
            respec.TrainerGUID = packet.ReadGuid().To128(GetSession().GameState);
            respec.Cost = packet.ReadUInt32();
            respec.RespecType = SpecResetType.PetTalents;
            SendPacketToClient(respec);
        }

        [PacketHandler(Opcode.MSG_LIST_STABLED_PETS)]
        void HandleListStabledPets(WorldPacket packet)
        {
            PetGuids pets = new PetGuids();
            var updateFields = GetSession().GameState.GetCachedObjectFieldsLegacy(GetSession().GameState.CurrentPlayerGuid);
            int UNIT_FIELD_SUMMON = LegacyVersion.GetUpdateField(UnitField.UNIT_FIELD_SUMMON);
            if (UNIT_FIELD_SUMMON >= 0 && updateFields.ContainsKey(UNIT_FIELD_SUMMON))
            {
                WowGuid128 guid = GetGuidValue(updateFields, UnitField.UNIT_FIELD_SUMMON).To128(GetSession().GameState);
                if (!guid.IsEmpty())
                    pets.Guids.Add(guid);
            }
            SendPacketToClient(pets);

            PetStableList stable = new PetStableList();
            stable.StableMaster = packet.ReadGuid().To128(GetSession().GameState);
            byte count = packet.ReadUInt8();
            stable.NumStableSlots = packet.ReadUInt8();
            for (byte i = 0; i < count; i++)
            {
                PetStableInfo pet = new PetStableInfo();
                pet.PetNumber = packet.ReadUInt32();
                pet.CreatureID = packet.ReadUInt32();
                pet.ExperienceLevel = packet.ReadUInt32();
                pet.PetName = packet.ReadCString();
                if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
                    pet.LoyaltyLevel = (byte)packet.ReadUInt32();
                pet.PetFlags = packet.ReadUInt8();

                if (pet.PetFlags != 1)
                    pet.PetFlags = 3;

                CreatureTemplate template = GameData.GetCreatureTemplate(pet.CreatureID);
                if (template != null)
                    pet.DisplayID = template.Display.CreatureDisplay[0].CreatureDisplayID;
                else
                {
                    WorldPacket query = new WorldPacket(Opcode.CMSG_QUERY_CREATURE);
                    query.WriteUInt32(pet.CreatureID);
                    query.WriteGuid(WowGuid64.Empty);
                    SendPacket(query);
                }

                stable.Pets.Add(pet);
            }
            SendPacketToClient(stable);
        }

        [PacketHandler(Opcode.SMSG_PET_STABLE_RESULT)]
        void HandlePetStableResult(WorldPacket packet)
        {
            PetStableResult stable = new PetStableResult();
            stable.Result = packet.ReadUInt8();
            SendPacketToClient(stable);
        }
    }
}
