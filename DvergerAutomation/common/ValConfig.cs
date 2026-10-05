using BepInEx;
using BepInEx.Configuration;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DvergerAutomation {
    internal class ValConfig {
        public static ConfigFile cfg;
        public static ConfigEntry<bool> EnableDebugMode;
        public static ConfigEntry<float> DebugStorageReplyDelay;
        public static ConfigEntry<bool> DebugStorageDropReplies;
        public static ConfigEntry<bool> ShowStorageCounts;
        public static ConfigEntry<bool> CraftFromStorageEnabled;
        public static ConfigEntry<bool> DepositKeepFood;
        public static ConfigEntry<string> DepositIgnoredTypes;
        public static ConfigEntry<bool> DepositIncludeHotbar;
        public static ConfigEntry<bool> DepositIncludeQuickSlots;
        public static ConfigEntry<string> HopperDepositItems;

        // Dverger AutoSorter - "craft/build from nearby storage"
        public static ConfigEntry<bool> AutomationEnabled;
        public static ConfigEntry<float> ScanInterval;
        public static ConfigEntry<bool> FastInitialScan;
        public static ConfigEntry<float> ScanRadius;
        public static ConfigEntry<float> RangePerCore;
        public static ConfigEntry<bool> RequireCores;
        public static ConfigEntry<bool> CraftFromBoats;
        public static ConfigEntry<bool> CraftFromCarts;

        // Dverger AutoSorter - "auto store" deposit box
        public static ConfigEntry<bool> AutoStoreEnabled;
        public static ConfigEntry<bool> SortMagicItems;
        public static ConfigEntry<int> DepositBoxWidth;
        public static ConfigEntry<int> DepositBoxHeight;

        // Shared chests - several players in one chest
        public static ConfigEntry<bool> SharedChestsEnabled;

        // Dverger Hopper - smelter automation
        public static ConfigEntry<bool> HopperEnabled;
        public static ConfigEntry<float> HopperInterval;
        public static ConfigEntry<float> HopperRadius;
        public static ConfigEntry<float> HopperRangePerCore;
        public static ConfigEntry<bool> HopperRequireCores;
        public static ConfigEntry<float> HopperSpeedPerCore;
        public static ConfigEntry<int> HopperItemsPerTick;
        public static ConfigEntry<bool> HopperCollectOutput;
        public static ConfigEntry<int> HopperStoreRows;

        public const string cfgFolder = "DvergerAutomation";

        public ValConfig(ConfigFile cf) {
            // ensure all the config values are created
            cfg = cf;
            cfg.SaveOnConfigSet = true;
            CreateConfigValues(cf);
            Logger.SetDebugLogging(EnableDebugMode.Value);
        }

        private void CreateConfigValues(ConfigFile Config) {
            // Debugmode
            EnableDebugMode = Config.Bind("Client config", "EnableDebugMode", false,
                new ConfigDescription("Enables Debug logging.",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            EnableDebugMode.SettingChanged += Logger.EnableDebugLogging;
            Logger.CheckEnableDebugLogging();

            // For reproducing a slow or vanished chest owner on one machine. Both act on this client's
            // answers to other players' storage requests, and only while debug mode is on.
            DebugStorageReplyDelay = Config.Bind("Client config", "Debug Storage Reply Delay", 0f,
                new ConfigDescription("Debug mode only. Seconds this client waits before answering another player's request to take items out of, or put them into, a chest it owns. While the answer is held back the other player's chest panel can show a move twice.",
                new AcceptableValueRange<float>(0f, 60f),
                new ConfigurationManagerAttributes { IsAdvanced = true }));
            DebugStorageDropReplies = Config.Bind("Client config", "Debug Storage Drop Replies", false,
                new ConfigDescription("Debug mode only. This client carries out other players' storage requests but never answers them, as if it had left mid-request. Destroys items by design: anything it takes out of a chest for another player is in the answer it does not send. Use on a test world.",
                null,
                new ConfigurationManagerAttributes { IsAdvanced = true }));

            // Display only, so it stays client-side: crafting from storage works the same either way.
            ShowStorageCounts = Config.Bind("Client config", "Show Storage Counts", true,
                "Shows, after each ingredient's required amount in the crafting panel and build HUD, how many of it the AutoSorter's linked chests hold (as a green +N). Hover the ingredient for the full line.");

            // Client-side on purpose: the container pool is read locally by whoever is crafting, so one
            // player opting out changes nothing for anyone else. This entry is what persists the in-game
            // switch on the crafting panel across sessions; it is not the server's "Enabled" master.
            CraftFromStorageEnabled = Config.Bind("Client config", "Craft From Storage", true,
                "Whether crafting, upgrading, Hammer building and the Epic Loot enchanting table may spend materials out of the AutoSorter's linked chests. Off means only what you are carrying counts, as if no AutoSorter were in range. Toggled in-game with the button on the crafting panel.");
            CraftFromStorageEnabled.SettingChanged += CraftFromStorageToggle.OnConfigChanged;

            // Client-side on purpose: Deposit Selected is the local player emptying their own pack, and
            // what they want to keep on them is nobody else's business. These four entries are what the
            // [C] filter beside the button writes; equipped gear and an inventory mod's equipment slots
            // are held back unconditionally.
            DepositKeepFood = Config.Bind("Client config", "Deposit All Keeps Food", true,
                "Leaves food in your pack when Deposit Selected runs at the AutoSorter (the Food icon in its [C] filter). Only actual food counts (anything that fills a food slot), and this is the only setting that decides it, whatever its item type. Meads and other potions follow the Consumable type in the list below.");
            DepositIgnoredTypes = Config.Bind("Client config", "Deposit All Ignored Types",
                "Helmet, Chest, Legs, Hands, Shoulder, Utility, Trinket, OneHandedWeapon, TwoHandedWeapon, TwoHandedWeaponLeft, Bow, Shield, Tool, Torch, Ammo, AmmoNonEquipable",
                "Item types Deposit Selected leaves in your pack at the AutoSorter, comma separated. Easiest set from the [C] filter beside the button, which groups them into categories; listing only some of a category's types here shows it half lit there. Defaults to gear and ammo. Valid names: None, Material, Consumable, OneHandedWeapon, Bow, Shield, Helmet, Chest, Ammo, Customization, Legs, Hands, Trophy, TwoHandedWeapon, Torch, Misc, Shoulder, Utility, Tool, Attach_Atgeir, Fish, TwoHandedWeaponLeft, AmmoNonEquipable, Trinket. Item types added by other mods count as Misc. Clear the entry to deposit every type.");
            DepositIncludeHotbar = Config.Bind("Client config", "Deposit All Includes Hotbar", false,
                "Lets Deposit Selected take items from your hotbar (the top inventory row) at the AutoSorter, filtered like the rest of your pack. Equipped items always stay. Toggled in-game from the [C] filter.");
            DepositIncludeQuickSlots = Config.Bind("Client config", "Deposit All Includes Quick Slots", false,
                "Lets Deposit Selected take items from an inventory mod's quick slots at the AutoSorter, filtered like the rest of your pack: EquipmentAndQuickSlots and AzuExtendedPlayerInventory quick slots, and ExtraSlots quick, ammo, food and misc slots. Equipment slots and custom slots other mods add always stay. Does nothing without one of those mods. Toggled in-game from the [C] filter.");
            HopperDepositItems = Config.Bind("Client config", "Hopper Deposit Items",
                "Wood, FineWood, RoundLog, CopperOre, TinOre, IronOre, IronScrap, BronzeScrap, SilverOre, CopperScrap, FlametalOreNew, BlackMetalScrap",
                "Item prefabs the Hopper's deposit button moves into the Dverger Hopper, comma separated. Defaults to the wood a charcoal kiln burns and every ore a smelter or blast furnace melts. Add Coal to hand it fuel directly. Unlike the AutoSorter's button this does not spare your hotbar - only equipped items are left alone.");
            // The lists are parsed once and cached, and the button's tooltip and the [C] filter both show
            // these settings, so any edit - a click in the filter, the F1 menu, a reload of the file - has
            // to drop the cache and repaint both.
            DepositKeepFood.SettingChanged += DepositAll.InvalidateFilters;
            DepositIgnoredTypes.SettingChanged += DepositAll.InvalidateFilters;
            DepositIncludeHotbar.SettingChanged += DepositAll.InvalidateFilters;
            DepositIncludeQuickSlots.SettingChanged += DepositAll.InvalidateFilters;
            HopperDepositItems.SettingChanged += DepositAll.InvalidateFilters;

            AutomationEnabled = BindServerConfig("Dverger AutoSorter", "Enabled", true, "Enables the Dverger AutoSorter: nearby accessible chests act as a shared material pool when crafting at linked stations or building with the Hammer near the sorter.");
            // The in-panel switch has nothing to switch once the server has turned the whole feature
            // off, so it hides itself; that only works if a sync of this value repaints it.
            AutomationEnabled.SettingChanged += CraftFromStorageToggle.OnConfigChanged;
            ScanInterval = BindServerConfig("Dverger AutoSorter", "Scan Interval", 30f, "Seconds between scans for nearby crafting stations and storage chests.", false, 5, 300);
            FastInitialScan = BindServerConfig("Dverger AutoSorter", "Fast Initial Scan", true, "For the first minute after an AutoSorter loads - you arrive through a portal, log in, or walk back into range - it rescans every second while the base around it is still loading, instead of waiting out the Scan Interval. Lets a crafting station show everything in your chests as soon as you reach it. It only rescans when something new has loaded, so the cost is small once the base is in.");
            ScanRadius = BindServerConfig("Dverger AutoSorter", "Scan Radius", 20f, "Radius (meters) around the AutoSorter in which crafting stations and chests are linked.", false, 1, 64);
            RangePerCore = BindServerConfig("Dverger AutoSorter", "Range Per Core", 25f, "Extra link radius (meters) added per inserted Surtling Core.", false, 0, 100);
            RequireCores = BindServerConfig("Dverger AutoSorter", "Require Cores", true, "When enabled, the AutoSorter only links stations/chests after at least one Surtling Core is inserted.");
            // No SettingChanged hook needed: every hub relinks its boats and carts every couple of seconds,
            // and a switched-off type simply stops turning up.
            CraftFromBoats = BindServerConfig("Dverger AutoSorter", "Craft From Boats", true, "Links the storage of boats (Karve, Longship, Drakkar) within the AutoSorter's range, so their holds feed crafting and building like any linked chest, including while someone else is aboard. Boats never receive auto-stored items.");
            CraftFromCarts = BindServerConfig("Dverger AutoSorter", "Craft From Carts", true, "Links carts within the AutoSorter's range, so their contents feed crafting and building like any linked chest, including while someone else is pulling or riding it. Carts never receive auto-stored items.");
            AutoStoreEnabled = BindServerConfig("Dverger AutoSorter", "Auto Store", true, "Enables the AutoSorter's deposit box: closing it distributes what you left inside into linked chests that already hold the same item. Anything with no home stays in the box.");
            SortMagicItems = BindServerConfig("Dverger AutoSorter", "Sort Magic Items", false, "When enabled, enchanted (Epic Loot) items are distributed like anything else. Off by default so a legendary is never filed away into a chest of ordinary gear.");
            // Defaults match the player's own inventory (Humanoid.m_inventory is a hard-coded 8x4), so one
            // full backpack always fits in a single trip.
            DepositBoxWidth = BindServerConfig("Dverger AutoSorter", "Deposit Box Width", AutoStore.DefaultWidth, $"Columns in the AutoSorter's deposit box. Capped at {AutoStore.MaxWidth}: the container panel does not scroll sideways, so wider grids spill off the screen.", false, AutoStore.MinSize, AutoStore.MaxWidth);
            DepositBoxHeight = BindServerConfig("Dverger AutoSorter", "Deposit Box Height", AutoStore.DefaultHeight, "Rows in the AutoSorter's deposit box. Rows beyond what the container panel shows scroll.", false, AutoStore.MinSize, AutoStore.MaxHeight);

            // Read live wherever it matters, so a change takes effect without anything having to be told.
            SharedChestsEnabled = BindServerConfig("Shared Chests", "Enabled", true, "Lets several players have the same chest, boat hold or cart open at once and move items in and out of it together. The first to open it works as normal; everyone after has their moves carried out by that player's game. Off means a chest someone has open reads as in use, as in vanilla. The AutoSorter's deposit box and the Hopper's storage are always one player at a time. Does nothing when MultiUserChest is installed, which takes over.");

            HopperEnabled = BindServerConfig("Dverger Hopper", "Enabled", true, "Enables the Dverger Hopper: it feeds ore and fuel from its own inventory into nearby smelters, charcoal kilns and blast furnaces, and collects what they produce back into that same inventory. Other processing stations (windmills, spinning wheels, eitr refineries, modded ones) can be turned on per Hopper from the [C] button beside its deposit button.");
            HopperInterval = BindServerConfig("Dverger Hopper", "Tick Interval", 4f, "Seconds between service passes. Each pass collects finished product, tops up fuel, then queues ore.", false, 1, 60);
            HopperRadius = BindServerConfig("Dverger Hopper", "Scan Radius", 16f, "Base radius (meters) in which the Hopper services smelters, before any Surtling Core bonus.", false, 1, 64);
            HopperRangePerCore = BindServerConfig("Dverger Hopper", "Range Per Core", 5f, "Extra service radius (meters) added per inserted Surtling Core.", false, 0, 50);
            HopperRequireCores = BindServerConfig("Dverger Hopper", "Require Cores", true, "When enabled, the Hopper stays dormant until at least one Surtling Core is inserted.");
            HopperSpeedPerCore = BindServerConfig("Dverger Hopper", "Speed Per Core", 0.15f, "Processing speed added per inserted Surtling Core, as a fraction. 0.15 means six cores run linked stations at 1.9x. Fuel cost per item produced is unchanged - only the wait shrinks.", false, 0, 2);
            HopperItemsPerTick = BindServerConfig("Dverger Hopper", "Items Per Tick", 8, "Maximum items the Hopper moves into smelters per pass. Lower values feed more gradually.", false, 1, 100);
            HopperCollectOutput = BindServerConfig("Dverger Hopper", "Collect Output", true, "Pulls finished product straight into the Hopper's inventory instead of letting it drop on the ground. When the Hopper is full, product drops normally.");
            HopperStoreRows = BindServerConfig("Dverger Hopper", "Storage Rows", HopperStore.DefaultRows, $"Rows in the Hopper's internal storage. The width is fixed at {HopperStore.Width} (the player's own inventory width) because a narrower grid deletes items sitting in the columns it drops.", false, HopperStore.MinRows, HopperStore.MaxRows);
        }

        /// <summary>
        /// Binds a server configuration entry for a list of strings with the specified category, key, default value,
        /// and description. This config will be server authoritative, editable by admins.
        /// </summary>
        /// <param name="category">The category under which the configuration entry is grouped. Cannot be null or empty.</param>
        /// <param name="key">The unique key identifying the configuration entry within the specified category. Cannot be null or empty.</param>
        /// <param name="value">The default list of strings to use for the configuration entry if no value is set.</param>
        /// <param name="description">A description of the configuration entry, used for documentation and display purposes.</param>
        /// <param name="advanced">Indicates whether the configuration entry is considered advanced. If <see langword="true"/>, the entry may
        /// be hidden from standard configuration views.</param>
        /// <returns>A <see cref="ConfigEntry{List{string}}"/> representing the bound server configuration entry.</returns>
        public static ConfigEntry<List<string>> BindServerConfig(string category, string key, List<string> value, string description, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                null,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<float[]> BindServerConfig(string category, string key, float[] value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        ///  Helper to bind configs for bool types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="acceptableValues"></param>>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<bool> BindServerConfig(string category, string key, bool value, string description, AcceptableValueBase acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for int types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<int> BindServerConfig(string category, string key, int value, string description, bool advanced = false, int valMin = 0, int valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<int>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for float types
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <param name="valMin"></param>
        /// <param name="valMax"></param>
        /// <returns></returns>
        public static ConfigEntry<float> BindServerConfig(string category, string key, float value, string description, bool advanced = false, float valMin = 0, float valMax = 150) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(description,
                new AcceptableValueRange<float>(valMin, valMax),
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }

        /// <summary>
        /// Helper to bind configs for strings
        /// </summary>
        /// <param name="config_file"></param>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="advanced"></param>
        /// <returns></returns>
        public static ConfigEntry<string> BindServerConfig(string category, string key, string value, string description, AcceptableValueList<string> acceptableValues = null, bool advanced = false) {
            return cfg.Bind(category, key, value,
                new ConfigDescription(
                    description,
                    acceptableValues,
                new ConfigurationManagerAttributes { IsAdminOnly = true, IsAdvanced = advanced })
                );
        }
    }
}
