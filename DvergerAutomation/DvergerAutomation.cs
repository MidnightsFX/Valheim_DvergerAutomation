using BepInEx;
using BepInEx.Logging;
using DvergerAutomation.Common;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System;
using System.Reflection;
using UnityEngine;

namespace DvergerAutomation
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    // Soft: Epic Loot is optional, but when it is installed it has to be loaded before Awake runs here,
    // or the reflection-bound API cannot resolve its assembly yet and the provider silently never registers.
    [BepInDependency(EpicLootIntegration.EpicLootGUID, BepInDependency.DependencyFlags.SoftDependency)]
    // Soft, and for the same reason: Deposit Selected asks EquipmentAndQuickSlots which grid cells are its
    // slots so it only empties one the player allowed, and the reflection shim can only answer once that
    // assembly is loaded.
    // AzuExtendedPlayerInventory and ExtraSlots are deliberately not declared: they are only asked when the
    // button is pressed, long after every plugin has loaded, and declaring AzuEPI opts this assembly into its
    // load-time rewriter, which reloads any dependent plugin from bytes to redirect its API stubs.
    [BepInDependency(EquipmentAndQuickSlotsGUID, BepInDependency.DependencyFlags.SoftDependency)]
    // Soft, so that when MultiUserChest is installed it has loaded by the time Awake here asks whether
    // it is there.
    [BepInDependency(MultiUserChestIntegration.MultiUserChestGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class DvergerAutomation : BaseUnityPlugin
    {
        public const string PluginGUID = "MidngightsFX.DvergerAutomation";
        public const string PluginName = "DvergerAutomation";
        public const string PluginVersion = "0.12.4";
        /// <summary>EquipmentAndQuickSlots' BepInEx plugin GUID, used for the soft dependency that orders load.</summary>
        internal const string EquipmentAndQuickSlotsGUID = "randyknapp.mods.equipmentandquickslots";

        internal static ManualLogSource Log;
        internal ValConfig cfg;

        // Set in Awake, not here: a static initialiser can run before BepInEx has registered this plugin, and Jotunn
        // then attributes the whole mod - its RPC names included - to Jotunn itself for the rest of the session.
        public static CustomLocalization Localization;
        public static AssetBundle EmbeddedResourceBundle;
        public static Harmony HarmonyInstance { get; private set; }

        public void Awake()
        {
            Localization = LocalizationManager.Instance.GetLocalization();
            Log = this.Logger;
            cfg = new ValConfig(Config);

            EmbeddedResourceBundle = AssetUtils.LoadAssetBundleFromResources("DvergerAutomation.embedded.automation", typeof(DvergerAutomation).Assembly);

            // Finish the deposit box before the piece is registered, let alone instantiated:
            // Container.Awake reads those fields to build its inventory and bind its network view.
            // LoadAsset hands back a cached instance, so this is the same object AddPieces then registers.
            GameObject autosorter = EmbeddedResourceBundle.LoadAsset<GameObject>("DA_Autosorter.prefab");
            AutoStore.ConfigureDepositPrefab(autosorter);
            MakeWeatherproof(autosorter);
            // Each null until the bundle carries the piece, which Setup reports rather than throwing.
            GameObject hopper = EmbeddedResourceBundle.LoadAsset<GameObject>("DA_ForgeHopper.prefab");
            MachineStore.Setup(hopper, "Hopper_Body/HopperStore", "$DA_hopper_store_name", ValConfig.HopperStoreRows,
                HopperHub.MinStoreRows, HopperHub.MaxStoreRows, StorageRegistry.Kind.HopperStore);
            MakeWeatherproof(hopper);
            // Both null until the bundle is rebuilt with the Seeking Table: the piece then skips itself, and
            // the line falls back to the vanilla workbench one.
            MakeWeatherproof(EmbeddedResourceBundle.LoadAsset<GameObject>("DA_SeekingTable.prefab"));
            GameObject grinder = EmbeddedResourceBundle.LoadAsset<GameObject>("DA_Grinder.prefab");
            GrinderStore.ConfigurePrefab(grinder);
            MakeWeatherproof(grinder);
            // The Grinder's hopper is a networked object of its own that the Grinder spawns, so the game has to
            // know its prefab to rebuild it from a saved world.
            GameObject grinderFeed = EmbeddedResourceBundle.LoadAsset<GameObject>($"{GrinderFeed.PrefabName}.prefab");
            if (grinderFeed != null) {
                GrinderStore.ConfigureFeedPrefab(grinderFeed);
                PrefabManager.Instance.AddPrefab(new CustomPrefab(grinderFeed, fixReference: true));
            }
            GameObject lighter = EmbeddedResourceBundle.LoadAsset<GameObject>("DA_Lighter.prefab");
            MachineStore.Setup(lighter, "Lighter_Body/LighterStore", "$DA_lighter_store_name", ValConfig.LighterStoreRows,
                LighterHub.MinStoreRows, LighterHub.MaxStoreRows, StorageRegistry.Kind.LighterTank);
            MakeWeatherproof(lighter);
            RetrievalBeam.Register(EmbeddedResourceBundle.LoadAsset<GameObject>($"{RetrievalBeam.PrefabName}.prefab"));

            ApplyPatches();

            LocalizationLoader.AddLocalizations();
            AddPieces();
            EpicLootIntegration.Register();
        }

        public void OnDestroy()
        {
            EpicLootIntegration.Unregister();
        }

        // What Harmony.PatchAll does, one class at a time, so the shared-chest patches can be left out
        // when MultiUserChest is installed: it does that job itself, and both mods rewrite the same call
        // in InventoryGui.UpdateContainer. The BepInEx pack's Harmony has no patch categories to do this
        // with. Everything else those patches do is behind SharedChests.Active, which follows the
        // server's setting at runtime.
        private static void ApplyPatches() {
            HarmonyInstance = new Harmony(PluginGUID);
            bool sharedChests = !MultiUserChestIntegration.Present;
            if (!sharedChests) {
                Log.LogInfo("MultiUserChest is installed: shared chests are left to it.");
            }
            foreach (Type type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly())) {
                if (!sharedChests && type.GetCustomAttribute<SharedChestPatchAttribute>() != null) { continue; }
                HarmonyInstance.CreateClassProcessor(type).Patch();
            }
        }

        // Despite the name, WearNTear.m_noRoofWear = true means "wears when it has NO roof": uncovered and
        // wet (rain, or standing in water) it loses 5 health a minute down to half. Vanilla turns it off for
        // the smelter, blast furnace, kiln, windmill and every chest, which these machines sit beside. Set
        // here as well as in the Unity prefabs so an older bundle still gets it.
        private static void MakeWeatherproof(GameObject prefab) {
            WearNTear wnt = prefab != null ? prefab.GetComponent<WearNTear>() : null;
            if (wnt != null) { wnt.m_noRoofWear = false; }
        }

        public void AddPieces() {
            AddPiece("Dverger AutoSorter", "DA_Autosorter", "forge",
                ("Stone", 20), ("Bronze", 8), ("GreydwarfEye", 20), ("Ectoplasm", 4));
            AddPiece("Dverger Hopper", "DA_ForgeHopper", "forge",
                ("Stone", 30), ("Iron", 12), ("Bronze", 8), ("Coal", 20));
            AddPiece("Dverger Seeking Table", "DA_SeekingTable", "blackforge",
                ("BlackMarble", 20), ("YggdrasilWood", 10), ("Copper", 10), ("Eitr", 10));
            AddPiece("Dverger Grinder", "DA_Grinder", "blackforge",
                ("Grausten", 20), ("FlametalNew", 10), ("Blackwood", 10), ("CharredCogwheel", 2));
            AddPiece("Dverger Lighter", "DA_Lighter", "forge",
                ("Silver", 10), ("Wood", 5), ("Coal", 40), ("Iron", 5));
        }

        // A Hammer piece in the Crafting category whose prefab and icon share a name, every material refunded.
        private static void AddPiece(string name, string prefab, string workbench, params (string prefab, int amount)[] cost) {
            JotunnPiece.JotunnBuildPiece piece = new JotunnPiece.JotunnBuildPiece {
                Name = name,
                Prefab = prefab,
                Sprite = prefab,
                Workbench = workbench,
                Category = "Crafting",
            };
            foreach ((string material, int amount) in cost) {
                piece.PieceCost.Add(new JotunnPiece.PieceCost { prefab = material, amount = amount, refundable = true });
            }
            JotunnPiece.RegisterJotunnPiece(piece);
        }
    }
}