using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Moving items in and out of inventories by measured amounts: what every feature that fills or empties
    /// a chest for the player shares, so each one counts and removes the same way.
    /// </summary>
    internal static class InventoryOps {
        /// <summary>How many of a material the inventory holds, by <see cref="ItemIdentity.Matches"/>.</summary>
        internal static int CountMatching(Inventory inv, string name, int quality) {
            if (inv == null) { return 0; }
            int held = 0;
            foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                if (ItemIdentity.Matches(item, name, quality)) { held += item.m_stack; }
            }
            return held;
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> of a material, stack by stack, and returns how many left
        /// the inventory. Removed per instance rather than by name so protected items are stepped over -
        /// vanilla's <c>Inventory.RemoveItem(string, ...)</c> would happily eat an enchanted sword - and only
        /// what a removal confirmed is counted, so a mod that refuses one costs nothing.
        ///
        /// With <paramref name="sent"/> given, a copy of each slice taken goes into it, to travel to another
        /// client: an item that could not be rebuilt there is left where it is, and the slices stop at what one
        /// message can carry.
        /// </summary>
        internal static int Take(Inventory inv, string name, int quality, int amount, List<ItemDrop.ItemData> sent = null) {
            if (inv == null) { return 0; }
            int removed = 0;
            // GetAllItems hands back the live backing list and emptied stacks drop out of it, so walk it
            // backwards.
            List<ItemDrop.ItemData> items = inv.GetAllItems();
            for (int i = items.Count - 1; i >= 0 && amount > 0; --i) {
                if (sent != null && sent.Count >= ItemCodec.MaxItems) { break; }
                ItemDrop.ItemData item = items[i];
                if (!ItemIdentity.Matches(item, name, quality)) { continue; }
                if (sent != null && !ItemIdentity.CanTravel(item)) { continue; }

                int take = Mathf.Min(item.m_stack, amount);
                ItemDrop.ItemData slice = sent != null ? item.Clone() : null;
                if (!inv.RemoveItem(item, take)) { continue; }
                if (slice != null) {
                    slice.m_stack = take;
                    sent.Add(slice);
                }
                amount -= take;
                removed += take;
            }
            return removed;
        }

        /// <summary>
        /// Moves up to <paramref name="amount"/> units of <paramref name="item"/> out of
        /// <paramref name="from"/> and into <paramref name="to"/>, returning how many actually landed.
        /// Partial moves are the normal case - that is what lets one stack of 50 wood spread across three
        /// chests.
        /// </summary>
        internal static int MoveMeasured(Inventory from, ItemDrop.ItemData item, Inventory to, int amount) {
            int landed = AddMeasured(to, item, amount);
            if (landed <= 0) { return 0; }

            // No yield or RPC between the add and this removal, so the units never exist twice.
            from.RemoveItem(item, landed);
            return landed;
        }

        /// <summary>
        /// Puts up to <paramref name="amount"/> units of a copy of <paramref name="item"/> into
        /// <paramref name="to"/> and returns how many landed. The item handed in is left as it was, for a
        /// caller that holds it outside any inventory.
        ///
        /// The amount is measured across the add rather than taken from <c>AddItem</c>'s return value: for
        /// stackables it merges into existing stacks as it goes and can still report failure, so trusting it
        /// would either strand units or remove more from the source than ever arrived.
        /// </summary>
        internal static int AddMeasured(Inventory to, ItemDrop.ItemData item, int amount) {
            amount = Mathf.Min(amount, Mathf.Min(item.m_stack, FreeSpaceFor(to, item)));
            if (amount <= 0) { return 0; }

            // A clone, because AddItem takes ownership of whatever instance it is handed. Clone carries
            // durability, quality, crafter and Epic Loot's m_customData across.
            string name = item.m_shared.m_name;
            ItemDrop.ItemData slice = item.Clone();
            slice.m_stack = amount;

            int before = to.CountItems(name, -1, matchWorldLevel: false);
            to.AddItem(slice);
            return Mathf.Max(0, to.CountItems(name, -1, matchWorldLevel: false) - before);
        }

        /// <summary>
        /// How much of <paramref name="item"/> the inventory can take: room on top of matching stacks plus
        /// whole empty slots. Hand-rolled rather than <c>Inventory.FindFreeStackSpace</c>, which ignores
        /// quality and so over-reports for gear.
        /// </summary>
        internal static int FreeSpaceFor(Inventory inv, ItemDrop.ItemData item) {
            int max = Mathf.Max(1, item.m_shared.m_maxStackSize);
            // Clamped: GetEmptySlots is a plain width*height - count, and Container.UpdateRows grows the
            // grid to fit an over-full ZDO, so it can read negative.
            int space = Mathf.Max(0, inv.GetEmptySlots()) * max;
            foreach (ItemDrop.ItemData have in inv.GetAllItems()) {
                if (have != null && have.m_stack < max && have.IsSameType(item)) {
                    space += max - have.m_stack;
                }
            }
            return space;
        }
    }
}
