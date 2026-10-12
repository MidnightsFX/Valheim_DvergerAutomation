using System.Collections.Generic;

namespace DvergerAutomation {
    /// <summary>
    /// The Grinder's state on its ZDO: the items waiting to be ground, the fractions of a unit it owes,
    /// and a couple of summary values for hover text.
    ///
    /// Each queued item is kept as its own <see cref="ItemCodec"/> payload, so every field it carries
    /// (quality, durability, crafter, Epic Loot's custom data) comes back intact when it is handed back
    /// or dropped. Decoding one builds a GameObject (<c>Inventory.Load</c>), so only the owner decodes,
    /// only the item it is working on, and never for hover text: the summary keys are there for that.
    /// </summary>
    internal static class GrinderQueue {
        internal const string QueueKey = "DA_grinder_queue";
        internal const string CountKey = "DA_grinder_qcount";
        internal const string HeadKey = "DA_grinder_head";
        internal const string RemainderKey = "DA_grinder_rem";

        // Epic Loot's custom data is the bulky part of an item; this keeps a full queue to a size the
        // ZDO, which also carries the results chest, can send without strain.
        internal const int MaxBytes = 16 * 1024;

        private const int Version = 1;

        internal sealed class Entry {
            /// <summary>The item's shared name, readable without decoding it.</summary>
            internal string Name;
            internal int Stack;
            internal byte[] Blob;

            // Owner-side only, never saved: the decoded item, so a grind spread over several ticks
            // decodes it once.
            internal ItemDrop.ItemData Decoded;
            internal bool Unreadable;
        }

        internal static byte[] Encode(ItemDrop.ItemData item) {
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

        internal static Entry NewEntry(ItemDrop.ItemData item) {
            return new Entry { Name = item.m_shared.m_name, Stack = item.m_stack, Blob = Encode(item), Decoded = item };
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
            zdo.Set(QueueKey, raw);
            zdo.Set(CountKey, entries.Count);
            zdo.Set(HeadKey, HeadName(entries));
            return raw;
        }

        // The first entry the owner can actually work on, which is the one hover text should name.
        private static string HeadName(List<Entry> entries) {
            foreach (Entry entry in entries) {
                if (!entry.Unreadable) { return entry.Name ?? ""; }
            }
            return "";
        }

        // ---- the fractions owed -------------------------------------------------------------------

        // Keyed by material prefab name, prefixed when the material came from a cheated item, so a
        // cheated sword never tops up a legitimate remainder.
        private const char CheatedMark = '!';

        internal static string RemainderKeyFor(string prefab, bool cheated) => cheated ? CheatedMark + prefab : prefab;

        internal static string PrefabOf(string remainderKey, out bool cheated) {
            cheated = remainderKey.Length > 0 && remainderKey[0] == CheatedMark;
            return cheated ? remainderKey.Substring(1) : remainderKey;
        }

        internal static Dictionary<string, long> LoadRemainders(ZDO zdo) {
            Dictionary<string, long> milli = new Dictionary<string, long>();
            byte[] raw = zdo.GetByteArray(RemainderKey);
            if (raw == null || raw.Length == 0) { return milli; }
            ZPackage pkg = new ZPackage(raw);
            if (pkg.ReadInt() != Version) { return milli; }
            int count = pkg.ReadInt();
            for (int i = 0; i < count; ++i) {
                string key = pkg.ReadString();
                milli[key] = pkg.ReadLong();
            }
            return milli;
        }

        internal static void SaveRemainders(ZDO zdo, Dictionary<string, long> milli) {
            ZPackage pkg = new ZPackage();
            pkg.Write(Version);
            int count = 0;
            foreach (KeyValuePair<string, long> entry in milli) {
                if (entry.Value > 0) { ++count; }
            }
            pkg.Write(count);
            foreach (KeyValuePair<string, long> entry in milli) {
                if (entry.Value <= 0) { continue; }
                pkg.Write(entry.Key);
                pkg.Write(entry.Value);
            }
            zdo.Set(RemainderKey, pkg.GetArray());
        }
    }
}
