using BepInEx;
using BepInEx.Logging;
using DvergerAutomation.Common;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DvergerAutomation
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    // Soft: Epic Loot is optional, but when it is installed it has to be loaded before Awake runs here,
    // or the reflection-bound API cannot resolve its assembly yet and the provider silently never registers.
    [BepInDependency(EpicLootIntegration.EpicLootGUID, BepInDependency.DependencyFlags.SoftDependency)]
    // Soft, and for the same reason: Deposit All asks EquipmentAndQuickSlots which grid cells are its
    // slots so it never empties one, and the reflection shim can only answer once that assembly is loaded.
    [BepInDependency(EquipmentAndQuickSlotsGUID, BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class DvergerAutomation : BaseUnityPlugin
    {
        public const string PluginGUID = "MidngightsFX.DvergerAutomation";
        public const string PluginName = "DvergerAutomation";
        public const string PluginVersion = "0.8.0";
        /// <summary>EquipmentAndQuickSlots' BepInEx plugin GUID, used for the soft dependency that orders load.</summary>
        internal const string EquipmentAndQuickSlotsGUID = "randyknapp.mods.equipmentandquickslots";

        internal static ManualLogSource Log;
        internal ValConfig cfg;

        public static CustomLocalization Localization = LocalizationManager.Instance.GetLocalization();
        public static AssetBundle EmbeddedResourceBundle;
        public static Harmony HarmonyInstance { get; private set; }

        public void Awake()
        {
            Log = this.Logger;
            cfg = new ValConfig(Config);

            EmbeddedResourceBundle = AssetUtils.LoadAssetBundleFromResources("DvergerAutomation.embedded.automation", typeof(DvergerAutomation).Assembly);

            // Finish the deposit box before the piece is registered, let alone instantiated:
            // Container.Awake reads those fields to build its inventory and bind its network view.
            // LoadAsset hands back a cached instance, so this is the same object AddPieces then registers.
            AutoStore.ConfigureDepositPrefab(EmbeddedResourceBundle.LoadAsset<GameObject>("DA_Autosorter.prefab"));
            // Same deal for the hopper's single shared store. Null until the rebuilt bundle carries the
            // piece, which ConfigurePrefab reports rather than throwing.
            HopperStore.ConfigurePrefab(EmbeddedResourceBundle.LoadAsset<GameObject>("DA_ForgeHopper.prefab"));

            HarmonyInstance = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGUID);

            LocalizationLoader.AddLocalizations();
            AddPieces();
            EpicLootIntegration.Register();
        }

        public void OnDestroy()
        {
            EpicLootIntegration.Unregister();
        }

        public void AddPieces() {
            JotunnPiece.JotunnBuildPiece DA_AutSorter = new JotunnPiece.JotunnBuildPiece();
            DA_AutSorter.Name = "Dverger AutoSorter";
            DA_AutSorter.Prefab = "DA_Autosorter";
            DA_AutSorter.Sprite = "DA_Autosorter";
            DA_AutSorter.Workbench = "forge";
            DA_AutSorter.Category = "Crafting";
            DA_AutSorter.PieceCost = new List<JotunnPiece.PieceCost>() {
                { new JotunnPiece.PieceCost() { prefab = "Stone", amount = 20, refundable = true } },
                { new JotunnPiece.PieceCost() { prefab = "Bronze", amount = 8, refundable = true } },
                { new JotunnPiece.PieceCost() { prefab = "GreydwarfEye", amount = 20, refundable = true } },
                { new JotunnPiece.PieceCost() { prefab = "Ectoplasm", amount = 4, refundable = true } }
            };
            JotunnPiece.RegisterJotunnPiece(DA_AutSorter);

            JotunnPiece.JotunnBuildPiece DA_Hopper = new JotunnPiece.JotunnBuildPiece();
            DA_Hopper.Name = "Dverger Hopper";
            DA_Hopper.Prefab = "DA_ForgeHopper";
            DA_Hopper.Sprite = "DA_ForgeHopper";
            DA_Hopper.Workbench = "forge";
            DA_Hopper.Category = "Crafting";
            DA_Hopper.PieceCost = new List<JotunnPiece.PieceCost>() {
                { new JotunnPiece.PieceCost() { prefab = "Stone", amount = 30, refundable = true } },
                { new JotunnPiece.PieceCost() { prefab = "Iron", amount = 12, refundable = true } },
                { new JotunnPiece.PieceCost() { prefab = "Bronze", amount = 8, refundable = true } },
                { new JotunnPiece.PieceCost() { prefab = "Coal", amount = 20, refundable = true } }
            };
            JotunnPiece.RegisterJotunnPiece(DA_Hopper);
        }
    }
}