using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Harmony patches that let the local player craft (at a linked station) and build (with the Hammer
    /// near an autosorter) using items stored in chests linked by an <see cref="AutomationHub"/>.
    /// Adapted from the well-known "craft from containers" pattern, sourced from the autosorter network
    /// rather than from a container attached to the station.
    /// </summary>
    internal static class CraftFromStoragePatches {
        // Counters marking which UI is currently building its requirement rows. Used to scope the shared
        // Inventory.CountItems postfix so it only augments the displayed counts while a panel is rendering.
        internal static int InCraftRequirements = 0;
        internal static int InBuildRequirements = 0;

        // ---- shared helpers -------------------------------------------------

        internal static int SumContainers(List<Container> pool, string name, int quality) {
            int total = 0;
            foreach (Container container in pool) {
                if (container == null) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }
                // Counted by hand rather than via Inventory.CountItems so enchanted gear is left out of
                // the total, matching what RemoveFromContainers below is willing to spend. Mirrors
                // CountItems(name, quality, matchWorldLevel: true) otherwise.
                foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                    if (!Matches(item, name, quality)) { continue; }
                    total += item.m_stack;
                }
            }
            return total;
        }

        // Shared predicate for the by-name material paths: vanilla's own name/quality/world-level test,
        // plus Epic Loot's magic items, which share m_shared.m_name with their mundane counterpart and
        // must never be spent as one.
        private static bool Matches(ItemDrop.ItemData item, string name, int quality) {
            if (item == null || item.m_shared == null) { return false; }
            if (item.m_shared.m_name != name) { return false; }
            if (quality >= 0 && item.m_quality != quality) { return false; }
            if (item.m_worldLevel < Game.m_worldLevel) { return false; }
            return !EpicLootIntegration.IsProtectedItem(item);
        }

        // Claims ownership before mutating a chest we do not own, so Container.OnContainerChanged -> Save
        // actually persists and syncs the change.
        internal static void ClaimOwnership(Container container) {
            if (container == null) { return; }
            if (container.m_nview != null && container.m_nview.IsValid() && !container.m_nview.IsOwner()) {
                container.m_nview.ClaimOwnership();
            }
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> of <paramref name="name"/> across the pool and returns
        /// how many were actually taken (Epic Loot's inventory provider contract requires the count).
        /// </summary>
        internal static int RemoveFromContainers(List<Container> pool, string name, int amount, int itemQuality) {
            int removed = 0;
            foreach (Container container in pool) {
                if (amount <= 0) { break; }
                if (container == null) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }

                // Removed per instance rather than by name so protected items can be stepped over; vanilla's
                // Inventory.RemoveItem(string, ...) would happily eat them. GetAllItems hands back the live
                // backing list and emptied stacks drop out of it, so walk it backwards.
                bool claimed = false;
                List<ItemDrop.ItemData> items = inv.GetAllItems();
                for (int i = items.Count - 1; i >= 0 && amount > 0; --i) {
                    ItemDrop.ItemData item = items[i];
                    if (!Matches(item, name, itemQuality)) { continue; }

                    if (!claimed) {
                        ClaimOwnership(container);
                        claimed = true;
                    }
                    int take = Mathf.Min(item.m_stack, amount);
                    inv.RemoveItem(item, take);
                    amount -= take;
                    removed += take;
                }
            }
            return removed;
        }
    }

    // ---- station crafting: "can I craft this?" --------------------------------

    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirementItems),
        new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
    internal static class Player_HaveRequirementItems_Patch {
        private static void Postfix(Player __instance, ref bool __result, Recipe piece, bool discover, int qualityLevel, int amount) {
            if (__result || discover) { return; }
            if (__instance == null || __instance != Player.m_localPlayer) { return; }

            CraftingStation station = __instance.GetCurrentCraftingStation();
            if (station == null) { return; }
            List<Container> pool = ContainerNetwork.GetContainersForStation(station);
            if (pool.Count == 0) { return; }

            bool requireOne = piece.m_requireOnlyOneIngredient;
            foreach (Piece.Requirement resource in piece.m_resources) {
                if (resource.m_resItem == null) { continue; }
                string name = resource.m_resItem.m_itemData.m_shared.m_name;
                int needed = resource.GetAmount(qualityLevel) * amount;

                // Mirror vanilla: take the best single-quality stack the player holds, then add containers.
                int playerBest = 0;
                for (int quality = 1; quality < resource.m_resItem.m_itemData.m_shared.m_maxQuality + 1; ++quality) {
                    int count = __instance.m_inventory.CountItems(name, quality);
                    if (count > playerBest) { playerBest = count; }
                }
                int total = playerBest + ContainerNetwork.CountInPool(pool, name);

                if (requireOne) {
                    if (total >= needed) { __result = true; return; }
                } else if (total < needed) {
                    return;
                }
            }

            __result = !requireOne;
        }
    }

    // ---- display counts: crafting panel + build HUD ---------------------------
    // Both route through InventoryGui.SetupRequirement -> player.GetInventory().CountItems(name).

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirementList))]
    internal static class InventoryGui_SetupRequirementList_Patch {
        private static void Prefix() { CraftFromStoragePatches.InCraftRequirements++; }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix() { CraftFromStoragePatches.InCraftRequirements--; }
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.SetupPieceInfo))]
    internal static class Hud_SetupPieceInfo_Patch {
        private static void Prefix() { CraftFromStoragePatches.InBuildRequirements++; }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix() { CraftFromStoragePatches.InBuildRequirements--; }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.CountItems))]
    internal static class Inventory_CountItems_Patch {
        private static void Postfix(Inventory __instance, ref int __result, string name, int quality) {
            if (string.IsNullOrEmpty(name)) { return; }
            Player localPlayer = Player.m_localPlayer;
            if (localPlayer == null || __instance != localPlayer.m_inventory) { return; }

            List<Container> pool;
            if (CraftFromStoragePatches.InCraftRequirements > 0) {
                CraftingStation station = localPlayer.GetCurrentCraftingStation();
                if (station == null) { return; }
                pool = ContainerNetwork.GetContainersForStation(station);
            } else if (CraftFromStoragePatches.InBuildRequirements > 0) {
                pool = ContainerNetwork.GetContainersNearPoint(localPlayer.transform.position);
            } else {
                return;
            }

            if (pool.Count == 0) { return; }
            // The display/availability path always queries quality-agnostic (-1); serve it from the
            // frame-memoized aggregate. Fall back to a live scan for any specific-quality caller.
            __result += quality < 0
                ? ContainerNetwork.CountInPool(pool, name)
                : CraftFromStoragePatches.SumContainers(pool, name, quality);
        }
    }

    // ---- Hammer building: "can I build this?" --------------------------------

    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirements),
        new[] { typeof(Piece), typeof(Player.RequirementMode) })]
    internal static class Player_HaveRequirements_Piece_Patch {
        private static void Postfix(Player __instance, ref bool __result, Piece piece, Player.RequirementMode mode) {
            if (__result || mode != Player.RequirementMode.CanBuild) { return; }
            if (__instance == null || __instance != Player.m_localPlayer) { return; }

            // Only override the resource-shortfall reason for false, never the structural gates below.
            if (piece.m_craftingStation != null
                && CraftingStation.HaveBuildStationInRange(piece.m_craftingStation.m_name, __instance.transform.position) == null
                && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench)) {
                return;
            }
            if (piece.m_dlc != null && piece.m_dlc.Length > 0 && !DLCMan.instance.IsDLCInstalled(piece.m_dlc)) { return; }

            List<Container> pool = ContainerNetwork.GetContainersNearPoint(__instance.transform.position);
            if (pool.Count == 0) { return; }

            foreach (Piece.Requirement resource in piece.m_resources) {
                if (resource.m_resItem == null || resource.m_amount <= 0) { continue; }
                string name = resource.m_resItem.m_itemData.m_shared.m_name;
                int total = __instance.m_inventory.CountItems(name) + ContainerNetwork.CountInPool(pool, name);
                if (total < resource.m_amount) { return; }
            }

            __result = true;
        }
    }

    // ---- consumption: shared by station crafting AND Hammer building ----------

    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    internal static class Player_ConsumeResources_Patch {
        private static void Prefix(Player __instance, Piece.Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier) {
            if (__instance == null || __instance != Player.m_localPlayer) { return; }

            CraftingStation station = __instance.GetCurrentCraftingStation();
            List<Container> pool = station != null
                ? ContainerNetwork.GetContainersForStation(station)
                : ContainerNetwork.GetContainersNearPoint(__instance.transform.position);
            if (pool.Count == 0) { return; }

            foreach (Piece.Requirement requirement in requirements) {
                if (requirement.m_resItem == null) { continue; }
                int amount = requirement.GetAmount(qualityLevel) * multiplier;
                if (amount <= 0) { continue; }
                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                int has = __instance.m_inventory.CountItems(name, itemQuality);
                int shortfall = amount - has;
                if (shortfall > 0) {
                    CraftFromStoragePatches.RemoveFromContainers(pool, name, shortfall, itemQuality);
                }
            }
            // Returns void: vanilla then removes the remainder (what the player actually holds) normally.
        }
    }
}
