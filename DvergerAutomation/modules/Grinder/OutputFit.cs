using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Puts several kinds of item into an inventory all at once or not at all.
    ///
    /// Vanilla has nothing for this. <c>CanAddItem</c> checks one item, ignores quality and the cheated
    /// flag, and <c>AddItem(GameObject, int)</c> quietly stops at one stack. A stackable
    /// <c>AddItem(ItemData)</c> tops up existing stacks before it can fail, and puts whatever is left into
    /// a single slot however far over a stack that is. So room is counted here with the same match vanilla
    /// stacks by (<c>Inventory.FindFreeStackItem</c>: name, quality, world level, cheated), and items go
    /// in no more than a stack at a time.
    /// </summary>
    internal static class OutputFit {
        internal struct Output {
            /// <summary>Stands for every unit added: they are clones of it.</summary>
            internal ItemDrop.ItemData Template;
            internal int Amount;
        }

        /// <summary>A unit of a material, as the Grinder pays it out.</summary>
        internal static ItemDrop.ItemData Material(GameObject prefab, bool cheated, int worldLevel) {
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null) { return null; }
            ItemDrop.ItemData item = drop.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_stack = 1;
            item.m_cheated = cheated;
            item.m_worldLevel = worldLevel;
            return item;
        }

        /// <summary>True when every output fits at once.</summary>
        internal static bool Fits(Inventory inv, List<Output> outputs) {
            if (inv == null) { return false; }
            List<ItemDrop.ItemData> have = inv.GetAllItems();
            int slots = 0;
            for (int i = 0; i < outputs.Count; ++i) {
                ItemDrop.ItemData template = outputs[i].Template;
                // Outputs that stack together share the room on existing stacks, so they are counted as one.
                if (SeenBefore(outputs, i)) { continue; }
                int amount = 0;
                for (int j = i; j < outputs.Count; ++j) {
                    if (StacksWith(outputs[j].Template, template)) { amount += outputs[j].Amount; }
                }

                int max = Mathf.Max(1, template.m_shared.m_maxStackSize);
                int room = 0;
                if (max > 1) {
                    foreach (ItemDrop.ItemData item in have) {
                        if (item != null && item.m_stack < max && StacksWith(item, template)) { room += max - item.m_stack; }
                    }
                }
                int left = amount - room;
                if (left > 0) { slots += (left + max - 1) / max; }
            }
            // GetEmptySlots is a plain width * height - count, and can read negative on an over-full grid.
            return slots <= Mathf.Max(0, inv.GetEmptySlots());
        }

        /// <summary>Adds every output, a stack at most at a time. Check <see cref="Fits"/> first.</summary>
        internal static void Add(Inventory inv, List<Output> outputs) {
            foreach (Output output in outputs) {
                int max = Mathf.Max(1, output.Template.m_shared.m_maxStackSize);
                int left = output.Amount;
                while (left > 0) {
                    int chunk = Mathf.Min(left, max);
                    ItemDrop.ItemData item = output.Template.Clone();
                    item.m_stack = chunk;
                    if (!inv.AddItem(item)) {
                        Logger.LogWarning($"[Grinder] {chunk} {item.m_shared.m_name} did not fit after all.");
                        return;
                    }
                    left -= chunk;
                }
            }
        }

        private static bool SeenBefore(List<Output> outputs, int index) {
            for (int k = 0; k < index; ++k) {
                if (StacksWith(outputs[k].Template, outputs[index].Template)) { return true; }
            }
            return false;
        }

        // Inventory.FindFreeStackItem's own test.
        private static bool StacksWith(ItemDrop.ItemData a, ItemDrop.ItemData b) {
            return a.m_shared.m_name == b.m_shared.m_name
                && a.m_quality == b.m_quality
                && a.m_worldLevel == b.m_worldLevel
                && a.m_cheated == b.m_cheated;
        }
    }
}
