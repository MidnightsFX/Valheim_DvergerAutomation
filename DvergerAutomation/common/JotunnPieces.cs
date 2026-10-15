using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static Piece;

namespace DvergerAutomation.Common {
    public static class JotunnPiece {

        public class LoadedGameObjects {
            public GameObject Prefab {
                get; set;
            }
            public Sprite Sprite {
                get; set;
            }
            public GameObject ScenePrefab {
                get; set;
            }
            // The resolved prefab's Piece component. Everything applied at runtime goes through this.
            public Piece ScenePiece {
                get; set;
            }
        }

        public class PieceCost {
            public string prefab {
                get; set;
            }
            public int amount {
                get; set;
            }
            public bool refundable { get; set; } = true;
        }

        public class PieceConfigs {
            public ConfigEntry<bool> Enabled {
                get; set;
            }
            public ConfigEntry<bool> RequiresWorkbench {
                get; set;
            }
            public ConfigEntry<string> Workbench {
                get; set;
            }
            public ConfigEntry<string> PieceCategory {
                get; set;
            }
            public ConfigEntry<string> PieceCost {
                get; set;
            }
            public List<PieceCost> UpdatedCost { get; set; } = new List<PieceCost>();
        }

        public class JotunnBuildPiece {
            public string Name {
                get; set;
            }
            public bool Enabled { get; set; } = true;
            public string Prefab {
                get; set;
            }
            public string Sprite {
                get; set;
            }
            public string Category { get; set; } = "Misc";
            public string Workbench { get; set; } = "piece_workbench";
            public List<PieceCost> PieceCost { get; set; } = new List<PieceCost>();

            // Populated by in-game related runtime objects
            public LoadedGameObjects Objs {
                get; set;
            }
            public PieceConfigs Cfgs {
                get; set;
            }
        }

        static List<JotunnBuildPiece> BuildPieces = new List<JotunnBuildPiece>();
        static bool PiecesReady = false;

        // Prefab ids that shipped in a default recipe but do not exist in game. A saved config still
        // holding one leaves the piece with an unresolvable requirement, which makes it unbuildable and
        // cannot be recovered from in-game, so the entry is forced back to the default on load.
        static readonly HashSet<string> KnownBadPrefabIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "GreyDwarfEyes", // <=0.5.0 default; the real item id is GreydwarfEye
        };

        public static void RegisterJotunnPiece(JotunnBuildPiece jbuildpiece) {

            LoadedGameObjects LGos = new LoadedGameObjects();

            // Set asset references
            LGos.Prefab = DvergerAutomation.EmbeddedResourceBundle.LoadAsset<GameObject>($"{jbuildpiece.Prefab}.prefab");
            // A piece whose prefab is not in the bundle yet (mid-development, or a bundle built before the
            // piece existed) would otherwise reach CustomPiece with a null GameObject and take the whole
            // plugin down in Awake. Skip it and say so instead.
            if (LGos.Prefab == null) {
                Logger.LogWarning($"{jbuildpiece.Name}: '{jbuildpiece.Prefab}.prefab' is not in the asset bundle; skipping this piece. Rebuild the bundle to enable it.");
                return;
            }
            LGos.Sprite = DvergerAutomation.EmbeddedResourceBundle.LoadAsset<Sprite>($"{jbuildpiece.Sprite}.png");
            jbuildpiece.Objs = LGos;
            jbuildpiece.Cfgs = new PieceConfigs();

            InitialPieceSetup(jbuildpiece);

            BuildPieces.Add(jbuildpiece);


            void ResolveAndApplyScenePrefab() {
                GameObject scenePrefab = ResolveScenePrefab(jbuildpiece.Prefab);
                if (scenePrefab == null) {
                    Logger.LogWarning($"Could not find a scene prefab named '{jbuildpiece.Prefab}' with a Piece component after prefab registration; skipping in-place setup for {jbuildpiece.Name}.");
                    return;
                }
                jbuildpiece.Objs.ScenePrefab = scenePrefab;
                jbuildpiece.Objs.ScenePiece = scenePrefab.GetComponent<Piece>();
                PiecesReady = true;
                // Bring the current config (default or server-synced) into effect a single time now that
                // every mod prefab is resolvable. This also covers values that arrived early via config sync.
                ApplyEnabled(jbuildpiece);
                ApplyWorkbench(jbuildpiece);
                ApplyCategory(jbuildpiece);
                // The prefab database is populated by now, so a config naming an item that does not
                // exist can finally be told apart from a valid customisation and reset.
                ResetUnresolvableRecipeConfig(jbuildpiece);
                ApplyRecipe(jbuildpiece);
            }
            PrefabManager.OnPrefabsRegistered += ResolveAndApplyScenePrefab;
        }

        // Finds the registered prefab to apply config to. Jotunn's PrefabManager is authoritative, so ask it
        // first; the Resources scan is only a fallback for prefabs injected into ZNetScene by other means.
        //
        // The scan must filter on the Piece component: several loaded GameObjects can share a prefab's name
        // (the bundle asset, Jotunn's registered clone, child objects), Resources.FindObjectsOfTypeAll returns
        // them in no defined order, and picking one without a Piece made every Apply* below throw an NRE.
        private static GameObject ResolveScenePrefab(string prefabName) {
            GameObject registered = PrefabManager.Instance.GetPrefab(prefabName);
            if (registered != null && registered.GetComponent<Piece>() != null) {
                return registered;
            }

            List<GameObject> candidates = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(obj => obj.name == prefabName && obj.GetComponent<Piece>() != null)
                .ToList();
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"Found {candidates.Count} scene object(s) named '{prefabName}' with a Piece component.");
            }
            return candidates.FirstOrDefault();
        }

        private static void InitialPieceSetup(JotunnBuildPiece jbuildpiece) {
            // Set where the recipe can be crafted. Gated on PiecesReady so an early config sync / file
            // reload (both fire before ZNetScene.Awake) can't run before the scene prefab is resolved.
            void RequiredBench_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                ApplyWorkbench(jbuildpiece);
            }
            jbuildpiece.Cfgs.Workbench = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Workbench", jbuildpiece.Workbench, $"The table required to allow building this piece, eg: 'forge', 'piece_workbench', 'blackforge', 'piece_artisanstation'.");
            jbuildpiece.Cfgs.Workbench.SettingChanged += RequiredBench_SettingChanged;

            // Crafting cost change
            void BuildRecipeChanged_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                if (sender.GetType() == typeof(ConfigEntry<string>)) {
                    ConfigEntry<string> sendEntry = (ConfigEntry<string>)sender;
                    if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Recieved new piece config {sendEntry.Value}"); }
                    // return if its an invalid change
                    if (PieceRecipeConfigUpdater(jbuildpiece, sendEntry.Value) == false) { return; }
                }
                ApplyRecipe(jbuildpiece);
            }

            // Setup enable/disable
            void Enabled_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                ApplyEnabled(jbuildpiece);
            }
            jbuildpiece.Cfgs.Enabled = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Enabled", jbuildpiece.Enabled, $"Enable/Disable the {jbuildpiece.Name}.");
            jbuildpiece.Cfgs.Enabled.SettingChanged += Enabled_SettingChanged;
            // Setup piece category
            jbuildpiece.Cfgs.PieceCategory = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Piece Category", jbuildpiece.Category, "Piece category for building.", PieceCategories.GetAcceptableValueList());
            void CraftingCategory_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePiece == null) { return; }
                ApplyCategory(jbuildpiece);
            }
            jbuildpiece.Cfgs.PieceCategory.SettingChanged += CraftingCategory_SettingChanged;

            // Build out the internal default recipe
            string recipe_cfg_default = BuildRecipeString(jbuildpiece.PieceCost);
            // Wire up the config and on-change for piece costs
            jbuildpiece.Cfgs.PieceCost = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Building Cost", recipe_cfg_default, $"Cost to build. Find item ids: https://valheim.fandom.com/wiki/Item_IDs Format: resouce_id,amount,refund eg: Wood,8,true|LeatherScraps,4,false", advanced: true);
            // Force out a saved recipe carried over from a build that shipped a bad prefab id. This has
            // to happen before the piece is registered below, and the prefab database is not populated
            // yet, so the ids are matched against a known-bad list rather than looked up.
            ResetRecipeConfigNamingKnownBadIds(jbuildpiece, recipe_cfg_default);
            if (PieceRecipeConfigUpdater(jbuildpiece, jbuildpiece.Cfgs.PieceCost.Value, false) == false) {
                Logger.LogWarning($"{jbuildpiece.Name} has an invalid piece cost. The default will be used instead.");
                PieceRecipeConfigUpdater(jbuildpiece, recipe_cfg_default, false);
            }


            jbuildpiece.Cfgs.PieceCost.SettingChanged += BuildRecipeChanged_SettingChanged;

            // An empty or unparseable cost (the config value and the code default both failed to parse)
            // would otherwise register a free-to-build piece. A piece with no valid cost is a definition
            // error, not a free piece, so it is not registered at all.
            if (jbuildpiece.Cfgs.UpdatedCost.Count == 0) {
                Logger.LogError($"{jbuildpiece.Name} has no valid build cost; skipping registration so it cannot be built for free.");
                return;
            }

            List<RequirementConfig> recipe = new List<RequirementConfig>();
            foreach (var entry in jbuildpiece.Cfgs.UpdatedCost) {
                recipe.Add(new RequirementConfig { Item = entry.prefab, Amount = entry.amount, Recover = entry.refundable });
            }

            // Build the jotunn piece definition
            PieceConfig piececfg = new PieceConfig() {
                CraftingStation = jbuildpiece.Cfgs.Workbench.Value,
                PieceTable = PieceTables.Hammer,
                Category = jbuildpiece.Cfgs.PieceCategory.Value,
                Icon = jbuildpiece.Objs.Sprite,
                Requirements = recipe.ToArray()
            };
            // Add the updated piece to the piece manager
            PieceManager.Instance.AddPiece(new CustomPiece(jbuildpiece.Objs.Prefab, fixReference: true, piececfg));
        }

        // Applies the configured Enabled switch to the in-scene piece. Setting m_enabled alone is not enough
        // in a running game: everything the hammer lets you place - the build menu, favourites, recent
        // pieces, copying the piece you look at - reads the player's list of available pieces, which the game
        // only rebuilds when the hammer is equipped, the category changes or something is picked up. Until
        // then a piece switched off stays placeable, so the list is rebuilt here.
        private static void ApplyEnabled(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            bool enabled = jbuildpiece.Cfgs.Enabled.Value;
            if (piece.m_enabled == enabled) { return; }
            piece.m_enabled = enabled;
            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"{jbuildpiece.Name} can {(enabled ? "now" : "no longer")} be built."); }
            RefreshLocalBuildMenu();
        }

        // Rebuilds the local player's list of pieces they can place, and learns any piece just switched on
        // that they already have the materials for. Nothing to do before the player spawns: spawning builds
        // the list from scratch, which is what makes the server's values hold from the moment you join.
        private static void RefreshLocalBuildMenu() {
            Player player = Player.m_localPlayer;
            // Leaving a server restores the local config while the world is being torn down, and both
            // calls below reach into these.
            if (player == null || ZoneSystem.instance == null || ObjectDB.instance == null) { return; }
            player.UpdateKnownRecipesList();
            player.UpdateAvailablePiecesList();
        }

        /// <summary>
        /// Takes switched-off pieces back out of a hammer's available list. The game skips disabled pieces
        /// itself, except while building costs nothing (the nocost command, or a world with every piece
        /// unlocked), where it lists them all - a piece the server switched off stays out regardless.
        /// </summary>
        internal static void RemoveDisabledPieces(PieceTable table) {
            foreach (JotunnBuildPiece jbuildpiece in BuildPieces) {
                Piece piece = jbuildpiece.Objs.ScenePiece;
                if (piece == null || piece.m_enabled) { continue; }
                table.m_enabledPieces.Remove(piece);
                if (!table.m_availablePieces.Remove(piece)) { continue; }
                foreach (List<Piece> category in table.m_availablePiecesByCategory) {
                    category.Remove(piece);
                }
            }
        }

        // Applies the configured crafting station to the in-scene piece. Callers must ensure the scene
        // prefab is resolved (PiecesReady) before invoking this.
        private static void ApplyWorkbench(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            // RequiresWorkbench is currently never bound; treat a missing entry as "workbench required".
            bool requiresWorkbench = jbuildpiece.Cfgs.RequiresWorkbench?.Value ?? true;
            string workbench = jbuildpiece.Cfgs.Workbench.Value;
            if (requiresWorkbench == false || string.IsNullOrEmpty(workbench) || string.Equals(workbench, "none", StringComparison.OrdinalIgnoreCase)) {
                Logger.LogInfo("Setting required crafting station to none.");
                piece.m_craftingStation = null;
                return;
            }

            CraftingStation craftable_at = PrefabManager.Instance.GetPrefab(workbench)?.GetComponent<CraftingStation>();
            if (craftable_at == null) {
                Logger.LogWarning($"Required crafting station does not exist or does not have a crafting station component, check your prefab name ({workbench}).");
                return;
            }

            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Setting crafting station to {workbench}."); }
            piece.m_craftingStation = craftable_at;
        }

        // Applies the configured build category to the in-scene piece.
        private static void ApplyCategory(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            Piece.PieceCategory? category = PieceManager.Instance.GetPieceCategory(jbuildpiece.Cfgs.PieceCategory.Value);
            if (category == null) {
                category = PieceManager.Instance.AddPieceCategory(jbuildpiece.Cfgs.PieceCategory.Value);
            }
            piece.m_category = (PieceCategory)category;
        }

        // Resolves the recipe in UpdatedCost against the live prefab database and applies it to the
        // in-scene piece. Bails out (leaving the existing recipe intact) if any requirement prefab is not
        // yet resolvable, so it is safe even if a dependency mod registered its items late.
        private static void ApplyRecipe(JotunnBuildPiece jbuildpiece) {
            Piece piece = jbuildpiece.Objs.ScenePiece;
            if (piece == null) { return; }

            // Applied while the piece is switched off too, so switching it back on needs nothing from here.
            List<Piece.Requirement> requirements = new List<Piece.Requirement>();
            foreach (PieceCost entry in jbuildpiece.Cfgs.UpdatedCost) {
                ItemDrop item = PrefabManager.Instance.GetPrefab(entry.prefab.Replace("JVLmock_", ""))?.GetComponent<ItemDrop>();
                if (item == null) {
                    if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"{entry.prefab} is not a valid item prefab, skipping recipe update."); }
                    return;
                }
                requirements.Add(new Piece.Requirement { m_resItem = item, m_amount = entry.amount, m_recover = entry.refundable });
            }
            piece.m_resources = requirements.ToArray();
            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"{jbuildpiece.Name} now costs {BuildRecipeString(jbuildpiece.Cfgs.UpdatedCost)}."); }
        }

        // Serialises a recipe back into the "prefab,amount,refund|..." config format.
        private static string BuildRecipeString(List<PieceCost> recipe) {
            List<string> raw_recipe = new List<string>();
            foreach (var entry in recipe) { raw_recipe.Add($"{entry.prefab},{entry.amount},{entry.refundable}"); }
            return string.Join("|", raw_recipe);
        }

        // Returns the prefab ids named by a raw recipe string. Malformed entries yield nothing; the
        // parser in PieceRecipeConfigUpdater reports those.
        private static IEnumerable<string> RecipePrefabIds(string rawrecipe) {
            foreach (String recipe_entry in rawrecipe.Split('|')) {
                String[] recipe_segments = recipe_entry.Split(',');
                if (recipe_segments.Length != 3 || recipe_segments[0].Length == 0) { continue; }
                yield return recipe_segments[0];
            }
        }

        // Bind-time reset: only ids on the known-bad list can be detected this early.
        private static void ResetRecipeConfigNamingKnownBadIds(JotunnBuildPiece jbuildpiece, string recipe_cfg_default) {
            string configured = jbuildpiece.Cfgs.PieceCost.Value;
            List<string> bad_ids = RecipePrefabIds(configured).Where(id => KnownBadPrefabIds.Contains(id)).ToList();
            if (bad_ids.Count == 0) { return; }
            Logger.LogWarning($"{jbuildpiece.Name} 'Building Cost' names item id(s) that do not exist ({string.Join(", ", bad_ids.ToArray())}); resetting it to the default: {recipe_cfg_default}");
            jbuildpiece.Cfgs.PieceCost.Value = recipe_cfg_default;
        }

        // Runtime reset: catches any unresolvable id, not just the ones we shipped. Callers must ensure
        // the prefab database is populated (PiecesReady) before invoking this.
        private static void ResetUnresolvableRecipeConfig(JotunnBuildPiece jbuildpiece) {
            string configured = jbuildpiece.Cfgs.PieceCost.Value;
            List<string> bad_ids = RecipePrefabIds(configured).Where(id => PrefabManager.Instance.GetPrefab(id) == null).ToList();
            if (bad_ids.Count == 0) { return; }

            string recipe_cfg_default = BuildRecipeString(jbuildpiece.PieceCost);
            Logger.LogWarning($"{jbuildpiece.Name} 'Building Cost' ({configured}) names item id(s) that do not exist ({string.Join(", ", bad_ids.ToArray())}); resetting it to the default: {recipe_cfg_default}");
            // Assigning an unchanged value does not raise SettingChanged, so reparse here rather than
            // relying on the handler to refresh UpdatedCost for us.
            jbuildpiece.Cfgs.PieceCost.Value = recipe_cfg_default;
            PieceRecipeConfigUpdater(jbuildpiece, recipe_cfg_default);
        }

        /// <summary>
        /// Parses a "prefab,amount,refund|..." recipe into <c>UpdatedCost</c>. False, leaving UpdatedCost as
        /// it was, when any entry is malformed - or, at runtime, names a prefab that does not exist.
        /// </summary>
        private static bool PieceRecipeConfigUpdater(JotunnBuildPiece jbuildpiece, string rawrecipe, bool during_runtime = true) {
            List<PieceCost> parsed = new List<PieceCost>();
            foreach (string recipe_entry in rawrecipe.Split('|')) {
                if (!TryParseCost(recipe_entry, during_runtime, out PieceCost cost)) { return false; }
                parsed.Add(cost);
            }
            jbuildpiece.Cfgs.UpdatedCost.Clear();
            jbuildpiece.Cfgs.UpdatedCost.AddRange(parsed);
            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Updated recipe: {BuildRecipeString(parsed)}"); }
            return true;
        }

        // One "prefab,amount,refund" entry. The prefab can only be checked at runtime, once pieces are available.
        private static bool TryParseCost(string recipe_entry, bool during_runtime, out PieceCost cost) {
            cost = null;
            string[] recipe_segments = recipe_entry.Split(',');
            if (recipe_segments.Length != 3) {
                Logger.LogWarning($"{recipe_entry} is invalid, it does not have enough segments. Proper format is: PREFABNAME,COST,REFUND_BOOL eg: Wood,8,false");
                return false;
            }
            if (recipe_segments[0].Length == 0 || recipe_segments[1].Length == 0 || recipe_segments[2].Length == 0) {
                Logger.LogWarning($"{recipe_entry} is invalid, one segment does not have enough data. Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                return false;
            }
            if (during_runtime && PrefabManager.Instance.GetPrefab(recipe_segments[0]) == null) {
                Logger.LogWarning($"{recipe_segments[0]} is an invalid prefab and does not exist.");
                return false;
            }
            if (!int.TryParse(recipe_segments[1], out int amount)) {
                Logger.LogWarning($"{recipe_entry} is invalid, the COST could not be parsed to a whole number. Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                return false;
            }
            if (!bool.TryParse(recipe_segments[2], out bool refundable)) {
                Logger.LogWarning($"{recipe_entry} is invalid, the REFUND_BOOL could not be parsed to (true/false). Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                return false;
            }
            cost = new PieceCost() { prefab = recipe_segments[0], amount = amount, refundable = refundable };
            return true;
        }
    }

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    internal static class PieceTable_UpdateAvailable_Patch {
        private static void Postfix(PieceTable __instance) {
            JotunnPiece.RemoveDisabledPieces(__instance);
        }
    }
}
