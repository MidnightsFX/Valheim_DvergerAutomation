using HarmonyLib;

namespace DvergerAutomation {
    /// <summary>
    /// Diverts smelter output into a linked hopper before it becomes a world drop.
    ///
    /// <c>Smelter.Spawn</c> is the single point every finished product passes through - both the
    /// immediate path (<c>m_spawnStack</c> off) and the batched one (<c>QueueProcessed</c> /
    /// <c>SpawnProcessed</c>) end up here - so one prefix covers every station that uses the component.
    /// Patched by string, because the method is private in vanilla and only the publicizer makes the
    /// <c>nameof</c> form compile.
    ///
    /// The caller clears <c>s_spawnOre</c> / <c>s_spawnAmount</c> *after* Spawn returns, so skipping the
    /// original still leaves the smelter's queue correctly emptied.
    /// </summary>
    [HarmonyPatch(typeof(Smelter), "Spawn")]
    internal static class Smelter_Spawn_Patch {
        private static bool Prefix(Smelter __instance, string ore, int stack) {
            try {
                return !HopperNetwork.TryCollect(__instance, ore, stack);
            } catch (System.Exception ex) {
                // Never swallow the product because our own code threw - fall through to vanilla and
                // let it drop on the ground.
                Logger.LogError($"Hopper: collecting smelter output failed: {ex}");
                return true;
            }
        }
    }

    /// <summary>
    /// Takes a stacking station's batch the moment an item joins it, rather than when the batch is full
    /// (see <see cref="HopperNetwork.CollectBatch"/>). <c>QueueProcessed</c> only runs on the station's
    /// owner, inside its own update.
    /// </summary>
    [HarmonyPatch(typeof(Smelter), "QueueProcessed")]
    internal static class Smelter_QueueProcessed_Patch {
        private static void Postfix(Smelter __instance) {
            try {
                HopperNetwork.CollectBatch(__instance);
            } catch (System.Exception ex) {
                // The batch stays in the station, and its next flush spawns it as usual.
                Logger.LogError($"Hopper: collecting a smelter's batch failed: {ex}");
            }
        }
    }

    /// <summary>
    /// Counts stations as they load, so a hopper knows when one may have appeared in its range. A
    /// placement ghost has no ZDO and is left out.
    /// </summary>
    [HarmonyPatch(typeof(Smelter), "Awake")]
    internal static class Smelter_Awake_Patch {
        private static void Postfix(Smelter __instance) {
            if (__instance.m_nview != null && __instance.m_nview.GetZDO() != null) {
                HopperNetwork.NoteStationLoaded();
            }
        }
    }
}
