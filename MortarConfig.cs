using System;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;

namespace MortarStrikes
{
    public class MortarConfig
    {
        [JsonProperty("chancePerRaid")]
        public float ChancePerRaid { get; set; } = 0.35f;

        [JsonProperty("_comment_chancePerRaid")]
        public string _c02 { get; set; } = "0.0 = never, 1.0 = every raid. Default 0.35 = 35% chance";

        [JsonProperty("minDelayMinutes")]
        public float MinDelayMinutes { get; set; } = 5f;

        [JsonProperty("maxDelayMinutes")]
        public float MaxDelayMinutes { get; set; } = 15f;

        [JsonProperty("_comment_delay")]
        public string _c03 { get; set; } = "First strike randomly between min/max minutes after raid start";

        [JsonProperty("allowMultipleStrikes")]
        public bool AllowMultipleStrikes { get; set; } = true;

        [JsonProperty("additionalStrikeChance")]
        public float AdditionalStrikeChance { get; set; } = 0.25f;

        [JsonProperty("maxStrikesPerRaid")]
        public int MaxStrikesPerRaid { get; set; } = 3;

        [JsonProperty("barrageCount")]
        public int BarrageCount { get; set; } = 3;

        [JsonProperty("_comment_barrageCount")]
        public string _c11 { get; set; } = "Overlapping barrages per strike. Each barrage fires ~7 times a burst of about ~6 shots. 1 = normal. 2-3 = heavier";

        [JsonProperty("barrageSpacing")]
        public float BarrageSpacing { get; set; } = 3f;

        [JsonProperty("barrageSpreadRadius")]
        public float BarrageSpreadRadius { get; set; } = 50f;

        [JsonProperty("minDistanceFromTarget")]
        public float MinDistanceFromTarget { get; set; } = 30f;

        [JsonProperty("maxDistanceFromTarget")]
        public float MaxDistanceFromTarget { get; set; } = 200f;

        [JsonProperty("playerTargetingWeight")]
        public int PlayerTargetingWeight { get; set; } = 30;

        [JsonProperty("_comment_playerTargetingWeight")]
        public string _c16 { get; set; } = "0 = evenly random between all bots and players. 100 = always targets a player. Values in between give players proportionally higher chance of being selected";

        [JsonProperty("warningSmokeEnabled")]
        public bool WarningFlareEnabled { get; set; } = true;

        [JsonProperty("_comment_warningSmoke")]
        public string _c20a { get; set; } = "If true, spawns a colored smoke column above the strike zone before the barrage";

        [JsonProperty("warningSmokeColorR")]
        public float WarningSmokeColorR { get; set; } = 0.9f;

        [JsonProperty("warningSmokeColorG")]
        public float WarningSmokeColorG { get; set; } = 0.2f;

        [JsonProperty("warningSmokeColorB")]
        public float WarningSmokeColorB { get; set; } = 0.1f;

        [JsonProperty("_comment_smokeColor")]
        public string _c20b { get; set; } = "RGB color of the warning smoke (0.0-1.0 each). Default is red-orange. Green: R=0.1, G=0.8, B=0.1 Yellow: R=0.9, G=0.8, B=0.1";

        [JsonProperty("smokeHeight")]
        public float SmokeHeight { get; set; } = 50f;

        [JsonProperty("_comment_smokeHeight")]
        public string _c20c { get; set; } = "How high (meters) the smoke column rises";

        [JsonProperty("smokeSoundRadius")]
        public float SmokeSoundRadius { get; set; } = 50f;

        [JsonProperty("_comment_smokeSoundRadius")]
        public string _c20e { get; set; } = "Max distance (meters) at which the smoke hissing sound is audible";

        [JsonProperty("warningDelaySeconds")]
        public float WarningDelaySeconds { get; set; } = 15f;

        [JsonProperty("_comment_warningDelay")]
        public string _c20d { get; set; } = "Seconds to wait after smoke appears before the barrage starts";

        [JsonProperty("sirenEnabled")]
        public bool SirenEnabled { get; set; } = true;

        [JsonProperty("sirenClipName")]
        public string SirenClipName { get; set; } = "firework_launch_outdoor";

        [JsonProperty("_comment_sirenClipName")]
        public string _c21 { get; set; } = "Set debugMode=true to log all clip names. Good options: 'firework_launch_outdoor'";

        [JsonProperty("sirenDurationSeconds")]
        public float SirenDurationSeconds { get; set; } = 15f;

        [JsonProperty("_comment_sirenDuration")]
        public string _c22 { get; set; } = "How long the siren plays. Should match warningDelaySeconds";

        [JsonProperty("sirenLoop")]
        public bool SirenLoop { get; set; } = false;

        [JsonProperty("sirenVolume")]
        public float SirenVolume { get; set; } = 0.8f;

        [JsonProperty("blacklistedMaps")]
        public string[] BlacklistedMaps { get; set; } = new[] { "factory4_day", "factory4_night", "laboratory" };

        [JsonProperty("_comment_blacklistedMaps")]
        public string _c31 { get; set; } = "Map IDs: factory4_day, factory4_night, laboratory";

        [JsonProperty("debugMode")]
        public bool DebugMode { get; set; } = false;

        [JsonProperty("_comment_debugMode")]
        public string _c41 { get; set; } = "Shows debug panel + logs all AudioClip names so you can pick a siren sound";

        private static string ConfigDir => Path.Combine(BepInEx.Paths.PluginPath, "MortarStrikes");
        private static string ConfigPath => Path.Combine(ConfigDir, "config.json");
        private static ManualLogSource Log => Plugin.Log;

        public static MortarConfig Load()
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonConvert.DeserializeObject<MortarConfig>(File.ReadAllText(ConfigPath));
                    if (cfg != null) { cfg.Save(); return cfg; }
                }
            }
            catch (Exception ex) { Log.LogError($"[Mortar] Config error: {ex.Message}"); }
            var def = new MortarConfig();
            def.Save();
            return def;
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch { }
        }
    }
}
