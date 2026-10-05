using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// What a chest's owner does with a guest's moves (see <see cref="SharedChests"/>): put an item in,
    /// take one out, or shift one about inside it. Each runs on the owner's own copy and is saved from
    /// there, so every other client - the guest included - sees it through the usual reload.
    ///
    /// A guest works from a copy that can be a moment old, so every request that names a slot says what
    /// it expects to find there, and a slot that holds something else is answered with
    /// <see cref="StorageRpc.Status.Changed"/> and left untouched. A swap is all or nothing: if the
    /// guest's item cannot be put in the slot, the chest's item stays where it is.
    ///
    /// The slot rules are vanilla's, taken from <c>InventoryGrid.DropItem</c> and
    /// <c>Inventory.AddItem</c>, so a move does for a guest what the same move does for the owner.
    /// The same routines also run on a throwaway copy to draw a guest's moves before they are answered
    /// (see <see cref="SharedChestPreview"/>).
    /// </summary>
    internal static class SharedChestOps {
        private static readonly List<ItemDrop.ItemData> None = new List<ItemDrop.ItemData>();

        internal static void Register() {
            StorageRpc.Register(StorageRpc.Op.ChestAdd, Add);
            StorageRpc.Register(StorageRpc.Op.ChestRemove, Remove);
            StorageRpc.Register(StorageRpc.Op.ChestMove, Move);
        }

        // ---- what a slot holds ------------------------------------------------------

        /// <summary>
        /// What a request expects to find in a slot: 0 for an empty one, otherwise a number that differs
        /// for a different kind or quality of item. The stack size is left out on purpose - it changes
        /// under two players stacking the same thing, and every move copes with a different amount.
        /// </summary>
        internal static int Fingerprint(ItemDrop.ItemData item) {
            if (item == null) { return 0; }
            string name = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared.m_name;
            int print = unchecked(name.GetStableHashCode() * 31 + item.m_quality);
            return print == 0 ? 1 : print;
        }

        /// <summary>
        /// Vanilla's test for a drop that trades places with what is in the slot rather than stacking onto
        /// it: only a whole stack is ever swapped, and only onto something it cannot stack with.
        /// </summary>
        internal static bool ShouldSwap(ItemDrop.ItemData inSlot, ItemDrop.ItemData dropped, bool wholeStack) {
            if (inSlot == null || !wholeStack) { return false; }
            return inSlot.m_shared.m_name != dropped.m_shared.m_name
                || (dropped.m_shared.m_maxQuality > 1 && inSlot.m_quality != dropped.m_quality)
                || inSlot.m_shared.m_maxStackSize == 1
                || inSlot.m_cheated != dropped.m_cheated;
        }

        /// <summary>How many of <paramref name="dropped"/> fit on top of what is in a slot, by vanilla's rule for adding to one.</summary>
        internal static int StackSpace(ItemDrop.ItemData inSlot, ItemDrop.ItemData dropped) {
            if (inSlot == null || !inSlot.IsSameType(dropped)) { return 0; }
            return Mathf.Max(0, inSlot.m_shared.m_maxStackSize - inSlot.m_stack);
        }

        private static bool InGrid(Inventory inv, Vector2i pos) {
            return pos.x >= 0 && pos.y >= 0 && pos.x < inv.GetWidth() && pos.y < inv.GetHeight();
        }

        // ---- the moves, on any inventory --------------------------------------------
        // `live` is the owner's real chest: an item is put into an empty slot through vanilla's AddItem,
        // so a mod that limits what a container accepts still has its say. On a preview copy the list is
        // edited directly - nothing there needs asking, and AddItem logs every stack it touches.

        /// <summary>
        /// Puts <paramref name="item"/> into a slot, or anywhere when <paramref name="to"/> is negative.
        /// Returns how many went in, and through <paramref name="swappedOut"/> the item that had to make
        /// way, if one did.
        /// </summary>
        internal static int ApplyAdd(Inventory inv, ItemDrop.ItemData item, Vector2i to, bool wholeStack, bool live, out ItemDrop.ItemData swappedOut) {
            swappedOut = null;
            int amount = item.m_stack;
            if (to.x < 0 || to.y < 0) { return AutoStore.AddMeasured(inv, item, amount); }
            if (!InGrid(inv, to)) { return 0; }

            ItemDrop.ItemData inSlot = inv.GetItemAt(to.x, to.y);
            if (inSlot == null) { return Place(inv, item, amount, to, live) ? amount : 0; }

            if (ShouldSwap(inSlot, item, wholeStack)) {
                inv.m_inventory.Remove(inSlot);
                if (Place(inv, item, amount, to, live)) {
                    swappedOut = inSlot;
                    return amount;
                }
                inv.m_inventory.Add(inSlot);
                return 0;
            }

            int fits = Mathf.Min(StackSpace(inSlot, item), amount);
            inSlot.m_stack += fits;
            return fits;
        }

        /// <summary>
        /// Takes up to <paramref name="amount"/> out of a slot and returns it, leaving
        /// <paramref name="swapIn"/> in its place when one is given. Null when there is nothing there, or
        /// the swap could not be made - in which case nothing was taken.
        /// </summary>
        internal static ItemDrop.ItemData ApplyRemove(Inventory inv, Vector2i from, int amount, ItemDrop.ItemData swapIn, bool live) {
            ItemDrop.ItemData inSlot = inv.GetItemAt(from.x, from.y);
            if (inSlot == null || amount <= 0) { return null; }

            if (swapIn != null) {
                // Only a whole stack trades places.
                if (amount < inSlot.m_stack) { return null; }
                inv.m_inventory.Remove(inSlot);
                if (!Place(inv, swapIn, swapIn.m_stack, from, live)) {
                    inv.m_inventory.Add(inSlot);
                    return null;
                }
                return inSlot;
            }

            int take = Mathf.Min(inSlot.m_stack, amount);
            ItemDrop.ItemData taken = inSlot.Clone();
            taken.m_stack = take;
            inSlot.m_stack -= take;
            if (inSlot.m_stack <= 0) { inv.m_inventory.Remove(inSlot); }
            return taken;
        }

        /// <summary>Moves up to <paramref name="amount"/> from one slot to another in the same inventory: onto an empty slot, onto a stack, or trading places.</summary>
        internal static bool ApplyMove(Inventory inv, Vector2i from, Vector2i to, int amount) {
            ItemDrop.ItemData moving = inv.GetItemAt(from.x, from.y);
            if (moving == null || !InGrid(inv, to)) { return false; }
            amount = Mathf.Min(amount, moving.m_stack);
            if (amount <= 0) { return false; }

            ItemDrop.ItemData inSlot = inv.GetItemAt(to.x, to.y);
            if (inSlot == moving) { return true; }

            if (inSlot == null) {
                if (amount == moving.m_stack) {
                    moving.m_gridPos = to;
                } else {
                    ItemDrop.ItemData part = moving.Clone();
                    part.m_stack = amount;
                    part.m_gridPos = to;
                    inv.m_inventory.Add(part);
                    moving.m_stack -= amount;
                }
                return true;
            }

            if (ShouldSwap(inSlot, moving, amount == moving.m_stack)) {
                inSlot.m_gridPos = from;
                moving.m_gridPos = to;
                return true;
            }

            int fits = Mathf.Min(StackSpace(inSlot, moving), amount);
            if (fits <= 0) { return false; }
            inSlot.m_stack += fits;
            moving.m_stack -= fits;
            if (moving.m_stack <= 0) { inv.m_inventory.Remove(moving); }
            return true;
        }

        // Only ever called for a slot that is empty at that moment.
        private static bool Place(Inventory inv, ItemDrop.ItemData item, int amount, Vector2i to, bool live) {
            ItemDrop.ItemData copy = item.Clone();
            copy.m_stack = amount;
            copy.m_equipped = false;
            if (!live) {
                copy.m_gridPos = to;
                inv.m_inventory.Add(copy);
                return true;
            }

            // Judged by what is in the slot afterwards, not by what AddItem said or whether it returned
            // at all: AddItem announces the change before it returns, and anything listening can throw
            // with the item already in. The callers decide what leaves the chest on this answer.
            try {
                inv.AddItem(copy, amount, to.x, to.y);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] something listening to an inventory failed while an item was put in: {ex}");
            }
            return inv.GetItemAt(to.x, to.y) != null;
        }

        // ---- the owner's handlers ---------------------------------------------------

        internal static void WriteAdd(ZPackage args, Vector2i to, bool wholeStack, int expect, ItemDrop.ItemData item) {
            args.Write(to);
            args.Write(wholeStack);
            args.Write(expect);
            ItemCodec.Write(args, new[] { item });
        }

        private static StorageRpc.Status Add(Container container, long sender, ZPackage args, ZPackage reply) {
            Vector2i to = args.ReadVector2i();
            bool wholeStack = args.ReadBool();
            int expect = args.ReadInt();
            List<ItemDrop.ItemData> items = ItemCodec.Read(args, out bool intact);
            if (!intact || items.Count != 1) { return StorageRpc.Status.Error; }
            if (!Shared(container)) { return StorageRpc.Status.Refused; }

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            ItemDrop.ItemData item = items[0];
            int sent = item.m_stack;
            if (to.x >= 0 && to.y >= 0 && (!InGrid(inv, to) || Fingerprint(inv.GetItemAt(to.x, to.y)) != expect)) {
                return StorageRpc.Status.Changed;
            }

            int accepted = ApplyAdd(inv, item, to, wholeStack, live: true, out ItemDrop.ItemData swappedOut);
            try {
                reply.Write(accepted);
                ItemCodec.Write(reply, swappedOut != null ? new List<ItemDrop.ItemData> { swappedOut } : None);
            } catch (Exception) {
                // The item that made way travels in the answer and nowhere else. If the answer cannot be
                // written the trade is called off: it goes back in its slot, and the guest - told the
                // request failed - puts its own item back.
                if (swappedOut != null) { TradeBack(inv, to, swappedOut); }
                throw;
            }
            if (accepted > 0) { Finish(container, inv); }
            return accepted >= sent ? StorageRpc.Status.Ok : StorageRpc.Status.Partial;
        }

        // Undoes a swap: whatever was put in the slot comes out, and the item that left it goes back.
        private static void TradeBack(Inventory inv, Vector2i slot, ItemDrop.ItemData original) {
            ItemDrop.ItemData placed = inv.GetItemAt(slot.x, slot.y);
            if (placed != null) { inv.m_inventory.Remove(placed); }
            original.m_gridPos = slot;
            inv.m_inventory.Add(original);
        }

        internal static void WriteRemove(ZPackage args, Vector2i from, int amount, int expect, ItemDrop.ItemData swapIn) {
            args.Write(from);
            args.Write(amount);
            args.Write(expect);
            args.Write(swapIn != null);
            if (swapIn != null) { ItemCodec.Write(args, new[] { swapIn }); }
        }

        private static StorageRpc.Status Remove(Container container, long sender, ZPackage args, ZPackage reply) {
            Vector2i from = args.ReadVector2i();
            int amount = args.ReadInt();
            int expect = args.ReadInt();
            ItemDrop.ItemData swapIn = null;
            if (args.ReadBool()) {
                List<ItemDrop.ItemData> items = ItemCodec.Read(args, out bool intact);
                if (!intact || items.Count != 1) { return StorageRpc.Status.Error; }
                swapIn = items[0];
            }
            if (!Shared(container)) { return StorageRpc.Status.Refused; }

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            ItemDrop.ItemData inSlot = inv.GetItemAt(from.x, from.y);
            if (inSlot == null || Fingerprint(inSlot) != expect) { return StorageRpc.Status.Changed; }
            // Cannot be rebuilt on the other side, so it would leave here and arrive nowhere.
            if (inSlot.m_dropPrefab == null) { return StorageRpc.Status.Refused; }

            ItemDrop.ItemData taken = ApplyRemove(inv, from, amount, swapIn, live: true);
            if (taken == null) { return StorageRpc.Status.Changed; }
            try {
                ItemCodec.Write(reply, new List<ItemDrop.ItemData> { taken });
            } catch (Exception) {
                // The answer is the only copy of what was taken. If it cannot be written, nothing leaves.
                if (swapIn != null) {
                    TradeBack(inv, from, taken);
                } else {
                    ApplyAdd(inv, taken, from, wholeStack: false, live: false, out _);
                }
                throw;
            }
            Finish(container, inv);
            return taken.m_stack >= amount || swapIn != null ? StorageRpc.Status.Ok : StorageRpc.Status.Partial;
        }

        internal static void WriteMove(ZPackage args, Vector2i from, Vector2i to, int amount, int expectFrom, int expectTo) {
            args.Write(from);
            args.Write(to);
            args.Write(amount);
            args.Write(expectFrom);
            args.Write(expectTo);
        }

        private static StorageRpc.Status Move(Container container, long sender, ZPackage args, ZPackage reply) {
            Vector2i from = args.ReadVector2i();
            Vector2i to = args.ReadVector2i();
            int amount = args.ReadInt();
            int expectFrom = args.ReadInt();
            int expectTo = args.ReadInt();
            if (!Shared(container)) { return StorageRpc.Status.Refused; }

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            ItemDrop.ItemData moving = inv.GetItemAt(from.x, from.y);
            if (moving == null || Fingerprint(moving) != expectFrom) { return StorageRpc.Status.Changed; }
            if (!InGrid(inv, to) || Fingerprint(inv.GetItemAt(to.x, to.y)) != expectTo) { return StorageRpc.Status.Changed; }

            if (!ApplyMove(inv, from, to, amount)) { return StorageRpc.Status.Changed; }
            Finish(container, inv);
            return StorageRpc.Status.Ok;
        }

        // Checked on the owner as well as the guest: the two can disagree for a moment while the
        // server's setting reaches them.
        private static bool Shared(Container container) {
            return SharedChests.Active && SharedChests.IsShareable(container);
        }

        // The moves above edit the list directly in places, and vanilla's slot-wise AddItem only raises
        // the change itself when there is a local player. Changed is what saves the chest.
        //
        // The move has been made by the time this runs, so nothing here may stop its answer going out -
        // for a removal, the answer is the only copy of what was taken.
        private static void Finish(Container container, Inventory inv) {
            try {
                inv.Changed();
                ContainerNetwork.InvalidateItemCounts();
                StorageOps.GuardOwnerGui(container);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] announcing a change to {container.name} failed: {ex}");
            }
        }
    }
}
