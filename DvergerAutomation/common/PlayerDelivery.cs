using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Gets an item that has nowhere else to go to the local player: into their pack, and what does not fit
    /// at their feet, dropped the way vanilla drops an item from the inventory. For items coming back from
    /// another player's chest - unspent crafting materials, the answer to a shared-chest move, a sort that
    /// could not be filed.
    ///
    /// With no player to give it to - dead, or between worlds - the item waits here until there is one.
    /// </summary>
    internal static class PlayerDelivery {
        private static readonly List<ItemDrop.ItemData> Waiting = new List<ItemDrop.ItemData>();
        private static readonly List<ItemDrop.ItemData> Scratch = new List<ItemDrop.ItemData>();

        // The crafting panel counts what the player carries, and redrawing it rebuilds its whole recipe
        // list. Take All brings one answer per stack, so it is redrawn once a frame, not once an item.
        private static bool packChanged;

        internal static void Reset() {
            Waiting.Clear();
            packChanged = false;
        }

        /// <summary>
        /// Into <paramref name="preferred"/> when given, then anywhere in the pack, then the ground.
        /// </summary>
        internal static void Give(ItemDrop.ItemData item, Inventory preferred = null) {
            if (item == null || item.m_stack <= 0) { return; }
            Player player = Player.m_localPlayer;
            if (player == null) {
                Waiting.Add(item);
                return;
            }
            Inventory pack = player.GetInventory();
            Inventory first = preferred ?? pack;
            int left = item.m_stack - InventoryOps.AddMeasured(first, item, item.m_stack);
            if (left > 0 && first != pack) { left -= InventoryOps.AddMeasured(pack, item, left); }
            if (left > 0) { Drop(player, item, left); }
            PackChanged();
        }

        /// <summary>Straight to the ground at the player's feet, as if they had dropped it.</summary>
        internal static void DropAtFeet(ItemDrop.ItemData item) {
            Player player = Player.m_localPlayer;
            if (player == null) {
                Waiting.Add(item);
                return;
            }
            Drop(player, item, item.m_stack);
        }

        // The tail of vanilla's Humanoid.DropItem, from the point where the item has left its inventory.
        private static void Drop(Player player, ItemDrop.ItemData item, int amount) {
            if (amount <= 0) { return; }
            if (!ItemIdentity.CanTravel(item)) {
                Logger.LogWarning($"[Storage] {amount} {item.m_shared.m_name} had nowhere to go and could not be dropped.");
                return;
            }
            Transform at = player.transform;
            ItemDrop drop = ItemDrop.DropItem(item, amount, at.position + at.forward + at.up, at.rotation);
            drop.OnPlayerDrop();
            Rigidbody body = drop.GetComponent<Rigidbody>();
            if (body != null) { body.linearVelocity = (at.forward + Vector3.up) * (item.GetWeight() >= 300f ? 0.5f : 5f); }
            player.m_zanim.SetTrigger("interact");
            player.m_dropEffects.Create(at.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
            player.Message(MessageHud.MessageType.TopLeft, "$msg_dropped " + drop.m_itemData.m_shared.m_name, drop.m_itemData.m_stack, drop.m_itemData.GetIcon());
        }

        /// <summary>The player's pack changed outside the inventory screen; the crafting panel is redrawn next frame.</summary>
        internal static void PackChanged() {
            packChanged = true;
        }

        internal static void Tick() {
            if (Waiting.Count > 0 && Player.m_localPlayer != null) {
                Scratch.Clear();
                Scratch.AddRange(Waiting);
                Waiting.Clear();
                foreach (ItemDrop.ItemData item in Scratch) { Give(item); }
                Scratch.Clear();
            }
            if (packChanged) {
                packChanged = false;
                InventoryGui gui = InventoryGui.m_instance;
                if (gui != null && Player.m_localPlayer != null) { gui.UpdateCraftingPanel(); }
            }
        }
    }
}
