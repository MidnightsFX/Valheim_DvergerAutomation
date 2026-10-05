using HarmonyLib;

namespace DvergerAutomation {
    /// <summary>
    /// Refuses every write to a guest's copy of a shared chest.
    ///
    /// That copy is replaced wholesale each time the chest reloads, and it is never saved: only the
    /// owner's is. So a write to it does not stick, and whatever was done to the other inventory in the
    /// same move does. An item moved "into" the chest is gone from the player's pack and never reaches
    /// the chest; one moved "out" is in the pack and comes back in the chest on the next reload.
    ///
    /// The moves the inventory screen makes never get this far - they are turned into requests first
    /// (see SharedChestIntentPatches). What is left is anything else: another mod's quick-stack or sort
    /// button, working directly on the chest the screen has open. Those are stopped at the inventory
    /// itself, on both halves of a move, so they do nothing for a guest rather than lose or duplicate
    /// something. They still work for whoever owns the chest.
    ///
    /// The one writer allowed is the chest reloading itself.
    /// </summary>
    internal static class SharedChestGuard {
        /// <summary>
        /// Whether an add must be refused: it targets the guest's copy, or it would put one of that
        /// copy's own items somewhere else - the first half of taking it out.
        /// </summary>
        internal static bool BlocksAdd(Inventory target, ItemDrop.ItemData item) {
            InventoryGui gui = InventoryGui.m_instance;
            if ((object)gui == null) { return false; }
            Container chest = gui.m_currentContainer;
            if ((object)chest == null) { return false; }
            Inventory open = chest.m_inventory;
            if (open == null) { return false; }

            if (target == open) { return !chest.m_loading && SharedChests.GuestContainer() == chest; }
            return item != null && open.m_inventory.Contains(item) && SharedChests.GuestContainer() == chest;
        }

        internal static bool BlocksRemove(Inventory target) {
            return SharedChests.IsGuestInventory(target);
        }
    }

    // ---- adds: the three that take an item instance. Every other AddItem overload ends in one of these.

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(int), typeof(int), typeof(bool) })]
    internal static class Inventory_AddItemAt_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result) {
            if (!SharedChestGuard.BlocksAdd(__instance, item)) { return true; }
            __result = false;
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })]
    internal static class Inventory_AddItem_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result) {
            if (!SharedChestGuard.BlocksAdd(__instance, item)) { return true; }
            __result = false;
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData), typeof(Vector2i) })]
    internal static class Inventory_AddItemPos_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ItemDrop.ItemData item, ref bool __result) {
            if (!SharedChestGuard.BlocksAdd(__instance, item)) { return true; }
            __result = false;
            return false;
        }
    }

    // ---- removes ------------------------------------------------------------------

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(int) })]
    internal static class Inventory_RemoveItemIndex_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ref bool __result) {
            if (!SharedChestGuard.BlocksRemove(__instance)) { return true; }
            __result = false;
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveOneItem))]
    internal static class Inventory_RemoveOneItem_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ref bool __result) {
            if (!SharedChestGuard.BlocksRemove(__instance)) { return true; }
            __result = false;
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData) })]
    internal static class Inventory_RemoveItem_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ref bool __result) {
            if (!SharedChestGuard.BlocksRemove(__instance)) { return true; }
            __result = false;
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class Inventory_RemoveItemAmount_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance, ref bool __result) {
            if (!SharedChestGuard.BlocksRemove(__instance)) { return true; }
            __result = false;
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })]
    internal static class Inventory_RemoveItemName_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance) {
            return !SharedChestGuard.BlocksRemove(__instance);
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.RemoveAll))]
    internal static class Inventory_RemoveAll_SharedChestGuard_Patch {
        private static bool Prefix(Inventory __instance) {
            return !SharedChestGuard.BlocksRemove(__instance);
        }
    }
}
