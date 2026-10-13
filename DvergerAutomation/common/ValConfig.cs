using BepInEx.Configuration;

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

        // Dverger Seeking Table - search and take from nearby storage
        public static ConfigEntry<bool> SeekEnabled;
        public static ConfigEntry<float> SeekRadius;
        public static ConfigEntry<float> SeekRangePerCore;
        public static ConfigEntry<bool> SeekRequireCores;
        public static ConfigEntry<bool> SeekFromBoats;
        public static ConfigEntry<bool> SeekFromCarts;
        public static ConfigEntry<bool> SeekFromHoppers;
        public static ConfigEntry<bool> SeekShowBeam;
        public static ConfigEntry<float> SeekBeamSeconds;
        public static ConfigEntry<bool> SeekGroundItems;
        public static ConfigEntry<bool> SeekAllowGround;

        // Dverger Grinder - breaks crafted items back down into part of their cost
        public static ConfigEntry<bool> GrinderEnabled;
        public static ConfigEntry<float> GrinderBaseYield;
        public static ConfigEntry<float> GrinderYieldPerCore;
        public static ConfigEntry<float> GrinderSpeedPerCore;
        public static ConfigEntry<float> GrinderSecondsPerItem;
        public static ConfigEntry<int> GrinderFeedRows;
        public static ConfigEntry<int> GrinderQueueSize;
        public static ConfigEntry<int> GrinderStoreRows;
        public static ConfigEntry<bool> GrinderMagicMundane;
        public static ConfigEntry<bool> GrinderMagicEnchant;
        public static ConfigEntry<string> GrinderBlacklist;
        public static ConfigEntry<int> GrinderBatchesPerTick;

        // Dverger Lighter - keeps fires, torches, ovens and hot tubs in range fuelled
        public static ConfigEntry<bool> LighterEnabled;
        public static ConfigEntry<float> LighterRadius;
        public static ConfigEntry<float> LighterInterval;
        public static ConfigEntry<int> LighterStoreRows;
        public static ConfigEntry<string> LighterIgnoredPieces;
        public static ConfigEntry<bool> LighterPhysicalSmoke;

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
            // to drop the cache and repaint both. The Hopper's list is only parsed, never shown.
            DepositKeepFood.SettingChanged += DepositAll.InvalidateFilters;
            DepositIgnoredTypes.SettingChanged += DepositAll.InvalidateFilters;
            DepositIncludeHotbar.SettingChanged += DepositAll.InvalidateFilters;
            DepositIncludeQuickSlots.SettingChanged += DepositAll.InvalidateFilters;
            HopperDepositItems.SettingChanged += HopperHub.DepositItemsChanged;

            // Client-side: the line is drawn on each player's own screen, from the RPC every Seeking Table
            // sends when it hands out an item.
            SeekShowBeam = Config.Bind("Client config", "Seeking Table Shows Retrieval Line", true,
                "Draws a short line from a Dverger Seeking Table to each chest it takes an item out of, for anyone nearby to see.");
            SeekBeamSeconds = Config.Bind("Client config", "Seeking Table Line Seconds", 1.5f,
                new ConfigDescription("How long the Seeking Table's retrieval line stays up, in seconds.",
                new AcceptableValueRange<float>(0.2f, 10f)));
            // Client-side: what one player's own table window lists. This entry is what the "Ground items"
            // switch beside the list writes, so the choice outlasts the session.
            SeekGroundItems = Config.Bind("Client config", "Seeking Table Lists Ground Items", false,
                "Also lists items lying on the ground within a Dverger Seeking Table's range, and lets you take them from where they lie. Toggled in-game with the Ground items switch beside the table's list.");
            SeekGroundItems.SettingChanged += SeekingIndex.GroundSettingChanged;

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
            SharedChestsEnabled = BindServerConfig("Shared Chests", "Enabled", true, "Lets several players have the same chest, boat hold or cart open at once and move items in and out of it together. The first to open it works as normal; everyone after has their moves carried out by that player's game. Off means a chest someone has open reads as in use, as in vanilla. The AutoSorter's deposit box, the Hopper's storage, the Lighter's fuel tank and the Grinder's hopper and results chest are always one player at a time. Does nothing when MultiUserChest is installed, which takes over.");

            HopperEnabled = BindServerConfig("Dverger Hopper", "Enabled", true, "Enables the Dverger Hopper: it feeds ore and fuel from its own inventory into nearby smelters, charcoal kilns and blast furnaces, and collects what they produce back into that same inventory. Other processing stations (windmills, spinning wheels, eitr refineries, modded ones) can be turned on per Hopper from the [C] button beside its deposit button.");
            HopperInterval = BindServerConfig("Dverger Hopper", "Tick Interval", 4f, "Seconds between service passes. Each pass collects finished product, queues ore, then adds the fuel that ore needs.", false, 1, 60);
            HopperRadius = BindServerConfig("Dverger Hopper", "Scan Radius", 16f, "Base radius (meters) in which the Hopper services smelters, before any Surtling Core bonus.", false, 1, 64);
            HopperRangePerCore = BindServerConfig("Dverger Hopper", "Range Per Core", 5f, "Extra service radius (meters) added per inserted Surtling Core.", false, 0, 50);
            HopperRequireCores = BindServerConfig("Dverger Hopper", "Require Cores", true, "When enabled, the Hopper stays dormant until at least one Surtling Core is inserted.");
            HopperSpeedPerCore = BindServerConfig("Dverger Hopper", "Speed Per Core", 0.15f, "Processing speed added per inserted Surtling Core, as a fraction. 0.15 means six cores run linked stations at 1.9x. Fuel cost per item produced is unchanged - only the wait shrinks.", false, 0, 2);
            HopperItemsPerTick = BindServerConfig("Dverger Hopper", "Items Per Tick", 8, "Maximum items the Hopper moves into smelters per pass. Lower values feed more gradually.", false, 1, 100);
            HopperCollectOutput = BindServerConfig("Dverger Hopper", "Collect Output", true, "Pulls finished product straight into the Hopper's inventory instead of letting it drop on the ground. When the Hopper is full, product drops normally.");
            HopperStoreRows = BindServerConfig("Dverger Hopper", "Storage Rows", HopperHub.DefaultStoreRows, $"Rows in the Hopper's internal storage. The width is fixed at {MachineStore.Width} (the player's own inventory width) because a narrower grid deletes items sitting in the columns it drops.", false, HopperHub.MinStoreRows, HopperHub.MaxStoreRows);

            // The section is the piece's own name, which RegisterJotunnPiece also binds its build settings
            // under - so "Enabled" here is one entry shared with the piece, as for the other two machines.
            SeekEnabled = BindServerConfig("Dverger Seeking Table", "Enabled", true, "Enables the Dverger Seeking Table: using it lists everything in the chests around it that you could open yourself, and lets you search that list and take any of it.");
            SeekRadius = BindServerConfig("Dverger Seeking Table", "Scan Radius", 20f, "Radius (meters) around the Seeking Table whose chests it lists, before any Black Core bonus.", false, 1, 64);
            SeekRangePerCore = BindServerConfig("Dverger Seeking Table", "Range Per Core", 20f, "Extra radius (meters) added per inserted Black Core. The table only reaches chests that are loaded around you, which in vanilla is roughly 120 meters.", false, 0, 50);
            SeekRequireCores = BindServerConfig("Dverger Seeking Table", "Require Cores", false, "When enabled, the Seeking Table lists nothing until at least one Black Core is inserted.");
            SeekFromBoats = BindServerConfig("Dverger Seeking Table", "Include Boats", true, "Lists the holds of boats in range, and lets you take from them, including while someone else is aboard.");
            SeekFromCarts = BindServerConfig("Dverger Seeking Table", "Include Carts", true, "Lists carts in range, and lets you take from them, including while someone else is pulling or riding one.");
            SeekFromHoppers = BindServerConfig("Dverger Seeking Table", "Include Hopper Storage", true, "Lists what is in the storage of Dverger Hoppers in range, so finished bars can be taken from the table.");
            SeekAllowGround = BindServerConfig("Dverger Seeking Table", "Allow Ground Items", true, "Lets players switch on listing the items lying on the ground within a Seeking Table's range, and take them from where they lie. Each player's switch is off until they turn it on. Off hides the switch.");
            SeekAllowGround.SettingChanged += SeekingIndex.GroundSettingChanged;

            // Shares the piece's own "Enabled" entry, as the Seeking Table does.
            GrinderEnabled = BindServerConfig("Dverger Grinder", "Enabled", true, "Enables the Dverger Grinder: items put in its hopper and sent in with its lever are broken down, one at a time, into part of what they cost to craft, which collects in its results chest.");
            GrinderBaseYield = BindServerConfig("Dverger Grinder", "Base Yield Percent", 20f, "Percent of an item's crafting cost the Grinder returns with no Molten Cores in it. Upgrades count: a sword at quality 3 returns part of everything spent making and upgrading it. Fractions carry over between items, so nothing is lost to rounding.", false, 0, 100);
            GrinderYieldPerCore = BindServerConfig("Dverger Grinder", "Yield Per Core Percent", 2f, "Percent added to the Grinder's yield per inserted Molten Core. The total never goes above 100.", false, 0, 10);
            GrinderSpeedPerCore = BindServerConfig("Dverger Grinder", "Speed Per Core", 0.10f, "Processing speed added per inserted Molten Core, as a fraction. 0.10 means ten cores grind at 2x.", false, 0, 1);
            GrinderSecondsPerItem = BindServerConfig("Dverger Grinder", "Seconds Per Item", 30f, "Seconds the Grinder takes over one item, before any Molten Core speed-up. A stack is ground one craft's worth at a time - 20 arrows, or one bronze bar.", false, 5, 600);
            GrinderFeedRows = BindServerConfig("Dverger Grinder", "Hopper Rows", GrinderStore.DefaultRows, $"Rows in the Grinder's hopper, where items are put before its lever sends them in to be ground. The width is fixed at {MachineStore.Width}, as for the results chest.", false, GrinderStore.MinRows, GrinderStore.MaxRows);
            GrinderQueueSize = BindServerConfig("Dverger Grinder", "Queue Size", 16, "Most stacks the Grinder holds in its processing queue. Pulling the lever moves the hopper's contents into the queue, as many stacks as there is room for; the rest stay in the hopper.", false, 1, 64);
            GrinderStoreRows = BindServerConfig("Dverger Grinder", "Storage Rows", GrinderStore.DefaultRows, $"Rows in the Grinder's results chest. The width is fixed at {MachineStore.Width} (the player's own inventory width) because a narrower grid deletes items sitting in the columns it drops.", false, GrinderStore.MinRows, GrinderStore.MaxRows);
            GrinderMagicMundane = BindServerConfig("Dverger Grinder", "Enchanted Items Return Mundane Materials", true, "When enabled, the Grinder breaks down enchanted (Epic Loot) items like any other, returning its share of their ordinary crafting cost. With this and Enchanted Items Return Magic Materials both off, enchanted items come out in the results chest as they went in.");
            GrinderMagicEnchant = BindServerConfig("Dverger Grinder", "Enchanted Items Return Magic Materials", false, "When enabled, enchanted (Epic Loot) items return the Grinder's share of what enchanting them to their rarity costs at the enchanting table - its runestone, dust and essence or reagent - on top of their crafting cost when Enchanted Items Return Mundane Materials is on too. Fractions carry over between items, as for any material.");
            GrinderBlacklist = BindServerConfig("Dverger Grinder", "Blacklist", "", "Comma-separated item prefab names the Grinder refuses, eg: 'BronzeNails,ArrowWood'. Items sold by a trader are always refused. Refused items come out in the results chest as they went in.");
            GrinderBatchesPerTick = BindServerConfig("Dverger Grinder", "Batches Per Tick", 4, "Most items the Grinder finishes in one second. Only matters when it catches up on time spent unloaded.", true, 1, 20);

            // Shares the piece's own "Enabled" entry, as the Grinder does.
            LighterEnabled = BindServerConfig("Dverger Lighter", "Enabled", true, "Enables the Dverger Lighter: it keeps the fires, torches, braziers, ovens and hot tubs around it topped up with fuel from its tank.");
            LighterRadius = BindServerConfig("Dverger Lighter", "Range", 64f, "Radius (meters) around the Lighter in which it keeps pieces fuelled. It only reaches pieces that are loaded, which in vanilla is roughly 120 meters around a player.", false, 1, 128);
            LighterInterval = BindServerConfig("Dverger Lighter", "Tick Interval", 5f, "Seconds between refuelling passes. Each pass tops up everything in range that has room for a whole unit of fuel, the emptiest first.", false, 1, 60);
            LighterStoreRows = BindServerConfig("Dverger Lighter", "Storage Rows", LighterHub.DefaultStoreRows, $"Rows in the Lighter's fuel tank. The width is fixed at {MachineStore.Width} (the player's own inventory width) because a narrower grid deletes items sitting in the columns it drops.", false, LighterHub.MinStoreRows, LighterHub.MaxStoreRows);
            LighterIgnoredPieces = BindServerConfig("Dverger Lighter", "Ignored Pieces", "", "Comma-separated piece prefab names the Lighter never fuels, eg: 'piece_oven,piece_bathtub'. Fires the world placed itself, and anything switched off, are always left alone.");
            // Server-side although the smoke is spawned by each game for itself: it gathers under roofs and
            // smokes players and fires out as a fire's does, so everyone should be playing by the same rule.
            LighterPhysicalSmoke = BindServerConfig("Dverger Lighter", "Physical Smoke", true, "While the Lighter is working, its centre column puffs out the same smoke a fire does. Like a fire's, it gathers under a roof, and with no way out it can choke nearby fires and smoke players out. Off leaves only the harmless wisps from its three pipes.");
        }

        /// <summary>
        /// Helper to bind configs for bool types
        /// </summary>
        /// <param name="category"></param>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <param name="description"></param>
        /// <param name="acceptableValues"></param>
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
