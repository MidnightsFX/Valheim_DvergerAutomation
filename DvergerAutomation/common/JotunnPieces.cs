using BepInEx.Configuration;
using DvergerAutomation;
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
                IEnumerable<GameObject> scene_parents = Resources.FindObjectsOfTypeAll<GameObject>().Where(obj => obj.name == jbuildpiece.Prefab);
                if (ValConfig.EnableDebugMode.Value) { Logger.LogInfo($"Found {jbuildpiece.Prefab} scene parent objects: {scene_parents.Count()}"); }
                GameObject scenePrefab = scene_parents.FirstOrDefault();
                if (scenePrefab == null) {
                    Logger.LogWarning($"Could not find scene prefab '{jbuildpiece.Prefab}' after prefab registration; skipping in-place setup for {jbuildpiece.Name}.");
                    return;
                }
                jbuildpiece.Objs.ScenePrefab = scenePrefab;
                PiecesReady = true;
                // Bring the current config (default or server-synced) into effect a single time now that
                // every mod prefab is resolvable. This also covers values that arrived early via config sync.
                ApplyWorkbench(jbuildpiece);
                ApplyCategory(jbuildpiece);
                // The prefab database is populated by now, so a config naming an item that does not
                // exist can finally be told apart from a valid customisation and reset.
                ResetUnresolvableRecipeConfig(jbuildpiece);
                ApplyRecipe(jbuildpiece);
            }
            PrefabManager.OnPrefabsRegistered += ResolveAndApplyScenePrefab;
        }

        private static void InitialPieceSetup(JotunnBuildPiece jbuildpiece) {
            // Set where the recipe can be crafted. Gated on PiecesReady so an early config sync / file
            // reload (both fire before ZNetScene.Awake) can't run before the scene prefab is resolved.
            void RequiredBench_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePrefab == null) { return; }
                ApplyWorkbench(jbuildpiece);
            }
            jbuildpiece.Cfgs.Workbench = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Workbench", jbuildpiece.Workbench, $"The table required to allow building this piece, eg: 'forge', 'piece_workbench', 'blackforge', 'piece_artisanstation'.");
            jbuildpiece.Cfgs.Workbench.SettingChanged += RequiredBench_SettingChanged;

            // Crafting cost change
            void BuildRecipeChanged_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePrefab == null) { return; }
                if (sender.GetType() == typeof(ConfigEntry<string>)) {
                    ConfigEntry<string> sendEntry = (ConfigEntry<string>)sender;
                    if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Recieved new piece config {sendEntry.Value}"); }
                    // return if its an invalid change
                    if (PieceRecipeConfigUpdater(jbuildpiece, sendEntry.Value) == false) { return; }
                }
                ApplyRecipe(jbuildpiece);
            }

            // Setup enable/disable
            jbuildpiece.Cfgs.Enabled = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Enabled", jbuildpiece.Enabled, $"Enable/Disable the {jbuildpiece.Name}.");
            jbuildpiece.Cfgs.Enabled.SettingChanged += BuildRecipeChanged_SettingChanged;
            // Setup piece category
            jbuildpiece.Cfgs.PieceCategory = ValConfig.BindServerConfig($"{jbuildpiece.Name}", $"Piece Category", jbuildpiece.Category, "Piece category for building.", PieceCategories.GetAcceptableValueList());
            void CraftingCategory_SettingChanged(object sender, EventArgs e) {
                if (!PiecesReady || jbuildpiece.Objs.ScenePrefab == null) { return; }
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

        // Applies the configured crafting station to the in-scene piece. Callers must ensure the scene
        // prefab is resolved (PiecesReady) before invoking this.
        private static void ApplyWorkbench(JotunnBuildPiece jbuildpiece) {
            // RequiresWorkbench is currently never bound; treat a missing entry as "workbench required".
            bool requiresWorkbench = jbuildpiece.Cfgs.RequiresWorkbench?.Value ?? true;
            if (requiresWorkbench == false || string.IsNullOrEmpty(jbuildpiece.Cfgs.Workbench.Value) || jbuildpiece.Cfgs.Workbench.Value.ToLower() == "none") {
                Logger.LogInfo("Setting required crafting station to none.");
                jbuildpiece.Objs.ScenePrefab.GetComponent<Piece>().m_craftingStation = null;
                return;
            }

            CraftingStation craftable_at = PrefabManager.Instance.GetPrefab(jbuildpiece.Cfgs.Workbench.Value)?.GetComponent<CraftingStation>();
            if (craftable_at == null) {
                Logger.LogWarning($"Required crafting station does not exist or does not have a crafting station componet, check your prefab name ({jbuildpiece.Cfgs.Workbench.Value}).");
                return;
            }

            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Setting crafting station to {jbuildpiece.Cfgs.Workbench.Value}."); }
            jbuildpiece.Objs.ScenePrefab.GetComponent<Piece>().m_craftingStation = craftable_at;
        }

        // Applies the configured build category to the in-scene piece.
        private static void ApplyCategory(JotunnBuildPiece jbuildpiece) {
            Piece.PieceCategory? category = PieceManager.Instance.GetPieceCategory(jbuildpiece.Cfgs.PieceCategory.Value);
            if (category == null) {
                category = PieceManager.Instance.AddPieceCategory(jbuildpiece.Cfgs.PieceCategory.Value);
            }
            jbuildpiece.Objs.ScenePrefab.GetComponent<Piece>().m_category = (PieceCategory)category;
        }

        // Resolves the recipe in UpdatedCost against the live prefab database and applies it to the
        // in-scene piece. Bails out (leaving the existing recipe intact) if any requirement prefab is not
        // yet resolvable, so it is safe even if a dependency mod registered its items late.
        private static void ApplyRecipe(JotunnBuildPiece jbuildpiece) {
            List<RequirementConfig> recipe = new List<RequirementConfig>();
            if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo("Validating and building requirementsConfig"); }
            foreach (var entry in jbuildpiece.Cfgs.UpdatedCost) {
                if (PrefabManager.Instance.GetPrefab(entry.prefab) == null) {
                    if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"{entry.prefab} is not a valid prefab, skipping recipe update."); }
                    return;
                }
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Checking entry {entry.prefab} amount:{entry.amount} refund?:{entry.refundable}"); }
                recipe.Add(new RequirementConfig { Item = entry.prefab, Amount = entry.amount, Recover = entry.refundable });
            }
            if (jbuildpiece.Cfgs.Enabled.Value) {
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo("Updating Piece."); }
                List<Piece.Requirement> newRequirements = new List<Piece.Requirement>();
                foreach (var recipe_entry in recipe) {
                    Piece.Requirement piece_req = new Piece.Requirement();
                    piece_req.m_resItem = PrefabManager.Instance.GetPrefab(recipe_entry.Item.Replace("JVLmock_", ""))?.GetComponent<ItemDrop>();
                    piece_req.m_amount = recipe_entry.Amount;
                    piece_req.m_recover = recipe_entry.Recover;
                    newRequirements.Add(piece_req);
                }
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"Fixed mock requirements {newRequirements.Count}."); }
                jbuildpiece.Objs.ScenePrefab.GetComponent<Piece>().m_resources = newRequirements.ToArray();
                if (ValConfig.EnableDebugMode.Value == true) { Logger.LogInfo($"New requirements set {jbuildpiece.Objs.ScenePrefab.GetComponent<Piece>().m_resources}."); }
            } else {
                // Set this piece not craftable
                jbuildpiece.Objs.ScenePrefab.GetComponent<Piece>().m_enabled = false;
            }
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

        private static bool PieceRecipeConfigUpdater(JotunnBuildPiece jbuildpiece, string rawrecipe, bool during_runtime = true) {
            String[] RawRecipeEntries = rawrecipe.Split('|');
            // Logger.LogInfo($"{RawRecipeEntries.Length} {string.Join(", ", RawRecipeEntries)}");
            List<PieceCost> updated_pieceRecipe = new List<PieceCost>();
            // we only clear out the default recipe if there is recipe data provided, otherwise we will continue to use the default recipe
            // TODO: Add a sanity check to ensure that recipe formatting is correct
            if (RawRecipeEntries.Length >= 1) {
                foreach (String recipe_entry in RawRecipeEntries) {
                    //Logger.LogInfo($"{recipe_entry}");
                    String[] recipe_segments = recipe_entry.Split(',');
                    if (recipe_segments.Length != 3) {
                        Logger.LogWarning($"{recipe_entry} is invalid, it does not have enough segments. Proper format is: PREFABNAME,COST,REFUND_BOOL eg: Wood,8,false");
                        return false;
                    }
                    if (ValConfig.EnableDebugMode.Value == true) {
                        String split_segments = "";
                        foreach (String segment in recipe_segments) {
                            split_segments += $" {segment}";
                        }
                        //Logger.LogInfo($"recipe segments: {split_segments} from {recipe_entry}");
                    }
                    // Add a sanity check to ensure the prefab we are trying to use exists
                    // This can only happen during runtime after pieces are available otherwise it will cause errors
                    if (during_runtime) {
                        if (PrefabManager.Instance.GetPrefab(recipe_segments[0]) == null) {
                            Logger.LogWarning($"{recipe_segments[0]} is an invalid prefab and does not exist.");
                            return false;
                        }
                    }
                    if (recipe_segments[0].Length == 0 || recipe_segments[1].Length == 0 || recipe_segments[2].Length == 0) {
                        Logger.LogWarning($"{recipe_entry} is invalid, one segment does not have enough data. Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                        return false;
                    }
                    bool refund_flag_parse;
                    if (bool.TryParse(recipe_segments[2], out refund_flag_parse) == false) {
                        Logger.LogWarning($"{recipe_entry} is invalid, the REFUND_BOOL could not be parsed to (true/false). Proper format is: PREFABNAME,CRAFT_COST,REFUND_BOOL eg: Wood,8,false");
                        return false;
                    }

                    if (ValConfig.EnableDebugMode.Value == true) {
                        Logger.LogInfo($"prefab: {recipe_segments[0]} c:{recipe_segments[1]} u:{recipe_segments[2]}");
                    }
                    updated_pieceRecipe.Add(new PieceCost() { prefab = recipe_segments[0], amount = Int32.Parse(recipe_segments[1]), refundable = refund_flag_parse });
                }
                //Logger.LogInfo("Done parsing recipe");
                jbuildpiece.Cfgs.UpdatedCost.Clear();
                foreach (var entry in updated_pieceRecipe) { jbuildpiece.Cfgs.UpdatedCost.Add(entry); }
                //Logger.LogInfo("Set UpdatedRecipe");
                if (ValConfig.EnableDebugMode.Value == true) {
                    String recipe_string = "";
                    foreach (var entry in updated_pieceRecipe) {
                        recipe_string += $" {entry.prefab} c:{entry.amount} r:{entry.refundable}";
                    }
                    Logger.LogInfo($"Updated recipe:{recipe_string}");
                }
                return true;
            } else {
                Logger.LogWarning($"Invalid recipe: {rawrecipe}. defaults will be used. Check your prefab names.");
                jbuildpiece.Cfgs.UpdatedCost = jbuildpiece.PieceCost;

            }
            return false;
        }
    }
}
