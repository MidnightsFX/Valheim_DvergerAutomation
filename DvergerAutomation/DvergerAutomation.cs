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
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    internal class DvergerAutomation : BaseUnityPlugin
    {
        public const string PluginGUID = "MidngightsFX.DvergerAutomation";
        public const string PluginName = "DvergerAutomation";
        public const string PluginVersion = "0.7.0";

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
        }
    }
}