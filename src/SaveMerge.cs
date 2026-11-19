using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TLDCoop
{
    /// <summary>
    /// Builds the joining player's copy of the host's world:
    /// everything about the world (scenes, containers, loot, weather, time) comes from the host's save,
    /// everything about the survivor (inventory, condition, afflictions, skills…) comes from the joiner's own save.
    /// </summary>
    internal static class SaveMerge
    {
        public const string CoopDisplayName = "ORTAK DUNYA";
        // One fixed slot per PC. The game builds its Load list when it starts, so a slot created later
        // does not show up there; re-using a slot that already exists does. Name and id must match.
        public const string FixedCoopSlot = "sandbox99999";
        public const uint FixedCoopGameId = 99999;

        /// <summary>Slot names this PC has used for co-op worlds (kept in the mod settings).</summary>
        public static Func<HashSet<string>> CoopSlots = () => new HashSet<string>();
        public static bool IsCoopSlot(string name) => !string.IsNullOrEmpty(name) && CoopSlots().Contains(name);

        // keys of the "global" section that describe the survivor, not the world
        private static readonly HashSet<string> PlayerKeys = new()
        {
            "m_Condition_Serialized", "m_Encumber_Serialized", "m_Hunger_Serialized", "m_Thirst_Serialized",
            "m_Fatigue_Serialized", "m_Freezing_Serialized", "m_Willpower_Serialized", "m_Inventory_Serialized",
            "m_PlayerManagerSerialized", "m_PlayerClimbRopeSerialized", "m_PlayerSkillsSerialized",
            "m_PlayerGameStatsSerialized", "m_HypothermiaSerialized", "m_WellFedSerialized", "m_FrostbiteSerialized",
            "m_FoodPoisoningSerialized", "m_DysenterySerialized", "m_SprainPainSerialized", "m_SprainedAnkleSerialized",
            "m_SprainedWristSerialized", "m_SprainsSerialized", "m_BurnsSerialized", "m_BurnsElectricSerialized",
            "m_BloodLossSerialized", "m_BrokenRibSerialized", "m_InfectionSerialized", "m_InfectionRiskSerialized",
            "m_LogSerialized", "m_RestSerialized", "m_PlayerMovementSerialized", "m_EmergencyStimSerialized",
            "m_EnergyBoostSerialized", "m_HeadacheSerialized", "m_CabinFeverSerialized", "m_IntestinalParasitesSerialized",
            "m_PlayerAnimationSerialized", "m_SkillsManagerSerialized", "m_FeatsEnabledSerialized",
            "m_KnowledgeManagerSerialized", "m_UnlockedBlueprintsSerialized", "m_CollectionManagerSerialized",
            "m_BigCarrySystemSerialized", "m_FearSerialized", "m_AnxietySerialized", "m_SuffocatingSerialized",
            "m_InsomniaSerialized", "m_TrackableItemSerialized", "m_RecipeBookState", "m_FoodMaxStatBuffEffects",
            "m_ChemicalPoisoningSerialized", "m_ScurvySerialized", "m_PlayerNutritionSerialized",
            "m_CheatDeathAfflictionSerialized", "m_DiminishedStateAfflictionSerialized", "m_SevereLacerationSerialized",
            "m_TempInventoryPlacementListSerialized", "m_HighResolutionTimerSerialized",
        };

        private static readonly JsonSerializerOptions WriteOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        public static string SaveDir()
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hinterland", "TheLongDark");
            var survival = Path.Combine(baseDir, "Survival");
            return Directory.Exists(survival) ? survival : baseDir;
        }

        public static string FindSlotFile(string slotName)
        {
            var p = Path.Combine(SaveDir(), slotName);
            if (File.Exists(p)) return p;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hinterland");
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, slotName, SearchOption.AllDirectories).FirstOrDefault();
        }

        private static JsonNode Clone(JsonNode n) => n == null ? null : JsonNode.Parse(n.ToJsonString());

        public static JsonObject ReadOuter(byte[] file) => JsonNode.Parse(Lzf.Decompress(file))!.AsObject();

        public static JsonObject ReadSection(JsonObject outer, string key)
        {
            var dict = outer["m_Dict"]!.AsObject();
            if (!dict.TryGetPropertyValue(key, out var node) || node == null) return null;
            var bytes = Convert.FromBase64String(node.GetValue<string>());
            return JsonNode.Parse(Lzf.Decompress(bytes))!.AsObject();
        }

        public static void WriteSection(JsonObject outer, string key, JsonObject section)
        {
            var raw = Encoding.UTF8.GetBytes(section.ToJsonString(WriteOpts));
            outer["m_Dict"]![key] = Convert.ToBase64String(Lzf.Compress(raw));
        }

        /// <summary>Newest survival save on this PC that is not the co-op slot — the joiner's own survivor.</summary>
        public static string FindOwnCharacterSlot(string lastCoopSlot)
        {
            if (!string.IsNullOrEmpty(lastCoopSlot))
            {
                var coop = Path.Combine(SaveDir(), lastCoopSlot);
                if (File.Exists(coop)) return coop;
            }
            var dir = SaveDir();
            if (!Directory.Exists(dir)) return null;
            var coopNames = CoopSlots();
            return new DirectoryInfo(dir).GetFiles("sandbox*")
                .Where(f => !coopNames.Contains(f.Name) && !f.Name.Contains('.'))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName).FirstOrDefault();
        }

        public sealed class Result
        {
            public byte[] FileBytes;
            public string OuterJson;
            public int Changelist;
            public string CharacterSource;
            public string Scene;
            public bool HasStart; public float StartX, StartY, StartZ; // where the joiner stands right after loading
        }

        /// <summary>Host world + own survivor → bytes of the co-op slot file.</summary>
        public static Result BuildCoopSlot(byte[] hostFile, string ownSlotPath, string slotName, uint gameId)
        {
            var outer = ReadOuter(hostFile);
            var global = ReadSection(outer, "global") ?? throw new InvalidDataException("host save has no global section");

            // where the host stands — the joiner appears right next to them
            float[] hostPos = null; JsonNode hostRot = null;
            try
            {
                var hostPm = JsonNode.Parse(global["m_PlayerManagerSerialized"]!.GetValue<string>())!.AsObject();
                hostPos = hostPm["m_SaveGamePosition"]!.AsArray().Select(n => n!.GetValue<float>()).ToArray();
                hostRot = Clone(hostPm["m_SaveGameRotation"]);
            }
            catch (Exception e) { CoopMod.Log("Could not read host position: " + e.Message); }

            string source = null;
            if (!string.IsNullOrEmpty(ownSlotPath) && File.Exists(ownSlotPath))
            {
                try
                {
                    var own = ReadSection(ReadOuter(File.ReadAllBytes(ownSlotPath)), "global");
                    int copied = 0;
                    foreach (var key in PlayerKeys)
                        if (own != null && own.TryGetPropertyValue(key, out var v)) { global[key] = Clone(v); copied++; }
                    source = $"{Path.GetFileName(ownSlotPath)} ({copied} alan)";
                }
                catch (Exception e) { CoopMod.Log("Own character could not be read, using a copy of the host's: " + e.Message); }
            }

            if (hostPos != null)
            {
                var pm = JsonNode.Parse(global["m_PlayerManagerSerialized"]!.GetValue<string>())!.AsObject();
                pm["m_SaveGamePosition"] = new JsonArray(hostPos[0] + 1.2f, hostPos[1] + 0.3f, hostPos[2] + 1.2f);
                if (hostRot != null) pm["m_SaveGameRotation"] = hostRot;
                global["m_PlayerManagerSerialized"] = pm.ToJsonString(WriteOpts);
            }
            WriteSection(outer, "global", global);

            outer["m_InternalName"] = slotName;
            outer["m_DisplayName"] = CoopDisplayName;
            outer["m_GameId"] = gameId;
            // newest save on this PC, so the menu's "Continue" opens it (the host's timestamp would tie with the host's own slot)
            outer["m_Timestamp"] = DateTime.Now.AddSeconds(5).ToString("yyyy-MM-dd HH:mm:ss");

            string scene = null;
            try { scene = ReadSection(outer, "boot")?["m_SceneName"]?.GetValue<string>(); } catch { }
            bool hasStart = hostPos != null && hostPos.Length >= 3;

            var json = outer.ToJsonString(WriteOpts);
            return new Result
            {
                OuterJson = json,
                FileBytes = Lzf.Compress(Encoding.UTF8.GetBytes(json)),
                Changelist = outer["m_Changelist"]?.GetValue<int>() ?? 0,
                CharacterSource = source ?? "a copy of the host's character",
                Scene = scene,
                HasStart = hasStart,
                StartX = hasStart ? hostPos[0] + 1.2f : 0f, StartY = hasStart ? hostPos[1] + 0.3f : 0f, StartZ = hasStart ? hostPos[2] + 1.2f : 0f,
            };
        }
    }
}
