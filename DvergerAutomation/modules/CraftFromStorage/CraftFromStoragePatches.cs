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
        /// Only from chests this client owns, which includes one it has open itself. One owned by someone
        /// else is asked for instead when it can be handed over (see <see cref="StorageOwnership"/>) and
        /// skipped; the checks in front of crafting and building make sure the owned ones, together with
        /// what <see cref="StorageReserve"/> already fetched, cover the amount, so in practice nothing is
        /// skipped here.
        /// </summary>
        internal static int RemoveFromContainers(List<Container> pool, string name, int amount, int itemQuality) {
            int removed = 0;
            foreach (Container container in pool) {
                if (amount <= 0) { break; }
                if (container == null) { continue; }
                // Read off this client's copy before asking for the chest: one that holds none of it is not
                // worth a handoff.
                Inventory inv = container.GetInventory();
                if (InventoryOps.CountMatching(inv, name, itemQuality) <= 0) { continue; }
                // Reloads the grid too, so the items taken below are the chest's current ones.
                if (!StorageOwnership.TryAcquireForSpend(container)) { continue; }

                int taken = InventoryOps.Take(inv, name, itemQuality, amount);
                amount -= taken;
                removed += taken;
                // It may be the chest this player has open, with one of those stacks on the cursor.
                if (taken > 0) { StorageOps.GuardOwnerGui(container); }
            }
            // Chest contents just changed: the frame-memoized counts have to look again.
            if (removed > 0) { ContainerNetwork.InvalidateItemCounts(); }
            return removed;
        }

        /// <summary>
        /// What the pool can put towards an ingredient: the stock of every linked chest, plus whatever
        /// has already been fetched out of them for the action in hand. Without the second half an
        /// ingredient would read as missing in the moment between leaving its chest and being spent.
        /// </summary>
        internal static int CountAvailable(List<Container> pool, string name) {
            return ContainerNetwork.CountInPool(pool, name) + StorageReserve.Count(name);
        }

        /// <summary>
        /// Whether the craft the panel is set up for has to wait on storage. The quality and multiplier
        /// are the ones <c>InventoryGui.DoCrafting</c> hands <c>ConsumeResources</c>.
        /// </summary>
        internal static bool CraftMustWait(InventoryGui gui, Player player) {
            if (player == null || player != Player.m_localPlayer) { return false; }
            Recipe recipe = gui.m_craftRecipe;
            // A one-of-these recipe spends only the ingredient the player carries (singleReqItem).
            if (recipe == null || recipe.m_requireOnlyOneIngredient) { return false; }
            if (player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost)) { return false; }

            CraftingStation station = player.GetCurrentCraftingStation();
            int quality = gui.m_craftUpgradeItem == null ? 1 : gui.m_craftUpgradeItem.m_quality + 1;
            int multiplier = gui.m_multiCrafting ? gui.m_multiCraftAmount : 1;
            return StorageOwnership.CheckStorage(player, ContainerNetwork.GetContainersForStation(station), recipe.m_resources, station, quality, multiplier)
                == StorageReserve.Readiness.Pending;
        }
    }

    // ---- station crafting: "can I craft this?" --------------------------------

    [HarmonyPatch(typeof(Player), nameof(Player.HaveRequirementItems),
        new[] { typeof(Recipe), typeof(bool), typeof(int), typeof(int) })]
    internal static class Player_HaveRequirementItems_Patch {
        private static void Postfix(Player __instance, ref bool __result, Recipe piece, bool discover, int qualityLevel, int amount) {
            if (__result || discover) { return; }
            if (__instance == null || __instance != Player.m_localPlayer) { return; }
            // A one-of-these recipe is paid from the player's own pack only: vanilla's Recipe.GetAmount looks
            // the ingredient up there (GetFirstRequiredItem) and throws when it is not found, so one read as
            // craftable on chest stock would crash the craft button.
            if (piece.m_requireOnlyOneIngredient) { return; }

            CraftingStation station = __instance.GetCurrentCraftingStation();
            if (station == null) { return; }
            List<Container> pool = ContainerNetwork.GetContainersForStation(station);
            if (pool.Count == 0) { return; }

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
                int total = playerBest + CraftFromStoragePatches.CountAvailable(pool, name);
                if (total < needed) { return; }
            }

            __result = true;
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
        // Each row's amount label, found once: the rows are pooled UI objects, redrawn every frame.
        private static readonly Dictionary<Transform, TMP_Text> AmountTexts = new Dictionary<Transform, TMP_Text>();

        // The "+N" and tooltip lines by N. A count changes far less often than the rows are drawn.
        private static readonly Dictionary<int, string> Suffixes = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> TooltipLines = new Dictionary<int, string>();

        static InventoryGui_SetupRequirement_Patch() {
            // The tooltip line is worded in the player's language.
            Localization.OnLanguageChange += TooltipLines.Clear;
        }

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
            int stored = CraftFromStoragePatches.CountAvailable(pool, name);
            if (stored <= 0) { return; }

            TMP_Text amountText = AmountText(elementRoot);
            if (amountText == null) { return; }

            // Vanilla judged the red flash on the player's own count alone. Storage covering the rest
            // should read as craftable, the same way the craft button already does.
            int needed = req.GetAmount(quality) * craftMultiplier;
            if (player.GetInventory().CountItems(name) + stored >= needed) {
                amountText.color = Color.white;
            }

            if (!ValConfig.ShowStorageCounts.Value) { return; }
            amountText.text += Cached(Suffixes, stored, n => " <color=" + CraftFromStoragePatches.StorageCountColor + ">+" + n + "</color>");

            // Vanilla resets the tooltip to the bare item name every draw, so this never accumulates.
            UITooltip tooltip = elementRoot.GetComponent<UITooltip>();
            if (tooltip != null) {
                tooltip.m_text += Cached(TooltipLines, stored, n => "\n<color=" + CraftFromStoragePatches.StorageCountColor + ">"
                    + Localization.instance.Localize("$DA_storage_count", n.ToString()) + "</color>");
            }
        }

        private static TMP_Text AmountText(Transform elementRoot) {
            if (AmountTexts.TryGetValue(elementRoot, out TMP_Text text) && text != null) { return text; }
            if (AmountTexts.Count > 64) { AmountTexts.Clear(); }
            Transform amountTransform = elementRoot.Find("res_amount");
            text = amountTransform != null ? amountTransform.GetComponent<TMP_Text>() : null;
            AmountTexts[elementRoot] = text;
            return text;
        }

        private static string Cached(Dictionary<int, string> cache, int stored, System.Func<int, string> make) {
            if (cache.TryGetValue(stored, out string line)) { return line; }
            // A language switch or a long session of changing counts: start over rather than grow.
            if (cache.Count > 256) { cache.Clear(); }
            line = make(stored);
            cache[stored] = line;
            return line;
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
                int total = __instance.m_inventory.CountItems(name) + CraftFromStoragePatches.CountAvailable(pool, name);
                if (total < resource.m_amount) { return; }
            }

            __result = true;
        }
    }

    // ---- waiting for storage: in front of station crafting and Hammer building ----
    // Availability counts every linked chest, but only what is in chests this client owns, or has already
    // been fetched out of another player's, can be spent (see StorageOwnership and StorageReserve). The
    // remaining gap is closed here, before the action: if part of what it needs is still elsewhere, it is
    // sent for and the action waits - the craft bar stays full, the Hammer click is finished a moment
    // later. Checked this early because consumption itself cannot fail - vanilla hands out the crafted
    // item or places the piece and only then spends.

    [HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
    internal static class InventoryGui_DoCrafting_Patch {
        private static bool Prefix(InventoryGui __instance, Player player) {
            try {
                if (!CraftFromStoragePatches.CraftMustWait(__instance, player)) { return true; }
                if (CraftHold.KeepWaiting()) { return false; }
                player.Message(MessageHud.MessageType.Center, "$DA_storage_waiting");
                return false;
            } catch (System.Exception ex) {
                // Let vanilla carry on: the worst case is the old behaviour, never a lost craft.
                Logger.LogError($"Craft from storage: checking storage before crafting failed: {ex}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateRecipe))]
    internal static class InventoryGui_UpdateRecipe_CraftHold_Patch {
        private static void Prefix(InventoryGui __instance) { CraftHold.Enter(__instance); }

        private static void Postfix(InventoryGui __instance) { CraftHold.Exit(__instance); }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.OnCraftPressed))]
    internal static class InventoryGui_OnCraftPressed_Patch {
        // Sends for the materials as the bar starts rather than as it fills, so most of the round trip is
        // over by the time they are needed. A postfix, and only once the timer is running: vanilla turns
        // the press down for a full pack and the like, and nothing should be fetched for a craft that
        // never started. The bar alone cannot be counted on to cover the wait - at high skill it is well
        // under a second - which is what CraftHold is for.
        private static void Postfix(InventoryGui __instance) {
            if (__instance.m_craftTimer < 0f) { return; }
            try {
                CraftFromStoragePatches.CraftMustWait(__instance, Player.m_localPlayer);
            } catch (System.Exception ex) {
                Logger.LogError($"Craft from storage: sending for storage as the craft started failed: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    internal static class Player_TryPlacePiece_Patch {
        private static bool Prefix(Player __instance, Piece piece, ref bool __result) {
            try {
                if (__instance == null || __instance != Player.m_localPlayer || piece == null) { return true; }
                // Anything wrong with the placement itself is vanilla's to report. Vanilla works the status
                // out afresh as its first step, so the same is done here: what the player carries at this
                // point is last frame's, and a ghost that has only just turned valid would otherwise walk
                // straight past this check and be placed unpaid.
                __instance.UpdatePlacementGhost(flashGuardStone: false);
                if (__instance.m_placementStatus != Player.PlacementStatus.Valid) { return true; }
                if (__instance.m_noPlacementCost || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) { return true; }

                // Building happens away from a station, so this is the pool ConsumeResources will use.
                List<Container> pool = ContainerNetwork.GetContainersNearPoint(__instance.transform.position);
                switch (StorageOwnership.CheckStorage(__instance, pool, piece.m_resources, null, 0, 1)) {
                    case StorageReserve.Readiness.Covered:
                        PlaceRetry.Disarm();
                        return true;
                    case StorageReserve.Readiness.Pending:
                        // No message yet: PlaceRetry finishes the click once the materials are in, and only
                        // says so if they never come.
                        PlaceRetry.Arm(piece);
                        __result = false;
                        return false;
                    default:
                        // Vanilla already decided the materials were there, before this ran, and will not
                        // look again. They are not: its count was from before a chest was brought up to
                        // date.
                        PlaceRetry.Disarm();
                        __instance.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
                        __result = false;
                        return false;
                }
            } catch (System.Exception ex) {
                Logger.LogError($"Craft from storage: checking storage before building failed: {ex}");
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
    internal static class Player_UpdatePlacement_PlaceRetry_Patch {
        private static void Prefix(Player __instance) {
            if (__instance != Player.m_localPlayer) { return; }
            try {
                PlaceRetry.Tick(__instance);
            } catch (System.Exception ex) {
                PlaceRetry.Disarm();
                Logger.LogError($"Craft from storage: finishing a placement that waited on storage failed: {ex}");
            }
        }
    }

    // ---- consumption: shared by station crafting AND Hammer building ----------

    [HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
    internal static class Player_ConsumeResources_Patch {
        private static void Prefix(Player __instance, Piece.Requirement[] requirements, int qualityLevel, int itemQuality, int multiplier) {
            if (__instance == null || __instance != Player.m_localPlayer) { return; }

            List<Container> pool = ContainerNetwork.PoolFor(__instance);
            if (pool.Count == 0) { return; }

            CraftingStation station = __instance.GetCurrentCraftingStation();
            foreach (KeyValuePair<string, int> shortfall in CraftFromStoragePatches.Shortfalls(__instance, requirements, station, qualityLevel, itemQuality, multiplier)) {
                // What was fetched for this action first - it has already left its chest - then the chests
                // this client owns.
                int rest = shortfall.Value - StorageReserve.Spend(shortfall.Key, shortfall.Value, itemQuality);
                if (rest > 0) { CraftFromStoragePatches.RemoveFromContainers(pool, shortfall.Key, rest, itemQuality); }
            }
            // Returns void: vanilla then removes the remainder (what the player actually holds) normally.
        }
    }
}
