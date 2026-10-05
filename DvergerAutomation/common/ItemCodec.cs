using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Items in a ZPackage, written and read by the game's own inventory serialiser, so every field an
    /// item carries - durability, quality, crafter, custom data, world level, the cheated flag, and
    /// whatever a later game version adds - travels without a field list kept here.
    ///
    /// <c>Inventory.Load</c> rebuilds each item through its prefab, which sets the limits this works
    /// inside:
    ///  - grid positions are written as bytes, and Load checks columns but not rows, so the items are
    ///    lined up down column 0 of a one-wide scratch grid. That caps a payload at <see cref="MaxItems"/>;
    ///  - Load clamps a stack to the prefab's maximum, so an over-full stack is split before it is written;
    ///  - an item with no drop prefab, or one the reader has no prefab for, is skipped without a word, so
    ///    the count and the total are written alongside and checked on the way back in.
    /// </summary>
    internal static class ItemCodec {
        internal const int MaxItems = 255;

        /// <summary>
        /// Writes copies of the items; the originals are not touched. Returns how many units went in, which
        /// is short of what was handed over only when the payload ran out of room.
        /// </summary>
        internal static int Write(ZPackage pkg, IList<ItemDrop.ItemData> items) {
            // Filled directly rather than through AddItem: that would merge stacks, and it is patched by
            // every mod that cares where items go.
            Inventory scratch = new Inventory("da", null, 1, 1);
            int total = 0;
            foreach (ItemDrop.ItemData item in items) {
                if (item == null || item.m_shared == null) { continue; }
                int max = Mathf.Max(1, item.m_shared.m_maxStackSize);
                int left = item.m_stack;
                while (left > 0 && scratch.m_inventory.Count < MaxItems) {
                    ItemDrop.ItemData copy = item.Clone();
                    copy.m_stack = Mathf.Min(left, max);
                    // Equipped travels with the item, and nothing arriving this way is being worn.
                    copy.m_equipped = false;
                    copy.m_gridPos = new Vector2i(0, scratch.m_inventory.Count);
                    scratch.m_inventory.Add(copy);
                    left -= copy.m_stack;
                    total += copy.m_stack;
                }
            }

            ZPackage body = new ZPackage();
            scratch.Save(body);
            pkg.Write(scratch.m_inventory.Count);
            pkg.Write(total);
            pkg.Write(body);
            return total;
        }

        /// <summary>
        /// Reads items written by <see cref="Write"/>. <paramref name="intact"/> is false when fewer came
        /// out than went in - an item this client could not rebuild.
        /// </summary>
        internal static List<ItemDrop.ItemData> Read(ZPackage pkg, out bool intact) {
            int count = pkg.ReadInt();
            int total = pkg.ReadInt();
            ZPackage body = pkg.ReadPackage();

            Inventory scratch = new Inventory("da", null, 1, 1);
            scratch.Load(body);

            List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>(scratch.m_inventory);
            int read = 0;
            foreach (ItemDrop.ItemData item in items) { read += item.m_stack; }
            intact = items.Count == count && read == total;
            return items;
        }
    }
}
