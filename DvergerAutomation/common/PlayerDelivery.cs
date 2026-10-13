using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Gets an item that has nowhere else to go to the local player: into their pack, and what does not fit
    /// at their feet. For items coming back from another player's chest - unspent crafting materials, the
    /// answer to a shared-chest move, a sort that could not be filed.
    ///
    /// With no player to give it to - dead, or between worlds - the item waits here until there is one.
    /// </summary>
    internal static class PlayerDelivery {
        private struct Parked {
            internal ItemDrop.ItemData Item;
            internal bool AsIfDropped;
        }

        private static readonly List<Parked> Waiting = new List<Parked>();
        private static readonly List<Parked> Scratch = new List<Parked>();

        // The crafting panel counts what the player carries, and redrawing it rebuilds its whole recipe
        // list. Take All brings one answer per stack, so it is redrawn once a frame, not once an item.
        private static bool packChanged;

        internal static void Reset() {
            Waiting.Clear();
            packChanged = false;
        }

        /// <summary>
        /// Into <paramref name="preferred"/> when given, then anywhere in the pack, then the ground. With
        /// <paramref name="asIfDropped"/> what lands on the ground is dropped the way vanilla drops an item
        /// from the inventory - thrown, with its sound and message, and not picked straight back up - as the
        /// player's own move would have; otherwise it is set down quietly at their feet.
        /// </summary>
        internal static void Give(ItemDrop.ItemData item, Inventory preferred = null, bool asIfDropped = false) {
            if (item == null || item.m_stack <= 0) { return; }
            Player player = Player.m_localPlayer;
            if (player == null) {
                Waiting.Add(new Parked { Item = item, AsIfDropped = asIfDropped });
                return;
            }
            Inventory pack = player.GetInventory();
            Inventory first = preferred ?? pack;
            int left = item.m_stack - InventoryOps.AddMeasured(first, item, item.m_stack);
            if (left > 0 && first != pack) { left -= InventoryOps.AddMeasured(pack, item, left); }
            if (left > 0) { Drop(player, item, left, asIfDropped); }
            PackChanged();
        }

        /// <summary>Straight to the ground at the player's feet, as if they had dropped it.</summary>
        internal static void DropAtFeet(ItemDrop.ItemData item) {
            Player player = Player.m_localPlayer;
            if (player == null) {
                Waiting.Add(new Parked { Item = item, AsIfDropped = true });
                return;
            }
            Drop(player, item, item.m_stack, asIfDropped: true);
        }

        private static void Drop(Player player, ItemDrop.ItemData item, int amount, bool asIfDropped) {
            if (amount <= 0) { return; }
            if (!ItemIdentity.CanTravel(item)) {
                Logger.LogWarning($"[Storage] {amount} {item.m_shared.m_name} had nowhere to go and could not be dropped.");
                return;
            }
            if (!asIfDropped) {
                ItemDrop.DropItem(item, amount, player.transform.position + player.transform.forward + Vector3.up, Quaternion.identity);
                return;
            }
            // The tail of vanilla's Humanoid.DropItem, from the point where the item has left its inventory.
            Transform at = player.transform;
            ItemDrop drop = ItemDrop.DropItem(item, amount, at.position + at.forward + at.up, at.rotation);
            drop.OnPlayerDrop();
            Rigidbody body = drop.GetComponent<Rigidbody>();
            if (body != null) { body.linearVelocity = (at.forward + Vector3.up) * (item.GetWeight() >= 300f ? 0.5f : 5f); }
            player.m_zanim.SetTrigger("interact");
            player.m_dropEffects.Create(at.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
            player.Message(MessageHud.MessageType.TopLeft, "$msg_dropped " + drop.m_itemData.m_shared.m_name, drop.m_itemData.m_stack, drop.m_itemData.GetIcon());
        }

        /// <summary>The player's pack changed outside the inventory screen; an open crafting panel is redrawn next frame.</summary>
        internal static void PackChanged() {
            packChanged = true;
        }

        internal static void Tick() {
            if (Waiting.Count > 0 && Player.m_localPlayer != null) {
                Scratch.Clear();
                Scratch.AddRange(Waiting);
                Waiting.Clear();
                foreach (Parked parked in Scratch) { Give(parked.Item, null, parked.AsIfDropped); }
                Scratch.Clear();
            }
            if (packChanged) {
                packChanged = false;
                // A closed panel is rebuilt when it next opens.
                InventoryGui gui = InventoryGui.m_instance;
                if (gui != null && Player.m_localPlayer != null && InventoryGui.IsVisible()) { gui.UpdateCraftingPanel(); }
            }
        }
    }
}
