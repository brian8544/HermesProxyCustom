using Framework.Logging;
using HermesProxy.Enums;
using Microsoft.VisualBasic.FileIO;
using System.Collections.Generic;
using System.IO;

namespace HermesProxy.World
{
    public readonly struct TalentSpellRank
    {
        public TalentSpellRank(uint talentId, byte rank)
        {
            TalentID = talentId;
            Rank = rank;
        }

        public uint TalentID { get; }
        public byte Rank { get; }
    }

    public static partial class GameData
    {
        public static Dictionary<uint, TalentSpellRank> TalentSpellRanks = new();

        public static bool TryGetTalentSpellRank(uint spellId, out TalentSpellRank talent)
        {
            return TalentSpellRanks.TryGetValue(spellId, out talent);
        }

        public static void LoadTalentSpells()
        {
            TalentSpellRanks.Clear();

            string path = Path.Combine("CSV", $"TalentSpells{ModernVersion.ExpansionVersion}.csv");
            if (!File.Exists(path))
                return;

            using TextFieldParser csvParser = new(path);
            csvParser.CommentTokens = new string[] { "#" };
            csvParser.SetDelimiters(new string[] { "," });
            csvParser.HasFieldsEnclosedInQuotes = true;

            csvParser.ReadLine();
            while (!csvParser.EndOfData)
            {
                string[] fields = csvParser.ReadFields();
                if (fields.Length < 3)
                    continue;

                uint spellId = uint.Parse(fields[0]);
                if (spellId == 0)
                    continue;

                uint talentId = uint.Parse(fields[1]);
                byte rank = byte.Parse(fields[2]);
                TalentSpellRanks[spellId] = new TalentSpellRank(talentId, rank);
            }

            Log.Print(LogType.Storage, $"Loaded {TalentSpellRanks.Count} talent spell mappings.");
        }
    }
}
