using HarmonyLib;

namespace DvergerAutomation {
    // Every fuel-burning piece registers itself as it wakes, so a Lighter can find the ones in its range
    // without overlapping the whole base (see FuelBurners). FuelBurners.Register leaves out placement ghosts
    // and anything that does not burn a refillable fuel.

    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
    internal static class Fireplace_Awake_Lighter_Patch {
        private static void Postfix(Fireplace __instance) {
            FuelBurners.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(CookingStation), nameof(CookingStation.Awake))]
    internal static class CookingStation_Awake_Lighter_Patch {
        private static void Postfix(CookingStation __instance) {
            FuelBurners.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
    internal static class Smelter_Awake_Lighter_Patch {
        private static void Postfix(Smelter __instance) {
            FuelBurners.Register(__instance);
        }
    }
}
