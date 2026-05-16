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

using HermesProxy.World.Enums;
using System;
using HermesProxy.World.Objects;
using Framework.Collections;
using Framework.Constants;
using Framework;
using System.Collections.Generic;
using Framework.IO;
using HermesProxy.Enums;

namespace HermesProxy.World.Server.Packets
{
    public class QueryTimeResponse : ServerPacket
    {
        public QueryTimeResponse() : base(Opcode.SMSG_QUERY_TIME_RESPONSE, ConnectionType.Instance) { }

        public override void Write()
        {
            _worldPacket.WriteInt64(CurrentTime);
        }

        public long CurrentTime;
    }

    class QueryPetName : ClientPacket
    {
        public QueryPetName(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            UnitGUID = _worldPacket.ReadPackedGuid128();
        }

        public WowGuid128 UnitGUID;
    }

    class QueryPetNameResponse : ServerPacket
    {
        public QueryPetNameResponse() : base(Opcode.SMSG_QUERY_PET_NAME_RESPONSE, ConnectionType.Instance) { }

        public override void Write()
        {
            _worldPacket.WritePackedGuid128(UnitGUID);
            _worldPacket.WriteBit(Allow);

            if (Allow)
            {
                _worldPacket.WriteBits(Name.GetByteCount(), 8);
                _worldPacket.WriteBit(HasDeclined);

                for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                    _worldPacket.WriteBits(DeclinedNames.name[i].GetByteCount(), 7);

                for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                    _worldPacket.WriteString(DeclinedNames.name[i]);

                _worldPacket.WriteInt64(Timestamp);
                _worldPacket.WriteString(Name);
            }

            _worldPacket.FlushBits();
        }

        public WowGuid128 UnitGUID;
        public bool Allow;

        public bool HasDeclined;
        public DeclinedName DeclinedNames = new();
        public long Timestamp;
        public string Name = "";
    }

    public class QueryPlayerName : ClientPacket
    {
        public QueryPlayerName(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            Player = _worldPacket.ReadPackedGuid128();
        }

        public WowGuid128 Player;
    }

    public class QueryPlayerNames : ClientPacket
    {
        public QueryPlayerNames(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            uint count = _worldPacket.ReadUInt32();
            for (uint i = 0; i < count; i++)
                Players.Add(_worldPacket.ReadPackedGuid128());
        }

        public List<WowGuid128> Players = new List<WowGuid128>();
    }

    public class QueryPlayerNameResponse : ServerPacket
    {
        public QueryPlayerNameResponse() : base(Opcode.SMSG_QUERY_PLAYER_NAME_RESPONSE)
        {
            Data = new PlayerGuidLookupData();
        }

        public override void Write()
        {
            _worldPacket.WriteInt8((sbyte)Result);
            _worldPacket.WritePackedGuid128(Player);

            if (Result == 0)
                Data.Write(_worldPacket);
        }

        public WowGuid128 Player;
        public byte Result; // 0 - full packet, != 0 - only guid
        public PlayerGuidLookupData Data;
    }

    public class PlayerGuidLookupData
    {
        public void Write(WorldPacket data)
        {
            data.WriteBit(IsDeleted);
            data.WriteBits(Name.GetByteCount(), 6);

            for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                data.WriteBits(DeclinedNames.name[i].GetByteCount(), 7);

            data.FlushBits();
            for (byte i = 0; i < PlayerConst.MaxDeclinedNameCases; ++i)
                data.WriteString(DeclinedNames.name[i]);

            data.WritePackedGuid128(AccountID);
            data.WritePackedGuid128(BnetAccountID);
            data.WritePackedGuid128(GuidActual);
            data.WriteUInt64(GuildClubMemberID);
            data.WriteUInt32(VirtualRealmAddress);
            data.WriteUInt8((byte)RaceID);
            data.WriteUInt8((byte)Sex);
            data.WriteUInt8((byte)ClassID);
            data.WriteUInt8(Level);
            data.WriteUInt8(Unused915);
            data.WriteString(Name);
        }

        public bool IsDeleted;
        public WowGuid128 AccountID;
        public WowGuid128 BnetAccountID;
        public WowGuid128 GuidActual;
        public string Name = "";
        public ulong GuildClubMemberID;   // same as bgs.protocol.club.v1.MemberId.unique_id
        public uint VirtualRealmAddress;
        public Race RaceID = Race.None;
        public Gender Sex = Gender.None;
        public Class ClassID = Class.None;
        public byte Level;
        public byte Unused915;
        public DeclinedName DeclinedNames = new();
    }

    public class DeclinedName
    {
        public StringArray name = new(PlayerConst.MaxDeclinedNameCases);
    }

    public class QueryQuestInfo : ClientPacket
    {
        public QueryQuestInfo(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            QuestID = _worldPacket.ReadUInt32();
            QuestGiver = _worldPacket.ReadPackedGuid128();
        }

        public WowGuid128 QuestGiver;
        public uint QuestID;
    }

    public class QueryQuestInfoResponse : ServerPacket
    {
        public QueryQuestInfoResponse() : base(Opcode.SMSG_QUERY_QUEST_INFO_RESPONSE, ConnectionType.Instance) { }

        public override void Write()
        {
            if (ModernVersion.GetUpdateFieldsDefiningBuild() == ClientVersionBuild.V3_3_5a_12340)
            {
                WriteWotlk335();
                return;
            }

            _worldPacket.WriteUInt32(QuestID);
            _worldPacket.WriteBit(Allow);
            _worldPacket.FlushBits();

            if (Allow)
            {
                _worldPacket.WriteUInt32(Info.QuestID);
                _worldPacket.WriteInt32(Info.QuestType);
                _worldPacket.WriteInt32(Info.QuestLevel);
                _worldPacket.WriteInt32(Info.QuestScalingFactionGroup);
                _worldPacket.WriteInt32(Info.QuestMaxScalingLevel);
                _worldPacket.WriteUInt32(Info.QuestPackageID);
                _worldPacket.WriteInt32(Info.MinLevel);
                _worldPacket.WriteInt32(Info.QuestSortID);
                _worldPacket.WriteUInt32(Info.QuestInfoID);
                _worldPacket.WriteUInt32(Info.SuggestedGroupNum);
                _worldPacket.WriteUInt32(Info.RewardNextQuest);
                _worldPacket.WriteUInt32(Info.RewardXPDifficulty);

                _worldPacket.WriteFloat(Info.RewardXPMultiplier);

                _worldPacket.WriteInt32(Info.RewardMoney);
                _worldPacket.WriteUInt32(Info.RewardMoneyDifficulty);
                _worldPacket.WriteFloat(Info.RewardMoneyMultiplier);
                _worldPacket.WriteUInt32(Info.RewardBonusMoney);

                for (uint i = 0; i < QuestConst.QuestRewardDisplaySpellCount; ++i)
                    _worldPacket.WriteUInt32(Info.RewardDisplaySpell[i]);

                _worldPacket.WriteUInt32(Info.RewardSpell);
                _worldPacket.WriteUInt32(Info.RewardHonor);

                _worldPacket.WriteFloat(Info.RewardKillHonor);

                _worldPacket.WriteInt32(Info.RewardArtifactXPDifficulty);
                _worldPacket.WriteFloat(Info.RewardArtifactXPMultiplier);
                _worldPacket.WriteInt32(Info.RewardArtifactCategoryID);

                _worldPacket.WriteUInt32(Info.StartItem);
                _worldPacket.WriteUInt32(Info.Flags);
                _worldPacket.WriteUInt32(Info.FlagsEx);
                _worldPacket.WriteUInt32(Info.FlagsEx2);

                for (uint i = 0; i < QuestConst.QuestRewardItemCount; ++i)
                {
                    _worldPacket.WriteUInt32(Info.RewardItems[i]);
                    _worldPacket.WriteUInt32(Info.RewardAmount[i]);
                    _worldPacket.WriteInt32(Info.ItemDrop[i]);
                    _worldPacket.WriteInt32(Info.ItemDropQuantity[i]);
                }

                for (uint i = 0; i < QuestConst.QuestRewardChoicesCount; ++i)
                {
                    _worldPacket.WriteUInt32(Info.UnfilteredChoiceItems[i].ItemID);
                    _worldPacket.WriteUInt32(Info.UnfilteredChoiceItems[i].Quantity);
                    _worldPacket.WriteUInt32(Info.UnfilteredChoiceItems[i].DisplayID);
                }

                _worldPacket.WriteUInt32(Info.POIContinent);
                _worldPacket.WriteFloat(Info.POIx);
                _worldPacket.WriteFloat(Info.POIy);
                _worldPacket.WriteUInt32(Info.POIPriority);

                _worldPacket.WriteUInt32(Info.RewardTitle);
                _worldPacket.WriteInt32(Info.RewardArenaPoints);
                _worldPacket.WriteUInt32(Info.RewardSkillLineID);
                _worldPacket.WriteUInt32(Info.RewardNumSkillUps);

                _worldPacket.WriteUInt32(Info.PortraitGiver);
                _worldPacket.WriteUInt32(Info.PortraitGiverMount);
                _worldPacket.WriteUInt32(Info.PortraitTurnIn);

                _worldPacket.WriteInt32(0); // Unk 2.5.2

                for (uint i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                {
                    _worldPacket.WriteUInt32(Info.RewardFactionID[i]);
                    _worldPacket.WriteInt32(Info.RewardFactionValue[i]);
                    _worldPacket.WriteInt32(Info.RewardFactionOverride[i]);
                    _worldPacket.WriteInt32(Info.RewardFactionCapIn[i]);
                }

                _worldPacket.WriteUInt32(Info.RewardFactionFlags);

                for (uint i = 0; i < QuestConst.QuestRewardCurrencyCount; ++i)
                {
                    _worldPacket.WriteUInt32(Info.RewardCurrencyID[i]);
                    _worldPacket.WriteUInt32(Info.RewardCurrencyQty[i]);
                }

                _worldPacket.WriteUInt32(Info.AcceptedSoundKitID);
                _worldPacket.WriteUInt32(Info.CompleteSoundKitID);

                _worldPacket.WriteUInt32(Info.AreaGroupID);
                _worldPacket.WriteUInt32(Info.TimeAllowed);

                _worldPacket.WriteInt32(Info.Objectives.Count);
                _worldPacket.WriteInt64(Info.AllowableRaces);
                _worldPacket.WriteInt32(Info.TreasurePickerID);
                _worldPacket.WriteInt32(Info.Expansion);

                _worldPacket.WriteBits(Info.LogTitle.GetByteCount(), 9);
                _worldPacket.WriteBits(Info.LogDescription.GetByteCount(), 12);
                _worldPacket.WriteBits(Info.QuestDescription.GetByteCount(), 12);
                _worldPacket.WriteBits(Info.AreaDescription.GetByteCount(), 9);
                _worldPacket.WriteBits(Info.PortraitGiverText.GetByteCount(), 10);
                _worldPacket.WriteBits(Info.PortraitGiverName.GetByteCount(), 8);
                _worldPacket.WriteBits(Info.PortraitTurnInText.GetByteCount(), 10);
                _worldPacket.WriteBits(Info.PortraitTurnInName.GetByteCount(), 8);
                _worldPacket.WriteBits(Info.QuestCompletionLog.GetByteCount(), 11);
                _worldPacket.WriteBit(Info.ReadyForTranslation);
                _worldPacket.FlushBits();

                foreach (QuestObjective questObjective in Info.Objectives)
                {
                    _worldPacket.WriteUInt32(questObjective.Id);
                    _worldPacket.WriteUInt8((byte)questObjective.Type);
                    _worldPacket.WriteInt8(questObjective.StorageIndex);
                    _worldPacket.WriteInt32(questObjective.ObjectID);
                    _worldPacket.WriteInt32(questObjective.Amount);
                    _worldPacket.WriteUInt32((uint)questObjective.Flags);
                    _worldPacket.WriteUInt32(questObjective.Flags2);
                    _worldPacket.WriteFloat(questObjective.ProgressBarWeight);

                    _worldPacket.WriteInt32(questObjective.VisualEffects.Length);
                    foreach (var visualEffect in questObjective.VisualEffects)
                        _worldPacket.WriteInt32(visualEffect);

                    _worldPacket.WriteBits(questObjective.Description.GetByteCount(), 8);
                    _worldPacket.FlushBits();

                    _worldPacket.WriteString(questObjective.Description);
                }

                _worldPacket.WriteString(Info.LogTitle);
                _worldPacket.WriteString(Info.LogDescription);
                _worldPacket.WriteString(Info.QuestDescription);
                _worldPacket.WriteString(Info.AreaDescription);
                _worldPacket.WriteString(Info.PortraitGiverText);
                _worldPacket.WriteString(Info.PortraitGiverName);
                _worldPacket.WriteString(Info.PortraitTurnInText);
                _worldPacket.WriteString(Info.PortraitTurnInName);
                _worldPacket.WriteString(Info.QuestCompletionLog);
            }
        }

        private void WriteWotlk335()
        {
            if (!Allow || Info == null)
            {
                _worldPacket.WriteUInt32(QuestID | 0x80000000u);
                return;
            }

            _worldPacket.WriteUInt32(Info.QuestID);
            _worldPacket.WriteUInt32((uint)Math.Max(0, Info.QuestType));
            _worldPacket.WriteInt32(Info.QuestLevel);
            _worldPacket.WriteInt32(Info.MinLevel);
            _worldPacket.WriteInt32(Info.QuestSortID);
            _worldPacket.WriteUInt32(Info.QuestInfoID);
            _worldPacket.WriteUInt32(Info.SuggestedGroupNum);

            WriteWotlk335ReputationObjectives();

            _worldPacket.WriteUInt32(Info.RewardNextQuest);
            _worldPacket.WriteUInt32(Info.RewardXPDifficulty);
            _worldPacket.WriteUInt32((uint)Math.Max(0, Info.RewardMoney));
            _worldPacket.WriteUInt32(Info.RewardBonusMoney);
            _worldPacket.WriteUInt32(Info.RewardDisplaySpell[0]);
            _worldPacket.WriteInt32((int)Info.RewardSpell);
            _worldPacket.WriteUInt32(Info.RewardHonor);
            _worldPacket.WriteFloat(Info.RewardKillHonor);
            _worldPacket.WriteUInt32(Info.StartItem);
            _worldPacket.WriteUInt32(Info.Flags & 0xFFFF);
            _worldPacket.WriteUInt32(Info.RewardTitle);
            _worldPacket.WriteUInt32(GetWotlk335PlayerKillObjective());
            _worldPacket.WriteUInt32(0); // bonus talents
            _worldPacket.WriteInt32(Info.RewardArenaPoints);
            _worldPacket.WriteUInt32(0); // review rep show mask

            for (int i = 0; i < QuestConst.QuestRewardItemCount; ++i)
            {
                _worldPacket.WriteUInt32(Info.RewardItems[i]);
                _worldPacket.WriteUInt32(Info.RewardAmount[i]);
            }

            for (int i = 0; i < QuestConst.QuestRewardChoicesCount; ++i)
            {
                _worldPacket.WriteUInt32(Info.UnfilteredChoiceItems[i].ItemID);
                _worldPacket.WriteUInt32(Info.UnfilteredChoiceItems[i].Quantity);
            }

            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                _worldPacket.WriteUInt32(Info.RewardFactionID[i]);
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                _worldPacket.WriteInt32(Info.RewardFactionValue[i]);
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; ++i)
                _worldPacket.WriteInt32(Info.RewardFactionOverride[i]);

            _worldPacket.WriteUInt32(Info.POIContinent);
            _worldPacket.WriteFloat(Info.POIx);
            _worldPacket.WriteFloat(Info.POIy);
            _worldPacket.WriteUInt32(Info.POIPriority);

            _worldPacket.WriteCString(Info.LogTitle ?? string.Empty);
            _worldPacket.WriteCString(Info.LogDescription ?? string.Empty);
            _worldPacket.WriteCString(Info.QuestDescription ?? string.Empty);
            _worldPacket.WriteCString(Info.AreaDescription ?? string.Empty);
            _worldPacket.WriteCString(Info.QuestCompletionLog ?? string.Empty);

            WriteWotlk335CreatureOrGoObjectives();
            WriteWotlk335ItemObjectives();
            WriteWotlk335ObjectiveTexts();
        }

        private void WriteWotlk335ReputationObjectives()
        {
            int written = 0;
            for (int i = 0; i < Info.Objectives.Count && written < 2; ++i)
            {
                QuestObjective objective = Info.Objectives[i];
                if (objective.Type != QuestObjectiveType.MinReputation &&
                    objective.Type != QuestObjectiveType.IncreaseReputation)
                    continue;

                _worldPacket.WriteUInt32((uint)Math.Max(0, objective.ObjectID));
                _worldPacket.WriteUInt32((uint)Math.Max(0, objective.Amount));
                ++written;
            }

            while (written++ < 2)
            {
                _worldPacket.WriteUInt32(0);
                _worldPacket.WriteUInt32(0);
            }
        }

        private uint GetWotlk335PlayerKillObjective()
        {
            for (int i = 0; i < Info.Objectives.Count; ++i)
            {
                QuestObjective objective = Info.Objectives[i];
                if (objective.Type == QuestObjectiveType.PlayerKills)
                    return (uint)Math.Max(0, objective.Amount);
            }

            return 0;
        }

        private void WriteWotlk335CreatureOrGoObjectives()
        {
            int written = 0;
            for (int i = 0; i < Info.Objectives.Count && written < 4; ++i)
            {
                QuestObjective objective = Info.Objectives[i];
                if (objective.Type != QuestObjectiveType.Monster &&
                    objective.Type != QuestObjectiveType.GameObject &&
                    objective.Type != QuestObjectiveType.TalkTo)
                    continue;

                uint objectId = (uint)Math.Max(0, objective.ObjectID);
                if (objective.Type == QuestObjectiveType.GameObject)
                    objectId |= 0x80000000u;

                _worldPacket.WriteUInt32(objectId);
                _worldPacket.WriteUInt32((uint)Math.Max(0, objective.Amount));
                _worldPacket.WriteUInt32(0); // item drop id
                _worldPacket.WriteUInt32(0); // required source count
                ++written;
            }

            while (written++ < 4)
            {
                _worldPacket.WriteUInt32(0);
                _worldPacket.WriteUInt32(0);
                _worldPacket.WriteUInt32(0);
                _worldPacket.WriteUInt32(0);
            }
        }

        private void WriteWotlk335ItemObjectives()
        {
            int written = 0;
            for (int i = 0; i < Info.Objectives.Count && written < 6; ++i)
            {
                QuestObjective objective = Info.Objectives[i];
                if (objective.Type != QuestObjectiveType.Item)
                    continue;

                _worldPacket.WriteUInt32((uint)Math.Max(0, objective.ObjectID));
                _worldPacket.WriteUInt32((uint)Math.Max(0, objective.Amount));
                ++written;
            }

            while (written++ < 6)
            {
                _worldPacket.WriteUInt32(0);
                _worldPacket.WriteUInt32(0);
            }
        }

        private void WriteWotlk335ObjectiveTexts()
        {
            for (int i = 0; i < 4; ++i)
            {
                string description = i < Info.Objectives.Count
                    ? Info.Objectives[i].Description ?? string.Empty
                    : string.Empty;
                _worldPacket.WriteCString(description);
            }
        }

        public bool Allow;
        public QuestTemplate Info;
        public uint QuestID;
    }

    public class QueryCreature : ClientPacket
    {
        public QueryCreature(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            CreatureID = _worldPacket.ReadUInt32();

            if (_worldPacket.CanRead() && (Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340 ||
                                           _worldPacket.GetCurrentStream().Length - _worldPacket.GetCurrentStream().Position >= sizeof(ulong)))
                Guid = _worldPacket.ReadGuid();
        }

        public uint CreatureID;
        public WowGuid64 Guid = WowGuid64.Empty;
    }

    public class QueryCreatureResponse : ServerPacket
    {
        public QueryCreatureResponse() : base(Opcode.SMSG_QUERY_CREATURE_RESPONSE, ConnectionType.Instance) { }

        public override void Write()
        {
            if (ModernVersion.GetUpdateFieldsDefiningBuild() == ClientVersionBuild.V3_3_5a_12340)
            {
                if (!Allow)
                {
                    _worldPacket.WriteUInt32(CreatureID | 0x80000000u);
                    return;
                }

                _worldPacket.WriteUInt32(CreatureID);

                for (int i = 0; i < CreatureConst.MaxCreatureNames; ++i)
                    _worldPacket.WriteCString(Stats.Name[i] ?? string.Empty);

                _worldPacket.WriteCString(Stats.Title ?? string.Empty);      // sub_name
                _worldPacket.WriteCString(Stats.CursorName ?? string.Empty); // description

                _worldPacket.WriteUInt32(Stats.Flags[0]); // type_flags
                _worldPacket.WriteUInt32((uint)Stats.Type); // creature_type
                _worldPacket.WriteUInt32((uint)Stats.Family); // creature_family
                _worldPacket.WriteUInt32((uint)Stats.Classification); // creature_rank
                _worldPacket.WriteUInt32(Stats.ProxyCreatureID[0]); // kill_credit1
                _worldPacket.WriteUInt32(Stats.ProxyCreatureID[1]); // kill_credit2

                for (int i = 0; i < 4; ++i)
                {
                    uint displayId = 0;
                    if (i < Stats.Display.CreatureDisplay.Count)
                        displayId = Stats.Display.CreatureDisplay[i].CreatureDisplayID;

                    _worldPacket.WriteUInt32(displayId);
                }

                _worldPacket.WriteFloat(Stats.HpMulti == 0 ? 1.0f : Stats.HpMulti);
                _worldPacket.WriteFloat(Stats.EnergyMulti == 0 ? 1.0f : Stats.EnergyMulti);
                _worldPacket.WriteUInt8((byte)(Stats.Leader ? 1 : 0)); // racial_leader

                for (int i = 0; i < 6; ++i)
                {
                    uint itemId = 0;
                    if (i < Stats.QuestItems.Count)
                        itemId = Stats.QuestItems[i];

                    _worldPacket.WriteUInt32(itemId);
                }

                _worldPacket.WriteUInt32(Stats.MovementInfoID);
                return;
            }

            _worldPacket.WriteUInt32(CreatureID);
            _worldPacket.WriteBit(Allow);
            _worldPacket.FlushBits();

            if (Allow)
            {
                _worldPacket.WriteBits(Stats.Title.IsEmpty() ? 0 : Stats.Title.GetByteCount() + 1, 11);
                _worldPacket.WriteBits(Stats.TitleAlt.IsEmpty() ? 0 : Stats.TitleAlt.GetByteCount() + 1, 11);
                _worldPacket.WriteBits(Stats.CursorName.IsEmpty() ? 0 : Stats.CursorName.GetByteCount() + 1, 6);
                _worldPacket.WriteBit(Stats.Civilian);
                _worldPacket.WriteBit(Stats.Leader);

                for (var i = 0; i < CreatureConst.MaxCreatureNames; ++i)
                {
                    _worldPacket.WriteBits(Stats.Name[i].GetByteCount() + 1, 11);
                    _worldPacket.WriteBits(Stats.NameAlt[i].GetByteCount() + 1, 11);
                }

                for (var i = 0; i < CreatureConst.MaxCreatureNames; ++i)
                {
                    if (!string.IsNullOrEmpty(Stats.Name[i]))
                        _worldPacket.WriteCString(Stats.Name[i]);
                    if (!string.IsNullOrEmpty(Stats.NameAlt[i]))
                        _worldPacket.WriteCString(Stats.NameAlt[i]);
                }

                for (var i = 0; i < 2; ++i)
                    _worldPacket.WriteUInt32(Stats.Flags[i]);

                _worldPacket.WriteInt32(Stats.Type);
                _worldPacket.WriteInt32(Stats.Family);
                _worldPacket.WriteInt32(Stats.Classification);
                _worldPacket.WriteUInt32(Stats.PetSpellDataId);

                for (var i = 0; i < CreatureConst.MaxCreatureKillCredit; ++i)
                    _worldPacket.WriteUInt32(Stats.ProxyCreatureID[i]);

                _worldPacket.WriteInt32(Stats.Display.CreatureDisplay.Count);
                _worldPacket.WriteFloat(Stats.Display.TotalProbability);

                foreach (CreatureXDisplay display in Stats.Display.CreatureDisplay)
                {
                    _worldPacket.WriteUInt32(display.CreatureDisplayID);
                    _worldPacket.WriteFloat(display.Scale);
                    _worldPacket.WriteFloat(display.Probability);
                }

                _worldPacket.WriteFloat(Stats.HpMulti);
                _worldPacket.WriteFloat(Stats.EnergyMulti);

                _worldPacket.WriteInt32(Stats.QuestItems.Count);
                _worldPacket.WriteUInt32(Stats.MovementInfoID);
                _worldPacket.WriteInt32(Stats.HealthScalingExpansion);
                _worldPacket.WriteUInt32(Stats.RequiredExpansion);
                _worldPacket.WriteUInt32(Stats.VignetteID);
                _worldPacket.WriteInt32(Stats.Class);
                _worldPacket.WriteInt32(Stats.DifficultyID);
                _worldPacket.WriteInt32(Stats.WidgetSetID);
                _worldPacket.WriteInt32(Stats.WidgetSetUnitConditionID);

                if (!Stats.Title.IsEmpty())
                    _worldPacket.WriteCString(Stats.Title);

                if (!Stats.TitleAlt.IsEmpty())
                    _worldPacket.WriteCString(Stats.TitleAlt);

                if (!Stats.CursorName.IsEmpty())
                    _worldPacket.WriteCString(Stats.CursorName);

                foreach (var questItem in Stats.QuestItems)
                    _worldPacket.WriteUInt32(questItem);
            }
        }

        public bool Allow;
        public CreatureTemplate Stats;
        public uint CreatureID;
    }

    public class QueryGameObject : ClientPacket
    {
        public QueryGameObject(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            GameObjectID = _worldPacket.ReadUInt32();
            // WotLK uses a legacy full 64-bit guid here, while modern builds use packed 128.
            if (Settings.ClientBuild == ClientVersionBuild.V3_3_5a_12340)
            {
                if (_worldPacket.CanRead())
                    Guid = _worldPacket.ReadGuid();
            }
            else if (_worldPacket.CanRead())
            {
                Guid = _worldPacket.ReadPackedGuid128().To64();
            }
        }

        public uint GameObjectID;
        public WowGuid64 Guid = WowGuid64.Empty;
    }

    public class QueryGameObjectResponse : ServerPacket
    {
        public QueryGameObjectResponse() : base(Opcode.SMSG_QUERY_GAME_OBJECT_RESPONSE, ConnectionType.Instance) { }

        public override void Write()
        {
            // entry + optional found block (no guid field / size-prefixed blob wrapper).
            if (ModernVersion.GetUpdateFieldsDefiningBuild() == ClientVersionBuild.V3_3_5a_12340)
            {
                if (!Allow)
                {
                    _worldPacket.WriteUInt32(GameObjectID | 0x80000000u);
                    return;
                }

                _worldPacket.WriteUInt32(GameObjectID);
                _worldPacket.WriteUInt32(Stats.Type);
                _worldPacket.WriteUInt32(Stats.DisplayID);

                for (int i = 0; i < 4; i++)
                    _worldPacket.WriteCString(Stats.Name[i] ?? string.Empty);

                _worldPacket.WriteCString(Stats.IconName ?? string.Empty);
                _worldPacket.WriteCString(Stats.CastBarCaption ?? string.Empty);
                _worldPacket.WriteCString(Stats.UnkString ?? string.Empty);

                // 3.3.5 expects 24 GO data dwords in query response payloads.
                // Legacy 1.12 sources may only provide the first 6; missing entries stay zero.
                for (int i = 0; i < 24; i++)
                    _worldPacket.WriteUInt32((uint)Stats.Data[i]);

                _worldPacket.WriteFloat(Stats.Size);

                for (int i = 0; i < 6; i++)
                {
                    uint itemId = 0;
                    if (i < Stats.QuestItems.Count)
                        itemId = Stats.QuestItems[i];

                    _worldPacket.WriteUInt32(itemId);
                }

                return;
            }

            _worldPacket.WriteUInt32(GameObjectID);
            _worldPacket.WritePackedGuid128(Guid);
            _worldPacket.WriteBit(Allow);
            _worldPacket.FlushBits();

            ByteBuffer statsData = new();
            if (Allow)
            {
                statsData.WriteUInt32(Stats.Type);
                statsData.WriteUInt32(Stats.DisplayID);
                for (int i = 0; i < 4; i++)
                    statsData.WriteCString(Stats.Name[i]);

                statsData.WriteCString(Stats.IconName);
                statsData.WriteCString(Stats.CastBarCaption);
                statsData.WriteCString(Stats.UnkString);

                int dataFieldsCount = ModernVersion.AddedInClassicVersion(1, 14, 1, 2, 5, 3) ? 35 : 34;
                for (int i = 0; i < dataFieldsCount; i++)
                    statsData.WriteInt32(Stats.Data[i]);

                statsData.WriteFloat(Stats.Size);
                statsData.WriteUInt8((byte)Stats.QuestItems.Count);
                foreach (uint questItem in Stats.QuestItems)
                    statsData.WriteUInt32(questItem);

                statsData.WriteUInt32(Stats.ContentTuningId);
            }

            _worldPacket.WriteUInt32(statsData.GetSize());
            if (statsData.GetSize() != 0)
                _worldPacket.WriteBytes(statsData);
        }

        public uint GameObjectID;
        public WowGuid128 Guid;
        public bool Allow;
        public GameObjectStats Stats;
    }

    public class GameObjectStats
    {
        public string[] Name = new string[4];
        public string IconName = "";
        public string CastBarCaption = "";
        public string UnkString = "";
        public uint Type;
        public uint DisplayID;
        public int[] Data = new int[35];
        public float Size = 1;
        public List<uint> QuestItems = new();
        public uint ContentTuningId;
    }

    public class QueryPageText : ClientPacket
    {
        public QueryPageText(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            PageTextID = _worldPacket.ReadUInt32();
            ItemGUID = _worldPacket.ReadPackedGuid128();
        }

        public WowGuid128 ItemGUID;
        public uint PageTextID;
    }

    public class QueryPageTextResponse : ServerPacket
    {
        public QueryPageTextResponse() : base(Opcode.SMSG_QUERY_PAGE_TEXT_RESPONSE) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(PageTextID);
            _worldPacket.WriteBit(Allow);
            _worldPacket.FlushBits();

            if (Allow)
            {
                _worldPacket.WriteInt32(Pages.Count);
                foreach (PageTextInfo pageText in Pages)
                    pageText.Write(_worldPacket);
            }
        }

        public uint PageTextID;
        public bool Allow;
        public List<PageTextInfo> Pages = new();

        public struct PageTextInfo
        {
            public void Write(WorldPacket data)
            {
                data.WriteUInt32(Id);
                data.WriteUInt32(NextPageID);
                data.WriteInt32(PlayerConditionID);
                data.WriteUInt8(Flags);
                data.WriteBits(Text.GetByteCount(), 12);
                data.FlushBits();

                data.WriteString(Text);
            }

            public uint Id;
            public uint NextPageID;
            public int PlayerConditionID;
            public byte Flags;
            public string Text;
        }
    }

    public class QueryNPCText : ClientPacket
    {
        public QueryNPCText(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            TextID = _worldPacket.ReadUInt32();
            Guid = _worldPacket.ReadPackedGuid128();
        }

        public WowGuid128 Guid;
        public uint TextID;
    }

    public class QueryNPCTextResponse : ServerPacket
    {
        public QueryNPCTextResponse() : base(Opcode.SMSG_QUERY_NPC_TEXT_RESPONSE, ConnectionType.Instance) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(TextID);
            _worldPacket.WriteBit(Allow);

            _worldPacket.WriteInt32(Allow ? 8 * (4 + 4) : 0);
            if (Allow)
            {
                for (uint i = 0; i < 8; ++i)
                    _worldPacket.WriteFloat(Probabilities[i]);

                for (uint i = 0; i < 8; ++i)
                    _worldPacket.WriteUInt32(BroadcastTextID[i]);
            }
        }

        public uint TextID;
        public bool Allow;
        public float[] Probabilities = new float[8];
        public uint[] BroadcastTextID = new uint[8];
        public string[] MaleText = new string[8];
        public string[] FemaleText = new string[8];
        public uint[] Language = new uint[8];
        public uint[,] EmoteDelays = new uint[8, 3];
        public uint[,] Emotes = new uint[8, 3];
    }

    public class WhoRequestPkt : ClientPacket
    {
        public WhoRequestPkt(WorldPacket packet) : base(packet) { }

        public override void Read()
        {
            uint areasCount = _worldPacket.ReadBits<uint>(4);

            Request.Read(_worldPacket);
            RequestID = _worldPacket.ReadUInt32();

            for (int i = 0; i < areasCount; ++i)
                Areas.Add(_worldPacket.ReadInt32());
        }

        public WhoRequest Request = new();
        public uint RequestID;
        public List<int> Areas = new();
    }

    public class WhoRequest
    {
        public void Read(WorldPacket data)
        {
            MinLevel = data.ReadInt32();
            MaxLevel = data.ReadInt32();
            RaceFilter = data.ReadInt64();
            ClassFilter = data.ReadInt32();

            uint nameLength = data.ReadBits<uint>(6);
            uint virtualRealmNameLength = data.ReadBits<uint>(9);
            uint guildNameLength = data.ReadBits<uint>(7);
            uint guildVirtualRealmNameLength = data.ReadBits<uint>(9);
            uint wordsCount = data.ReadBits<uint>(3);

            ShowEnemies = data.HasBit();
            ShowArenaPlayers = data.HasBit();
            ExactName = data.HasBit();
            if (data.HasBit())
                ServerInfo = new();
            data.ResetBitPos();

            for (int i = 0; i < wordsCount; ++i)
            {
                Words.Add(data.ReadString(data.ReadBits<uint>(7)));
                data.ResetBitPos();
            }

            Name = data.ReadString(nameLength);
            VirtualRealmName = data.ReadString(virtualRealmNameLength);
            Guild = data.ReadString(guildNameLength);
            GuildVirtualRealmName = data.ReadString(guildVirtualRealmNameLength);

            if (ServerInfo != null)
                ServerInfo.Read(data);
        }

        public int MinLevel;
        public int MaxLevel;
        public string Name;
        public string VirtualRealmName;
        public string Guild;
        public string GuildVirtualRealmName;
        public long RaceFilter;
        public int ClassFilter = -1;
        public List<string> Words = new();
        public bool ShowEnemies;
        public bool ShowArenaPlayers;
        public bool ExactName;
        public WhoRequestServerInfo ServerInfo;
    }

    public class WhoRequestServerInfo
    {
        public void Read(WorldPacket data)
        {
            FactionGroup = data.ReadInt32();
            Locale = data.ReadInt32();
            RequesterVirtualRealmAddress = data.ReadUInt32();
        }

        public int FactionGroup;
        public int Locale;
        public uint RequesterVirtualRealmAddress;
    }

    public class WhoResponsePkt : ServerPacket
    {
        public WhoResponsePkt() : base(Opcode.SMSG_WHO) { }

        public override void Write()
        {
            _worldPacket.WriteUInt32(RequestID);
            _worldPacket.WriteBits(Players.Count, 6);
            _worldPacket.FlushBits();

            Players.ForEach(p => p.Write(_worldPacket));
        }

        public uint RequestID;
        public List<WhoEntry> Players = new();
    }

    public class WhoEntry
    {
        public void Write(WorldPacket data)
        {
            PlayerData.Write(data);

            data.WritePackedGuid128(GuildGUID);
            data.WriteUInt32(GuildVirtualRealmAddress);
            data.WriteInt32(AreaID);

            data.WriteBits(GuildName.GetByteCount(), 7);
            data.WriteBit(IsGM);
            data.WriteString(GuildName);

            data.FlushBits();
        }

        public PlayerGuidLookupData PlayerData = new();
        public WowGuid128 GuildGUID = WowGuid128.Empty;
        public uint GuildVirtualRealmAddress;
        public string GuildName = "";
        public int AreaID;
        public bool IsGM;
    }
}
