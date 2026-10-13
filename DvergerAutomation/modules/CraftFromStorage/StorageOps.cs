using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// What a container's owner does for another player's crafting (see <see cref="StorageRpc"/>): take
    /// materials out and send them over, and put back the ones that went unused. Both run on the owner's
    /// own copy of the chest, open or not, so its save is the only one and nobody's panel is disturbed.
    /// </summary>
    internal static class StorageOps {
        internal static void Register() {
            StorageRpc.Register(StorageRpc.Op.Take, Take);
            StorageRpc.Register(StorageRpc.Op.Give, Give);
            StorageRpc.Register(StorageRpc.Op.TakeExact, TakeExact);
        }

        internal static void WriteTake(ZPackage args, string name, int amount) {
            args.Write(name);
            args.Write(amount);
        }

        /// <summary>
        /// Removes up to the asked amount of a material and replies with the items removed. The same
        /// by-instance walk as <see cref="CraftFromStoragePatches.RemoveFromContainers"/>, so what another
        /// player can be sent is exactly what this client could have spent itself.
        /// </summary>
        private static StorageRpc.Status Take(Container container, ZPackage args, ZPackage reply) {
            string name = args.ReadString();
            int amount = args.ReadInt();

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            List<ItemDrop.ItemData> taken = new List<ItemDrop.ItemData>();
            int removed;
            try {
                removed = InventoryOps.Take(inv, name, -1, amount, taken);
                ItemCodec.Write(reply, taken);
            } catch (Exception) {
                // The reply is the only copy of what was taken, and it is not going out: anything listening
                // to the chest can throw from inside a removal. Put it all back before the failure is
                // reported, so the asker is told nothing came and nothing is missing.
                PutBack(inv, taken);
                throw;
            }

            if (taken.Count > 0) { AfterChange(container); }
            return removed >= amount ? StorageRpc.Status.Ok : StorageRpc.Status.Partial;
        }

        /// <summary>
        /// Asks for <paramref name="amount"/> units of exactly <paramref name="prototype"/>: for a stackable
        /// item any stack it would merge with, for anything else only an item nobody could tell from it
        /// (see <see cref="ItemIdentity"/>). <paramref name="hint"/> is where the asker last saw it.
        /// </summary>
        internal static void WriteTakeExact(ZPackage args, ItemDrop.ItemData prototype, Vector2i hint, int amount) {
            ItemDrop.ItemData one = prototype.Clone();
            one.m_stack = 1;
            ItemCodec.Write(args, new[] { one });
            args.Write(hint.x);
            args.Write(hint.y);
            args.Write(amount);
        }

        /// <summary>
        /// The Seeking Table's fetch: removes up to the asked amount of one particular item and replies
        /// with what was removed. Unlike <see cref="Take"/> this serves enchanted gear too - the asker
        /// named the very item it wants, so there is no mistaking it for plain material.
        ///
        /// The item is looked for where the asker saw it first, so of several identical swords the one
        /// clicked on is the one that comes, then anywhere else in the chest, since the owner may have
        /// moved it. Nothing matching at all is <see cref="StorageRpc.Status.Changed"/>: someone got there
        /// first.
        /// </summary>
        private static StorageRpc.Status TakeExact(Container container, ZPackage args, ZPackage reply) {
            List<ItemDrop.ItemData> prototypes = ItemCodec.Read(args, out bool intact);
            Vector2i hint = new Vector2i(args.ReadInt(), args.ReadInt());
            int amount = args.ReadInt();
            // Not an item this game has a prefab for, so nothing here can match it.
            if (!intact || prototypes.Count != 1 || amount <= 0) { return StorageRpc.Status.Error; }
            ItemDrop.ItemData prototype = prototypes[0];

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            List<ItemDrop.ItemData> taken = new List<ItemDrop.ItemData>();
            int remaining = amount;
            try {
                if (inv != null) {
                    ItemDrop.ItemData at = inv.GetItemAt(hint.x, hint.y);
                    if (at != null) { remaining -= TakeMatching(inv, at, prototype, remaining, taken); }

                    // Backwards, as in Take: emptied stacks drop out of the live list as it goes.
                    List<ItemDrop.ItemData> items = inv.GetAllItems();
                    for (int i = items.Count - 1; i >= 0 && remaining > 0 && taken.Count < ItemCodec.MaxItems; --i) {
                        remaining -= TakeMatching(inv, items[i], prototype, remaining, taken);
                    }
                }
                ItemCodec.Write(reply, taken);
            } catch (Exception) {
                // As in Take: the reply is the only copy, and it is not going out.
                PutBack(inv, taken);
                throw;
            }

            if (taken.Count == 0) { return StorageRpc.Status.Changed; }
            AfterChange(container);
            return remaining <= 0 ? StorageRpc.Status.Ok : StorageRpc.Status.Partial;
        }

        private static int TakeMatching(Inventory inv, ItemDrop.ItemData item, ItemDrop.ItemData prototype, int remaining, List<ItemDrop.ItemData> taken) {
            if (remaining <= 0 || !ItemIdentity.Interchangeable(prototype, item)) { return 0; }
            // Cannot be rebuilt on the other side, so it would leave here and arrive nowhere.
            if (!ItemIdentity.CanTravel(item)) { return 0; }

            int take = Mathf.Min(item.m_stack, remaining);
            ItemDrop.ItemData slice = item.Clone();
            slice.m_stack = take;
            // Only what actually left the chest is sent. A mod that refuses the removal would otherwise
            // have the asker handed a copy of something still here.
            if (!inv.RemoveItem(item, take)) { return 0; }
            taken.Add(slice);
            return take;
        }

        private static void PutBack(Inventory inv, List<ItemDrop.ItemData> taken) {
            if (inv == null) { return; }
            foreach (ItemDrop.ItemData item in taken) {
                try {
                    InventoryOps.AddMeasured(inv, item, item.m_stack);
                } catch (Exception ex) {
                    Logger.LogError($"[Storage] could not put {item.m_stack} {item.m_shared.m_name} back after a failed request: {ex}");
                }
            }
        }

        // Housekeeping after the chest changed. The request has been carried out by now, so nothing here
        // may stop its answer going out.
        private static void AfterChange(Container container) {
            try {
                ContainerNetwork.InvalidateItemCounts();
                GuardOwnerGui(container);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] tidying up after a change to {container.name} failed: {ex}");
            }
        }

        /// <summary>
        /// Puts items into the chest and replies with how much of each went in, in the order they were
        /// sent. Nothing is added when the items did not all arrive whole: the amounts would no longer line
        /// up with what the sender kept, and it restores from that instead.
        /// </summary>
        private static StorageRpc.Status Give(Container container, ZPackage args, ZPackage reply) {
            List<ItemDrop.ItemData> items = ItemCodec.Read(args, out bool intact);
            if (!intact) { return StorageRpc.Status.Error; }

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            bool all = true;
            int landedTotal = 0;
            reply.Write(items.Count);
            foreach (ItemDrop.ItemData item in items) {
                int landed = inv != null ? InventoryOps.AddMeasured(inv, item, item.m_stack) : 0;
                reply.Write(landed);
                landedTotal += landed;
                if (landed < item.m_stack) { all = false; }
            }

            if (landedTotal > 0) { AfterChange(container); }
            return all ? StorageRpc.Status.Ok : StorageRpc.Status.Partial;
        }

        /// <summary>
        /// Keeps this client's open panel straight after its chest changed underneath it. Vanilla already
        /// copes with the item on the cursor having gone or shrunk - the drop checks it is still there and
        /// clamps the amount - so this only stops the cursor and the split dialog showing a stack that is
        /// no longer what they say.
        /// </summary>
        internal static void GuardOwnerGui(Container container) {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || gui.m_currentContainer != container) { return; }
            Inventory inv = container.GetInventory();
            if (inv == null) { return; }

            if (gui.m_dragItem != null && gui.m_dragInventory == inv) {
                if (!inv.ContainsItem(gui.m_dragItem)) {
                    gui.SetupDragItem(null, null, 1);
                } else if (gui.m_dragAmount > gui.m_dragItem.m_stack) {
                    gui.m_dragAmount = gui.m_dragItem.m_stack;
                }
            }
            if (gui.m_splitItem != null && gui.m_splitInventory == inv && !inv.ContainsItem(gui.m_splitItem)) {
                gui.OnSplitCancel();
            }
        }
    }
}
