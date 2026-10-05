using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    // A guest's moves, picked up where the inventory screen funnels them: every way vanilla lets a
    // player move something into, out of or around a chest ends in one of the six methods below. For a
    // guest each is answered with a request to the chest's owner (see SharedChestRequests) and the
    // original is skipped, since it would only edit the guest's copy.
    //
    // MultiUserChest listens lower down, on Inventory.AddItem, and works out from there what move it
    // must be part of. Listening here means being told: which inventory it is from, which it is going
    // to, how much, and to which slot.

    internal static class SharedChestIntents {
        private static readonly Vector2i Anywhere = new Vector2i(-1, -1);

        /// <summary>
        /// A drop onto a grid slot: the end of every drag, split or not. True when the drag is finished
        /// with - which vanilla takes as its cue to let go of the item.
        /// </summary>
        internal static bool Drop(Container chest, Inventory from, Inventory to, ItemDrop.ItemData item, int amount, Vector2i pos) {
            Inventory replica = chest.m_inventory;
            if (from == replica && to == replica) { return SharedChestRequests.Move(chest, item, pos, amount); }

            // As in vanilla, a drop that only partly fits is not finished: what went is sent, and the
            // rest stays on the cursor.
            amount = Mathf.Min(amount, item.m_stack);
            if (to == replica) { return amount > 0 && SharedChestRequests.Add(chest, from, item, amount, pos) == amount; }

            // Out of the chest, onto a slot on the player's side: empty, something to trade places with,
            // or a stack to top up.
            ItemDrop.ItemData inSlot = to.GetItemAt(pos.x, pos.y);
            if (inSlot == null) {
                return SharedChestRequests.Remove(chest, item, amount, SharedChestRequests.Destination.Slot, to, pos, null);
            }
            if (SharedChestOps.ShouldSwap(inSlot, item, amount == item.m_stack)) {
                return SharedChestRequests.Remove(chest, item, amount, SharedChestRequests.Destination.Slot, to, pos, inSlot);
            }
            int fits = Mathf.Min(SharedChestOps.StackSpace(inSlot, item), amount);
            return SharedChestRequests.Remove(chest, item, fits, SharedChestRequests.Destination.Slot, to, pos, null) && fits == amount;
        }

        /// <summary>Take All: everything in the chest that the player's side has room for.</summary>
        internal static void TakeAll(Container chest, Inventory to) {
            // Planned on a copy, so each stack is asked for knowing what the ones before it will use up.
            Inventory room = SharedChestPreview.Other(to);
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(chest.m_inventory.GetAllItems())) {
                // Vanilla keeps each stack in the slot it had in the chest when that slot is free.
                Vector2i pos = item.m_gridPos;
                bool sameSlot = pos.x < room.GetWidth() && pos.y < room.GetHeight() && room.GetItemAt(pos.x, pos.y) == null;
                if (sameSlot) {
                    SharedChestOps.ApplyAdd(room, item, pos, wholeStack: false, live: false, out _);
                    SharedChestRequests.Remove(chest, item, item.m_stack, SharedChestRequests.Destination.Slot, to, pos, null);
                    continue;
                }
                int fits = AutoStore.AddMeasured(room, item, item.m_stack);
                if (fits > 0) { SharedChestRequests.Remove(chest, item, fits, SharedChestRequests.Destination.Any, to, Anywhere, null); }
            }
        }

        /// <summary>
        /// Place Stacks: everything on the player's side that the chest already holds some of. Returns
        /// how many units were sent. Does not need the chest to be on screen.
        /// </summary>
        internal static int StackAll(Container chest, Inventory from, bool message) {
            Player player = Player.m_localPlayer;
            int sent = 0;
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(from.GetAllItems())) {
                // Asked afresh for each item: every request sent changes what the chest will hold.
                if (!SharedChestPreview.Chest(chest).ContainsItemByName(item.m_shared.m_name)) { continue; }
                if (player != null && player.IsItemEquiped(item)) { continue; }
                // Sends what fits and leaves the rest of the stack where it was.
                sent += SharedChestRequests.Add(chest, from, item, item.m_stack, Anywhere);
            }

            // The rest of what vanilla's StackAll does besides moving items.
            if (message && player != null) {
                player.Message(MessageHud.MessageType.Center, sent > 0 ? "$msg_stackall " + sent : "$msg_stackall_none");
            }
            if (Game.instance != null) { Game.instance.IncrementPlayerStat(PlayerStatType.PlaceStacks); }
            return sent;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(InventoryGrid), nameof(InventoryGrid.DropItem))]
    internal static class InventoryGrid_DropItem_SharedChest_Patch {
        private static bool Prefix(InventoryGrid __instance, Inventory fromInventory, ItemDrop.ItemData item, int amount, Vector2i pos, ref bool __result) {
            Container chest = SharedChests.GuestContainer();
            if (chest == null) { return true; }
            Inventory to = __instance.m_inventory;
            if (fromInventory != chest.m_inventory && to != chest.m_inventory) { return true; }
            __result = SharedChestIntents.Drop(chest, fromInventory, to, item, amount, pos);
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveItemToThis), new[] { typeof(Inventory), typeof(ItemDrop.ItemData) })]
    internal static class Inventory_MoveItemToThis_SharedChest_Patch {
        // The quick move: a whole stack sent across to the first place it fits.
        private static bool Prefix(Inventory __instance, Inventory fromInventory, ItemDrop.ItemData item) {
            Container chest = SharedChests.GuestContainer();
            if (chest == null || item == null) { return true; }
            Inventory replica = chest.m_inventory;
            if (__instance == replica && fromInventory != replica) {
                SharedChestRequests.Add(chest, fromInventory, item, item.m_stack, new Vector2i(-1, -1));
                return false;
            }
            if (fromInventory == replica && __instance != replica) {
                int fits = AutoStore.AddMeasured(SharedChestPreview.Other(__instance), item, item.m_stack);
                if (fits > 0) {
                    SharedChestRequests.Remove(chest, item, fits, SharedChestRequests.Destination.Any, __instance, new Vector2i(-1, -1), null);
                }
                return false;
            }
            return true;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.MoveAll))]
    internal static class Inventory_MoveAll_SharedChest_Patch {
        private static bool Prefix(Inventory __instance, Inventory fromInventory) {
            Container chest = SharedChests.GuestContainer();
            if (chest == null || fromInventory != chest.m_inventory || __instance == fromInventory) { return true; }
            SharedChestIntents.TakeAll(chest, __instance);
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.StackAll))]
    internal static class Inventory_StackAll_SharedChest_Patch {
        private static bool Prefix(Inventory __instance, Inventory fromInventory, bool message, ref int __result) {
            Container chest = SharedChests.GuestContainer();
            if (chest == null || __instance != chest.m_inventory || fromInventory == __instance) { return true; }
            __result = SharedChestIntents.StackAll(chest, fromInventory, message);
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Container), nameof(Container.RPC_StackResponse))]
    internal static class Container_RPC_StackResponse_SharedChest_Patch {
        // Holding Use on an open chest places stacks into it, then closes the panel half a second
        // later - and the owner's go-ahead for the stacking can arrive after that, or after the player
        // has walked off. Vanilla then stacks into a chest that is no longer on screen, and the patch
        // above, which only knows the chest on screen, would let it: the items would leave the pack for
        // a copy of the chest that is never saved. So a go-ahead for a chest this client does not own is
        // answered here, where the chest is known whatever the panel is doing.
        //
        // That includes the vanilla case of a chest nobody had open, handed over along with the
        // go-ahead: the go-ahead usually arrives first, the requests go to the old owner, come back as
        // no longer theirs, and are sent again - to this client, by then the owner.
        private static bool Prefix(Container __instance, bool granted) {
            if (!granted || !SharedChests.Active || !SharedChests.IsShareable(__instance) || __instance.m_nview.IsOwner()) { return true; }
            Player player = Player.m_localPlayer;
            if (player == null) { return true; }

            if (SharedChestIntents.StackAll(__instance, player.GetInventory(), message: true) > 0 && InventoryGui.m_instance != null) {
                InventoryGui.m_instance.m_moveItemEffects.Create(__instance.transform.position, Quaternion.identity);
            }
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem), new[] { typeof(Inventory), typeof(ItemDrop.ItemData), typeof(int) })]
    internal static class Humanoid_DropItem_SharedChest_Patch {
        // Dragged out of the chest and let go of outside the panel: it goes to the ground at the
        // player's feet, once the owner has taken it out.
        private static bool Prefix(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item, int amount, ref bool __result) {
            if (inventory == null || item == null || __instance != Player.m_localPlayer) { return true; }
            Container chest = SharedChests.GuestContainer();
            if (chest == null || inventory != chest.m_inventory) { return true; }
            // Vanilla refuses these with a message of its own, before it touches the inventory.
            if (amount == 0 || item.m_shared.m_questItem) { return true; }

            __result = SharedChestRequests.Remove(chest, item, amount, SharedChestRequests.Destination.Ground, null, new Vector2i(-1, -1), null);
            return false;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnRightClickItem))]
    internal static class InventoryGui_OnRightClickItem_SharedChest_Patch {
        // Right-clicking food or mead in a chest eats it straight out of the chest. For a guest one unit
        // is asked for and eaten when it arrives. Anything else right-clicked in a chest does nothing in
        // vanilla either.
        private static bool Prefix(InventoryGrid grid, ItemDrop.ItemData item) {
            Container chest = SharedChests.GuestContainer();
            if (chest == null || grid == null || grid.GetInventory() != chest.m_inventory) { return true; }
            Player player = Player.m_localPlayer;
            if (item == null || player == null || SharedChestRequests.ConsumePending) { return false; }
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) { return false; }
            // Says why not, the same as trying to eat it would.
            if (!player.CanConsumeItem(item, checkWorldLevel: true)) { return false; }

            SharedChestRequests.Remove(chest, item, 1, SharedChestRequests.Destination.Consume, null, new Vector2i(-1, -1), null);
            return false;
        }
    }
}
