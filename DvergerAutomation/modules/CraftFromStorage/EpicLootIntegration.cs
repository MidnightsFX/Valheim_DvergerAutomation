using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Epic Loot compatibility, in two halves.
    ///
    /// Outward: registers the autosorter's container pool as an Epic Loot *inventory provider*, so the
    /// enchanting table spends runestones, shards and dust out of linked chests. Epic Loot keeps its own
    /// provider-based accounting rather than going through <see cref="Inventory.CountItems"/>, so none of
    /// the Harmony patches in <see cref="CraftFromStoragePatches"/> reach it - this is the only route in.
    ///
    /// Inward: <see cref="IsProtectedItem"/> keeps enchanted gear in linked chests from being spent as
    /// plain crafting material. Vanilla consumption matches on <c>m_shared.m_name</c>, which an enchanted
    /// item shares with its mundane counterpart, so without this an upgrade recipe can quietly destroy a
    /// legendary sitting in a chest.
    ///
    /// The whole API is reflection-bound (see common/EpicLootAPI): with Epic Loot absent every call is a
    /// no-op returning false, so nothing here needs guarding beyond <see cref="Active"/>.
    /// </summary>
    internal static class EpicLootIntegration {
        /// <summary>Epic Loot's BepInEx plugin GUID, used for the soft dependency that orders load.</summary>
        internal const string EpicLootGUID = "randyknapp.mods.epicloot";

        private static readonly List<Container> Empty = new List<Container>();

        /// <summary>True once Epic Loot is present and the inventory provider is registered.</summary>
        internal static bool Active { get; private set; }

        internal static void Register() {
            if (Active) { return; }

            if (!EpicLootAPI.EpicLoot.IsLoaded()) {
                Logger.LogDebug("[Autosorter] Epic Loot not present; skipping integration.");
                return;
            }

            Active = EpicLootAPI.EpicLoot.RegisterInventoryProvider(
                DvergerAutomation.PluginGUID,
                GetItems,
                CountItem,
                RemoveItem,
                RemoveExactItem);

            if (Active) {
                Logger.LogInfo($"[Autosorter] Registered inventory provider with Epic Loot {EpicLootAPI.EpicLoot.GetPluginVersion()}.");
                HookEnchantingUI();
            } else {
                Logger.LogWarning("[Autosorter] Epic Loot is present but refused the inventory provider registration.");
            }
        }

        internal static void Unregister() {
            if (!Active) { return; }
            UnhookEnchantingUI();
            EpicLootAPI.EpicLoot.UnregisterInventoryProvider(DvergerAutomation.PluginGUID);
            Active = false;
        }

        // ---- enchanting table window ---------------------------------------------

        // Epic Loot's UI lives in its own assembly (EpicLoot_UnityLib) that this mod deliberately does
        // not reference - same reason the rest of the integration goes through the reflection-bound
        // shim in common/EpicLootAPI. Harmony resolves the type by name across loaded assemblies, so
        // the window can still be patched without a compile-time dependency, and every step below
        // fails soft: with the type, method or field renamed the enchanting window simply shows no
        // switch, while the provider that feeds it keeps working.
        private static MethodInfo enchantingShow;
        private static MethodInfo enchantingInstance;
        private static MethodInfo enchantingSourceTable;
        private static FieldInfo enchantingRoot;

        private static void HookEnchantingUI() {
            try {
                Type uiType = AccessTools.TypeByName("EpicLoot_UnityLib.EnchantingTableUI");
                if (uiType == null) {
                    Logger.LogWarning("[Autosorter] Epic Loot's EnchantingTableUI was not found; no craft-from-storage switch on the enchanting window.");
                    return;
                }

                enchantingShow = AccessTools.Method(uiType, "Show");
                enchantingInstance = AccessTools.PropertyGetter(uiType, "instance");
                enchantingSourceTable = AccessTools.PropertyGetter(uiType, "SourceTable");
                enchantingRoot = AccessTools.Field(uiType, "Root");
                if (enchantingShow == null || enchantingInstance == null || enchantingSourceTable == null || enchantingRoot == null) {
                    Logger.LogWarning("[Autosorter] Epic Loot's EnchantingTableUI does not look the way this mod expects; no craft-from-storage switch on the enchanting window.");
                    enchantingShow = null;
                    return;
                }

                DvergerAutomation.HarmonyInstance.Patch(
                    enchantingShow,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(EpicLootIntegration), nameof(OnEnchantingUIShown))));
            } catch (Exception ex) {
                enchantingShow = null;
                Logger.LogWarning($"[Autosorter] Could not hook Epic Loot's enchanting window: {ex.Message}");
            }
        }

        private static void UnhookEnchantingUI() {
            if (enchantingShow == null) { return; }
            DvergerAutomation.HarmonyInstance.Unpatch(enchantingShow, HarmonyPatchType.Postfix, DvergerAutomation.PluginGUID);
            enchantingShow = null;
        }

        /// <summary>
        /// Adds (or repaints) the craft-from-storage switch each time the enchanting window opens.
        /// Idempotent, because Epic Loot creates the window once and re-shows that same object.
        /// </summary>
        private static void OnEnchantingUIShown() {
            object ui = enchantingInstance.Invoke(null, null);
            if (ui == null) { return; }
            // Root is the window's 1120x700 "Panel" object, not the full-screen wrapper - the scrim
            // beside it is toggled separately.
            CraftFromStorageToggle.AttachToEnchantingPanel(enchantingRoot.GetValue(ui) as GameObject);
        }

        /// <summary>
        /// Rebuilds an open enchanting window after the local craft-from-storage switch was flipped.
        /// Epic Loot's panels list the items they can act on only when something reselects, so without
        /// this the table keeps offering chest items the provider has just stopped serving - and a
        /// selection already made on one of them would be spent out of a pool that is now off limits.
        /// Re-running Show is the same path Epic Loot uses when the window opens: it refreshes the
        /// table and deselects every panel, which is exactly the cleanup needed here. No-op while the
        /// window is closed, so an F1-menu change costs nothing.
        /// </summary>
        internal static void RefreshEnchantingWindow() {
            if (enchantingShow == null) { return; }
            try {
                object ui = enchantingInstance.Invoke(null, null);
                if (ui == null) { return; }
                GameObject root = enchantingRoot.GetValue(ui) as GameObject;
                if (root == null || !root.activeSelf) { return; }
                object table = enchantingSourceTable.Invoke(ui, null);
                if (table == null) { return; }
                enchantingShow.Invoke(null, new[] { table });
            } catch (Exception ex) {
                Logger.LogWarning($"[Autosorter] Could not refresh Epic Loot's enchanting window: {ex.Message}");
            }
        }

        /// <summary>
        /// True when Epic Loot considers this item enchanted.
        ///
        /// Deliberately not gated on <see cref="Active"/>, unlike <see cref="IsProtectedItem"/>: whether
        /// an item is magical is a fact about the item, not about whether our inventory provider was
        /// accepted. The auto-store switch has to hold even when registration was refused, or "do not
        /// sort magic items" would silently stop applying.
        ///
        /// Ordered so the common case costs nothing: Epic Loot only enchants equipment, which never
        /// stacks in Valheim, so materials stop at the stack-size check before reaching the reflection
        /// call. With Epic Loot absent the shim resolves nothing and returns false.
        /// </summary>
        internal static bool IsMagicItem(ItemDrop.ItemData item) {
            if (item == null || item.m_shared == null) { return false; }
            if (item.m_shared.m_maxStackSize > 1) { return false; }
            return EpicLootAPI.EpicLoot.IsMagicItem(item);
        }

        /// <summary>
        /// True when this item must not be consumed as a by-name crafting material. Additionally gated on
        /// <see cref="Active"/> so that with Epic Loot absent the per-frame aggregate in
        /// <see cref="ContainerNetwork"/>, which walks every item in every linked chest, stops immediately.
        /// </summary>
        internal static bool IsProtectedItem(ItemDrop.ItemData item) {
            return Active && IsMagicItem(item);
        }

        /// <summary>
        /// Drops the frame memo behind <see cref="GetItems"/>. Must be called whenever a linked chest's
        /// contents change outside a rescan, or the enchanting table is served the pre-change item list
        /// for the rest of the frame.
        /// </summary>
        internal static void InvalidateItemCache() {
            itemsFrame = -1;
        }

        // ---- provider callbacks -------------------------------------------------

        // Same pool the crafting and building paths use: the station's chests when the player is at a
        // station (Epic Loot's enchanting table is one), otherwise every hub whose radius covers them.
        private static List<Container> Pool() {
            Player player = Player.m_localPlayer;
            if (player == null) { return Empty; }
            CraftingStation station = player.GetCurrentCraftingStation();
            return station != null
                ? ContainerNetwork.GetContainersForStation(station)
                : ContainerNetwork.GetContainersNearPoint(player.transform.position);
        }

        // Live instances, deliberately including magic items: this list is what the table may act *on*,
        // and Epic Loot addresses those by reference through RemoveExactItem. IsProtectedItem only guards
        // the by-name material accounting below.
        //
        // Frame-memoized for the same reason ContainerNetwork memoizes its aggregate: the enchanting UI
        // asks once per frame while it is open, and rebuilding this across a few hundred linked chests
        // every frame is exactly the stall that memo exists to avoid. Reusing the list is safe because
        // Epic Loot copies the contents out rather than holding the reference.
        private static readonly List<ItemDrop.ItemData> ItemsResult = new List<ItemDrop.ItemData>();
        private static int itemsFrame = -1;

        private static List<ItemDrop.ItemData> GetItems() {
            if (itemsFrame == Time.frameCount) { return ItemsResult; }

            ItemsResult.Clear();
            foreach (Container container in Pool()) {
                if (container == null || CraftFromStoragePatches.IsBusy(container)) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }
                ItemsResult.AddRange(inv.GetAllItems());
            }
            itemsFrame = Time.frameCount;
            return ItemsResult;
        }

        private static int CountItem(string name) {
            return string.IsNullOrEmpty(name) ? 0 : ContainerNetwork.CountInPool(Pool(), name);
        }

        private static int RemoveItem(string name, int amount) {
            if (string.IsNullOrEmpty(name) || amount <= 0) { return 0; }
            return CraftFromStoragePatches.RemoveFromContainers(Pool(), name, amount, -1);
        }

        // Reference match, per Epic Loot's contract: magic data lives on the instance, so finding the
        // stack by name would consume the wrong enchanted item.
        private static int RemoveExactItem(ItemDrop.ItemData item, int amount) {
            if (item == null || amount <= 0) { return 0; }
            foreach (Container container in Pool()) {
                if (container == null || CraftFromStoragePatches.IsBusy(container)) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null || !inv.ContainsItem(item)) { continue; }

                CraftFromStoragePatches.ClaimOwnership(container);
                int take = Mathf.Min(item.m_stack, amount);
                inv.RemoveItem(item, take);
                return take;
            }
            return 0;
        }
    }
}
