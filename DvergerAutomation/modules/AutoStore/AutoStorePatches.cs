using HarmonyLib;

namespace DvergerAutomation {
    /// <summary>
    /// Runs the auto-store pass when the player closes the AutoSorter's deposit box.
    ///
    /// Both of vanilla's close paths end with <c>m_currentContainer.SetInUse(false); m_currentContainer =
    /// null;</c>, so they are patched as prefixes - by the time either returns there is no longer any way
    /// to tell which container was open.
    /// </summary>
    internal static class AutoStorePatches {
        internal static void OnContainerClosing(InventoryGui gui) {
            if (gui == null) { return; }

            Container container = gui.m_currentContainer;
            if (container == null) { return; }

            AutomationHub hub = container.GetComponentInParent<AutomationHub>();
            // Any other chest, including a different sorter's box, is none of our business.
            if (hub == null || hub.DepositBox != container) { return; }

            try {
                // Off, or a dormant hub - Scan() deliberately links nothing, so sorting would just hand
                // everything straight back. Either way the box sits there as a plain chest.
                bool dormant = ValConfig.RequireCores.Value && hub.CoreCount == 0;
                if (ValConfig.AutoStoreEnabled.Value && !dormant) {
                    AutoStore.Sort(hub, Player.m_localPlayer);
                }
            } catch (System.Exception ex) {
                // This runs inside InventoryGui.Hide; letting it escape would abort the close and leave
                // the player staring at a half-shut inventory.
                Logger.LogError($"AutoStore: sorting the deposit box failed: {ex}");
            }

            // Picks up a size change that was held back while the panel was open, and lets a box that grew
            // to fit its contents shrink back now that the sort has emptied it.
            AutoStore.ApplySize(container);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class InventoryGui_Hide_Patch {
        // Hide() can bail early with m_currentContainer still set, so the sort has to be a no-op on an
        // empty box - which it is.
        private static void Prefix(InventoryGui __instance) {
            AutoStorePatches.OnContainerClosing(__instance);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), "CloseContainer")]
    internal static class InventoryGui_CloseContainer_Patch {
        private static void Prefix(InventoryGui __instance) {
            AutoStorePatches.OnContainerClosing(__instance);
        }
    }

    /// <summary>
    /// Keeps a configurable-width deposit box from deleting items when it loads.
    ///
    /// <c>Inventory.Load</c> re-adds every item at its saved grid position, and the positional
    /// <c>Inventory.AddItem</c> rejects <c>x &gt;= m_width</c> outright (<c>skipValidPositionCheck</c> only
    /// relaxes the row check). The caller still returns true, so an item saved in a column the box no
    /// longer has - after an admin narrows it, or on a client whose config has not synced yet - is
    /// dropped without a trace, and gone for good the next time the owner saves. Rows are safe:
    /// <c>Container.UpdateRows</c> grows the grid to fit.
    ///
    /// So every deposit box loads at the widest size the config allows, then shrinks back to the
    /// configured width, or to whatever its contents need if that is wider.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Load")]
    internal static class Container_Load_Patch {
        private static void Prefix(Container __instance, out int __state) {
            __state = -1;
            if (!AutoStore.IsDepositBox(__instance)) { return; }
            Inventory inv = __instance.GetInventory();
            if (inv == null) { return; }
            __state = inv.m_width;
            inv.m_width = AutoStore.MaxWidth;
        }

        private static void Postfix(Container __instance, bool __result, int __state) {
            if (__state < 0) { return; }
            if (__result) {
                AutoStore.ApplySize(__instance);
            } else {
                // Load bailed before touching the inventory (nothing new, or the box is open): put the width
                // back exactly as it was.
                __instance.GetInventory().m_width = __state;
            }
        }
    }
}
