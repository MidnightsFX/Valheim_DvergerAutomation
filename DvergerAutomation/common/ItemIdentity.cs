using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// When two items are "the same thing", for code that has to name an item in a chest it does not own
    /// and have its owner find it again.
    ///
    /// Two levels, because the game has two:
    ///  - <see cref="SameStack"/>: the items would merge into one stack. That is what a material is - a
    ///    player asking for 40 wood does not care which stack it comes out of.
    ///  - <see cref="SameExact"/>: nothing a player could tell apart. Gear is one of a kind - its
    ///    durability, who made it, its style, and any enchantment Epic Loot keeps in its custom data.
    /// </summary>
    internal static class ItemIdentity {
        // ItemData.Save writes durability as (int)(durability * 100) and Load reads it back times 0.01, so
        // the copy a request carries can be a hundredth off the owner's own. Float rounding on top of that
        // can tip the stored integer by one, which is why this is not compared as the integer.
        private const float DurabilityTolerance = 0.02f;

        /// <summary>
        /// Vanilla's stacking rule (<c>Inventory.FindFreeStackItem</c>): name, quality, world level and the
        /// cheated flag. Variant and custom data play no part - an item added to a stack takes on that
        /// stack's - so they play no part here either.
        /// </summary>
        internal static bool SameStack(ItemDrop.ItemData a, ItemDrop.ItemData b) {
            if (a == null || b == null || a.m_shared == null || b.m_shared == null) { return false; }
            return a.m_shared.m_name == b.m_shared.m_name
                && a.m_quality == b.m_quality
                && a.m_worldLevel == b.m_worldLevel
                && a.m_cheated == b.m_cheated;
        }

        /// <summary><see cref="SameStack"/> plus everything else an item carries through a save.</summary>
        internal static bool SameExact(ItemDrop.ItemData a, ItemDrop.ItemData b) {
            if (!SameStack(a, b)) { return false; }
            if (a.m_variant != b.m_variant) { return false; }
            if (a.m_crafterID != b.m_crafterID || (a.m_crafterName ?? "") != (b.m_crafterName ?? "")) { return false; }
            if (PrefabName(a) != PrefabName(b)) { return false; }
            if (Mathf.Abs(a.m_durability - b.m_durability) > DurabilityTolerance) { return false; }
            return SameCustomData(a.m_customData, b.m_customData);
        }

        /// <summary>
        /// The rule that decides whether two of these are interchangeable: a stackable one by
        /// <see cref="SameStack"/>, anything else - each piece of gear has its own durability and maker -
        /// by <see cref="SameExact"/>.
        /// </summary>
        internal static bool Interchangeable(ItemDrop.ItemData a, ItemDrop.ItemData b) {
            if (a == null || a.m_shared == null) { return false; }
            return a.m_shared.m_maxStackSize > 1 ? SameStack(a, b) : SameExact(a, b);
        }

        /// <summary>
        /// Whether an item is the named material for the by-name crafting paths: vanilla's own name, quality
        /// and world-level test, plus Epic Loot's magic items, which share <c>m_shared.m_name</c> with their
        /// mundane counterpart and must never be spent as one. A negative quality matches any.
        /// </summary>
        internal static bool Matches(ItemDrop.ItemData item, string name, int quality) {
            if (item == null || item.m_shared == null) { return false; }
            if (item.m_shared.m_name != name) { return false; }
            if (quality >= 0 && item.m_quality != quality) { return false; }
            if (item.m_worldLevel < Game.m_worldLevel) { return false; }
            return !EpicLootIntegration.IsProtectedItem(item);
        }

        /// <summary>
        /// Whether the item can be sent to another client: it is rebuilt there from its drop prefab, so one
        /// without would leave here and arrive nowhere.
        /// </summary>
        internal static bool CanTravel(ItemDrop.ItemData item) {
            return item != null && item.m_dropPrefab != null;
        }

        /// <summary>
        /// Appends an item's custom data - where Epic Loot keeps an enchantment - to <paramref name="into"/>
        /// in key order, so two items carrying the same data write the same text.
        /// </summary>
        internal static void AppendCustomData(System.Text.StringBuilder into, ItemDrop.ItemData item) {
            Dictionary<string, string> data = item.m_customData;
            if (data == null || data.Count == 0) { return; }
            List<string> keys = new List<string>(data.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (string key in keys) { into.Append('|').Append(key).Append('=').Append(data[key]); }
        }

        private static string PrefabName(ItemDrop.ItemData item) {
            return item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
        }

        private static bool SameCustomData(Dictionary<string, string> a, Dictionary<string, string> b) {
            int countA = a != null ? a.Count : 0;
            int countB = b != null ? b.Count : 0;
            if (countA != countB) { return false; }
            if (countA == 0) { return true; }
            foreach (KeyValuePair<string, string> entry in a) {
                if (!b.TryGetValue(entry.Key, out string value) || value != entry.Value) { return false; }
            }
            return true;
        }
    }
}
