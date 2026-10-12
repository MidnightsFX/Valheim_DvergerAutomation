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
