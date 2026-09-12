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

        // Dverger AutoSorter - "craft/build from nearby storage"
        public static ConfigEntry<bool> AutomationEnabled;
        public static ConfigEntry<float> ScanInterval;
        public static ConfigEntry<float> ScanRadius;
        public static ConfigEntry<float> RangePerCore;
        public static ConfigEntry<bool> RequireCores;

        // Dverger AutoSorter - "auto store" deposit box
        public static ConfigEntry<bool> AutoStoreEnabled;
        public static ConfigEntry<bool> SortMagicItems;
        public static ConfigEntry<int> DepositBoxWidth;
        public static ConfigEntry<int> DepositBoxHeight;

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

            AutomationEnabled = BindServerConfig("Dverger AutoSorter", "Enabled", true, "Enables the Dverger AutoSorter: nearby accessible chests act as a shared material pool when crafting at linked stations or building with the Hammer near the sorter.");
            ScanInterval = BindServerConfig("Dverger AutoSorter", "Scan Interval", 30f, "Seconds between scans for nearby crafting stations and storage chests.", false, 5, 300);
            ScanRadius = BindServerConfig("Dverger AutoSorter", "Scan Radius", 20f, "Radius (meters) around the AutoSorter in which crafting stations and chests are linked.", false, 1, 64);
            RangePerCore = BindServerConfig("Dverger AutoSorter", "Range Per Core", 25f, "Extra link radius (meters) added per inserted Surtling Core.", false, 0, 100);
            RequireCores = BindServerConfig("Dverger AutoSorter", "Require Cores", true, "When enabled, the AutoSorter only links stations/chests after at least one Surtling Core is inserted.");
            AutoStoreEnabled = BindServerConfig("Dverger AutoSorter", "Auto Store", true, "Enables the AutoSorter's deposit box: closing it distributes what you left inside into linked chests that already hold the same item. Anything with no home is handed back to you.");
            SortMagicItems = BindServerConfig("Dverger AutoSorter", "Sort Magic Items", false, "When enabled, enchanted (Epic Loot) items are distributed like anything else. Off by default so a legendary is never filed away into a chest of ordinary gear.");
            // Defaults match the player's own inventory (Humanoid.m_inventory is a hard-coded 8x4), so one
            // full backpack always fits in a single trip.
            DepositBoxWidth = BindServerConfig("Dverger AutoSorter", "Deposit Box Width", AutoStore.DefaultWidth, $"Columns in the AutoSorter's deposit box. Capped at {AutoStore.MaxWidth}: the container panel does not scroll sideways, so wider grids spill off the screen.", false, AutoStore.MinSize, AutoStore.MaxWidth);
            DepositBoxHeight = BindServerConfig("Dverger AutoSorter", "Deposit Box Height", AutoStore.DefaultHeight, "Rows in the AutoSorter's deposit box. Rows beyond what the container panel shows scroll.", false, AutoStore.MinSize, AutoStore.MaxHeight);
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
