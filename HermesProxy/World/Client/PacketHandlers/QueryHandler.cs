using Framework;
using Framework.Logging;
using HermesProxy.Enums;
using HermesProxy.World.Enums;
using HermesProxy.World.Objects;
using HermesProxy.World.Server.Packets;
using System;
using System.Collections.Generic;
using static HermesProxy.World.Server.Packets.QueryPageTextResponse;

namespace HermesProxy.World.Client
{
    public partial class WorldClient
    {
        // Handlers for SMSG opcodes coming the legacy world server
        [PacketHandler(Opcode.SMSG_QUERY_TIME_RESPONSE)]
        void HandleQueryTimeResponse(WorldPacket packet)
        {
            QueryTimeResponse response = new QueryTimeResponse();
            response.CurrentTime = packet.ReadInt32();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) && packet.CanRead())
                packet.ReadInt32(); // Next Daily Quest Reset Time
            SendPacketToClient(response);
        }
        [PacketHandler(Opcode.SMSG_QUERY_QUEST_INFO_RESPONSE)]
        void HandleQueryQuestInfoResponse(WorldPacket packet)
        {
            QueryQuestInfoResponse response = new QueryQuestInfoResponse();
            var id = packet.ReadEntry();
            response.QuestID = (uint)id.Key;
            if (id.Value) // entry is masked
            {
                response.Allow = false;
                SendPacketToClient(response);
                return;
            }

            response.Allow = true;
            response.Info = new QuestTemplate();
            QuestTemplate quest = response.Info;

            quest.QuestID = response.QuestID;
            quest.QuestType = packet.ReadInt32();
            quest.QuestLevel = packet.ReadInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                quest.MinLevel = packet.ReadInt32();
            else
                quest.MinLevel = 1;

            quest.QuestSortID = packet.ReadInt32();
            quest.QuestInfoID = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                quest.SuggestedGroupNum = packet.ReadUInt32();

            sbyte objectiveCounter = 0;
            for (int i = 0; i < 2; i++)
            {
                int factionId = packet.ReadInt32(); // RequiredFactionID
                int factionValue = packet.ReadInt32(); // RequiredFactionValue
                if (factionId != 0 && factionValue != 0)
                {
                    QuestObjective objective = new QuestObjective();
                    objective.QuestID = response.QuestID;
                    objective.Id = QuestObjective.QuestObjectiveCounter++;
                    objective.StorageIndex = objectiveCounter++;
                    objective.Type = QuestObjectiveType.MinReputation;
                    objective.ObjectID = factionId;
                    objective.Amount = factionValue;
                    quest.Objectives.Add(objective);
                }
            }

            quest.RewardNextQuest = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                quest.RewardXPDifficulty = packet.ReadUInt32();

            int rewOrReqMoney = packet.ReadInt32();
            if (rewOrReqMoney >= 0)
                quest.RewardMoney = rewOrReqMoney;
            else
            {
                QuestObjective objective = new QuestObjective();
                objective.QuestID = response.QuestID;
                objective.Id = QuestObjective.QuestObjectiveCounter++;
                objective.StorageIndex = objectiveCounter++;
                objective.Type = QuestObjectiveType.Money;
                objective.ObjectID = 0;
                objective.Amount = -rewOrReqMoney;
                quest.Objectives.Add(objective);
            }
            quest.RewardBonusMoney = packet.ReadUInt32();
            quest.RewardDisplaySpell[0] = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                quest.RewardSpell = packet.ReadUInt32();
                quest.RewardHonor = packet.ReadUInt32();
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                quest.RewardKillHonor = packet.ReadFloat();

            quest.StartItem = packet.ReadUInt32();
            quest.Flags = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_4_0_8089))
                quest.RewardTitle = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
            {
                int requiredPlayerKills = packet.ReadInt32();
                if (requiredPlayerKills != 0)
                {
                    QuestObjective objective = new QuestObjective();
                    objective.QuestID = response.QuestID;
                    objective.Id = QuestObjective.QuestObjectiveCounter++;
                    objective.StorageIndex = objectiveCounter++;
                    objective.Type = QuestObjectiveType.PlayerKills;
                    objective.ObjectID = 0;
                    objective.Amount = requiredPlayerKills;
                    quest.Objectives.Add(objective);
                }
                packet.ReadUInt32(); // RewardTalents
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                quest.RewardArenaPoints = packet.ReadInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                packet.ReadInt32(); // Unk

            for (int i = 0; i < 4; i++)
            {
                quest.RewardItems[i] = packet.ReadUInt32();
                quest.RewardAmount[i] = packet.ReadUInt32();
            }

            for (int i = 0; i < 6; i++)
            {
                QuestInfoChoiceItem choiceItem = new QuestInfoChoiceItem();
                choiceItem.ItemID = packet.ReadUInt32();
                choiceItem.Quantity = packet.ReadUInt32();

                uint displayId = GameData.GetItemDisplayId(choiceItem.ItemID);
                if (displayId != 0)
                    choiceItem.DisplayID = displayId;

                quest.UnfilteredChoiceItems[i] = choiceItem;
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
            {
                for (int i = 0; i < 5; i++)
                    quest.RewardFactionID[i] = packet.ReadUInt32();

                for (int i = 0; i < 5; i++)
                    quest.RewardFactionValue[i] = packet.ReadInt32();

                for (int i = 0; i < 5; i++)
                    quest.RewardFactionOverride[i] = (int)packet.ReadUInt32();
            }

            quest.POIContinent = packet.ReadUInt32();
            quest.POIx = packet.ReadFloat();
            quest.POIy = packet.ReadFloat();
            quest.POIPriority = packet.ReadUInt32();
            quest.LogTitle = packet.ReadCString();
            quest.LogDescription = packet.ReadCString();
            quest.QuestDescription = packet.ReadCString();
            quest.AreaDescription = packet.ReadCString();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                quest.QuestCompletionLog = packet.ReadCString();

            var reqId = new KeyValuePair<int, bool>[4];
            var reqItemFieldCount = 4;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
                reqItemFieldCount = 5;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192))
                reqItemFieldCount = 6;
            int[] requiredItemID = new int[reqItemFieldCount];
            int[] requiredItemCount = new int[reqItemFieldCount];

            for (int i = 0; i < 4; i++)
            {
                reqId[i] = packet.ReadEntry();
                bool isGo = reqId[i].Value;

                int creatureOrGoId = reqId[i].Key;
                int creatureOrGoAmount = packet.ReadInt32();

                if (creatureOrGoId != 0 && creatureOrGoAmount != 0)
                {
                    QuestObjective objective = new QuestObjective();
                    objective.QuestID = response.QuestID;
                    objective.Id = QuestObjective.QuestObjectiveCounter++;
                    objective.StorageIndex = objectiveCounter++;
                    objective.Type = isGo ? QuestObjectiveType.GameObject : QuestObjectiveType.Monster;
                    objective.ObjectID = creatureOrGoId;
                    objective.Amount = creatureOrGoAmount;
                    quest.Objectives.Add(objective);
                }

                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                    requiredItemID[i] = packet.ReadInt32();

                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_3_0_10958))
                    requiredItemCount[i] = packet.ReadInt32();

                if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_8_9464))
                {
                    requiredItemID[i] = packet.ReadInt32();
                    requiredItemCount[i] = packet.ReadInt32();
                }
            }

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_8_9464))
            {
                for (int i = 0; i < reqItemFieldCount; i++)
                {
                    requiredItemID[i] = packet.ReadInt32();
                    requiredItemCount[i] = packet.ReadInt32();
                }
            }

            for (int i = 0; i < reqItemFieldCount; i++)
            {
                if (requiredItemID[i] != 0 && requiredItemCount[i] != 0)
                {
                    QuestObjective objective = new QuestObjective();
                    objective.QuestID = response.QuestID;
                    objective.Id = QuestObjective.QuestObjectiveCounter++;
                    objective.StorageIndex = objectiveCounter++;
                    objective.Type = QuestObjectiveType.Item;
                    objective.ObjectID = requiredItemID[i];
                    objective.Amount = requiredItemCount[i];
                    quest.Objectives.Add(objective);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                string objectiveText = packet.ReadCString();
                if (quest.Objectives.Count > i)
                    quest.Objectives[i].Description = objectiveText;
            }

            // Placeholders
            quest.QuestMaxScalingLevel = 255;
            quest.RewardXPMultiplier = 1;
            quest.RewardMoneyMultiplier = 1;
            quest.RewardArtifactXPMultiplier = 1;
            for (int i = 0; i < QuestConst.QuestRewardReputationsCount; i++)
                quest.RewardFactionCapIn[i] = 7;
            quest.AllowableRaces = 511;
            quest.AcceptedSoundKitID = 890;
            quest.CompleteSoundKitID = 878;

            GameData.StoreQuestTemplate(response.QuestID, quest);
            SendPacketToClient(response);
        }

        [PacketHandler(Opcode.SMSG_QUERY_CREATURE_RESPONSE)]
        void HandleQueryCreatureResponse(WorldPacket packet)
        {
            QueryCreatureResponse response = new QueryCreatureResponse();
            var id = packet.ReadEntry();
            response.CreatureID = (uint)id.Key;
            if (id.Value) // entry is masked
            {
                response.Allow = false;
                SendPacketToClient(response);
                return;
            }

            response.Allow = true;
            response.Stats = new CreatureTemplate();
            CreatureTemplate creature = response.Stats;

            for (int i = 0; i < 4; i++)
                creature.Name[i] = packet.ReadCString();

            creature.Title = packet.ReadCString();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                creature.CursorName = packet.ReadCString();

            creature.Flags[0] = packet.ReadUInt32(); // Type Flags
            creature.Type = packet.ReadInt32();
            creature.Family = packet.ReadInt32();
            creature.Classification = packet.ReadInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
            {
                for (int i = 0; i < 2; ++i)
                    creature.ProxyCreatureID[i] = packet.ReadUInt32();
            }
            else
            {
                if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V3_0_2_9056))
                    packet.ReadInt32(); // unk
                creature.PetSpellDataId = packet.ReadUInt32();
            }

            int displayIdCount = LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180) ? 4 : 1;
            for (int i = 0; i < displayIdCount; i++)
            {
                uint displayId = packet.ReadUInt32();
                if (displayId != 0)
                {
                    uint safeDisplayId = IsWotlkFrontendClient() ? GameData.GetSafeCreatureDisplayId(displayId) : displayId;
                    creature.Display.CreatureDisplay.Add(new CreatureXDisplay(safeDisplayId, 1, 0));
                }
            }

            if (IsWotlkFrontendClient() && creature.Display.CreatureDisplay.Count == 0)
                creature.Display.CreatureDisplay.Add(new CreatureXDisplay(GameData.GetSafeCreatureDisplayId(), 1, 0));

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                creature.HpMulti = packet.ReadFloat();
                creature.EnergyMulti = packet.ReadFloat();
            }
            else
            {
                creature.HpMulti = 1;
                creature.EnergyMulti = 1;
            }

            if (LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180))
                creature.Civilian = packet.ReadBool();
            creature.Leader = packet.ReadBool();

            int questItems = LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192) ? 6 : 4;
            creature.MovementInfoID = 0;
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
            {
                for (uint i = 0; i < questItems; ++i)
                {
                    uint itemId = packet.ReadUInt32();
                    if (itemId != 0)
                        creature.QuestItems.Add(itemId);
                }

                creature.MovementInfoID = packet.ReadUInt32();
            }

            // Placeholders
            creature.Flags[0] |= 134217728;
            creature.Class = 1;

            GameData.StoreCreatureTemplate(response.CreatureID, creature);
            SendPacketToClient(response);
        }
        [PacketHandler(Opcode.SMSG_QUERY_GAME_OBJECT_RESPONSE)]
        void HandleQueryGameObjectResposne(WorldPacket packet)
        {
            QueryGameObjectResponse response = new QueryGameObjectResponse();
            var id = packet.ReadEntry();
            response.GameObjectID = (uint)id.Key;
            response.Guid = WowGuid128.Empty;
            if (id.Value) // entry is masked
            {
                response.Allow = false;
                SendPacketToClient(response);
                return;
            }

            response.Allow = true;
            response.Stats = new GameObjectStats();
            GameObjectStats gameObject = response.Stats;

            gameObject.Type = packet.ReadUInt32();
            gameObject.DisplayID = packet.ReadUInt32();

            for (int i = 0; i < 4; i++)
                gameObject.Name[i] = packet.ReadCString();

            gameObject.IconName = packet.ReadCString();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                gameObject.CastBarCaption = packet.ReadCString();
                gameObject.UnkString = packet.ReadCString();
            }

            // Classic-era servers may only send the first 6 GO data dwords,
            // while later builds send the full 24. Read safely and zero-fill.
            int expectedDataWords = LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180) ? 6 : 24;
            var stream = packet.GetCurrentStream();
            int remainingBytes = (int)(stream.Length - stream.Position);
            int readableWords = Math.Min(expectedDataWords, remainingBytes / 4);

            for (int i = 0; i < readableWords; i++)
                gameObject.Data[i] = packet.ReadInt32();

            for (int i = readableWords; i < 24; i++)
                gameObject.Data[i] = 0;

            if (readableWords < expectedDataWords)
                Log.Print(LogType.Warn, $"[GOQuery] Entry={response.GameObjectID} short raw data payload: expected {expectedDataWords} dwords, got {readableWords}.");

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_0_2_9056))
                gameObject.Size = packet.ReadFloat();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V3_1_0_9767))
            {
                uint count = (uint)(LegacyVersion.AddedInVersion(ClientVersionBuild.V3_2_0_10192) ? 6 : 4);
                for (uint i = 0; i < count; i++)
                {
                    uint itemId = packet.ReadUInt32();
                    if (itemId != 0)
                        gameObject.QuestItems.Add(itemId);
                }
            }

            GetSession().GameState.GameObjectQueryCache[response.GameObjectID] = response;

            SendPacketToClient(response);
        }
        [PacketHandler(Opcode.SMSG_QUERY_PAGE_TEXT_RESPONSE)]
        void HandleQueryPageTextResponse(WorldPacket packet)
        {
            QueryPageTextResponse response = new QueryPageTextResponse();
            response.PageTextID = packet.ReadUInt32();
            response.Allow = true;
            PageTextInfo page = new PageTextInfo();
            page.Id = response.PageTextID;
            page.Text = packet.ReadCString();
            page.NextPageID = packet.ReadUInt32();
            response.Pages.Add(page);
            SendPacketToClient(response);
        }
        [PacketHandler(Opcode.SMSG_QUERY_NPC_TEXT_RESPONSE)]
        void HandleQueryNpcTextResponse(WorldPacket packet)
        {
            QueryNPCTextResponse response = new QueryNPCTextResponse();
            var id = packet.ReadEntry();
            response.TextID = (uint)id.Key;
            if (id.Value) // entry is masked
            {
                response.Allow = false;
                SendPacketToClient(response);
                return;
            }

            response.Allow = true;

            for (int i = 0; i < 8; i++)
            {
                response.Probabilities[i] = packet.ReadFloat();
                string maleText = packet.ReadCString().TrimEnd().Replace("\0", "");
                string femaleText = packet.ReadCString().TrimEnd().Replace("\0", "");
                uint language = packet.ReadUInt32();
                response.MaleText[i] = maleText;
                response.FemaleText[i] = femaleText;
                response.Language[i] = language;

                ushort[] emoteDelays = new ushort[3];
                ushort[]  emotes = new ushort[3];
                for (int j = 0; j < 3; j++)
                {
                    emoteDelays[j] = (ushort)packet.ReadUInt32();
                    emotes[j] = (ushort)packet.ReadUInt32();
                    response.EmoteDelays[i, j] = emoteDelays[j];
                    response.Emotes[i, j] = emotes[j];
                }

                const string placeholderGossip = "Greetings $N";

                if (String.IsNullOrEmpty(maleText) && String.IsNullOrEmpty(femaleText) ||
                    maleText.Equals(placeholderGossip) && femaleText.Equals(placeholderGossip) && i != 0)
                    response.BroadcastTextID[i] = 0;
                else
                    response.BroadcastTextID[i] = GameData.GetBroadcastTextId(maleText, femaleText, language, emoteDelays, emotes);
            }

            SendPacketToClient(response);
        }

        [PacketHandler(Opcode.SMSG_ITEM_QUERY_SINGLE_RESPONSE)]
        void HandleItemQueryResponse(WorldPacket packet)
        {
            long startPos = packet.GetCurrentStream().Position;
            uint firstDword = packet.ReadUInt32();
            packet.GetCurrentStream().Position = startPos;

            bool hasLeadingEntry =
                LegacyVersion.RemovedInVersion(ClientVersionBuild.V2_0_1_6180) ||
                firstDword > 64 ||
                (GetSession().GameState.PendingLegacyItemQueries.Count > 0 &&
                 GetSession().GameState.PendingLegacyItemQueries.Peek() == firstDword);
            uint entryId;

            if (hasLeadingEntry)
            {
                var entry = packet.ReadEntry();
                if (entry.Value)
                {
                    if (IsWotlkFrontendClient())
                    {
                        byte[] missingPayload = BuildWotlkMissingItemQueryResponsePayload((uint)entry.Key);
                        TryForwardLegacyPayloadToWotlkClient(packet, Opcode.SMSG_ITEM_QUERY_SINGLE_RESPONSE, missingPayload);
                    }

                    if (GetSession().GameState.RequestedItemHotfixes.Contains((uint)entry.Key))
                    {
                        DBReply reply = new();
                        reply.RecordID = (uint)entry.Key;
                        reply.TableHash = DB2Hash.Item;
                        reply.Status = HotfixStatus.Invalid;
                        reply.Timestamp = (uint)Time.UnixTime;
                        SendPacketToClient(reply);
                    }
                    if (GetSession().GameState.RequestedItemSparseHotfixes.Contains((uint)entry.Key))
                    {
                        DBReply reply2 = new();
                        reply2.RecordID = (uint)entry.Key;
                        reply2.TableHash = DB2Hash.ItemSparse;
                        reply2.Status = HotfixStatus.Invalid;
                        reply2.Timestamp = (uint)Time.UnixTime;
                        SendPacketToClient(reply2);
                    }
                    return;
                }

                entryId = (uint)entry.Key;
                if (GetSession().GameState.PendingLegacyItemQueries.Count > 0 &&
                    GetSession().GameState.PendingLegacyItemQueries.Peek() == entryId)
                    GetSession().GameState.PendingLegacyItemQueries.Dequeue();
            }
            else
            {
                if (GetSession().GameState.PendingLegacyItemQueries.Count == 0)
                {
                    Log.Print(LogType.Warn, "[ItemQuery] Received response without entry and no pending query id.");
                    return;
                }

                entryId = GetSession().GameState.PendingLegacyItemQueries.Dequeue();
            }

            ItemTemplate item = new ItemTemplate();
            item.ReadFromLegacyPacket(entryId, packet);

            if (IsWotlkFrontendClient())
            {
                byte[] payload = BuildWotlkItemQueryResponsePayload(entryId, item);
                TryForwardLegacyPayloadToWotlkClient(packet, Opcode.SMSG_ITEM_QUERY_SINGLE_RESPONSE, payload);
            }

            SendItemUpdatesIfNeeded(item);
            GameData.StoreItemTemplate(entryId, item);
        }

        static byte[] BuildWotlkMissingItemQueryResponsePayload(uint entryId)
        {
            WorldPacket payload = new();
            payload.WriteUInt32(entryId | 0x80000000);
            return payload.GetData() ?? Array.Empty<byte>();
        }

        static byte[] BuildWotlkItemQueryResponsePayload(uint entryId, ItemTemplate item)
        {
            WorldPacket payload = new();

            payload.WriteUInt32(entryId);
            payload.WriteInt32(item.Class);
            payload.WriteUInt32(item.SubClass);
            payload.WriteInt32(item.SoundOverrideSubclass);
            payload.WriteCString(item.Name[0] ?? string.Empty);
            payload.WriteUInt8(0);
            payload.WriteUInt8(0);
            payload.WriteUInt8(0);

            uint displayId = item.DisplayID;
            if (displayId == 0 || GameData.GetItemIconFileDataIdByDisplayId(displayId) == 0)
            {
                uint mappedDisplay = GameData.GetItemDisplayId(entryId);
                if (mappedDisplay != 0 && GameData.GetItemIconFileDataIdByDisplayId(mappedDisplay) != 0)
                    displayId = mappedDisplay;
            }
            if (displayId == 0)
                displayId = 25; // conservative fallback display id

            payload.WriteUInt32(displayId);
            payload.WriteInt32(item.Quality);
            payload.WriteUInt32(item.Flags);
            payload.WriteUInt32(item.FlagsExtra);
            payload.WriteUInt32(item.BuyPrice);
            payload.WriteUInt32(item.SellPrice);
            payload.WriteInt32(item.InventoryType);
            payload.WriteInt32(item.AllowedClasses);
            payload.WriteInt32(item.AllowedRaces);
            payload.WriteUInt32(item.ItemLevel);
            payload.WriteUInt32(item.RequiredLevel);
            payload.WriteUInt32(item.RequiredSkillId);
            payload.WriteUInt32(item.RequiredSkillLevel);
            payload.WriteUInt32(item.RequiredSpell);
            payload.WriteUInt32(item.RequiredHonorRank);
            payload.WriteUInt32(item.RequiredCityRank);
            payload.WriteUInt32(item.RequiredRepFaction);
            payload.WriteUInt32(item.RequiredRepValue);
            payload.WriteInt32(item.MaxCount);
            payload.WriteInt32(item.MaxStackSize);
            payload.WriteUInt32(item.ContainerSlots);

            uint statsCount = item.StatsCount;
            if (statsCount == 0)
                statsCount = (uint)Math.Min(item.StatTypes.Length, item.StatValues.Length);
            statsCount = Math.Min(statsCount, (uint)Math.Min(item.StatTypes.Length, item.StatValues.Length));
            int statsCountInt = (int)statsCount;

            List<(int Type, int Value)> stats = new(statsCountInt);
            for (int i = 0; i < statsCountInt; i++)
            {
                int statType = item.StatTypes[i];
                int statValue = item.StatValues[i];

                // Vanilla payloads commonly carry unused stats as -1/0.
                // Forwarding those to WotLK as u32 can destabilize tooltips.
                if (statType < 0 || statValue == 0)
                    continue;
                if (!IsVanillaItemStatTypeSafeForWotlk(statType))
                    continue;

                stats.Add((statType, statValue));
            }

            payload.WriteUInt32((uint)stats.Count);
            foreach (var stat in stats)
            {
                payload.WriteInt32(stat.Type);
                payload.WriteInt32(stat.Value);
            }

            payload.WriteInt32(0);
            payload.WriteUInt32(0);

            for (int i = 0; i < 2; i++)
            {
                payload.WriteFloat(i < item.DamageMins.Length ? item.DamageMins[i] : 0.0f);
                payload.WriteFloat(i < item.DamageMaxs.Length ? item.DamageMaxs[i] : 0.0f);
                int damageType = i < item.DamageTypes.Length ? item.DamageTypes[i] : 0;
                payload.WriteInt32(damageType);
            }

            payload.WriteUInt32(item.Armor);
            payload.WriteUInt32(item.HolyResistance);
            payload.WriteUInt32(item.FireResistance);
            payload.WriteUInt32(item.NatureResistance);
            payload.WriteUInt32(item.FrostResistance);
            payload.WriteUInt32(item.ShadowResistance);
            payload.WriteUInt32(item.ArcaneResistance);
            payload.WriteUInt32(item.Delay);
            payload.WriteInt32(item.AmmoType);
            payload.WriteFloat(item.RangedMod);

            for (int i = 0; i < 5; i++)
            {
                int spellId = i < item.TriggeredSpellIds.Length ? item.TriggeredSpellIds[i] : 0;
                int spellType = i < item.TriggeredSpellTypes.Length ? item.TriggeredSpellTypes[i] : 0;
                int spellCharges = i < item.TriggeredSpellCharges.Length ? item.TriggeredSpellCharges[i] : 0;
                int spellCooldown = i < item.TriggeredSpellCooldowns.Length ? item.TriggeredSpellCooldowns[i] : -1;
                uint spellCategory = i < item.TriggeredSpellCategories.Length ? item.TriggeredSpellCategories[i] : 0;
                int spellCategoryCooldown = i < item.TriggeredSpellCategoryCooldowns.Length ? item.TriggeredSpellCategoryCooldowns[i] : -1;

                if (spellId <= 0)
                {
                    payload.WriteUInt32(0);
                    payload.WriteInt32(0);
                    payload.WriteInt32(0);
                    payload.WriteUInt32(uint.MaxValue);
                    payload.WriteUInt32(0);
                    payload.WriteUInt32(uint.MaxValue);
                }
                else
                {
                    payload.WriteUInt32((uint)spellId);
                    payload.WriteInt32(spellType);
                    payload.WriteInt32(spellCharges);
                    payload.WriteUInt32(unchecked((uint)spellCooldown));
                    payload.WriteUInt32(spellCategory);
                    payload.WriteUInt32(unchecked((uint)spellCategoryCooldown));
                }
            }

            payload.WriteInt32(item.Bonding);
            payload.WriteCString(item.Description ?? string.Empty);
            payload.WriteUInt32(item.PageText);
            payload.WriteInt32(item.Language);
            payload.WriteInt32(item.PageMaterial);
            payload.WriteUInt32(item.StartQuestId);
            payload.WriteUInt32(item.LockId);
            payload.WriteInt32(item.Material);
            payload.WriteInt32(item.SheathType);
            payload.WriteInt32(0);
            payload.WriteUInt32(0);
            payload.WriteUInt32(item.Block);
            payload.WriteUInt32(item.ItemSet);
            payload.WriteUInt32(item.MaxDurability);
            payload.WriteUInt32(item.AreaID);
            payload.WriteInt32(item.MapID);
            payload.WriteUInt32(item.BagFamily);
            payload.WriteInt32(item.TotemCategory);

            for (int i = 0; i < 3; i++)
            {
                payload.WriteInt32(i < item.ItemSocketColors.Length ? item.ItemSocketColors[i] : 0);
                payload.WriteUInt32(item.SocketContent[i]);
            }

            payload.WriteInt32(item.SocketBonus);
            payload.WriteInt32(item.GemProperties);
            payload.WriteInt32(item.RequiredDisenchantSkill);
            payload.WriteFloat(item.ArmorDamageModifier);
            payload.WriteUInt32(item.Duration);
            payload.WriteInt32(item.ItemLimitCategory);
            payload.WriteInt32(item.HolidayID);

            return payload.GetData() ?? Array.Empty<byte>();
        }

        static bool IsVanillaItemStatTypeSafeForWotlk(int statType)
        {
            // MaNGOS classic item stat ids are sparse and only define:
            // mana, health, agility, strength, intellect, spirit, stamina.
            // Filtering here prevents custom/shifted 1.12 values from being
            // interpreted as Wrath combat ratings or spell power in tooltips.
            return statType == 0 ||
                   statType == 1 ||
                   (statType >= 3 && statType <= 7);
        }

        void SendItemUpdatesIfNeeded(ItemTemplate item)
        {
            if (IsWotlkFrontendClient())
                return;

            Server.Packets.HotFixMessage? reply;

            reply = GameData.GenerateItemUpdateIfNeeded(item);
            if (reply != null)
                SendPacketToClient(reply);

            reply = GameData.GenerateItemSparseUpdateIfNeeded(item);
            if (reply != null)
            {
                // TODO!!! Something might be wrong here.
                // TODO: When I send the ItemSpare entry with HotFixMessage it does not work

                SendPacketToClient(reply); // TODO: <-- Optional??

                Server.Packets.DBReply replyA = new();
                replyA.Status = HotfixStatus.Valid;
                replyA.Timestamp = (uint)Time.UnixTime;
                replyA.RecordID = reply.Hotfixes[0].RecordId;
                replyA.TableHash = reply.Hotfixes[0].TableHash;
                replyA.Data = reply.Hotfixes[0].HotfixContent;
                SendPacketToClient(replyA);
            }

            for (byte i = 0; i < 5; i++)
            {
                reply = GameData.GenerateItemEffectUpdateIfNeeded(item, i);
                if (reply != null)
                    SendPacketToClient(reply);
            }

            if (!GameData.ItemCanHaveModel(item))
                return;

            reply = GameData.GenerateItemAppearanceUpdateIfNeeded(item);
            if (reply != null)
                SendPacketToClient(reply);

            reply = GameData.GenerateItemModifiedAppearanceUpdateIfNeeded(item);
            if (reply != null)
                SendPacketToClient(reply);
        }

        [PacketHandler(Opcode.SMSG_QUERY_PET_NAME_RESPONSE)]
        void HandleQueryPetNameResponse(WorldPacket packet)
        {
            uint petNumber = packet.ReadUInt32();
            WowGuid128 guid = GetSession().GameState.GetPetGuidByNumber(petNumber);
            if (guid == null)
            {
                Log.Print(LogType.Error, $"Pet name query response for unknown pet {petNumber}!");
                return;
            }

            QueryPetNameResponse response = new QueryPetNameResponse();
            response.UnitGUID = guid;
            response.Name = packet.ReadCString();
            if (response.Name.Length == 0)
            {
                response.Allow = false;
                packet.ReadBytes(7); // 0s
                return;
            }

            response.Allow = true;
            response.Timestamp = packet.ReadUInt32();

            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
            {
                var declined = packet.ReadBool();

                const int maxDeclinedNameCases = 5;

                if (declined)
                {
                    for (var i = 0; i < maxDeclinedNameCases; i++)
                        response.DeclinedNames.name[i] = packet.ReadCString();
                }
            }
            SendPacketToClient(response);
        }
        [PacketHandler(Opcode.SMSG_ITEM_NAME_QUERY_RESPONSE)]
        void HandleItemNameQueryResponse(WorldPacket packet)
        {
            uint entry = packet.ReadUInt32();
            string name = packet.ReadCString();
            if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                packet.ReadUInt32(); // Inventory Type
            GameData.StoreItemName(entry, name);
        }
        [PacketHandler(Opcode.SMSG_WHO)]
        void HandleWhoResponse(WorldPacket packet)
        {
            WhoResponsePkt response = new WhoResponsePkt();
            response.RequestID = GetSession().GameState.LastWhoRequestId;
            var count = packet.ReadUInt32();
            packet.ReadUInt32(); // Online count
            for (var i = 0; i < count; ++i)
            {
                WhoEntry player = new();
                player.PlayerData.Name = packet.ReadCString();
                player.GuildName = packet.ReadCString();
                player.PlayerData.Level = (byte)packet.ReadUInt32();
                player.PlayerData.ClassID = (Class)packet.ReadUInt32();
                player.PlayerData.RaceID = (Race)packet.ReadUInt32();
                if (LegacyVersion.AddedInVersion(ClientVersionBuild.V2_0_1_6180))
                    player.PlayerData.Sex = (Gender)packet.ReadUInt8();
                player.AreaID = packet.ReadInt32();

                player.PlayerData.GuidActual = GetSession().GameState.GetPlayerGuidByName(player.PlayerData.Name);
                if (player.PlayerData.GuidActual == null)
                    player.PlayerData.GuidActual = WowGuid128.CreateUnknownPlayerGuid();
                player.PlayerData.AccountID = GetSession().GetGameAccountGuidForPlayer(player.PlayerData.GuidActual);
                player.PlayerData.BnetAccountID = GetSession().GetBnetAccountGuidForPlayer(player.PlayerData.GuidActual);
                player.PlayerData.VirtualRealmAddress = GetSession().RealmId.GetAddress();

                if (!String.IsNullOrEmpty(player.GuildName))
                {
                    player.GuildGUID = GetSession().GetGuildGuid(player.GuildName);
                    player.GuildVirtualRealmAddress = player.PlayerData.VirtualRealmAddress;
                }
                response.Players.Add(player);
                Session.GameState.UpdatePlayerCache(player.PlayerData.GuidActual, new PlayerCache
                {
                    Name = player.PlayerData.Name,
                    RaceId = player.PlayerData.RaceID,
                    ClassId = player.PlayerData.ClassID,
                    SexId = player.PlayerData.Sex,
                    Level = player.PlayerData.Level,
                });
            }
            SendPacketToClient(response);
        }
    }
}
