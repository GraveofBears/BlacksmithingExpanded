using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using ItemDataManager;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BlacksmithingExpanded
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BlacksmithingExpanded : BaseUnityPlugin
    {
        internal const string ModName = "Blacksmithing Expanded";
        internal const string ModVersion = "1.1.7";
        internal const string ModGUID = "org.bepinex.plugins.blacksmithingexpanded";

        private Harmony harmony;

        private static readonly ConfigSync configSync = new(ModGUID)
        {
            DisplayName = ModName,
            CurrentVersion = ModVersion,
            MinimumRequiredVersion = ModVersion,
            ModRequired = true
        };

        internal static Skill blacksmithSkill;

        // IMPROVED: Thread-safe base stats cache with proper lazy initialization
        private static readonly Dictionary<string, ItemBaseStats> baseStatsCache = new();
        private static readonly object cacheLocker = new object();

        // Item filtering system
        private static ItemFilterConfig itemFilterConfig;
        private static readonly HashSet<string> whitelistedItems = new();
        private static readonly HashSet<string> blacklistedItems = new();
        private static string configPath;

        // YAML sync system (like Jewelcrafting)
        public static readonly CustomSyncedValue<List<string>> syncedWhitelistItems = new(configSync, "whitelist items", new List<string>());
        public static readonly CustomSyncedValue<List<string>> syncedBlacklistItems = new(configSync, "blacklist items", new List<string>());

        // Workstation infusions
        internal static Dictionary<ZDOID, WorkstationInfusion> smelterInfusions = new();
        internal static Dictionary<ZDOID, WorkstationInfusion> kilnInfusions = new();
        internal static Dictionary<ZDOID, WorkstationInfusion> blastFurnaceInfusions = new();
        private static readonly Dictionary<ZDOID, float> originalBlastFurnaceSpeeds = new();
        private static readonly Dictionary<ZDOID, float> originalSmelterSpeeds = new();
        private static readonly Dictionary<ZDOID, float> originalKilnSpeeds = new();
        private static readonly Dictionary<ZDOID, GameObject> activeGlowEffects = new();

        // IMPROVED: Spear data storage with proper cleanup and limits
        private static readonly Dictionary<string, BlacksmithingItemData> tempSpearDataStorage = new();
        private const int MAX_SPEAR_DATA_ENTRIES = 50; // Prevent memory leaks
        private const float SPEAR_DATA_CLEANUP_INTERVAL = 30f; // Cleanup every 30 seconds
        private static float lastSpearCleanup = 0f;

        // Config entries
        internal static ConfigEntry<float> cfg_SkillGainFactor;
        internal static ConfigEntry<float> cfg_SkillEffectFactor;
        internal static ConfigEntry<int> cfg_InfusionTierInterval;
        internal static ConfigEntry<float> cfg_ChanceExtraItemAt100;
        internal static ConfigEntry<float> cfg_SmelterSaveOreChanceAt100;
        internal static ConfigEntry<bool> cfg_EnableSmeltingSpeedBonus;
        internal static ConfigEntry<bool> cfg_EnableKilnSpeedBonus;
        internal static ConfigEntry<float> cfg_SmeltingSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_KilnSpeedBonusPerTier;
        internal static ConfigEntry<float> cfg_InfusionExpireTime;
        internal static ConfigEntry<bool> cfg_ShowInfusionVisualEffect;
        internal static ConfigEntry<bool> cfg_ShowBlacksmithLevelInTooltip;
        internal static ConfigEntry<bool> cfg_ShowInfusionInTooltip;
        internal static ConfigEntry<float> cfg_FirstCraftBonusXP;
        internal static ConfigEntry<float> UpgradeBonus;
        internal static ConfigEntry<bool> cfg_EnableStationLevelBonus;

        // YAML Configuration
        internal static ConfigEntry<bool> cfg_UseYamlFiltering;
        internal static ConfigEntry<bool> cfg_LogFilteredItems;

        // Durability
        internal static ConfigEntry<int> cfg_DurabilityTierInterval;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerTier;
        internal static ConfigEntry<float> cfg_DurabilityBonusPerUpgrade;
        internal static ConfigEntry<bool> cfg_RespectOriginalDurability;
        internal static ConfigEntry<float> cfg_MaxDurabilityCap;
        internal static ConfigEntry<bool> cfg_AllowNonRepairableItems;
        internal static ConfigEntry<bool> cfg_EnableDurabilityBonus;

        // Armor & Weapons
        internal static ConfigEntry<bool> cfg_EnableDamageBonus;
        internal static ConfigEntry<bool> cfg_EnableArmorBonus;
        internal static ConfigEntry<int> cfg_StatTierInterval;
        internal static ConfigEntry<float> cfg_ArmorBonusPerTier;
        internal static ConfigEntry<float> cfg_ArmorBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_ArmorCap;
        internal static ConfigEntry<int> cfg_DamageBonusPerTier;
        internal static ConfigEntry<float> cfg_StatBonusPerUpgrade;
        internal static ConfigEntry<bool> cfg_UsePercentageDamageBonus;
        internal static ConfigEntry<float> cfg_DamagePercentageBonusPerTier;
        internal static ConfigEntry<bool> cfg_UsePercentageUpgradeBonus;
        internal static ConfigEntry<float> cfg_StatPercentageBonusPerUpgrade;

        // Elemental
        internal static ConfigEntry<bool> cfg_EnableElementalBonus;
        internal static ConfigEntry<bool> cfg_AlwaysAddElementalAtMax;
        internal static ConfigEntry<int> cfg_ElementalUnlockLevel;
        internal static ConfigEntry<float> cfg_FireBonusPerTier;
        internal static ConfigEntry<float> cfg_FrostBonusPerTier;
        internal static ConfigEntry<float> cfg_LightningBonusPerTier;
        internal static ConfigEntry<float> cfg_PoisonBonusPerTier;
        internal static ConfigEntry<float> cfg_SpiritBonusPerTier;
        internal static ConfigEntry<bool> cfg_BoostElementalWeapons;
        internal static ConfigEntry<float> cfg_ElementalWeaponBoostChance;
        internal static ConfigEntry<bool> cfg_UsePercentageElementalBonus;
        internal static ConfigEntry<float> cfg_ElementalPercentageBonusPerTier;

        // Shields
        internal static ConfigEntry<bool> cfg_EnableShieldBonus;
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerTier;
        internal static ConfigEntry<float> cfg_TimedBlockBonusPerUpgrade;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerTier;
        internal static ConfigEntry<float> cfg_BlockPowerBonusPerUpgrade;

        // Bonus features
        internal static ConfigEntry<bool> cfg_EnableExtraItemBonus;
        internal static ConfigEntry<bool> cfg_EnableOreSaveBonus;

        // XP
        internal static ConfigEntry<float> cfg_XPPerCraft;
        internal static ConfigEntry<float> cfg_XPPerSmelt;
        internal static ConfigEntry<float> cfg_XPPerRepair;
        internal static ConfigEntry<float> cfg_XPPerUpgrade;

        private static Sprite s_skillIcon;

        private struct ItemBaseStats
        {
            public float armor;
            public HitData.DamageTypes damages;
            public float durability;
            public List<HitData.DamageModPair> resistances;
            public bool isCached;
        }

        public class WorkstationInfusion
        {
            public int tier;
            public float timestamp;
            public float originalSpeed;
            public float bonusSpeed;
            public bool wasActive;

            public bool IsExpired => Time.time - timestamp > cfg_InfusionExpireTime.Value;
            public float RemainingTime => Mathf.Max(0f, cfg_InfusionExpireTime.Value - (Time.time - timestamp));
        }

        private class ItemFilterConfig
        {
            public List<string> Whitelist { get; set; } = new List<string>();
            public List<string> Blacklist { get; set; } = new List<string>();
        }

        public static class ItemEligibilityCache
        {
            private static readonly Dictionary<string, bool> eligibilityCache = new();
            private static readonly Dictionary<string, bool> allowedCache = new();
            private const int MAX_CACHE_SIZE = 200;

            public static bool IsEligibleForBlacksmithingBonuses(ItemDrop.ItemData item)
            {
                if (item?.m_shared == null) return false;

                string key = item.m_shared.m_name;
                if (eligibilityCache.TryGetValue(key, out bool cached))
                    return cached;

                bool eligible = CalculateEligibility(item);

                if (eligibilityCache.Count >= MAX_CACHE_SIZE)
                {
                    var firstKey = eligibilityCache.Keys.First();
                    eligibilityCache.Remove(firstKey);
                }

                eligibilityCache[key] = eligible;
                return eligible;
            }

            public static bool IsItemAllowed(ItemDrop.ItemData item)
            {
                if (!cfg_UseYamlFiltering.Value) return true;
                if (item?.m_shared == null) return false;

                string key = item.m_shared.m_name;
                if (allowedCache.TryGetValue(key, out bool cached))
                    return cached;

                bool allowed = BlacksmithingExpanded.IsItemAllowed(item);

                if (allowedCache.Count >= MAX_CACHE_SIZE)
                {
                    var firstKey = allowedCache.Keys.First();
                    allowedCache.Remove(firstKey);
                }

                allowedCache[key] = allowed;
                return allowed;
            }

            private static bool CalculateEligibility(ItemDrop.ItemData item)
            {
                if (!(item.IsWeapon() ||
                      item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield ||
                      item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet ||
                      item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest ||
                      item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs ||
                      item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder))
                    return false;

                if (item.m_shared.m_maxStackSize > 1)
                    return false;

                if (!IsItemAllowed(item))
                    return false;

                return true;
            }

            public static void ClearCache()
            {
                eligibilityCache.Clear();
                allowedCache.Clear();
            }
        }

        private ConfigEntry<T> AddConfig<T>(string group, string name, T value, string description, bool sync = true)
        {
            var entry = Config.Bind(group, name, value, new ConfigDescription(description));
            var syncEntry = configSync.AddConfigEntry(entry);
            syncEntry.SynchronizedConfig = sync;
            return entry;
        }

        private void Awake()
        {
            harmony = new Harmony(ModGUID);
            configPath = Path.Combine(Path.GetDirectoryName(Config.ConfigFilePath), "BlacksmithExpItemList.yml");

            // Register skill FIRST before anything else
            try
            {
                s_skillIcon = LoadEmbeddedSprite("smithing.png", 64, 64);
                if (s_skillIcon == null)
                    throw new Exception("Failed to load embedded sprite: smithing.png");

                blacksmithSkill = new Skill("Blacksmithing", s_skillIcon);
                blacksmithSkill.Name.English("Blacksmithing");
                blacksmithSkill.Description.English("Craft better, last longer. Improves durability, damage, and armor of crafted items.");
                blacksmithSkill.Configurable = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Skill setup failed: {ex}");
            }

            SetupConfigs();

            // Apply skill configuration AFTER configs are set up
            if (blacksmithSkill != null)
            {
                blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
                cfg_SkillGainFactor.SettingChanged += (_, _) => blacksmithSkill.SkillGainFactor = cfg_SkillGainFactor.Value;
                cfg_SkillEffectFactor.SettingChanged += (_, _) => blacksmithSkill.SkillEffectFactor = cfg_SkillEffectFactor.Value;
            }

            InitializeYamlFiltering();

            // Set up sync callbacks
            syncedWhitelistItems.ValueChanged += () =>
            {
                whitelistedItems.Clear();
                foreach (var item in syncedWhitelistItems.Value)
                {
                    whitelistedItems.Add(item);
                }
                Logger.LogDebug($"[BlacksmithingExpanded] Received synced whitelist: {whitelistedItems.Count} items");
            };

            syncedBlacklistItems.ValueChanged += () =>
            {
                blacklistedItems.Clear();
                foreach (var item in syncedBlacklistItems.Value)
                {
                    blacklistedItems.Add(item);
                }
                Logger.LogDebug($"[BlacksmithingExpanded] Received synced blacklist: {blacklistedItems.Count} items");
            };

            cfg_UseYamlFiltering.SettingChanged += (_, _) =>
            {
                if (ZNet.instance == null || ZNet.instance.IsServer())
                {
                    ReloadYamlConfiguration();
                }
            };

            ItemInfo.ForceLoadTypes.Add(typeof(BlacksmithingItemData));

            StartCoroutine(SpearDataCleanupRoutine());
            StartCoroutine(PerformanceMaintenance());

            harmony.PatchAll();

            Logger.LogInfo($"[BlacksmithingExpanded] Initialized successfully. Skill registered: {blacksmithSkill != null}");
        }

        private void Update()
        {
            if (Time.time - lastSpearCleanup > SPEAR_DATA_CLEANUP_INTERVAL)
            {
                CleanupOldSpearData();
                lastSpearCleanup = Time.time;
            }
        }

        private IEnumerator SpearDataCleanupRoutine()
        {
            while (true)
            {
                yield return new WaitForSeconds(SPEAR_DATA_CLEANUP_INTERVAL);
                CleanupOldSpearData();
            }
        }

        private IEnumerator PerformanceMaintenance()
        {
            while (true)
            {
                yield return new WaitForSeconds(60f);

                ItemEligibilityCache.ClearCache();

                if (tempSpearDataStorage.Count > MAX_SPEAR_DATA_ENTRIES / 2)
                {
                    CleanupOldSpearData();
                }

                var connectedPlayerIds = new HashSet<long>();
                foreach (var player in Player.GetAllPlayers())
                {
                    connectedPlayerIds.Add(player.GetPlayerID());
                }

                var playersToRemove = Patch_UpgradeDetection.playerItemQualities.Keys
                    .Where(id => !connectedPlayerIds.Contains(id))
                    .ToList();

                foreach (var playerId in playersToRemove)
                {
                    Patch_UpgradeDetection.playerItemQualities.Remove(playerId);
                }
            }
        }

        private static void CacheBaseStats(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null) return;

            string key = item.m_shared.m_name;

            lock (cacheLocker)
            {
                if (!baseStatsCache.ContainsKey(key))
                {
                    baseStatsCache[key] = new ItemBaseStats
                    {
                        armor = item.m_shared.m_armor,
                        damages = item.m_shared.m_damages.Clone(),
                        durability = item.m_shared.m_maxDurability,
                        resistances = new List<HitData.DamageModPair>(item.m_shared.m_damageModifiers),
                        isCached = true
                    };
                }
            }
        }

        private static ItemBaseStats GetBaseStats(ItemDrop.ItemData item)
        {
            if (item?.m_shared == null)
            {
                return new ItemBaseStats { isCached = false };
            }

            string key = item.m_shared.m_name;

            lock (cacheLocker)
            {
                if (!baseStatsCache.ContainsKey(key))
                {
                    CacheBaseStats(item);
                }
                return baseStatsCache[key];
            }
        }

        private static void CleanupOldSpearData()
        {
            var currentTime = Time.time;
            var keysToRemove = new List<string>();

            foreach (var kvp in tempSpearDataStorage.ToList())
            {
                var keyParts = kvp.Key.Split('_');
                if (keyParts.Length >= 5 && float.TryParse(keyParts[4], out float timestamp))
                {
                    if (currentTime - timestamp > 120f)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }
                else
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            if (tempSpearDataStorage.Count > MAX_SPEAR_DATA_ENTRIES)
            {
                var oldestEntries = tempSpearDataStorage
                    .OrderBy(kvp => {
                        var keyParts = kvp.Key.Split('_');
                        return keyParts.Length >= 5 && float.TryParse(keyParts[4], out float timestamp) ? timestamp : 0f;
                    })
                    .Take(tempSpearDataStorage.Count - MAX_SPEAR_DATA_ENTRIES)
                    .Select(kvp => kvp.Key)
                    .ToList();

                keysToRemove.AddRange(oldestEntries);
            }

            foreach (var key in keysToRemove)
            {
                tempSpearDataStorage.Remove(key);
            }

            if (keysToRemove.Count > 0)
            {
                Debug.Log($"[BlacksmithingExpanded] Cleaned up {keysToRemove.Count} old spear data entries");
            }
        }

        private class BlacksmithingItemData : ItemData
        {
            public static readonly Dictionary<ItemDrop.ItemData.SharedData, BlacksmithingItemData> activeItems = new();

            [SerializeField] public int level = 0;
            [SerializeField] public int lastKnownQuality = 0;
            [SerializeField] public string infusion = "";
            [SerializeField] public float baseDurability = 0f;
            [SerializeField] public float maxDurability = 0f;
            [SerializeField] public float armorBonus = 0f;
            [SerializeField] public float damageBlunt = 0f;
            [SerializeField] public float damageSlash = 0f;
            [SerializeField] public float damagePierce = 0f;
            [SerializeField] public float damageFire = 0f;
            [SerializeField] public float damageFrost = 0f;
            [SerializeField] public float damageLightning = 0f;
            [SerializeField] public float damagePoison = 0f;
            [SerializeField] public float damageSpirit = 0f;
            [SerializeField] public float blockPowerBonus = 0f;
            [SerializeField] public float timedBlockBonus = 0f;
            [SerializeField] public bool statsApplied = false;

            ~BlacksmithingItemData() => activeItems.Remove(Item.m_shared);

            public override void Load()
            {
                base.Load();
                activeItems[Item.m_shared] = this;

                if (!IsCloned && level > 0)
                {
                    ApplyStoredStats();
                    statsApplied = true;
                }
            }

            private void ApplyStoredStats()
            {
                var baseStats = GetBaseStats(Item);
                if (!baseStats.isCached) return;

                var originalShared = Item.m_shared;
                Item.m_shared = (ItemDrop.ItemData.SharedData)originalShared.GetType()
                    .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(originalShared, null);

                if (cfg_EnableDurabilityBonus.Value && maxDurability > 0f)
                {
                    Item.m_shared.m_maxDurability = maxDurability;
                    Item.m_durability = Mathf.Min(Item.m_durability, maxDurability);
                }

                if (cfg_EnableArmorBonus.Value && armorBonus > 0f)
                {
                    Item.m_shared.m_armor = baseStats.armor + armorBonus;
                }

                Item.m_shared.m_damages.m_blunt = baseStats.damages.m_blunt + damageBlunt;
                Item.m_shared.m_damages.m_slash = baseStats.damages.m_slash + damageSlash;
                Item.m_shared.m_damages.m_pierce = baseStats.damages.m_pierce + damagePierce;
                Item.m_shared.m_damages.m_fire = baseStats.damages.m_fire + damageFire;
                Item.m_shared.m_damages.m_frost = baseStats.damages.m_frost + damageFrost;
                Item.m_shared.m_damages.m_lightning = baseStats.damages.m_lightning + damageLightning;
                Item.m_shared.m_damages.m_poison = baseStats.damages.m_poison + damagePoison;
                Item.m_shared.m_damages.m_spirit = baseStats.damages.m_spirit + damageSpirit;

                if (Item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    if (blockPowerBonus > 0f) Item.m_shared.m_blockPower += blockPowerBonus;
                    if (timedBlockBonus > 0f) Item.m_shared.m_timedBlockBonus += timedBlockBonus;
                }
            }

            public override void Unload()
            {
                activeItems.Remove(Item.m_shared);
            }

            protected override bool AllowStackingIdenticalValues { get; set; } = true;
        }

        private void SetupConfigs()
        {
            cfg_SkillGainFactor = AddConfig("General", "Skill gain factor", 1f, "Multiplier for blacksmithing XP gain rate (1.5 = 50% faster leveling)");
            cfg_SkillEffectFactor = AddConfig("General", "Skill effect factor", 1f, "Global multiplier for all blacksmithing bonuses (damage, armor, durability, etc). Higher = stronger effects");
            cfg_InfusionTierInterval = AddConfig("General", "Workstation infusion milestone interval", 10, "Every X blacksmithing levels unlocks a new tier of smelter/kiln speed bonus");
            cfg_EnableSmeltingSpeedBonus = AddConfig("General", "Enable smelting speed bonus", true, "Enable/disable smelter (and blast furnace) speed bonuses. Disable this if you have other mods that modify smelter speeds to prevent conflicts");
            cfg_EnableKilnSpeedBonus = AddConfig("General", "Enable kiln speed bonus", true, "Enable/disable kiln speed bonuses. Disable this if you have other mods that modify kiln speeds to prevent conflicts");
            cfg_SmeltingSpeedBonusPerTier = AddConfig("General", "Smelting speed bonus per tier", 0.15f, "Speed bonus per tier - 0.15 = 15% faster smelting. Stacks with each tier");
            cfg_KilnSpeedBonusPerTier = AddConfig("General", "Kiln speed bonus per tier", 0.15f, "Speed bonus per tier - 0.15 = 15% faster charcoal production. Stacks with each tier");
            cfg_InfusionExpireTime = AddConfig("General", "Infusion expire time", 300f, "Seconds that speed bonuses last after adding fuel/ore to smelters/kilns (300 = 5 minutes)");
            cfg_ShowInfusionVisualEffect = AddConfig("General", "Show infusion visual effect", true, "Show orange glowing light effect when smelters/kilns have speed bonuses active");
            cfg_SmelterSaveOreChanceAt100 = AddConfig("General", "Ore save chance at 100", 0.2f, "At level 100: chance to not consume ore when smelting (0.2 = 20% ore savings)");
            cfg_ChanceExtraItemAt100 = AddConfig("General", "Extra item chance at 100", 0.05f, "At level 100: chance to get bonus item when crafting (0.05 = 5% chance for double output)");
            cfg_UsePercentageUpgradeBonus = AddConfig("PercentageSystem", "Use percentage upgrade bonus", true, "If enabled, upgrade bonuses are percentage-based instead of flat. When disabled, uses flat bonuses");
            cfg_StatPercentageBonusPerUpgrade = AddConfig("PercentageSystem", "Stat percentage bonus per upgrade", 5f, "Percentage bonus per upgrade level when using percentage upgrade system (5 = 5% per upgrade level)");
            cfg_EnableStationLevelBonus = AddConfig("General", "Enable station level bonus", true, "Allow blacksmithing skill to add virtual levels to workbenches and forges");

            cfg_UseYamlFiltering = AddConfig("Item Filtering", "Use YAML item filtering", true, "Enable custom whitelist/blacklist system via BlacksmithExpItemList.yml file");
            cfg_LogFilteredItems = AddConfig("Item Filtering", "Log filtered items", false, "Write to console when items are blocked by whitelist/blacklist filters");

            cfg_XPPerCraft = AddConfig("XP", "XP per craft", 1f, "Base blacksmithing XP gained when crafting any item");
            cfg_XPPerSmelt = AddConfig("XP", "XP per smelt", 0.75f, "Base blacksmithing XP gained when adding ore to smelters/kilns");
            cfg_XPPerRepair = AddConfig("XP", "XP per repair", 0.1f, "Base blacksmithing XP gained when repairing items");
            cfg_XPPerUpgrade = AddConfig("XP", "XP per upgrade", 3f, "Base blacksmithing XP gained when upgrading items at workbenches");
            cfg_FirstCraftBonusXP = AddConfig("XP", "First craft bonus XP", 10f, "One-time bonus XP when crafting each item type for the first time");

            cfg_ShowBlacksmithLevelInTooltip = AddConfig("Tooltip", "Show level in tooltip", true, "Display blacksmithing level used to craft item in item tooltips");
            cfg_ShowInfusionInTooltip = AddConfig("Tooltip", "Show infusion in tooltip", false, "Display elemental infusion type in item tooltips (Fire, Frost, etc.)");

            cfg_DurabilityTierInterval = AddConfig("Durability", "Durability tier interval", 10, "Every X blacksmithing levels unlocks next tier of durability bonuses");
            cfg_DurabilityBonusPerTier = AddConfig("Durability", "Durability bonus per tier", 25f, "Flat durability points added per tier when crafting items");
            cfg_DurabilityBonusPerUpgrade = AddConfig("Durability", "Durability bonus per upgrade", 50f, "Extra durability points per item quality level (star rating)");
            cfg_RespectOriginalDurability = AddConfig("Durability", "Respect original durability", true, "Only boost durability on items that already have durability (prevents boosting consumables/arrows)");
            cfg_MaxDurabilityCap = AddConfig("Durability", "Max durability cap", 2000f, "Maximum durability any item can reach (0 = no limit)");
            cfg_AllowNonRepairableItems = AddConfig("Durability", "Allow non-repairable items", false, "Allow blacksmithing bonuses on items with no durability (torches, consumables, etc.)");
            cfg_EnableDurabilityBonus = AddConfig("Durability", "Enable durability bonus", true, "Enable/disable the blacksmithing durability bonus entirely. Disable this if other mods manage durability to prevent conflicts (also fixes modded armor losing durability on login)");

            cfg_EnableDamageBonus = AddConfig("Stats", "Enable damage bonus", true, "Enable/disable the blacksmithing damage bonus entirely. Disable if other mods handle weapon damage scaling to prevent conflicts");
            cfg_EnableArmorBonus = AddConfig("Stats", "Enable armor bonus", true, "Enable/disable the blacksmithing armor bonus entirely. Disable if other mods handle armor scaling to prevent conflicts");
            cfg_BoostElementalWeapons = AddConfig("Stats", "Boost elemental weapons", true, "Allow boosting weapons that already have elemental damage (like Frostner)");
            cfg_ElementalWeaponBoostChance = AddConfig("Stats", "Elemental weapon boost chance", 0.25f, "For weapons with both physical and elemental damage: chance to boost elemental instead of physical (0.25 = 25% chance)");

            cfg_StatTierInterval = AddConfig("Stats", "Stat tier interval", 5, "Every X blacksmithing levels unlocks next tier of damage/armor bonuses");
            cfg_ArmorBonusPerTier = AddConfig("Stats", "Armor bonus per tier", 0.5f, "Flat armor points added per tier when crafting armor pieces");
            cfg_ArmorBonusPerUpgrade = AddConfig("Stats", "Armor bonus per upgrade", 2f, "Extra armor points per item quality level (star rating)");
            cfg_ArmorCap = AddConfig("Stats", "Armor cap", 150f, "Maximum armor value any piece can reach (0 = no limit)");
            cfg_UsePercentageDamageBonus = AddConfig("PercentageSystem", "Use percentage damage bonus", true, "If enabled, damage bonuses are percentage-based instead of flat. Much more balanced for all weapon types");
            cfg_DamagePercentageBonusPerTier = AddConfig("PercentageSystem", "Damage percentage bonus per tier", 1f, "Percentage damage bonus per tier when using percentage system (1 = 1% per tier)");
            cfg_DamageBonusPerTier = AddConfig("Stats", "Damage bonus per tier", 5, "Flat damage bonus added per stat tier. Applied to one random damage type (slash/pierce/blunt). Only used if percentage system is disabled");
            cfg_StatBonusPerUpgrade = AddConfig("Stats", "Stat bonus per upgrade", 4f, "Extra damage/armor bonus per item quality level (star rating). Only used if percentage upgrade system is disabled");

            cfg_EnableElementalBonus = AddConfig("Elemental", "Enable elemental bonus", true, "Enable/disable the blacksmithing elemental damage bonus entirely. Disable if other mods handle elemental damage to prevent conflicts");
            cfg_AlwaysAddElementalAtMax = AddConfig("Elemental", "Add elemental at milestone", true, "Automatically add random elemental damage when reaching elemental unlock level");
            cfg_ElementalUnlockLevel = AddConfig("Elemental", "Elemental unlock level", 75, "Blacksmithing level required to add elemental damage bonuses to weapons");
            cfg_FireBonusPerTier = AddConfig("Elemental", "Fire bonus per tier", 3f, "Fire damage points per tier (causes burning damage over time)");
            cfg_FrostBonusPerTier = AddConfig("Elemental", "Frost bonus per tier", 6f, "Frost damage points per tier (causes instant cold damage)");
            cfg_LightningBonusPerTier = AddConfig("Elemental", "Lightning bonus per tier", 5f, "Lightning damage points per tier (good vs wet enemies)");
            cfg_PoisonBonusPerTier = AddConfig("Elemental", "Poison bonus per tier", 2.5f, "Poison damage points per tier (causes poison damage over time)");
            cfg_SpiritBonusPerTier = AddConfig("Elemental", "Spirit bonus per tier", 4f, "Spirit damage points per tier (extra effective vs undead enemies)");
            cfg_UsePercentageElementalBonus = AddConfig("PercentageSystem", "Use percentage elemental bonus", true, "If enabled, elemental bonuses are percentage-based instead of flat. Much more balanced for all weapon types");
            cfg_ElementalPercentageBonusPerTier = AddConfig("PercentageSystem", "Elemental percentage bonus per tier", 0.75f, "Percentage elemental bonus per tierwhen using percentage system (0.75 = 0.75% per tier)");

            cfg_EnableShieldBonus = AddConfig("Shields", "Enable shield bonus", true, "Enable/disable the blacksmithing shield block power and parry bonuses entirely. Disable if other mods handle shield stats to prevent conflicts");
            cfg_TimedBlockBonusPerTier = AddConfig("Shields", "Timed block bonus per tier", 0.01f, "Parry/perfect block bonus per tier (0.01 = 1% better parry window/damage)");
            cfg_TimedBlockBonusPerUpgrade = AddConfig("Shields", "Timed block bonus per upgrade", 0.05f, "Extra parry bonus per shield quality level (star rating)");
            cfg_BlockPowerBonusPerTier = AddConfig("Shields", "Block power bonus per tier", 1f, "Block strength points per tier (reduces stamina cost when blocking)");
            cfg_BlockPowerBonusPerUpgrade = AddConfig("Shields", "Block power bonus per upgrade", 1f, "Extra block power per shield quality level (star rating)");

            cfg_EnableExtraItemBonus = AddConfig("General", "Enable extra item bonus", true, "Enable/disable the chance to receive a bonus crafted item at high blacksmithing levels");
            cfg_EnableOreSaveBonus = AddConfig("General", "Enable ore save bonus", true, "Enable/disable the chance to save ore when smelting at high blacksmithing levels");
        }

        private void InitializeYamlFiltering()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    Logger.LogInfo($"[BlacksmithingExpanded] YAML not found at {configPath}. Generating default YAML...");
                    GenerateDefaultYaml();
                    Logger.LogInfo("[BlacksmithingExpanded] Default YAML created. Please restart the client.");
                    return;
                }

                ReloadYamlConfiguration();
                Logger.LogDebug($"[BlacksmithingExpanded] YAML filtering system initialized. File: {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlacksmithingExpanded] Failed to initialize YAML filtering: {ex}");
            }
        }

        private void ReloadYamlConfiguration()
        {
            whitelistedItems.Clear();
            blacklistedItems.Clear();

            if (!cfg_UseYamlFiltering.Value)
            {
                Logger.LogInfo("[BlacksmithingExpanded] YAML filtering disabled");
                return;
            }

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(PascalCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                string yamlContent = File.ReadAllText(configPath);
                itemFilterConfig = deserializer.Deserialize<ItemFilterConfig>(yamlContent) ?? new ItemFilterConfig();

                if (itemFilterConfig.Whitelist != null)
                {
                    foreach (var item in itemFilterConfig.Whitelist.Where(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        whitelistedItems.Add(item.Trim());
                    }
                }

                if (itemFilterConfig.Blacklist != null)
                {
                    foreach (var item in itemFilterConfig.Blacklist.Where(x => !string.IsNullOrWhiteSpace(x)))
                    {
                        blacklistedItems.Add(item.Trim());
                    }
                }

                Logger.LogInfo($"[BlacksmithingExpanded] Loaded YAML config - Whitelist: {whitelistedItems.Count} items, Blacklist: {blacklistedItems.Count} items");

                if (whitelistedItems.Count > 0)
                    Logger.LogDebug($"[BlacksmithingExpanded] Whitelisted items: {string.Join(", ", whitelistedItems)}");

                if (blacklistedItems.Count > 0)
                    Logger.LogDebug($"[BlacksmithingExpanded] Blacklisted items: {string.Join(", ", blacklistedItems)}");
            }
            catch (YamlDotNet.Core.YamlException yamlEx)
            {
                Logger.LogError($"[BlacksmithingExpanded] YAML parsing error: {yamlEx.Message}");
                Logger.LogError($"[BlacksmithingExpanded] Check your YAML syntax in: {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlacksmithingExpanded] Failed to load YAML config: {ex}");
            }
        }

        private void GenerateDefaultYaml()
        {
            try
            {
                var defaultYaml = @"# If whitelist is empty:
# Items not on blacklist → Enhanced (normal mod behavior)
# Items on blacklist → Not enhanced

# If whitelist has items:
# Items on whitelist → Enhanced
# Items NOT on whitelist → Not enhanced (regardless of blacklist)

Whitelist:

Blacklist:
  - Club
  - AxeStone
  - Torch
  - Tankard
  - TankardAnniversary
  - TrinketBronzeHealth
  - TrinketBronzeStamina
  - TrinketCarapaceEitr
  - TrinketBlackDamageHealth
  - TrinketFlametalEitr
  - TrinketChitinSwim
  - TrinketFlametalStaminaHealth
  - TrinketIronHealth
  - TrinketIronStamina
  - TrinketScaleStaminaDamage
  - TrinketSilverDamage
  - TrinketSilverResist
  - Demister
  - DvergrKey
  - SaddleAsksvin
  - SaddleLox
";
                File.WriteAllText(configPath, defaultYaml);
                Logger.LogDebug($"[BlacksmithingExpanded] Created default YAML file at {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"[BlacksmithingExpanded] Failed to create default YAML: {ex}");
            }
        }

        internal static bool IsItemAllowed(ItemDrop.ItemData item)
        {
            if (!cfg_UseYamlFiltering.Value) return true;
            if (item?.m_shared == null) return false;

            string prefabName = GetItemPrefabName(item);
            if (string.IsNullOrEmpty(prefabName))
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Could not determine prefab name for item - allowing by default");
                }
                return true;
            }

            if (whitelistedItems.Count > 0)
            {
                bool allowed = whitelistedItems.Contains(prefabName);
                if (cfg_LogFilteredItems.Value)
                {
                    if (allowed)
                    {
                        Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' found in whitelist - allowing bonuses");
                    }
                    else
                    {
                        Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' not in whitelist - filtering out");
                    }
                }
                return allowed;
            }

            if (blacklistedItems.Contains(prefabName))
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' is blacklisted - filtering out");
                }
                return false;
            }

            if (cfg_LogFilteredItems.Value)
            {
                Debug.Log($"[BlacksmithingExpanded] Item prefab '{prefabName}' allowed (not blacklisted)");
            }
            return true;
        }

        private static string GetItemPrefabName(ItemDrop.ItemData item)
        {
            try
            {
                if (item.m_dropPrefab != null)
                {
                    return item.m_dropPrefab.name;
                }

                var prefab = ObjectDB.instance?.GetItemPrefab(item.m_shared.m_name);
                if (prefab != null)
                {
                    return prefab.name;
                }

                string sharedName = item.m_shared.m_name;
                if (sharedName.StartsWith("$item_"))
                {
                    string cleanName = sharedName.Substring(6);
                    return char.ToUpper(cleanName[0]) + cleanName.Substring(1);
                }

                return sharedName;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Error getting prefab name: {ex}");
                return item.m_shared?.m_name ?? "Unknown";
            }
        }

        internal static int GetPlayerBlacksmithingLevel(Player player)
        {
            if (player?.GetComponent<Skills>() == null)
                return 0;

            try
            {
                // Check if the skill actually exists
                if (!Skill.skillByName.ContainsKey("Blacksmithing"))
                {
                    Debug.LogWarning("[BlacksmithingExpanded] Blacksmithing skill not found, returning 0");
                    return 0;
                }

                var skillType = Skill.fromName("Blacksmithing");
                float level = player.GetComponent<Skills>().GetSkillLevel(skillType);

                return Mathf.FloorToInt(level);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Error getting blacksmithing level: {ex}");
                return 0;
            }
        }

        internal static void GiveBlacksmithingXP(Player player, float amount)
        {
            if (player == null || amount <= 0f) return;
            try
            {
                float adjusted = amount * cfg_SkillGainFactor.Value;
                SkillManager.SkillExtensions.RaiseSkill(player, "Blacksmithing", adjusted);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] XP grant failed: {ex}");
            }
        }

        internal static void ApplyCraftingBonuses(ItemDrop.ItemData item, int level)
        {
            if (item?.m_shared == null || level <= 0) return;
            if (item.m_shared.m_maxStackSize > 1) return;

            if (!IsItemAllowed(item))
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Item '{item.m_shared.m_name}' filtered out by YAML configuration");
                }
                return;
            }

            var baseStats = GetBaseStats(item);
            if (!baseStats.isCached)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to get base stats for {item.m_shared.m_name}");
                return;
            }

            bool hasOriginalDurability = baseStats.durability > 0f;

            if (!hasOriginalDurability && !cfg_AllowNonRepairableItems.Value)
            {
                if (cfg_LogFilteredItems.Value)
                {
                    Debug.Log($"[BlacksmithingExpanded] Item '{item.m_shared.m_name}' has no durability and non-repairable items are disabled - skipping");
                }
                return;
            }

            var data = item.Data().GetOrCreate<BlacksmithingItemData>();
            string preservedInfusion = data.infusion ?? "";

            bool shouldRecalculate = false;

            if (data.level != level || data.level == 0)
            {
                shouldRecalculate = true;
            }
            else if (data.lastKnownQuality > 0 && data.lastKnownQuality != item.m_quality)
            {
                shouldRecalculate = true;
            }
            else if (data.lastKnownQuality == 0 && data.level > 0)
            {
                data.lastKnownQuality = item.m_quality;
                data.Save();
                return;
            }

            if (shouldRecalculate)
            {
                data.level = level;
                data.lastKnownQuality = item.m_quality;
                data.baseDurability = baseStats.durability;
                data.armorBonus = 0f;
                data.damageBlunt = 0f;
                data.damageSlash = 0f;
                data.damagePierce = 0f;
                data.damageFire = 0f;
                data.damageFrost = 0f;
                data.damageLightning = 0f;
                data.damagePoison = 0f;
                data.damageSpirit = 0f;
                data.blockPowerBonus = 0f;
                data.timedBlockBonus = 0f;
                data.statsApplied = false;

                int statTier = level / cfg_StatTierInterval.Value;
                int durabilityTier = level / cfg_DurabilityTierInterval.Value;

                if (cfg_EnableDurabilityBonus.Value && (hasOriginalDurability || cfg_AllowNonRepairableItems.Value))
                {
                    bool shouldApplyDurability = (cfg_RespectOriginalDurability.Value && hasOriginalDurability) ||
                                               !cfg_RespectOriginalDurability.Value ||
                                               cfg_AllowNonRepairableItems.Value;

                    if (shouldApplyDurability)
                    {
                        float durabilityBonus = (durabilityTier * cfg_DurabilityBonusPerTier.Value) +
                                                (item.m_quality * cfg_DurabilityBonusPerUpgrade.Value);

                        data.maxDurability = baseStats.durability + durabilityBonus;

                        if (cfg_MaxDurabilityCap.Value > 0f)
                            data.maxDurability = Mathf.Min(data.maxDurability, cfg_MaxDurabilityCap.Value);
                    }
                }

                if (cfg_EnableDamageBonus.Value)
                {
                    ApplyDamageBonuses(item, baseStats, statTier, data);
                }

                if (cfg_EnableArmorBonus.Value && baseStats.armor > 0f)
                {
                    float armorTierBonus = statTier * cfg_ArmorBonusPerTier.Value;
                    float armorUpgradeBonus;

                    if (cfg_UsePercentageUpgradeBonus.Value)
                    {
                        float upgradePercentage = item.m_quality * cfg_StatPercentageBonusPerUpgrade.Value / 100f;
                        armorUpgradeBonus = baseStats.armor * upgradePercentage;
                    }
                    else
                    {
                        armorUpgradeBonus = item.m_quality * cfg_ArmorBonusPerUpgrade.Value;
                    }

                    data.armorBonus = armorTierBonus + armorUpgradeBonus;
                    if (cfg_ArmorCap.Value > 0f && baseStats.armor + data.armorBonus > cfg_ArmorCap.Value)
                        data.armorBonus = cfg_ArmorCap.Value - baseStats.armor;
                }

                if (cfg_EnableShieldBonus.Value && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                {
                    if (item.m_shared.m_blockPower > 0f)
                    {
                        data.blockPowerBonus = statTier * cfg_BlockPowerBonusPerTier.Value +
                                              item.m_quality * cfg_BlockPowerBonusPerUpgrade.Value;
                    }

                    if (item.m_shared.m_timedBlockBonus > 0f)
                    {
                        data.timedBlockBonus = statTier * cfg_TimedBlockBonusPerTier.Value +
                                              item.m_quality * cfg_TimedBlockBonusPerUpgrade.Value;
                    }
                }

                if (cfg_EnableElementalBonus.Value && level >= cfg_ElementalUnlockLevel.Value && cfg_AlwaysAddElementalAtMax.Value && item.IsWeapon())
                {
                    if (!string.IsNullOrEmpty(preservedInfusion))
                    {
                        float elementalEffectiveTier = CalculateElementalEffectiveTier(statTier, item.m_quality);
                        ApplySpecificInfusion(preservedInfusion, baseStats, elementalEffectiveTier, data);
                    }
                    else
                    {
                        bool appliedElementalBonus = HasElementalDamageBonus(data);
                        if (!appliedElementalBonus)
                        {
                            float elementalEffectiveTier = CalculateElementalEffectiveTier(statTier, item.m_quality);
                            ApplyElementalInfusion(item, baseStats, elementalEffectiveTier, data);
                        }
                    }
                }

                data.Save();
                data.Load();

                Debug.Log($"[BlacksmithingExpanded] Applied bonuses to {item.m_shared.m_name} (level {level}, quality {item.m_quality})");
            }
        }

        private static void ApplyDamageBonuses(ItemDrop.ItemData item, ItemBaseStats baseStats, int statTier, BlacksmithingItemData data)
        {
            float tierBonus;
            if (cfg_UsePercentageDamageBonus.Value)
            {
                tierBonus = statTier * cfg_DamagePercentageBonusPerTier.Value / 100f;
            }
            else
            {
                tierBonus = statTier * cfg_DamageBonusPerTier.Value;
            }

            float upgradeBonus = 0f;
            if (item.m_quality > 0)
            {
                if (cfg_UsePercentageUpgradeBonus.Value)
                {
                    upgradeBonus = item.m_quality * cfg_StatPercentageBonusPerUpgrade.Value / 100f;
                }
                else
                {
                    if (cfg_UsePercentageDamageBonus.Value)
                    {
                        float totalBaseDamage = GetTotalPhysicalDamage(baseStats);
                        if (totalBaseDamage > 0f)
                        {
                            upgradeBonus = (item.m_quality * cfg_StatBonusPerUpgrade.Value) / totalBaseDamage;
                        }
                    }
                    else
                    {
                        upgradeBonus = item.m_quality * cfg_StatBonusPerUpgrade.Value;
                    }
                }
            }

            ApplyRandomDamageBonus(item, baseStats, tierBonus, upgradeBonus, data);
        }

        private static float CalculateElementalEffectiveTier(int statTier, int quality)
        {
            float tierComponent = statTier;

            if (cfg_UsePercentageUpgradeBonus.Value)
            {
                tierComponent += quality * cfg_StatPercentageBonusPerUpgrade.Value / cfg_ElementalPercentageBonusPerTier.Value;
            }
            else
            {
                tierComponent += quality * cfg_StatBonusPerUpgrade.Value / cfg_ElementalPercentageBonusPerTier.Value;
            }

            return tierComponent;
        }

        private static bool HasElementalDamageBonus(BlacksmithingItemData data)
        {
            return data.damageFire > 0f || data.damageFrost > 0f || data.damageLightning > 0f ||
                   data.damagePoison > 0f || data.damageSpirit > 0f;
        }

        private static void ApplySpecificInfusion(string infusionType, ItemBaseStats baseStats, float effectiveTiers, BlacksmithingItemData data)
        {
            if (cfg_UsePercentageElementalBonus.Value)
            {
                float percentageBonus = effectiveTiers * cfg_ElementalPercentageBonusPerTier.Value / 100f;
                float totalBaseDamage = GetTotalPhysicalDamage(baseStats);

                switch (infusionType)
                {
                    case "Fire":
                        data.damageFire = totalBaseDamage * percentageBonus;
                        break;
                    case "Frost":
                        data.damageFrost = totalBaseDamage * percentageBonus;
                        break;
                    case "Lightning":
                        data.damageLightning = totalBaseDamage * percentageBonus;
                        break;
                    case "Poison":
                        data.damagePoison = totalBaseDamage * percentageBonus;
                        break;
                    case "Spirit":
                        data.damageSpirit = totalBaseDamage * percentageBonus;
                        break;
                }
            }
            else
            {
                switch (infusionType)
                {
                    case "Fire":
                        data.damageFire = effectiveTiers * cfg_FireBonusPerTier.Value;
                        break;
                    case "Frost":
                        data.damageFrost = effectiveTiers * cfg_FrostBonusPerTier.Value;
                        break;
                    case "Lightning":
                        data.damageLightning = effectiveTiers * cfg_LightningBonusPerTier.Value;
                        break;
                    case "Poison":
                        data.damagePoison = effectiveTiers * cfg_PoisonBonusPerTier.Value;
                        break;
                    case "Spirit":
                        data.damageSpirit = effectiveTiers * cfg_SpiritBonusPerTier.Value;
                        break;
                }
            }

            data.infusion = infusionType;
        }

        private static float GetTotalPhysicalDamage(ItemBaseStats baseStats)
        {
            return baseStats.damages.m_blunt + baseStats.damages.m_slash + baseStats.damages.m_pierce;
        }

        private static void ApplyRandomDamageBonus(ItemDrop.ItemData item, ItemBaseStats baseStats, float tierBonus, float upgradeBonus, BlacksmithingItemData data)
        {
            var validTypes = new List<System.Action>();

            if (cfg_UsePercentageDamageBonus.Value)
            {
                float totalPercentageBonus = tierBonus + upgradeBonus;
                float totalBaseDamage = GetTotalPhysicalDamage(baseStats);

                if (baseStats.damages.m_blunt > 0f)
                    validTypes.Add(() => data.damageBlunt = totalBaseDamage * totalPercentageBonus);
                if (baseStats.damages.m_slash > 0f)
                    validTypes.Add(() => data.damageSlash = totalBaseDamage * totalPercentageBonus);
                if (baseStats.damages.m_pierce > 0f)
                    validTypes.Add(() => data.damagePierce = totalBaseDamage * totalPercentageBonus);

                if (cfg_BoostElementalWeapons.Value)
                {
                    if (baseStats.damages.m_fire > 0f)
                        validTypes.Add(() => data.damageFire = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_frost > 0f)
                        validTypes.Add(() => data.damageFrost = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_lightning > 0f)
                        validTypes.Add(() => data.damageLightning = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_poison > 0f)
                        validTypes.Add(() => data.damagePoison = totalBaseDamage * totalPercentageBonus);
                    if (baseStats.damages.m_spirit > 0f)
                        validTypes.Add(() => data.damageSpirit = totalBaseDamage * totalPercentageBonus);
                }
            }
            else
            {
                float totalFlatBonus = tierBonus + upgradeBonus;

                if (baseStats.damages.m_blunt > 0f) validTypes.Add(() => data.damageBlunt = totalFlatBonus);
                if (baseStats.damages.m_slash > 0f) validTypes.Add(() => data.damageSlash = totalFlatBonus);
                if (baseStats.damages.m_pierce > 0f) validTypes.Add(() => data.damagePierce = totalFlatBonus);

                if (cfg_BoostElementalWeapons.Value)
                {
                    if (baseStats.damages.m_fire > 0f) validTypes.Add(() => data.damageFire = totalFlatBonus);
                    if (baseStats.damages.m_frost > 0f) validTypes.Add(() => data.damageFrost = totalFlatBonus);
                    if (baseStats.damages.m_lightning > 0f) validTypes.Add(() => data.damageLightning = totalFlatBonus);
                    if (baseStats.damages.m_poison > 0f) validTypes.Add(() => data.damagePoison = totalFlatBonus);
                    if (baseStats.damages.m_spirit > 0f) validTypes.Add(() => data.damageSpirit = totalFlatBonus);
                }
            }

            if (validTypes.Count > 0)
            {
                validTypes[UnityEngine.Random.Range(0, validTypes.Count)]();
            }
        }

        private static void ApplyElementalInfusion(ItemDrop.ItemData item, ItemBaseStats baseStats, float effectiveTiers, BlacksmithingItemData data)
        {
            var elements = new List<(string name, System.Action apply)>();

            if (cfg_UsePercentageElementalBonus.Value)
            {
                float percentageBonus = effectiveTiers * cfg_ElementalPercentageBonusPerTier.Value / 100f;
                float totalBaseDamage = GetTotalPhysicalDamage(baseStats);

                if (baseStats.damages.m_fire <= 0f)
                {
                    elements.Add(("Fire", () => {
                        data.damageFire = totalBaseDamage * percentageBonus;
                        data.infusion = "Fire";
                    }
                    ));
                }

                if (baseStats.damages.m_frost <= 0f)
                {
                    elements.Add(("Frost", () => {
                        data.damageFrost = totalBaseDamage * percentageBonus;
                        data.infusion = "Frost";
                    }
                    ));
                }

                if (baseStats.damages.m_lightning <= 0f)
                {
                    elements.Add(("Lightning", () => {
                        data.damageLightning = totalBaseDamage * percentageBonus;
                        data.infusion = "Lightning";
                    }
                    ));
                }

                if (baseStats.damages.m_poison <= 0f)
                {
                    elements.Add(("Poison", () => {
                        data.damagePoison = totalBaseDamage * percentageBonus;
                        data.infusion = "Poison";
                    }
                    ));
                }

                if (baseStats.damages.m_spirit <= 0f)
                {
                    elements.Add(("Spirit", () => {
                        data.damageSpirit = totalBaseDamage * percentageBonus;
                        data.infusion = "Spirit";
                    }
                    ));
                }
            }
            else
            {
                if (baseStats.damages.m_fire <= 0f)
                {
                    elements.Add(("Fire", () => {
                        data.damageFire = effectiveTiers * cfg_FireBonusPerTier.Value;
                        data.infusion = "Fire";
                    }
                    ));
                }

                if (baseStats.damages.m_frost <= 0f)
                {
                    elements.Add(("Frost", () => {
                        data.damageFrost = effectiveTiers * cfg_FrostBonusPerTier.Value;
                        data.infusion = "Frost";
                    }
                    ));
                }

                if (baseStats.damages.m_lightning <= 0f)
                {
                    elements.Add(("Lightning", () => {
                        data.damageLightning = effectiveTiers * cfg_LightningBonusPerTier.Value;
                        data.infusion = "Lightning";
                    }
                    ));
                }

                if (baseStats.damages.m_poison <= 0f)
                {
                    elements.Add(("Poison", () => {
                        data.damagePoison = effectiveTiers * cfg_PoisonBonusPerTier.Value;
                        data.infusion = "Poison";
                    }
                    ));
                }

                if (baseStats.damages.m_spirit <= 0f)
                {
                    elements.Add(("Spirit", () => {
                        data.damageSpirit = effectiveTiers * cfg_SpiritBonusPerTier.Value;
                        data.infusion = "Spirit";
                    }
                    ));
                }
            }

            if (elements.Count > 0)
            {
                var selected = elements[UnityEngine.Random.Range(0, elements.Count)];
                selected.apply();
            }
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.DoCrafting))]
        public static class Patch_Crafting
        {
            static void Prefix(InventoryGui __instance, out Recipe __state)
            {
                __state = __instance.m_craftRecipe;
            }

            static void Postfix(InventoryGui __instance, Recipe __state)
            {
                var player = Player.m_localPlayer;
                if (player?.GetInventory() == null || __state?.m_item == null) return;

                int level = GetPlayerBlacksmithingLevel(player);
                var craftedItem = FindMostRecentCraftedItem(player, __state);
                if (craftedItem != null)
                {
                    ApplyCraftingBonuses(craftedItem, Math.Max(level, 1));

                    var data = craftedItem.Data().Get<BlacksmithingItemData>();
                    if (data?.maxDurability > 0f)
                    {
                        craftedItem.m_durability = data.maxDurability;
                    }

                    float xpEarned = HandleCraftingXP(player, craftedItem);

                    if (xpEarned >= 1.0f)
                    {
                        Debug.Log($"[BlacksmithingExpanded] Crafted {craftedItem.m_shared.m_name}: level={level}, quality={craftedItem.m_quality}, xp={xpEarned:F2}");
                    }

                    if (cfg_EnableExtraItemBonus.Value && ItemEligibilityCache.IsEligibleForBlacksmithingBonuses(craftedItem))
                    {
                        float extraChance = cfg_ChanceExtraItemAt100.Value * (level / 100f);
                        if (UnityEngine.Random.value <= extraChance)
                        {
                            var extraItem = craftedItem.Clone();
                            player.GetInventory().AddItem(extraItem);
                            player.Message(MessageHud.MessageType.TopLeft, "Masterwork crafting created an extra item!");
                        }
                    }
                }
            }

            private static ItemDrop.ItemData FindMostRecentCraftedItem(Player player, Recipe recipe)
            {
                var inventory = player.GetInventory();
                if (inventory == null || recipe?.m_item == null) return null;

                var templateItem = recipe.m_item.GetComponent<ItemDrop>()?.m_itemData;
                if (templateItem?.m_shared == null) return null;

                var matchingItems = inventory.GetAllItems()
                    .Where(item => item.m_shared.m_name == templateItem.m_shared.m_name)
                    .Where(item => item.m_quality == templateItem.m_quality)
                    .OrderByDescending(item => item.m_durability)
                    .ToList();

                foreach (var item in matchingItems)
                {
                    var data = item.Data().Get<BlacksmithingItemData>();
                    if (data == null || data.level == 0)
                    {
                        return item;
                    }
                }

                return null;
            }
        }

        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.GetLevel))]
        public static class Patch_CraftingStationLevel
        {
            static void Postfix(CraftingStation __instance, ref int __result)
            {
                // Check if feature is enabled
                if (!cfg_EnableStationLevelBonus.Value) return;

                if (!__instance.m_craftRequireRoof && __instance.m_rangeBuild > 0f)
                {
                    var player = Player.m_localPlayer;
                    if (player == null) return;

                    int blacksmithLevel = GetPlayerBlacksmithingLevel(player);
                    int bonusLevels = CalculateWorkbenchBonus(__instance.m_name, blacksmithLevel);

                    if (bonusLevels > 0)
                    {
                        __result += bonusLevels;
                    }
                }
            }

            private static int CalculateWorkbenchBonus(string stationName, int level)
            {
                // Use exact matching instead of partial matching
                switch (stationName)
                {
                    // Workbenches
                    case "piece_workbench":
                        if (level >= 20) return 2;
                        if (level >= 10) return 1;
                        return 0;

                    // Forges (not black forges)
                    case "forge":
                        if (level >= 40) return 2;
                        if (level >= 30) return 1;
                        return 0;

                    // Black forges and Galdr tables
                    case "blackforge":
                    case "piece_galdrtable":
                        if (level >= 60) return 2;
                        if (level >= 50) return 1;
                        return 0;

                    default:
                        return 0;
                }
            }
        }

        private static float HandleCraftingXP(Player player, ItemDrop.ItemData item)
        {
            if (!ItemEligibilityCache.IsEligibleForBlacksmithingBonuses(item))
                return 0f;

            float totalXP = 0f;
            string itemHash = item.m_shared.m_name.GetStableHashCode().ToString();
            string craftKey = $"crafted_{itemHash}";

            if (!player.m_customData.ContainsKey(craftKey))
            {
                player.m_customData[craftKey] = "1";
                float firstXP = cfg_FirstCraftBonusXP.Value;
                GiveBlacksmithingXP(player, firstXP);
                totalXP += firstXP;
            }

            float regularXP = cfg_XPPerCraft.Value;
            GiveBlacksmithingXP(player, regularXP);
            totalXP += regularXP;

            return totalXP;
        }

        [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
        public static class Patch_UpgradeDetection_Alternative
        {
            static void Postfix(InventoryGui __instance)
            {
                var player = Player.m_localPlayer;
                if (player?.GetInventory() == null) return;

                foreach (var item in player.GetInventory().GetAllItems())
                {
                    if (!ItemEligibilityCache.IsEligibleForBlacksmithingBonuses(item)) continue;

                    var data = item.Data().Get<BlacksmithingItemData>();
                    if (data != null && data.lastKnownQuality < item.m_quality)
                    {
                        int level = GetPlayerBlacksmithingLevel(player);
                        GiveBlacksmithingXP(player, cfg_XPPerUpgrade.Value);
                        ApplyCraftingBonuses(item, level);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int))]
        public static class Patch_Tooltip
        {
            public static void Postfix(ItemDrop.ItemData item, bool crafting, ref string __result)
            {
                if (item == null) return;

                var data = item.Data().Get<BlacksmithingItemData>();
                if (data?.level > 0)
                {
                    if (cfg_ShowBlacksmithLevelInTooltip.Value)
                    {
                        __result += $"\n<color=orange>Forged at Blacksmithing {data.level}</color>";
                    }

                    if (cfg_ShowInfusionInTooltip.Value && !string.IsNullOrEmpty(data.infusion))
                    {
                        __result += $"\n<color=#87CEEB>Elemental Infusion: {data.infusion}</color>";
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
        public static class Patch_UpgradeDetection
        {
            public static readonly Dictionary<long, Dictionary<string, int>> playerItemQualities = new();
            private static float lastUpdateTime = 0f;
            private const float UPDATE_INTERVAL = 2f;

            static void Postfix(Player __instance)
            {
                if (Time.time - lastUpdateTime < UPDATE_INTERVAL) return;
                lastUpdateTime = Time.time;

                CheckForUpgrades(__instance);
            }

            private static void CheckForUpgrades(Player player)
            {
                var inventory = player.GetInventory();
                if (inventory == null) return;

                long playerId = player.GetPlayerID();

                if (!playerItemQualities.TryGetValue(playerId, out var lastKnownQualities))
                {
                    lastKnownQualities = new Dictionary<string, int>();
                    playerItemQualities[playerId] = lastKnownQualities;
                }

                var currentItems = new HashSet<string>();

                foreach (var item in inventory.GetAllItems())
                {
                    if (!ItemEligibilityCache.IsEligibleForBlacksmithingBonuses(item)) continue;

                    string itemKey = $"{item.m_shared.m_name}_{item.GetHashCode()}";
                    currentItems.Add(itemKey);

                    if (lastKnownQualities.TryGetValue(itemKey, out int lastQuality))
                    {
                        if (item.m_quality > lastQuality)
                        {
                            Debug.Log($"[BlacksmithingExpanded] UPGRADE DETECTED: {item.m_shared.m_name} from quality {lastQuality} to {item.m_quality}");

                            GiveBlacksmithingXP(player, cfg_XPPerUpgrade.Value);

                            int level = GetPlayerBlacksmithingLevel(player);
                            Debug.Log($"[BlacksmithingExpanded] Applying upgrade bonuses at level {level}");

                            ApplyCraftingBonuses(item, level);

                            lastKnownQualities[itemKey] = item.m_quality;

                            Debug.Log($"[BlacksmithingExpanded] Upgrade processing complete for {item.m_shared.m_name}");
                        }
                    }
                    else
                    {
                        lastKnownQualities[itemKey] = item.m_quality;
                    }
                }

                if (Time.time % (UPDATE_INTERVAL * 10f) < UPDATE_INTERVAL)
                {
                    var keysToRemove = lastKnownQualities.Keys.Where(key => !currentItems.Contains(key)).ToList();
                    foreach (var key in keysToRemove)
                    {
                        lastKnownQualities.Remove(key);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Smelter), "OnAddOre")]
        public static class Patch_Smelter_AddOre
        {
            static void Postfix(Smelter __instance, Humanoid user, bool __result)
            {
                try
                {
                    if (!__result || !(user is Player player)) return;

                    GiveBlacksmithingXP(player, cfg_XPPerSmelt.Value);

                    var zdo = __instance.m_nview?.GetZDO();
                    if (zdo == null) return;

                    bool isKiln = __instance.m_name.Contains("charcoal_kiln");
                    bool isBlastFurnace = __instance.m_name.Contains("blastfurnace");

                    // Check if speed bonus is enabled for this workstation type
                    if (isKiln && !cfg_EnableKilnSpeedBonus.Value) return;
                    if (!isKiln && !cfg_EnableSmeltingSpeedBonus.Value) return;

                    int level = GetPlayerBlacksmithingLevel(player);

                    // Safety check: prevent division by zero if tier interval is 0
                    if (cfg_InfusionTierInterval.Value <= 0)
                    {
                        Debug.LogWarning($"[BlacksmithingExpanded] Invalid tier interval ({cfg_InfusionTierInterval.Value}). " +
                            $"Must be greater than 0. Speed bonuses will not be applied.");
                        return;
                    }

                    int tier = level / cfg_InfusionTierInterval.Value;

                    if (tier <= 0) return;

                    float speedBonusPerTier = isKiln ? cfg_KilnSpeedBonusPerTier.Value : cfg_SmeltingSpeedBonusPerTier.Value;
                    float speedMultiplier = 1f + (tier * speedBonusPerTier);

                    // Safety check: prevent division by zero and invalid multipliers
                    if (speedMultiplier <= 0f)
                    {
                        string workstationType = isKiln ? "kiln" : (isBlastFurnace ? "blast furnace" : "smelter");
                        Debug.LogWarning($"[BlacksmithingExpanded] Invalid speed multiplier ({speedMultiplier}) for {workstationType}. " +
                            $"This may be caused by conflicting mods modifying {workstationType} speeds. " +
                            $"Consider disabling 'Enable {(isKiln ? "kiln" : "smelting")} speed bonus' in the config.");
                        return;
                    }

                    // Additional safety: check if original speed is valid
                    if (__instance.m_secPerProduct <= 0f)
                    {
                        string workstationType = isKiln ? "kiln" : (isBlastFurnace ? "blast furnace" : "smelter");
                        Debug.LogWarning($"[BlacksmithingExpanded] Invalid original {workstationType} speed ({__instance.m_secPerProduct}). " +
                            $"This may indicate a conflict with another mod. " +
                            $"Consider disabling 'Enable {(isKiln ? "kiln" : "smelting")} speed bonus' in the config.");
                        return;
                    }

                    var infusion = new WorkstationInfusion
                    {
                        tier = tier,
                        timestamp = Time.time,
                        originalSpeed = __instance.m_secPerProduct,
                        bonusSpeed = __instance.m_secPerProduct / speedMultiplier
                    };

                    bool isNewInfusion = false;

                    if (isKiln)
                    {
                        if (kilnInfusions.ContainsKey(zdo.m_uid))
                        {
                            kilnInfusions[zdo.m_uid].timestamp = Time.time;
                            kilnInfusions[zdo.m_uid].tier = tier;
                        }
                        else
                        {
                            kilnInfusions[zdo.m_uid] = infusion;
                            isNewInfusion = true;
                        }
                    }
                    else if (isBlastFurnace)
                    {
                        blastFurnaceInfusions[zdo.m_uid] = infusion;
                        isNewInfusion = true;
                    }
                    else
                    {
                        smelterInfusions[zdo.m_uid] = infusion;
                        isNewInfusion = true;
                    }

                    if (cfg_EnableOreSaveBonus.Value && !isKiln)
                    {
                        float saveChance = cfg_SmelterSaveOreChanceAt100.Value * (level / 100f);
                        if (UnityEngine.Random.value <= saveChance)
                        {
                            if (__instance.GetFuel() < __instance.m_maxFuel)
                            {
                                __instance.m_nview.GetZDO().Set("fuel", __instance.GetFuel() + 1f);
                            }
                        }
                    }

                    __instance.m_secPerProduct = infusion.bonusSpeed;

                    if (isNewInfusion)
                    {
                        ManageInfusionGlow(__instance.transform, zdo.m_uid, true);
                    }
                }
                catch (DivideByZeroException ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Division by zero in smelting speed bonus calculation. " +
                        $"Please check your config values (Tier Interval, Speed Bonus Per Tier). " +
                        $"Consider disabling speed bonuses if the issue persists. Error: {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[BlacksmithingExpanded] Unexpected error in Patch_Smelter_AddOre: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(Smelter), "UpdateSmelter")]
        public static class Patch_Smelter_Update
        {
            static void Prefix(Smelter __instance)
            {
                var zdo = __instance.m_nview?.GetZDO();
                if (zdo == null) return;

                bool isKiln = __instance.m_name.Contains("charcoal_kiln");
                bool isBlastFurnace = __instance.m_name.Contains("blastfurnace");

                // Check if speed bonus is enabled for this workstation type
                if (isKiln && !cfg_EnableKilnSpeedBonus.Value) return;
                if (!isKiln && !cfg_EnableSmeltingSpeedBonus.Value) return;

                if (isKiln)
                {
                    HandleKilnInfusion(__instance, zdo);
                }
                else if (isBlastFurnace)
                {
                    HandleBlastFurnaceInfusion(__instance, zdo);
                }
                else
                {
                    HandleSmelterInfusion(__instance, zdo);
                }
            }

            private static void HandleBlastFurnaceInfusion(Smelter blastFurnace, ZDO zdo)
            {
                if (!originalBlastFurnaceSpeeds.ContainsKey(zdo.m_uid))
                {
                    originalBlastFurnaceSpeeds[zdo.m_uid] = blastFurnace.m_secPerProduct;
                }

                if (blastFurnaceInfusions.TryGetValue(zdo.m_uid, out var infusion))
                {
                    float timeSinceInfusion = Time.time - infusion.timestamp;
                    bool gracePeriodActive = timeSinceInfusion < 1f;

                    bool shouldExpire = !gracePeriodActive && (
                        infusion.IsExpired ||
                        blastFurnace.GetQueueSize() == 0 ||
                        blastFurnace.GetFuel() <= 0f);

                    if (shouldExpire)
                    {
                        blastFurnace.m_secPerProduct = originalBlastFurnaceSpeeds[zdo.m_uid];
                        blastFurnaceInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(blastFurnace.transform, zdo.m_uid, false);
                    }
                    else
                    {
                        // Safety check: ensure bonusSpeed is valid
                        if (infusion.bonusSpeed > 0f)
                        {
                            blastFurnace.m_secPerProduct = infusion.bonusSpeed;
                            infusion.wasActive = true;
                        }
                        else
                        {
                            Debug.LogWarning($"[BlacksmithingExpanded] Invalid bonus speed detected for blast furnace. Reverting to original speed.");
                            blastFurnace.m_secPerProduct = originalBlastFurnaceSpeeds[zdo.m_uid];
                            blastFurnaceInfusions.Remove(zdo.m_uid);
                        }
                    }
                }
                else
                {
                    blastFurnace.m_secPerProduct = originalBlastFurnaceSpeeds[zdo.m_uid];
                }
            }

            private static void HandleKilnInfusion(Smelter kiln, ZDO zdo)
            {
                if (!originalKilnSpeeds.ContainsKey(zdo.m_uid))
                {
                    originalKilnSpeeds[zdo.m_uid] = kiln.m_secPerProduct;
                }

                if (kilnInfusions.TryGetValue(zdo.m_uid, out var infusion))
                {
                    bool kilnStillRunning = kiln.GetFuel() > 0f || kiln.GetBakeTimer() > 0f;

                    if (infusion.IsExpired || !kilnStillRunning)
                    {
                        kiln.m_secPerProduct = originalKilnSpeeds[zdo.m_uid];
                        kilnInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(kiln.transform, zdo.m_uid, false);
                    }
                    else
                    {
                        // Safety check: ensure bonusSpeed is valid
                        if (infusion.bonusSpeed > 0f)
                        {
                            kiln.m_secPerProduct = infusion.bonusSpeed;
                        }
                        else
                        {
                            Debug.LogWarning($"[BlacksmithingExpanded] Invalid bonus speed detected for kiln. Reverting to original speed.");
                            kiln.m_secPerProduct = originalKilnSpeeds[zdo.m_uid];
                            kilnInfusions.Remove(zdo.m_uid);
                        }
                    }
                }
                else
                {
                    kiln.m_secPerProduct = originalKilnSpeeds[zdo.m_uid];
                }
            }

            private static void HandleSmelterInfusion(Smelter smelter, ZDO zdo)
            {
                if (!originalSmelterSpeeds.ContainsKey(zdo.m_uid))
                {
                    originalSmelterSpeeds[zdo.m_uid] = smelter.m_secPerProduct;
                }

                if (smelterInfusions.TryGetValue(zdo.m_uid, out var infusion))
                {
                    float timeSinceInfusion = Time.time - infusion.timestamp;
                    bool gracePeriodActive = timeSinceInfusion < 1f;

                    bool shouldExpire = !gracePeriodActive && (
                        infusion.IsExpired ||
                        smelter.GetQueueSize() == 0 ||
                        smelter.GetFuel() <= 0f);

                    if (shouldExpire)
                    {
                        smelter.m_secPerProduct = originalSmelterSpeeds[zdo.m_uid];
                        smelterInfusions.Remove(zdo.m_uid);
                        ManageInfusionGlow(smelter.transform, zdo.m_uid, false);
                    }
                    else
                    {
                        // Safety check: ensure bonusSpeed is valid
                        if (infusion.bonusSpeed > 0f)
                        {
                            smelter.m_secPerProduct = infusion.bonusSpeed;
                            infusion.wasActive = true;
                        }
                        else
                        {
                            Debug.LogWarning($"[BlacksmithingExpanded] Invalid bonus speed detected for smelter. Reverting to original speed.");
                            smelter.m_secPerProduct = originalSmelterSpeeds[zdo.m_uid];
                            smelterInfusions.Remove(zdo.m_uid);
                        }
                    }
                }
                else
                {
                    smelter.m_secPerProduct = originalSmelterSpeeds[zdo.m_uid];
                }
            }
        }

        private static void ManageInfusionGlow(Transform workstation, ZDOID zdoid, bool enable)
        {
            if (!cfg_ShowInfusionVisualEffect.Value)
            {
                if (activeGlowEffects.TryGetValue(zdoid, out var existingEffect))
                {
                    if (existingEffect != null)
                        UnityEngine.Object.Destroy(existingEffect);

                    activeGlowEffects.Remove(zdoid);
                }
                return;
            }

            try
            {
                if (enable && !activeGlowEffects.ContainsKey(zdoid))
                {
                    var glowObject = new GameObject("InfusionGlow_Light");
                    glowObject.transform.position = workstation.position + Vector3.up * 0.5f;
                    glowObject.transform.SetParent(workstation);

                    var light = glowObject.AddComponent<Light>();
                    light.color = new Color(1f, 0.5f, 0.1f);
                    light.intensity = 4f;
                    light.range = 8f;
                    light.type = LightType.Point;

                    glowObject.AddComponent<FlickerLight>();

                    activeGlowEffects[zdoid] = glowObject;
                }
                else if (!enable && activeGlowEffects.TryGetValue(zdoid, out var existingEffect))
                {
                    if (existingEffect != null)
                        UnityEngine.Object.Destroy(existingEffect);

                    activeGlowEffects.Remove(zdoid);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Light management failed: {ex}");
            }
        }

        public class FlickerLight : MonoBehaviour
        {
            private Light lightSource;
            private float baseIntensity;

            void Start()
            {
                lightSource = GetComponent<Light>();
                baseIntensity = lightSource.intensity;
            }

            void Update()
            {
                if (lightSource != null)
                {
                    lightSource.intensity = baseIntensity + UnityEngine.Random.Range(-0.2f, 0.2f);
                }
            }
        }

        [HarmonyPatch(typeof(Attack), "Start")]
        public static class Patch_AttackStart
        {
            static void Prefix(Attack __instance, ItemDrop.ItemData weapon, Humanoid character)
            {
                if (weapon?.IsWeapon() != true || !(character is Player player)) return;
                if (!weapon.m_shared.m_name.ToLower().Contains("spear")) return;

                if (__instance.m_attackType == Attack.AttackType.Projectile)
                {
                    var data = weapon.Data().Get<BlacksmithingItemData>();
                    if (data?.level > 0)
                    {
                        string key = GenerateSpearKey(weapon, player);

                        var storedData = new BlacksmithingItemData();
                        CopyBlacksmithingData(data, storedData);

                        if (tempSpearDataStorage.Count >= MAX_SPEAR_DATA_ENTRIES)
                        {
                            CleanupOldSpearData();
                        }

                        tempSpearDataStorage[key] = storedData;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ItemDrop), "Start")]
        public static class Patch_ItemDropStart
        {
            static void Postfix(ItemDrop __instance)
            {
                if (__instance?.m_itemData?.IsWeapon() != true) return;
                if (!__instance.m_itemData.m_shared.m_name.ToLower().Contains("spear")) return;

                var item = __instance.m_itemData;
                var existingData = item.Data().Get<BlacksmithingItemData>();

                if (existingData?.level > 0) return;

                var bestMatch = FindBestSpearMatch(item);
                if (bestMatch.Key != null && bestMatch.Value != null)
                {
                    var newData = item.Data().GetOrCreate<BlacksmithingItemData>();
                    CopyBlacksmithingData(bestMatch.Value, newData);

                    newData.Save();

                    ApplyStoredBlacksmithingStats(item, newData);

                    tempSpearDataStorage.Remove(bestMatch.Key);
                }
            }
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.Clone))]
        public static class Patch_ItemDataClone
        {
            static void Postfix(ItemDrop.ItemData __result, ItemDrop.ItemData __instance)
            {
                if (__instance == null || __result == null) return;

                var originalData = __instance.Data().Get<BlacksmithingItemData>();
                if (originalData?.level > 0)
                {
                    var clonedData = __result.Data().GetOrCreate<BlacksmithingItemData>();
                    CopyBlacksmithingData(originalData, clonedData);

                    clonedData.Save();
                    clonedData.Load();
                }
            }
        }

        private static string GenerateSpearKey(ItemDrop.ItemData weapon, Player player)
        {
            return $"{weapon.m_shared.m_name}_{weapon.m_quality}_{weapon.m_durability:F1}_{player.GetPlayerID()}_{Time.time:F2}";
        }

        private static KeyValuePair<string, BlacksmithingItemData> FindBestSpearMatch(ItemDrop.ItemData item)
        {
            string bestKey = null;
            BlacksmithingItemData bestData = null;
            float bestScore = 0f;

            var currentTime = Time.time;

            foreach (var kvp in tempSpearDataStorage.ToList())
            {
                var keyParts = kvp.Key.Split('_');
                if (keyParts.Length < 5) continue;

                string storedName = keyParts[0];
                if (!int.TryParse(keyParts[1], out int storedQuality)) continue;
                if (!float.TryParse(keyParts[2], out float storedDurability)) continue;
                if (!long.TryParse(keyParts[3], out long storedPlayerId)) continue;
                if (!float.TryParse(keyParts[4], out float timestamp)) continue;

                if (storedName != item.m_shared.m_name || storedQuality != item.m_quality) continue;

                float timeDiff = currentTime - timestamp;
                if (timeDiff > 60f) continue;

                float durabilityDiff = Mathf.Abs(item.m_durability - storedDurability);
                float score = 1000f - (durabilityDiff * 10f) - (timeDiff * 5f);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestKey = kvp.Key;
                    bestData = kvp.Value;
                }
            }

            return new KeyValuePair<string, BlacksmithingItemData>(bestKey, bestData);
        }

        private static void ApplyStoredBlacksmithingStats(ItemDrop.ItemData item, BlacksmithingItemData data)
        {
            var baseStats = GetBaseStats(item);

            if (cfg_EnableDurabilityBonus.Value && data.maxDurability > 0f)
            {
                item.m_shared.m_maxDurability = data.maxDurability;
                item.m_durability = Mathf.Min(item.m_durability, data.maxDurability);
            }

            if (cfg_EnableArmorBonus.Value && data.armorBonus > 0f)
            {
                item.m_shared.m_armor = baseStats.armor + data.armorBonus;
            }

            item.m_shared.m_damages.m_blunt = baseStats.damages.m_blunt + data.damageBlunt;
            item.m_shared.m_damages.m_slash = baseStats.damages.m_slash + data.damageSlash;
            item.m_shared.m_damages.m_pierce = baseStats.damages.m_pierce + data.damagePierce;
            item.m_shared.m_damages.m_fire = baseStats.damages.m_fire + data.damageFire;
            item.m_shared.m_damages.m_frost = baseStats.damages.m_frost + data.damageFrost;
            item.m_shared.m_damages.m_lightning = baseStats.damages.m_lightning + data.damageLightning;
            item.m_shared.m_damages.m_poison = baseStats.damages.m_poison + data.damagePoison;
            item.m_shared.m_damages.m_spirit = baseStats.damages.m_spirit + data.damageSpirit;

            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
            {
                if (data.blockPowerBonus > 0f) item.m_shared.m_blockPower += data.blockPowerBonus;
                if (data.timedBlockBonus > 0f) item.m_shared.m_timedBlockBonus += data.timedBlockBonus;
            }
        }

        private static void CopyBlacksmithingData(BlacksmithingItemData source, BlacksmithingItemData target)
        {
            target.level = source.level;
            target.lastKnownQuality = source.lastKnownQuality;
            target.infusion = source.infusion;
            target.baseDurability = source.baseDurability;
            target.maxDurability = source.maxDurability;
            target.armorBonus = source.armorBonus;
            target.damageBlunt = source.damageBlunt;
            target.damageSlash = source.damageSlash;
            target.damagePierce = source.damagePierce;
            target.damageFire = source.damageFire;
            target.damageFrost = source.damageFrost;
            target.damageLightning = source.damageLightning;
            target.damagePoison = source.damagePoison;
            target.damageSpirit = source.damageSpirit;
            target.blockPowerBonus = source.blockPowerBonus;
            target.timedBlockBonus = source.timedBlockBonus;
            target.statsApplied = source.statsApplied;
        }

        private static Sprite LoadEmbeddedSprite(string resourceName, int width, int height)
        {
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BlacksmithingExpanded.icons." + resourceName))
                {
                    if (stream == null) return null;

                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        byte[] bytes = ms.ToArray();

                        Texture2D tex = new Texture2D(width, height);
                        if (tex.LoadImage(bytes))
                        {
                            return Sprite.Create(tex, new Rect(0, 0, width, height), Vector2.zero);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[BlacksmithingExpanded] Failed to load sprite {resourceName}: {ex}");
            }
            return null;
        }
    }
}