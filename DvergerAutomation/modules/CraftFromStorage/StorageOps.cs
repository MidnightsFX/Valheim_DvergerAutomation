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
        }

        internal static void WriteTake(ZPackage args, string name, int quality, int amount) {
            args.Write(name);
            args.Write(quality);
            args.Write(amount);
        }

        /// <summary>
        /// Removes up to the asked amount of a material and replies with the items removed. The same
        /// by-instance walk as <see cref="CraftFromStoragePatches.RemoveFromContainers"/>, so what another
        /// player can be sent is exactly what this client could have spent itself.
        /// </summary>
        private static StorageRpc.Status Take(Container container, long sender, ZPackage args, ZPackage reply) {
            string name = args.ReadString();
            int quality = args.ReadInt();
            int amount = args.ReadInt();

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            List<ItemDrop.ItemData> taken = new List<ItemDrop.ItemData>();
            int remaining = amount;
            try {
                if (inv != null) {
                    // GetAllItems hands back the live backing list and emptied stacks drop out of it, so
                    // walk it backwards.
                    List<ItemDrop.ItemData> items = inv.GetAllItems();
                    for (int i = items.Count - 1; i >= 0 && remaining > 0 && taken.Count < ItemCodec.MaxItems; --i) {
                        ItemDrop.ItemData item = items[i];
                        if (!CraftFromStoragePatches.Matches(item, name, quality)) { continue; }
                        // Cannot be rebuilt on the other side, so it would leave here and arrive nowhere.
                        if (item.m_dropPrefab == null) { continue; }

                        int take = Mathf.Min(item.m_stack, remaining);
                        ItemDrop.ItemData slice = item.Clone();
                        slice.m_stack = take;
                        taken.Add(slice);
                        inv.RemoveItem(item, take);
                        remaining -= take;
                    }
                }
                ItemCodec.Write(reply, taken);
            } catch (Exception) {
                // The reply is the only copy of what was taken, and it is not going out: anything listening
                // to the chest can throw from inside a removal. Put it all back before the failure is
                // reported, so the asker is told nothing came and nothing is missing.
                PutBack(inv, taken);
                throw;
            }

            if (taken.Count > 0) { AfterChange(container); }
            return remaining <= 0 ? StorageRpc.Status.Ok : StorageRpc.Status.Partial;
        }

        private static void PutBack(Inventory inv, List<ItemDrop.ItemData> taken) {
            if (inv == null) { return; }
            foreach (ItemDrop.ItemData item in taken) {
                try {
                    AutoStore.AddMeasured(inv, item, item.m_stack);
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
        private static StorageRpc.Status Give(Container container, long sender, ZPackage args, ZPackage reply) {
            List<ItemDrop.ItemData> items = ItemCodec.Read(args, out bool intact);
            if (!intact) { return StorageRpc.Status.Error; }

            StorageOwnership.Sync(container);
            Inventory inv = container.GetInventory();
            bool all = true;
            int landedTotal = 0;
            reply.Write(items.Count);
            foreach (ItemDrop.ItemData item in items) {
                int landed = inv != null ? AutoStore.AddMeasured(inv, item, item.m_stack) : 0;
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
