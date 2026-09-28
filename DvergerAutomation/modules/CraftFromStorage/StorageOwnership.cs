using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Makes sure a container is only ever written by its owner.
    ///
    /// Crafting from storage, auto-store and Epic Loot's table all write to chests the local player does
    /// not necessarily own. They used to take the chest first with <c>ZNetView.ClaimOwnership</c>, which
    /// grabs it rather than being handed it. ZDO data syncs as one revision per object and the host keeps
    /// whichever copy carries the higher revision, so under lag the grabber - working from a copy that can
    /// be seconds old - and the owner it grabbed from both save, and one save wipes out the other: items
    /// put in vanish, or items taken out come back.
    ///
    /// Instead, a client that needs a chest asks its owner for it (<see cref="Want"/>), and the owner hands
    /// it over the way vanilla hands a chest to the player opening it: <c>ForceSendZDO</c> then
    /// <c>SetOwner</c>, from the owner's side, so its last save travels with the ownership and it stops
    /// writing at that moment. Writers only touch containers they own (<see cref="TryAcquire"/>), after
    /// reloading the grid in case the handoff has only just landed.
    ///
    /// Asking takes a round trip, so it is done ahead of time (<see cref="Tick"/>): the chests holding what
    /// the selected recipe or piece needs beyond what the player carries, what the AutoSorter's open box
    /// would be filed into, and Epic Loot's pool while its table is reading it. A chest this client is
    /// working from is held, and while it is held this client refuses to hand it on, so two players
    /// wanting the same chest do not pass it back and forth - the first keeps it until they are done.
    /// </summary>
    internal static class StorageOwnership {
        private const string RequestRpc = "DA_RequestStorage";

        // How long a chest stays held after this client last wanted or wrote to it.
        private const float HoldSeconds = 5f;

        // Minimum gap between two requests for the same chest. A handoff takes a round trip, and a repeat
        // that lands after it simply finds the chest no longer the recipient's to give.
        private const float RequestInterval = 2f;

        private const float TickInterval = 0.5f;

        private static readonly Dictionary<ZDOID, float> heldUntil = new Dictionary<ZDOID, float>();
        private static readonly Dictionary<ZDOID, float> lastRequest = new Dictionary<ZDOID, float>();
        private static readonly List<ZDOID> pruneScratch = new List<ZDOID>();

        // ZRoutedRpc is rebuilt with every session, and registering twice on the same one throws.
        private static ZRoutedRpc registeredOn;
        private static float nextTick;

        internal static void RegisterRpc() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == registeredOn) { return; }
            rpc.Register<ZDOID, long>(RequestRpc, RPC_RequestStorage);
            registeredOn = rpc;
            heldUntil.Clear();
            lastRequest.Clear();
        }

        // ---- writing ------------------------------------------------------------

        /// <summary>
        /// True when this client may write to the container right now: it owns it and nobody has it open.
        /// The grid is reloaded first, so a handoff that has just landed is written on top of, not over.
        /// Otherwise the container is asked for and false comes back - the caller skips it this time.
        /// </summary>
        internal static bool TryAcquire(Container container) {
            if (!IsLive(container) || CraftFromStoragePatches.IsBusy(container)) { return false; }
            Hold(container);
            if (!container.m_nview.IsOwner()) {
                Request(container);
                return false;
            }
            Sync(container);
            return true;
        }

        /// <summary>Owned here and not open: can be spent from without asking anyone.</summary>
        internal static bool IsSpendable(Container container) {
            return IsLive(container) && container.m_nview.IsOwner() && !CraftFromStoragePatches.IsBusy(container);
        }

        /// <summary>
        /// Brings the container's grid up to its ZDO. Container only reloads on its once-a-second
        /// CheckForChanges, so for up to a second after a handoff lands the grid is the one from before
        /// it, and saving that would roll the previous owner's work back. A no-op when nothing changed.
        /// </summary>
        internal static void Sync(Container container) {
            if (container.Load()) { ContainerNetwork.InvalidateItemCounts(); }
        }

        // ---- wanting --------------------------------------------------------------

        /// <summary>
        /// Marks the container as one this client is working from and, when someone else owns it, asks
        /// them to hand it over. Cheap to call repeatedly: requests are rate-limited per container.
        /// </summary>
        internal static void Want(Container container) {
            if (!IsLive(container) || CraftFromStoragePatches.IsBusy(container)) { return; }
            Hold(container);
            if (!container.m_nview.IsOwner()) { Request(container); }
        }

        /// <summary>
        /// Wants, in pool order, the containers holding <paramref name="name"/> until they cover
        /// <paramref name="amount"/> - the same order <see cref="CraftFromStoragePatches.RemoveFromContainers"/>
        /// spends in, so the chests asked for are the ones that will be drawn from.
        /// </summary>
        internal static void WantHolders(List<Container> pool, string name, int amount, int quality) {
            int covered = 0;
            foreach (Container container in pool) {
                if (covered >= amount) { return; }
                if (container == null || CraftFromStoragePatches.IsBusy(container)) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }
                int held = 0;
                foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                    if (CraftFromStoragePatches.Matches(item, name, quality)) { held += item.m_stack; }
                }
                if (held <= 0) { continue; }
                Want(container);
                covered += held;
            }
        }

        /// <summary>
        /// Checks, before anything is spent, that every material the storage pool has to supply is in
        /// containers this client owns. When the pool as a whole has enough but some of it is still with
        /// another player, those containers are asked for and true comes back: the action has to wait.
        /// False when it can go ahead - or when the pool is short anyway, which vanilla reports itself.
        /// </summary>
        internal static bool MustWait(Player player, List<Container> pool, Piece.Requirement[] requirements, CraftingStation station, int qualityLevel, int multiplier) {
            if (pool == null || pool.Count == 0) { return false; }
            List<KeyValuePair<string, int>> shortfalls = CraftFromStoragePatches.Shortfalls(player, requirements, station, qualityLevel, -1, multiplier);
            bool asked = false;
            foreach (KeyValuePair<string, int> shortfall in shortfalls) {
                if (ContainerNetwork.CountSpendableInPool(pool, shortfall.Key) >= shortfall.Value) { continue; }
                // Short across the whole pool: vanilla turns the action down by itself.
                if (ContainerNetwork.CountInPool(pool, shortfall.Key) < shortfall.Value) { return false; }
                WantHolders(pool, shortfall.Key, shortfall.Value, -1);
                asked = true;
            }
            if (!asked) { return false; }

            // A chest nobody owned was simply taken (see Request), so it may be spendable already.
            ContainerNetwork.InvalidateItemCounts();
            foreach (KeyValuePair<string, int> shortfall in shortfalls) {
                if (ContainerNetwork.CountSpendableInPool(pool, shortfall.Key) < shortfall.Value) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Asks for what the local player is about to draw on. Run from the local player's Update, at most
        /// every <see cref="TickInterval"/>.
        /// </summary>
        internal static void Tick(Player player) {
            if (Time.time < nextTick) { return; }
            nextTick = Time.time + TickInterval;
            try {
                WantAhead(player);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] asking for storage ahead of time failed: {ex}");
            }
            Prune();
        }

        private static void WantAhead(Player player) {
            // Station crafting: the chests holding what the selected recipe needs beyond what is carried.
            CraftingStation station = player.GetCurrentCraftingStation();
            InventoryGui gui = InventoryGui.instance;
            if (station != null && gui != null && InventoryGui.IsVisible()) {
                Recipe recipe = gui.m_selectedRecipe.Recipe;
                if (recipe != null && !recipe.m_requireOnlyOneIngredient) {
                    ItemDrop.ItemData upgrading = gui.m_selectedRecipe.ItemData;
                    int quality = upgrading != null ? upgrading.m_quality + 1 : 1;
                    int multiplier = gui.m_multiCrafting ? gui.m_multiCraftAmount : 1;
                    WantShortfalls(player, ContainerNetwork.GetContainersForStation(station), recipe.m_resources, station, quality, multiplier);
                }
            }

            // Hammer building: the selected piece's materials.
            if (player.InPlaceMode()) {
                Piece piece = player.GetSelectedPiece();
                if (piece != null) {
                    WantShortfalls(player, ContainerNetwork.GetContainersNearPoint(player.transform.position), piece.m_resources, null, 0, 1);
                }
            }

            EpicLootIntegration.WantAhead();
            AutoStore.WantAhead(gui != null ? gui.m_currentContainer : null);
        }

        private static void WantShortfalls(Player player, List<Container> pool, Piece.Requirement[] requirements, CraftingStation station, int qualityLevel, int multiplier) {
            if (pool == null || pool.Count == 0 || requirements == null) { return; }
            foreach (KeyValuePair<string, int> shortfall in CraftFromStoragePatches.Shortfalls(player, requirements, station, qualityLevel, -1, multiplier)) {
                WantHolders(pool, shortfall.Key, shortfall.Value, -1);
            }
        }

        internal static bool IsHeld(Container container) {
            if (!IsLive(container)) { return false; }
            return heldUntil.TryGetValue(container.m_nview.GetZDO().m_uid, out float until) && until > Time.time;
        }

        private static void Hold(Container container) {
            heldUntil[container.m_nview.GetZDO().m_uid] = Time.time + HoldSeconds;
        }

        private static void Request(Container container) {
            ZDO zdo = container.m_nview.GetZDO();
            float now = Time.time;
            if (lastRequest.TryGetValue(zdo.m_uid, out float last) && now - last < RequestInterval) { return; }
            lastRequest[zdo.m_uid] = now;

            long owner = zdo.GetOwner();
            if (owner == 0L) {
                // Nobody is simulating it, so there is no one to race. Taking it is what the ownership pass
                // would do for whoever is nearest.
                container.m_nview.ClaimOwnership();
                return;
            }
            if (ZRoutedRpc.instance == null) { return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(owner, RequestRpc, zdo.m_uid, AutomationHub.LocalPlayerId());
        }

        private static bool IsLive(Container container) {
            return container != null && container.m_nview != null && container.m_nview.IsValid();
        }

        // Both maps only ever gain keys, one per chest touched. Cheap to keep, but not forever.
        private static void Prune() {
            if (heldUntil.Count + lastRequest.Count < 512) { return; }
            float now = Time.time;
            pruneScratch.Clear();
            foreach (KeyValuePair<ZDOID, float> entry in heldUntil) {
                if (entry.Value <= now) { pruneScratch.Add(entry.Key); }
            }
            foreach (ZDOID id in pruneScratch) { heldUntil.Remove(id); }
            pruneScratch.Clear();
            foreach (KeyValuePair<ZDOID, float> entry in lastRequest) {
                if (now - entry.Value >= RequestInterval) { pruneScratch.Add(entry.Key); }
            }
            foreach (ZDOID id in pruneScratch) { lastRequest.Remove(id); }
        }

        // ---- the owner's side -----------------------------------------------------

        /// <summary>
        /// Someone wants a container this client owns. Handed over unless it is open, held here, or is not
        /// the kind of storage the pools draw from. A refusal is silent: the requester keeps treating the
        /// container as unavailable and asks again later.
        /// </summary>
        private static void RPC_RequestStorage(long sender, ZDOID id, long playerId) {
            try {
                if (ZDOMan.instance == null || ZNetScene.instance == null) { return; }
                if (sender == ZDOMan.GetSessionID()) { return; }
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsOwner()) { return; }
                GameObject root = ZNetScene.instance.FindInstance(id);
                if (root == null || !CanHandOver(root, playerId)) {
                    if (ValConfig.EnableDebugMode.Value) {
                        Logger.LogInfo($"[Storage] kept {(root != null ? root.name : id.ToString())}; {sender} will have to wait.");
                    }
                    return;
                }

                // The same two calls vanilla's Container.RPC_RequestOpen makes to hand a chest to the player
                // opening it.
                ZDOMan.instance.ForceSendZDO(sender, id);
                zdo.SetOwner(sender);
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Storage] handed {root.name} to {sender}.");
                }
            } catch (Exception ex) {
                Logger.LogError($"[Storage] handing over storage failed: {ex}");
            }
        }

        private static bool CanHandOver(GameObject root, long playerId) {
            // Only the kinds of storage the pools hold: storage pieces (chests, the Hopper's store), boat
            // holds and cart beds. Never a creature carrying a container, and never an AutoSorter, whose
            // box is its user's alone.
            Ship ship = root.GetComponent<Ship>();
            Vagon cart = root.GetComponent<Vagon>();
            if (ship == null && cart == null && root.GetComponent<Piece>() == null) { return false; }
            if (root.GetComponent<AutomationHub>() != null) { return false; }

            ZNetView nview = root.GetComponent<ZNetView>();
            bool any = false;
            foreach (Container container in root.GetComponentsInChildren<Container>(includeInactive: true)) {
                if (container.m_nview != nview) { continue; }
                any = true;
                if (CraftFromStoragePatches.IsBusy(container) || IsHeld(container)) { return false; }
                // Vanilla's open request checks the same thing: a private chest goes only to its builder.
                if (!container.CheckAccess(playerId)) { return false; }
            }
            if (!any) { return false; }

            // A hold or cart bed shares the vehicle's ZDO, so this would hand over the vehicle itself.
            if (ship != null && ship.HasPlayerOnboard()) { return false; }
            if (cart != null && (cart.IsAttached() || (cart.m_chair != null && cart.m_chair.IsInUse()))) { return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(Game), "Start")]
    internal static class Game_Start_StorageOwnership_Patch {
        private static void Postfix() {
            StorageOwnership.RegisterRpc();
        }
    }

    [HarmonyPatch(typeof(Player), "Update")]
    internal static class Player_Update_StorageOwnership_Patch {
        private static void Postfix(Player __instance) {
            if (__instance != Player.m_localPlayer || __instance == null) { return; }
            StorageOwnership.Tick(__instance);
        }
    }
}
