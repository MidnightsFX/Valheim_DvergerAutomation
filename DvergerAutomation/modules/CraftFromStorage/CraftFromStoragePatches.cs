using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Harmony patches that let the local player craft (at a linked station) and build (with the Hammer
    /// near an autosorter) using items stored in chests linked by an <see cref="AutomationHub"/>.
    /// Adapted from the well-known "craft from containers" pattern, sourced from the autosorter network
    /// rather than from a container attached to the station.
    /// </summary>
    internal static class CraftFromStoragePatches {
        // The container pool behind whichever requirement panel is being rendered right now - the
        // crafting panel's ingredient rows or the build HUD's piece cost - and null outside either.
        // Set by the SetupRequirementList / SetupPieceInfo prefixes, read by the SetupRequirement
        // postfix that annotates each row. Both panels redraw every frame, so nothing here persists.
        internal static List<Container> DisplayPool;

        // Tint of the "+N" storage figure on a requirement row. A rich-text span so it keeps its own
        // colour while vanilla flashes the required amount red around it.
        internal const string StorageCountColor = "#9BDB9B";

        // ---- shared helpers -------------------------------------------------

        // Shared predicate for the by-name material paths: vanilla's own name/quality/world-level test,
        // plus Epic Loot's magic items, which share m_shared.m_name with their mundane counterpart and
        // must never be spent as one.
        internal static bool Matches(ItemDrop.ItemData item, string name, int quality) {
            if (item == null || item.m_shared == null) { return false; }
            if (item.m_shared.m_name != name) { return false; }
            if (quality >= 0 && item.m_quality != quality) { return false; }
            if (item.m_worldLevel < Game.m_worldLevel) { return false; }
            return !EpicLootIntegration.IsProtectedItem(item);
        }

        /// <summary>
        /// Whether a recipe ingredient is charged at this station, mirroring vanilla's filter in
        /// <c>Player.HaveRequirementItems</c> / <c>ConsumeResources</c>. Since 1.0 a recipe can carry upgrader
        /// resources: they are the only ingredients at an upgrader station and are ignored everywhere else.
        /// Their <c>GetAmount</c> is never 0, so skipping this demands (and spends) an item vanilla never asks for.
        /// </summary>
        internal static bool AppliesAtStation(Piece.Requirement requirement, CraftingStation station) {
            if (requirement == null || requirement.m_resItem == null) { return false; }
            return station != null ? station.m_upgrader == requirement.m_upgraderResource : !requirement.m_upgraderResource;
        }

        /// <summary>
        /// True when a chest should be left alone because someone has it open. Taking ownership out from
        /// under them blanks their container panel, and their <c>m_inUse</c> can then never clear, because
        /// <c>Container.SetInUse</c> is itself owner-gated - and a stuck <c>m_inUse</c> blocks
        /// <c>Container.Load</c>, so their copy of the chest stops updating for good. <c>IsInUse()</c> only
        /// reports the *local* field, so a remote player's session is visible only through the flag the
        /// owner mirrors into the ZDO. The owner also refuses to hand over a chest that is open (see
        /// <see cref="StorageOwnership"/>), so this is the requester not bothering to ask.
        ///
        /// A boat's hold or a cart's bed is also busy while someone else is aboard, pulling or riding it:
        /// the hold shares the vehicle's ZDO, so taking it takes the whole vehicle (see
        /// <see cref="VehicleStorage.InUse"/>).
        /// </summary>
        internal static bool IsBusy(Container container) {
            if (container.IsInUse()) { return true; }
            ZNetView nview = container.m_nview;
            if (nview == null || !nview.IsValid()) { return true; }
            if (nview.GetZDO().GetInt(ZDOVars.s_inUse) == 1) { return true; }
            return VehicleStorage.InUse(container);
        }

        /// <summary>
        /// What the storage pool has to supply for <paramref name="requirements"/>: each ingredient's
        /// amount less what the player carries, by shared name. Mirrors vanilla's
        /// <c>Player.ConsumeResources</c>, which the consumption prefix below runs ahead of, so the check
        /// before an action and the spend inside it agree on every number.
        /// </summary>
        internal static List<KeyValuePair<string, int>> Shortfalls(Player player, Piece.Requirement[] requirements, CraftingStation station, int qualityLevel, int itemQuality, int multiplier) {
            List<KeyValuePair<string, int>> result = new List<KeyValuePair<string, int>>();
            if (requirements == null) { return result; }
            foreach (Piece.Requirement requirement in requirements) {
                if (!AppliesAtStation(requirement, station)) { continue; }
                int amount = requirement.GetAmount(qualityLevel) * multiplier;
                if (amount <= 0) { continue; }
                string name = requirement.m_resItem.m_itemData.m_shared.m_name;
                int shortfall = amount - player.m_inventory.CountItems(name, itemQuality);
                if (shortfall > 0) { result.Add(new KeyValuePair<string, int>(name, shortfall)); }
            }
            return result;
        }

        /// <summary>
        /// Removes up to <paramref name="amount"/> of <paramref name="name"/> across the pool and returns
        /// how many were actually taken (Epic Loot's inventory provider contract requires the count).
        ///
        /// Only from chests this client owns. One owned by someone else is asked for instead (see
        /// <see cref="StorageOwnership"/>) and skipped; the checks in front of crafting and building make
        /// sure the owned ones already cover the amount, so in practice nothing is skipped here.
        /// </summary>
        internal static int RemoveFromContainers(List<Container> pool, string name, int amount, int itemQuality) {
            int removed = 0;
            foreach (Container container in pool) {
                if (amount <= 0) { break; }
                if (container == null) { continue; }
                // Never spend out of a chest someone has open: taking it over wrecks their session (see
                // IsBusy). The aggregate skips the same chests, so availability never promises its stock.
                if (IsBusy(container)) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null || !Holds(inv, name, itemQuality)) { continue; }
                // Reloads the grid too, so the items walked below are the chest's current ones.
                if (!StorageOwnership.TryAcquire(container)) { continue; }

                // Removed per instance rather than by name so protected items can be stepped over; vanilla's
                // Inventory.RemoveItem(string, ...) would happily eat them. GetAllItems hands back the live
                // backing list and emptied stacks drop out of it, so walk it backwards.
                List<ItemDrop.ItemData> items = inv.GetAllItems();
                for (int i = items.Count - 1; i >= 0 && amount > 0; --i) {
                    ItemDrop.ItemData item = items[i];
                    if (!Matches(item, name, itemQuality)) { continue; }

                    int take = Mathf.Min(item.m_stack, amount);
                    inv.RemoveItem(item, take);
                    amount -= take;
                    removed += take;
                }
            }
            // Chest contents just changed: the frame-memoized aggregate is now stale.
            if (removed > 0) { ContainerNetwork.InvalidateItemCounts(); }
            return removed;
        }

        // Read off this client's copy, before asking for the chest: one that holds none of it is not worth
        // a handoff.
        private static bool Holds(Inventory inv, string name, int itemQuality) {
            foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                if (Matches(item, name, itemQuality)) { return true; }
            }
            return false;
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
                if (!CraftFromStoragePatches.AppliesAtStation(resource, station)) { continue; }
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

    // ---- display: crafting panel + build HUD ---------------------------------
    // Both draw each ingredient through the static InventoryGui.SetupRequirement, which prints the
    // required amount and flashes it red when player.GetInventory().CountItems(name) falls short. The
    // two prefixes below record which pool backs the panel being drawn; the SetupRequirement postfix
    // then appends what that pool holds and lifts the red flash once inventory plus storage covers it.
    //
    // Deliberately not done by intercepting Inventory.CountItems for the duration of the panel: other
    // mods print the player's own count on the same row from that call (MyLittleUI's "(N)"), and
    // folding chest stock into it showed them the combined total with no way to tell the two apart.

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirementList))]
    internal static class InventoryGui_SetupRequirementList_Patch {
        private static void Prefix(Player player) {
            CraftFromStoragePatches.DisplayPool = null;
            if (player == null || player != Player.m_localPlayer) { return; }
            // A null station (crafting by hand) resolves to the shared empty list.
            CraftFromStoragePatches.DisplayPool = ContainerNetwork.GetContainersForStation(player.GetCurrentCraftingStation());
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix() { CraftFromStoragePatches.DisplayPool = null; }
    }

    [HarmonyPatch(typeof(Hud), nameof(Hud.SetupPieceInfo))]
    internal static class Hud_SetupPieceInfo_Patch {
        private static void Prefix() {
            Player localPlayer = Player.m_localPlayer;
            CraftFromStoragePatches.DisplayPool = localPlayer != null
                ? ContainerNetwork.GetContainersNearPoint(localPlayer.transform.position)
                : null;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix() { CraftFromStoragePatches.DisplayPool = null; }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.SetupRequirement))]
    internal static class InventoryGui_SetupRequirement_Patch {
        // Low: mods that annotate this row with the player's own count (MyLittleUI) run at Normal and
        // int.TryParse the text before touching it, so the storage figure has to land after theirs.
        [HarmonyPriority(Priority.Low)]
        private static void Postfix(Transform elementRoot, Piece.Requirement req, Player player, int quality, int craftMultiplier, bool __result) {
            // False means the row was hidden (nothing required at this quality).
            if (!__result) { return; }
            List<Container> pool = CraftFromStoragePatches.DisplayPool;
            if (pool == null || pool.Count == 0) { return; }
            if (req == null || req.m_resItem == null) { return; }
            if (player == null || player != Player.m_localPlayer) { return; }

            string name = req.m_resItem.m_itemData.m_shared.m_name;
            int stored = ContainerNetwork.CountInPool(pool, name);
            if (stored <= 0) { return; }

            Transform amountTransform = elementRoot.Find("res_amount");
            TMP_Text amountText = amountTransform != null ? amountTransform.GetComponent<TMP_Text>() : null;
            if (amountText == null) { return; }

            // Vanilla judged the red flash on the player's own count alone. Storage covering the rest
            // should read as craftable, the same way the craft button already does.
            int needed = req.GetAmount(quality) * craftMultiplier;
            if (player.GetInventory().CountItems(name) + stored >= needed) {
                amountText.color = Color.white;
            }

            if (!ValConfig.ShowStorageCounts.Value) { return; }
            amountText.text += " <color=" + CraftFromStoragePatches.StorageCountColor + ">+" + stored + "</color>";

            // Vanilla resets the tooltip to the bare item name every draw, so this never accumulates.
            UITooltip tooltip = elementRoot.GetComponent<UITooltip>();
            if (tooltip != null) {
                tooltip.m_text += "\n<color=" + CraftFromStoragePatches.StorageCountColor + ">"
                    + Localization.instance.Localize("$DA_storage_count", stored.ToString()) + "</color>";
            }
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

    // ---- waiting for storage: in front of station crafting and Hammer building ----
    // Availability counts every linked chest, but only chests this client owns can be spent from (see
    // StorageOwnership). The remaining gap is closed here, before the action: if part of what it needs is
    // still in another player's chest, the action is held back, that chest is asked for, and the player
    // tries again a moment later. Checked this early because consumption itself cannot fail - vanilla
    // hands out the crafted item or places the piece and only then spends.

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class InventoryGui_DoCrafting_Patch {
        private static bool Prefix(InventoryGui __instance, Player player) {
            try {
                if (player == null || player != Player.m_localPlayer) { return true; }
                Recipe recipe = __instance.m_craftRecipe;
                // A one-of-these recipe spends only the ingredient the player carries (singleReqItem).
                if (recipe == null || recipe.m_requireOnlyOneIngredient) { return true; }
                if (player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)) { return true; }

                // The quality and multiplier DoCrafting hands ConsumeResources.
                CraftingStation station = player.GetCurrentCraftingStation();
                int quality = __instance.m_craftUpgradeItem == null ? 1 : __instance.m_craftUpgradeItem.m_quality + 1;
                int multiplier = __instance.m_multiCrafting ? __instance.m_multiCraftAmount : 1;
                if (!StorageOwnership.MustWait(player, ContainerNetwork.GetContainersForStation(station), recipe.m_resources, station, quality, multiplier)) {
                    return true;
                }
                player.Message(MessageHud.MessageType.Center, "$DA_storage_waiting");
                return false;
            } catch (System.Exception ex) {
                // Let vanilla carry on: the worst case is the old behaviour, never a lost craft.
                Logger.LogError($"Craft from storage: checking storage before crafting failed: {ex}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    internal static class Player_TryPlacePiece_Patch {
        private static bool Prefix(Player __instance, Piece piece, ref bool __result) {
            try {
                if (__instance == null || __instance != Player.m_localPlayer || piece == null) { return true; }
                // Anything wrong with the placement itself is vanilla's to report.
                if (__instance.m_placementStatus != Player.PlacementStatus.Valid) { return true; }
                if (__instance.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) { return true; }

                // Building happens away from a station, so this is the pool ConsumeResources will use.
                List<Container> pool = ContainerNetwork.GetContainersNearPoint(__instance.transform.position);
                if (!StorageOwnership.MustWait(__instance, pool, piece.m_resources, null, 0, 1)) { return true; }
                __instance.Message(MessageHud.MessageType.Center, "$DA_storage_waiting");
                __result = false;
                return false;
            } catch (System.Exception ex) {
                Logger.LogError($"Craft from storage: checking storage before building failed: {ex}");
                return true;
            }
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

            foreach (KeyValuePair<string, int> shortfall in CraftFromStoragePatches.Shortfalls(__instance, requirements, station, qualityLevel, itemQuality, multiplier)) {
                CraftFromStoragePatches.RemoveFromContainers(pool, shortfall.Key, shortfall.Value, itemQuality);
            }
            // Returns void: vanilla then removes the remainder (what the player actually holds) normally.
        }
    }
}
