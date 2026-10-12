using System;
using HarmonyLib;

namespace DvergerAutomation {
    /// <summary>
    /// Marks an item picked in the radial, so the Grinder's funnel can tell it from a hotbar key. Both end
    /// in <c>Switch.UseItem</c>, and hotbar keys keep working while a hover radial is open, so whether the
    /// radial is showing says nothing about where the item came from. The radial's item elements are the
    /// only caller of <c>TryUseItemOnInteractable</c>.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.TryUseItemOnInteractable))]
    internal static class Humanoid_TryUseItemOnInteractable_Grinder_Patch {
        private static void Prefix() {
            GrinderHub.FromRadial = true;
        }

        private static Exception Finalizer(Exception __exception) {
            GrinderHub.FromRadial = false;
            return __exception;
        }
    }
}
