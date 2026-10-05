using HarmonyLib;

namespace DvergerAutomation {
    /// <summary>
    /// Shows a guest their moves in a shared chest straight away, rather than a round trip later.
    ///
    /// A guest's copy of the chest only changes when it reloads from the owner's save, so without this
    /// an item dragged into the chest would vanish from the pack and appear in the chest a moment after,
    /// and one taken out would sit there until the answer came. Instead each grid is drawn from a
    /// throwaway copy of its inventory with the outstanding requests played onto it: the chest as it
    /// will be once the owner has done what was asked, the pack as it will be once what is on its way
    /// has arrived. Nothing real is touched; when the answer and the reload come in, the copy and the
    /// real thing simply agree.
    ///
    /// The drawing itself is left to the game. For the length of <c>InventoryGrid.UpdateGui</c> - which
    /// draws and does nothing else - the grid is handed the copy in place of its inventory, and it is
    /// handed the real one back before anything can click on it. So a predicted item gets its icon,
    /// stack count, durability bar, tooltip and whatever another mod paints onto a slot, all by the
    /// same code that draws a real one.
    /// </summary>
    internal static class SharedChestPreview {
        /// <summary>
        /// The chest as it will be once the owner has carried out everything this client has asked of
        /// it. Also what the next request is judged against, since that is the chest the owner will see
        /// when it arrives. A plain copy when nothing is outstanding.
        /// </summary>
        internal static Inventory Chest(Container chest) {
            Inventory copy = Copy(chest.GetInventory());
            foreach (SharedChestRequests.Pending pending in SharedChestRequests.All) {
                if (pending.Chest != chest) { continue; }
                switch (pending.Kind) {
                    case SharedChestRequests.Kind.Add:
                        SharedChestOps.ApplyAdd(copy, pending.Item, pending.ChestSlot, pending.WholeStack, live: false, out _);
                        break;
                    case SharedChestRequests.Kind.Remove:
                        SharedChestOps.ApplyRemove(copy, pending.ChestSlot, pending.Item.m_stack, pending.Swap, live: false);
                        break;
                    case SharedChestRequests.Kind.Move:
                        SharedChestOps.ApplyMove(copy, pending.ChestSlot, pending.OtherSlot, pending.Item.m_stack);
                        break;
                }
            }
            return copy;
        }

        /// <summary>
        /// The player's side as it will be once what is on its way out of the chest has arrived. Only
        /// requests still waiting on an answer count: the answer is what puts the item in for real.
        /// </summary>
        internal static Inventory Other(Inventory inventory) {
            Inventory copy = Copy(inventory);
            foreach (SharedChestRequests.Pending pending in SharedChestRequests.All) {
                if (pending.Answered || pending.Other != inventory) { continue; }
                if (pending.Kind == SharedChestRequests.Kind.Remove) {
                    if (pending.Destination == SharedChestRequests.Destination.Slot) {
                        Arrive(copy, pending.Item, pending.OtherSlot);
                    } else if (pending.Destination == SharedChestRequests.Destination.Any) {
                        AutoStore.AddMeasured(copy, pending.Item, pending.Item.m_stack);
                    }
                } else if (pending.Kind == SharedChestRequests.Kind.Add && pending.Swap != null) {
                    Arrive(copy, pending.Swap, pending.OtherSlot);
                }
            }
            return copy;
        }

        /// <summary>The copy a grid should be drawn from, or null when its inventory has nothing outstanding.</summary>
        internal static Inventory For(Inventory inventory) {
            if (inventory == null) { return null; }
            foreach (SharedChestRequests.Pending pending in SharedChestRequests.All) {
                if (pending.Chest != null && pending.Chest.m_inventory == inventory) { return Chest(pending.Chest); }
            }
            foreach (SharedChestRequests.Pending pending in SharedChestRequests.All) {
                if (!pending.Answered && pending.Other == inventory) { return Other(inventory); }
            }
            return null;
        }

        // Into the slot it is headed for, and whatever does not fit there anywhere else - the same order
        // the real item is delivered in.
        private static void Arrive(Inventory copy, ItemDrop.ItemData item, Vector2i slot) {
            int left = item.m_stack - SharedChestOps.ApplyAdd(copy, item, slot, wholeStack: false, live: false, out _);
            if (left <= 0) { return; }
            ItemDrop.ItemData rest = item.Clone();
            rest.m_stack = left;
            AutoStore.AddMeasured(copy, rest, left);
        }

        private static Inventory Copy(Inventory inventory) {
            Inventory copy = new Inventory(inventory.m_name, inventory.m_bkg, inventory.GetWidth(), inventory.GetHeight());
            foreach (ItemDrop.ItemData item in inventory.GetAllItems()) { copy.m_inventory.Add(item.Clone()); }
            return copy;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.UpdateGui))]
    internal static class InventoryGrid_UpdateGui_SharedChestPreview_Patch {
        // The grid whose inventory is swapped out right now, and the inventory to give it back. UpdateGui
        // does not call itself, so one of each is enough.
        private static InventoryGrid swapped;
        private static Inventory real;

        // First, so that any other mod drawing onto the slots from its own patch sees the same inventory
        // the game is about to draw.
        [HarmonyPriority(Priority.First)]
        private static void Prefix(InventoryGrid __instance, ref ItemDrop.ItemData dragItem) {
            if (SharedChestRequests.All.Count == 0) { return; }
            Inventory shown = SharedChestPreview.For(__instance.m_inventory);
            if (shown == null) { return; }

            // The game greys the slot of the item on the cursor by comparing instances, and the copy has
            // its own. Point it at the copy's item in the same slot.
            if (dragItem != null && __instance.m_inventory.ContainsItem(dragItem)) {
                ItemDrop.ItemData copyOfDragged = shown.GetItemAt(dragItem.m_gridPos.x, dragItem.m_gridPos.y);
                if (copyOfDragged != null) { dragItem = copyOfDragged; }
            }

            swapped = __instance;
            real = __instance.m_inventory;
            __instance.m_inventory = shown;
        }

        // A finalizer rather than a postfix: the real inventory has to go back even if the drawing threw,
        // or the next click would land on the copy. It runs after every postfix.
        private static void Finalizer(InventoryGrid __instance) {
            if (swapped != __instance) { return; }
            __instance.m_inventory = real;
            swapped = null;
            real = null;
        }
    }
}
