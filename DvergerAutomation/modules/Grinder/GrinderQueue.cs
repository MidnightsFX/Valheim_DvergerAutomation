using System.Collections.Generic;

namespace DvergerAutomation {
    /// <summary>
    /// The Grinder's processing queue on its ZDO: everything the lever took out of the hopper, waiting to
    /// be ground.
    ///
    /// Each entry is its own <see cref="ItemCodec"/> payload, so every field an item carries (quality,
    /// durability, crafter, Epic Loot's custom data) comes back intact when it drops with a destroyed
    /// Grinder. Decoding one builds a GameObject (<c>Inventory.Load</c>), so only the owner decodes, only the
    /// item it is working on, and never for hover text: the summary keys are there for that.
    /// </summary>
    internal static class GrinderQueue {
        // Hashed once: some are read every frame, by every client, to set the lever.
        internal static readonly int QueueKey = "DA_grinder_queue".GetStableHashCode();
        internal static readonly int CountKey = "DA_grinder_qcount".GetStableHashCode();
        internal static readonly int HeadKey = "DA_grinder_head".GetStableHashCode();
        // Every item queued, counting each one in a stack.
        internal static readonly int ItemsKey = "DA_grinder_qitems".GetStableHashCode();

        // Epic Loot's custom data is the bulky part of an item; this keeps a full queue to a size the ZDO,
        // which also carries the results chest, can send without strain.
        internal const int MaxBytes = 16 * 1024;

        private const int Version = 1;

        internal sealed class Entry {
            /// <summary>The item's shared name, readable without decoding it.</summary>
            internal string Name;
            internal int Stack;
            internal byte[] Blob;

            // Owner-side only, never saved: the decoded item, so a grind spread over several ticks decodes
            // it once.
            internal ItemDrop.ItemData Decoded;
            internal bool Unreadable;
        }

        internal static Entry NewEntry(ItemDrop.ItemData item) {
            ItemDrop.ItemData copy = item.Clone();
            return new Entry { Name = copy.m_shared.m_name, Stack = copy.m_stack, Blob = Encode(copy), Decoded = copy };
        }

        private static byte[] Encode(ItemDrop.ItemData item) {
            ZPackage pkg = new ZPackage();
            ItemCodec.Write(pkg, new[] { item });
            return pkg.GetArray();
        }

        /// <summary>The entry's item, or null when this game cannot rebuild it (a mod that made it is gone).</summary>
        internal static ItemDrop.ItemData Decode(Entry entry) {
            if (entry.Decoded != null) { return entry.Decoded; }
            if (entry.Unreadable || entry.Blob == null) { return null; }
            List<ItemDrop.ItemData> items = ItemCodec.Read(new ZPackage(entry.Blob), out bool intact);
            if (!intact || items.Count != 1) {
                entry.Unreadable = true;
                return null;
            }
            entry.Decoded = items[0];
            return entry.Decoded;
        }

        /// <summary>Re-encodes an entry after part of its stack was ground.</summary>
        internal static void SetStack(Entry entry, ItemDrop.ItemData item, int stack) {
            item.m_stack = stack;
            entry.Stack = stack;
            entry.Blob = Encode(item);
        }

        internal static int Bytes(List<Entry> entries) {
            int total = 0;
            foreach (Entry entry in entries) { total += entry.Blob != null ? entry.Blob.Length : 0; }
            return total;
        }

        internal static List<Entry> Parse(byte[] raw) {
            List<Entry> entries = new List<Entry>();
            if (raw == null || raw.Length == 0) { return entries; }
            ZPackage pkg = new ZPackage(raw);
            if (pkg.ReadInt() != Version) { return entries; }
            int count = pkg.ReadInt();
            for (int i = 0; i < count; ++i) {
                entries.Add(new Entry { Name = pkg.ReadString(), Stack = pkg.ReadInt(), Blob = pkg.ReadByteArray() });
            }
            return entries;
        }

        /// <summary>Writes the queue and its summary keys. Returns the array stored, which identifies this revision.</summary>
        internal static byte[] Save(ZDO zdo, List<Entry> entries) {
            ZPackage pkg = new ZPackage();
            pkg.Write(Version);
            pkg.Write(entries.Count);
            foreach (Entry entry in entries) {
                pkg.Write(entry.Name ?? "");
                pkg.Write(entry.Stack);
                pkg.Write(entry.Blob ?? new byte[0]);
            }
            byte[] raw = pkg.GetArray();
            int items = 0;
            foreach (Entry entry in entries) { items += entry.Stack; }
            zdo.Set(QueueKey, raw);
            zdo.Set(CountKey, entries.Count);
            zdo.Set(ItemsKey, items);
            // The item being worked on: the first this game can rebuild, as far as the owner knows.
            Entry head = entries.Find(e => !e.Unreadable);
            zdo.Set(HeadKey, head != null ? head.Name ?? "" : "");
            return raw;
        }
    }
}
