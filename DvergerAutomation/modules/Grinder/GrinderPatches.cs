using System;
using HarmonyLib;

namespace DvergerAutomation {
    /// <summary>
    /// Adds the Grinder's status - what it is grinding and how long is left, or why it is waiting - under
    /// the hover text of both its containers, the hopper and the results chest. They are vanilla Containers,
    /// whose hover text is otherwise just their name and the Open prompt.
    /// </summary>
    [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
    internal static class Container_GetHoverText_Grinder_Patch {
        private static void Postfix(Container __instance, ref string __result) {
            try {
                string status = GrinderHub.StatusFor(__instance);
                if (!string.IsNullOrEmpty(status)) { __result += "\n" + status; }
            } catch (Exception ex) {
                Logger.LogWarning($"[Grinder] status for hover text failed: {ex.Message}");
            }
        }
    }
}
