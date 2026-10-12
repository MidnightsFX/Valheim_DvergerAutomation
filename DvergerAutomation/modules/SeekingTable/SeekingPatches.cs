using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace DvergerAutomation {
    // ---- the window's life ------------------------------------------------------------
    // None of these are shared-chest patches: the Seeking Table works the same with MultiUserChest.

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Update))]
    internal static class InventoryGui_Update_SeekingTable_Patch {
        // After the game has run its own panels for the frame, so the window draws over a settled screen.
        private static void Postfix(InventoryGui __instance) {
            try {
                SeekingTablePanel.Update(__instance);
            } catch (Exception ex) {
                Logger.LogError($"[Seeking Table] updating the window failed: {ex}");
                SeekingTablePanel.Close();
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class InventoryGui_Hide_SeekingTable_Patch {
        private static void Postfix() {
            SeekingTablePanel.Close();
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnDestroy))]
    internal static class InventoryGui_OnDestroy_SeekingTable_Patch {
        // Logging out takes the inventory screen down, and the window was built on it.
        private static void Postfix() {
            SeekingTablePanel.Forget();
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnSplitOk))]
    internal static class InventoryGui_OnSplitOk_SeekingTable_Patch {
        // The amount dialog answered for the window. The game would put that many of the display copy on
        // the cursor; the window takes them out of the chests instead. Enter in the dialog lands here too.
        private static bool Prefix(InventoryGui __instance) {
            if (!SeekingTablePanel.IsSplitMarker(__instance.m_splitInventory)) { return true; }
            int amount = (int)__instance.m_splitDialog.m_splitSlider.value;
            __instance.m_splitItem = null;
            __instance.m_splitInventory = null;
            __instance.HideSplitDialog();
            SeekingTablePanel.TakeFromDialog(amount);
            return false;
        }
    }

    [HarmonyPatch(typeof(Game), "Start")]
    internal static class Game_Start_SeekingTable_Patch {
        private static void Postfix() {
            SeekingRetrieval.Reset();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Update))]
    internal static class Game_Update_SeekingTable_Patch {
        // Game's, not the inventory screen's: a take waiting on a chest's new owner is sent whether or not
        // the window is still up.
        private static void Postfix() {
            try {
                SeekingRetrieval.Tick();
            } catch (Exception ex) {
                Logger.LogError($"[Seeking Table] retrying a take failed: {ex}");
            }
        }
    }

    // ---- the controller ------------------------------------------------------------------
    // The game gives the chest panel controller focus as group 0, but only while a chest is open: with
    // none, it sends focus straight back to the player's grid. The window sits in the chest's slot, so
    // the three places that ask "is a chest open?" are made to count the window as one. IsContainerOpen
    // itself is left alone - other mods call it and then reach for the chest, which would be null.

    internal static class SeekingFocus {
        internal static bool ContainerOrSeekOpen(InventoryGui gui) {
            return gui.IsContainerOpen() || SeekingTablePanel.IsOpen;
        }

        // Swaps every IsContainerOpen() call for ContainerOrSeekOpen, which takes the same argument off the
        // stack. A game update that moved the call costs the window its controller focus, with a warning,
        // rather than the inventory screen.
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, string where) {
            MethodInfo isOpen = AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.IsContainerOpen));
            MethodInfo either = AccessTools.Method(typeof(SeekingFocus), nameof(ContainerOrSeekOpen));
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);
            bool found = false;
            foreach (CodeInstruction instruction in code) {
                if (!instruction.Calls(isOpen)) { continue; }
                instruction.opcode = OpCodes.Call;
                instruction.operand = either;
                found = true;
            }
            if (!found) {
                Logger.LogWarning($"[Seeking Table] could not find the open-chest check in InventoryGui.{where}; a controller may not reach the table's window.");
            }
            return code;
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateGamepad))]
    internal static class InventoryGui_UpdateGamepad_SeekingTable_Patch {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            return SeekingFocus.Rewrite(instructions, nameof(InventoryGui.UpdateGamepad));
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetActiveGroup), new[] { typeof(int), typeof(bool) })]
    internal static class InventoryGui_SetActiveGroup_SeekingTable_Patch {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            return SeekingFocus.Rewrite(instructions, nameof(InventoryGui.SetActiveGroup));
        }

        // The game switches the hidden chest panel's focus; the window's follows it.
        private static void Postfix(InventoryGui __instance) {
            SeekingTablePanel.SyncFocus(__instance);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.MoveToLowerInventoryGrid))]
    internal static class InventoryGui_MoveToLowerInventoryGrid_SeekingTable_Patch {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
            return SeekingFocus.Rewrite(instructions, nameof(InventoryGui.MoveToLowerInventoryGrid));
        }

        private static void Postfix(InventoryGui __instance) {
            SeekingTablePanel.EnterFromAbove(__instance);
        }
    }
}
