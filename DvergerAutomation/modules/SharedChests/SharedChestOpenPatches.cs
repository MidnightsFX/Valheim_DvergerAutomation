using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    // ---- the owner lets a second player in ---------------------------------------
    // Vanilla answers "in use" to anyone asking for a chest its owner has open. Here the owner answers
    // yes without handing the chest over, which makes the asker a guest. A chest nobody has open is
    // still handed over the vanilla way, so a player alone at a chest is its owner as always.

    [SharedChestPatch]
    [HarmonyPatch(typeof(Container), nameof(Container.RPC_RequestOpen))]
    internal static class Container_RPC_RequestOpen_SharedChest_Patch {
        private static bool Prefix(Container __instance, long uid, long playerID) {
            if (!LetsInAsGuest(__instance, uid, playerID)) { return true; }
            __instance.m_nview.InvokeRPC(uid, "RPC_OpenResponse", true);
            return false;
        }

        // The cases vanilla would turn away as in use, and nothing else: no access is still vanilla's
        // to refuse.
        internal static bool LetsInAsGuest(Container container, long uid, long playerID) {
            if (!SharedChests.Active || !SharedChests.IsShareable(container)) { return false; }
            if (!container.m_nview.IsOwner() || uid == ZNet.GetUID()) { return false; }
            bool inUse = container.IsInUse() || (container.m_wagon != null && container.m_wagon.InUse());
            return inUse && container.CheckAccess(playerID);
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Container), nameof(Container.RPC_RequestStack))]
    internal static class Container_RPC_RequestStack_SharedChest_Patch {
        // Holding Use on a chest to place stacks into it asks the same way opening does.
        private static bool Prefix(Container __instance, long uid, long playerID) {
            if (!Container_RPC_RequestOpen_SharedChest_Patch.LetsInAsGuest(__instance, uid, playerID)) { return true; }
            __instance.m_nview.InvokeRPC(uid, "RPC_StackResponse", true);
            return false;
        }
    }

    // ---- the guest's panel ------------------------------------------------------

    [SharedChestPatch]
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateContainer))]
    internal static class InventoryGui_UpdateContainer_SharedChest_Patch {
        // UpdateContainer draws the chest only while the local player owns it, and hides the panel the
        // moment they do not. The one call that decides it is swapped for SharedChests.CanView. The rest
        // of that branch is harmless for a guest: SetInUse does nothing for a non-owner.
        //
        // Left as vanilla wrote it if the call cannot be found, with a warning - a game update that moved
        // it should cost this feature, not the inventory screen.
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            MethodInfo isOwner = AccessTools.Method(typeof(Container), nameof(Container.IsOwner));
            MethodInfo canView = AccessTools.Method(typeof(SharedChests), nameof(SharedChests.CanView));
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            foreach (CodeInstruction instruction in code) {
                if (!instruction.Calls(isOwner)) { continue; }
                instruction.opcode = OpCodes.Call;
                instruction.operand = canView;
                return code;
            }
            Logger.LogWarning("Shared chests: could not find the owner check in InventoryGui.UpdateContainer; a chest another player has open will show as in use.");
            return code;
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Update))]
    internal static class InventoryGui_Update_SharedChest_Patch {
        // A chest checks its ZDO for changes once a second, which is fine for one nobody is looking at.
        // A guest is watching other players' moves - and waiting on their own - so theirs is checked
        // every frame. The check is a revision compare; it only reloads when something changed.
        private static void Postfix() {
            Container chest = SharedChests.GuestContainer();
            if (chest != null) { chest.CheckForChanges(); }
        }
    }

    [SharedChestPatch]
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.Load), new[] { typeof(ZPackage) })]
    internal static class Inventory_Load_SharedChest_Patch {
        // A reload replaces every item in the inventory with a new instance, and the inventory screen
        // holds on to instances: the item on the cursor and the one in the split dialog. For the chest on
        // screen both are pointed at whatever now sits in the same slot, or dropped if that is no longer
        // the same kind of thing.
        private static void Postfix(Inventory __instance) {
            InventoryGui gui = InventoryGui.m_instance;
            if ((object)gui == null) { return; }
            Container chest = gui.m_currentContainer;
            if ((object)chest == null || chest.m_inventory != __instance) { return; }

            SharedChestRequests.OnChestLoaded(__instance);

            if (gui.m_dragItem != null && gui.m_dragInventory == __instance) {
                ItemDrop.ItemData now = SameSlot(__instance, gui.m_dragItem);
                if (now == null) {
                    gui.SetupDragItem(null, null, 1);
                } else {
                    gui.m_dragItem = now;
                    gui.m_dragAmount = Mathf.Min(gui.m_dragAmount, now.m_stack);
                }
            }
            if (gui.m_splitItem != null && gui.m_splitInventory == __instance) {
                ItemDrop.ItemData now = SameSlot(__instance, gui.m_splitItem);
                if (now == null) {
                    gui.OnSplitCancel();
                } else {
                    gui.m_splitItem = now;
                }
            }
        }

        private static ItemDrop.ItemData SameSlot(Inventory inventory, ItemDrop.ItemData was) {
            ItemDrop.ItemData now = inventory.GetItemAt(was.m_gridPos.x, was.m_gridPos.y);
            return now != null && SharedChestOps.Fingerprint(now) == SharedChestOps.Fingerprint(was) ? now : null;
        }
    }
}
