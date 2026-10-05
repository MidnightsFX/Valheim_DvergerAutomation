using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_Autosorter piece. Attached to the prefab in Unity (not in code).
    /// On a timer it links nearby crafting stations and nearby accessible storage chests, turning the
    /// chests into a shared material pool. The actual "use chest items while crafting/building" behaviour
    /// is applied by the Harmony patches in CraftFromStoragePatches, which read this hub's links via
    /// <see cref="ContainerNetwork"/>.
    /// </summary>
    public class AutomationHub : MonoBehaviour {
        public Switch CoreSwitch1;
        public Switch CoreSwitch2;
        public Switch CoreSwitch3;
        public Switch CoreSwitch4;
        public GameObject Core1Visual;
        public GameObject Core2Visual;
        public GameObject Core3Visual;
        public GameObject Core4Visual;

        public GameObject EnabledVisuals;

        // ZDO key holding the inserted-core bitmask (bit n set => slot n filled). Persisted/synced so the
        // active state, lit visuals and extended range survive reloads and replicate to other clients.
        private const string CoreMaskKey = "DA_cores";

        private ZNetView nview;

        // Links produced by the most recent scan. Read by ContainerNetwork.
        internal readonly HashSet<CraftingStation> LinkedStations = new HashSet<CraftingStation>();
        internal readonly List<Container> LinkedContainers = new List<Container>();

        // The boat holds and cart beds among LinkedContainers, mapped to the vehicle carrying each (see
        // VehicleStorage). Vehicles come and go far faster than chests get built, so they are relinked on
        // a short cycle of their own rather than waiting out the chest scan.
        internal readonly Dictionary<Container, MonoBehaviour> LinkedVehicles = new Dictionary<Container, MonoBehaviour>();
        private readonly Dictionary<Container, MonoBehaviour> vehicleScratch = new Dictionary<Container, MonoBehaviour>();
        private const float VehicleRelinkInterval = 2f;

        /// <summary>
        /// The piece's own deposit inventory (the StoreGoods child), into which the player drops goods
        /// for <see cref="AutoStore"/> to distribute. Resolved from the hierarchy rather than serialized
        /// so the prefab needs no extra Unity wiring.
        /// </summary>
        internal Container DepositBox { get; private set; }

        private Coroutine scanLoop;
        private Coroutine vehicleLoop;
        private static int pieceMask = 0;

        // For a while after a hub loads - a player arriving through a portal, logging in, or walking
        // back into range - the chests around it are still streaming in: nearest to the player first,
        // and on a server only as fast as their ZDOs arrive, which in a large base can take most of a
        // minute. A scan in that time links only some of them, and at the normal interval the rest would
        // not count until the next one. With Fast Initial Scan on, the hub rescans every second through
        // this window instead, but only when the scene has gained or lost objects since its last look,
        // which keeps it close to free once loading is done.
        private const float InitialScanInterval = 1f;
        private const float InitialScanWindow = 60f;

        private void Awake() {
            nview = GetComponent<ZNetView>();
            // The only Container under the piece is the deposit box; the root itself has none.
            DepositBox = GetComponentInChildren<Container>(includeInactive: true);
            if (nview != null && nview.GetZDO() != null) {
                nview.Register("DA_RefreshCores", new Action<long>(RPC_RefreshCores));
                nview.Register<int, bool>("DA_SorterCoreRequest", RPC_CoreRequest);
                nview.Register<int, bool>("DA_SorterCoreResult", RPC_CoreResult);
                WearNTear wnt = GetComponent<WearNTear>();
                if (wnt != null) { wnt.m_onDestroyed += OnDestroyedDropCores; }
                // Live pieces only - the placement ghost has no ZDO and no inventory to size or protect.
                // Registered here rather than in OnEnable because Container.Load can run for the box as soon as
                // its first CheckForChanges tick, and the load guard has to recognise it by then.
                AutoStore.RegisterDepositBox(DepositBox);
            }

            // The Switch callbacks are C# delegates and cannot be assigned in Unity, so wire them here.
            WireSwitch(CoreSwitch1, 0);
            WireSwitch(CoreSwitch2, 1);
            WireSwitch(CoreSwitch3, 2);
            WireSwitch(CoreSwitch4, 3);

            // Refresh visuals shortly after load (and periodically) so a late ZDO sync still lights up.
            InvokeRepeating(nameof(UpdateVisuals), 1f, 4f);
        }

        private void OnEnable() {
            ContainerNetwork.Register(this);
            scanLoop = StartCoroutine(ScanLoopRoutine());
            vehicleLoop = StartCoroutine(VehicleLoopRoutine());
        }

        private void OnDisable() {
            if (scanLoop != null) {
                StopCoroutine(scanLoop);
                scanLoop = null;
            }
            if (vehicleLoop != null) {
                StopCoroutine(vehicleLoop);
                vehicleLoop = null;
            }
            LinkedStations.Clear();
            LinkedContainers.Clear();
            LinkedVehicles.Clear();
            ContainerNetwork.Unregister(this);
        }

        private void OnDestroy() {
            AutoStore.UnregisterDepositBox(DepositBox);
        }

        private IEnumerator ScanLoopRoutine() {
            // Let the world finish loading before the first scan.
            yield return new WaitForSeconds(2f);
            // Seconds of the initial window used up, and the scene's object count at its last scan.
            float initialElapsed = 0f;
            int lastSceneObjects = -1;
            while (true) {
                bool initial = ValConfig.FastInitialScan.Value && initialElapsed < InitialScanWindow;
                if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) {
                    if (!initial) {
                        Scan();
                    } else {
                        int sceneObjects = ZNetScene.instance != null ? ZNetScene.instance.NrOfInstances() : 0;
                        if (sceneObjects != lastSceneObjects) {
                            Scan();
                            lastSceneObjects = sceneObjects;
                        }
                        // A portal holds the player in its loading screen for several seconds while the
                        // base streams in, so only time spent back in the world uses up the window.
                        if (!Player.m_localPlayer.IsTeleporting()) { initialElapsed += InitialScanInterval; }
                    }
                }
                yield return new WaitForSeconds(initial ? InitialScanInterval : Mathf.Max(1f, ValConfig.ScanInterval.Value));
            }
        }

        // A cart pulled up to the workshop should count within moments, not after the next chest scan,
        // and one pulled away should stop counting just as quickly. Cheap: it only walks the loaded
        // boats and carts, and rebuilds the station cache only when the set actually changed.
        private IEnumerator VehicleLoopRoutine() {
            yield return new WaitForSeconds(2f);
            while (true) {
                yield return new WaitForSeconds(VehicleRelinkInterval);
                if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) {
                    RelinkVehicles();
                }
            }
        }

        internal static long LocalPlayerId() {
            return (Game.instance != null && Game.instance.GetPlayerProfile() != null)
                ? Game.instance.GetPlayerProfile().GetPlayerID()
                : 0L;
        }

        private void Scan() {
            LinkedStations.Clear();
            LinkedContainers.Clear();
            LinkedVehicles.Clear();

            // Gated inactive: with no cores inserted the hub links nothing (feature off) when required.
            if (ValConfig.RequireCores.Value && CoreCount == 0) {
                ContainerNetwork.RebuildStationCache();
                return;
            }

            float radius = EffectiveRadius;
            Vector3 pos = transform.position;
            long playerId = LocalPlayerId();

            // Crafting stations: iterate the game's global station list and distance-check (no physics needed).
            foreach (CraftingStation station in CraftingStation.m_allStations) {
                if (station == null) { continue; }
                if (Vector3.Distance(pos, station.transform.position) <= radius) {
                    LinkedStations.Add(station);
                }
            }

            // Containers: overlap the piece layers and resolve the owning Container component.
            if (pieceMask == 0) { pieceMask = LayerMask.GetMask("piece", "piece_nonsolid"); }
            Collider[] hits = Physics.OverlapSphere(pos, radius, pieceMask);
            HashSet<Container> seen = new HashSet<Container>();
            foreach (Collider hit in hits) {
                if (hit == null) { continue; }
                Container container = hit.GetComponentInParent<Container>();
                if (container == null || !seen.Add(container)) { continue; }
                // An autosorter's own deposit box holds items waiting to be sorted, not storage: never a
                // crafting source, and never a sort target - it holds the very items being sorted, so it
                // would match everything and file them straight back into itself.
                if (container.GetComponentInParent<AutomationHub>() != null) { continue; }
                if (IsAccessible(container, playerId)) {
                    LinkedContainers.Add(container);
                }
            }

            // After the chests, so crafting spends out of this hub's chests before a boat's or cart's load.
            VehicleStorage.Collect(pos, radius, playerId, LinkedVehicles);
            LinkedContainers.AddRange(LinkedVehicles.Keys);

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Autosorter] scan linked {LinkedStations.Count} stations, {LinkedContainers.Count - LinkedVehicles.Count} chests, {LinkedVehicles.Count} boats/carts within {radius}m.");
            }

            ContainerNetwork.RebuildStationCache();
        }

        // The vehicle half of Scan on its own: swaps this hub's boat and cart links for whatever is in
        // range right now, leaving its chests alone.
        private void RelinkVehicles() {
            vehicleScratch.Clear();
            if (!ValConfig.RequireCores.Value || CoreCount > 0) {
                VehicleStorage.Collect(transform.position, EffectiveRadius, LocalPlayerId(), vehicleScratch);
            }
            if (SameVehicles(vehicleScratch, LinkedVehicles)) { return; }

            LinkedContainers.RemoveAll(container => LinkedVehicles.ContainsKey(container));
            LinkedVehicles.Clear();
            foreach (KeyValuePair<Container, MonoBehaviour> link in vehicleScratch) {
                LinkedVehicles.Add(link.Key, link.Value);
                LinkedContainers.Add(link.Key);
            }
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Autosorter] relinked {LinkedVehicles.Count} boats/carts.");
            }
            ContainerNetwork.RebuildStationCache();
        }

        private static bool SameVehicles(Dictionary<Container, MonoBehaviour> a, Dictionary<Container, MonoBehaviour> b) {
            if (a.Count != b.Count) { return false; }
            foreach (Container container in a.Keys) {
                if (!b.ContainsKey(container)) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Forces an immediate relink. Used by the auto-store pass so a chest built since the last scan
        /// tick is still a valid destination.
        /// </summary>
        internal void Rescan() {
            Scan();
        }

        // A chest is usable only if the local player can freely access it: not Private/Group-locked to
        // someone else, and not inside a ward the player is not permitted in (the player's own ward passes).
        internal static bool IsAccessible(Container container, long playerId) {
            if (container.m_inventory == null) { return false; } // Container.Awake not run yet.
            if (!container.CheckAccess(playerId)) { return false; }
            if (!PrivateArea.CheckAccess(container.transform.position, 0f, flash: false)) { return false; }
            return true;
        }

        // ---- Surtling Core slots --------------------------------------------

        // Current inserted-core bitmask from the ZDO (0 when the network view is not yet valid).
        private int GetCoreMask() {
            if (nview == null || !nview.IsValid()) { return 0; }
            return nview.GetZDO().GetInt(CoreMaskKey, 0);
        }

        /// <summary>Number of inserted cores (0-4).</summary>
        internal int CoreCount {
            get {
                int mask = GetCoreMask();
                int count = 0;
                for (int slot = 0; slot < 4; ++slot) {
                    if ((mask & (1 << slot)) != 0) { ++count; }
                }
                return count;
            }
        }

        /// <summary>The hub is active once at least one core is inserted.</summary>
        internal bool IsActive => GetCoreMask() != 0;

        /// <summary>Base link radius plus the configured bonus for each inserted core.</summary>
        internal float EffectiveRadius => ValConfig.ScanRadius.Value + CoreCount * ValConfig.RangePerCore.Value;

        /// <summary>
        /// Owner side of a core socket being used (the exchange is described on <see cref="SurtlingCore"/>):
        /// the mask shares this ZDO with the deposit box's items, so only the owner writes it. A request
        /// that finds the slot already in the asked-for state - two players at once - changes nothing and
        /// is not answered.
        /// </summary>
        private void RPC_CoreRequest(long sender, int slot, bool insert) {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            if (slot < 0 || slot >= 4) { return; }
            int mask = GetCoreMask();
            int bit = 1 << slot;
            if (((mask & bit) != 0) == insert) { return; }

            nview.GetZDO().Set(CoreMaskKey, insert ? mask | bit : mask & ~bit);
            nview.InvokeRPC(sender, "DA_SorterCoreResult", slot, insert);
            // Everybody includes this client: every copy refreshes its visuals and relinks.
            nview.InvokeRPC(ZNetView.Everybody, "DA_RefreshCores");
        }

        private void RPC_CoreResult(long sender, int slot, bool inserted) {
            SurtlingCore.Settle(inserted);
        }

        private void RPC_RefreshCores(long sender) {
            UpdateVisuals();
            // Relink immediately so the new range / active state applies without waiting for the scan tick.
            if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) { Scan(); }
        }

        private void WireSwitch(Switch sw, int slot) {
            if (sw == null) { return; }
            sw.m_onUse = OnCoreSwitchUsed;
            sw.m_onHover = () => GetSwitchHover(slot);
            sw.m_name = "$DA_autosorter_name";
        }

        private int SlotForSwitch(Switch caller) {
            if (caller == CoreSwitch1) { return 0; }
            if (caller == CoreSwitch2) { return 1; }
            if (caller == CoreSwitch3) { return 2; }
            if (caller == CoreSwitch4) { return 3; }
            return -1;
        }

        // Switch use: insert a core into an empty slot (consuming one from the player) or remove a core
        // from a filled slot (returning one to the player).
        private bool OnCoreSwitchUsed(Switch caller, Humanoid user, ItemDrop.ItemData item) {
            int slot = SlotForSwitch(caller);
            if (slot < 0) { return false; }
            if (!(user is Player player)) { return false; }
            if (!PrivateArea.CheckAccess(transform.position)) { return false; }
            if (nview == null || !nview.IsValid()) { return false; }

            GameObject corePrefab = SurtlingCore.Prefab;
            if (corePrefab == null) { return false; }
            string coreName = SurtlingCore.SharedName(corePrefab);

            bool filled = (GetCoreMask() & (1 << slot)) != 0;
            if (filled) {
                if (!player.GetInventory().CanAddItem(corePrefab, 1)) {
                    user.Message(MessageHud.MessageType.Center, "$inventory_full");
                    return false;
                }
            } else if (player.GetInventory().CountItems(coreName) <= 0) {
                user.Message(MessageHud.MessageType.Center, "$DA_Need_Core");
                return false;
            }

            // Asked of the owner rather than written here (see SurtlingCore). When this client is the
            // owner, both RPCs run synchronously.
            nview.InvokeRPC("DA_SorterCoreRequest", slot, !filled);
            return true;
        }

        private string GetSwitchHover(int slot) {
            bool filled = (GetCoreMask() & (1 << slot)) != 0;
            string action = filled ? "$DA_Remove_Core" : "$DA_Add_Core";
            return Localization.instance.Localize("[<color=yellow><b>$KEY_Use</b></color>] " + action);
        }

        // Reflects the inserted-core bitmask onto the per-slot visuals and the overall active visual.
        private void UpdateVisuals() {
            if (nview == null || !nview.IsValid()) { return; }
            // Rides this timer because it has to be repeated, not because it is a visual.
            MultiUserChestIntegration.KeepExclusive(nview);
            int mask = GetCoreMask();
            SetActiveSafe(Core1Visual, (mask & 1) != 0);
            SetActiveSafe(Core2Visual, (mask & 2) != 0);
            SetActiveSafe(Core3Visual, (mask & 4) != 0);
            SetActiveSafe(Core4Visual, (mask & 8) != 0);
            SetActiveSafe(EnabledVisuals, mask != 0);
        }

        private static void SetActiveSafe(GameObject go, bool active) {
            if (go != null && go.activeSelf != active) { go.SetActive(active); }
        }

        // On destroy/deconstruct, drop the inserted cores so they are not lost.
        private void OnDestroyedDropCores() {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            int count = CoreCount;
            if (count <= 0) { return; }
            GameObject corePrefab = SurtlingCore.Prefab;
            if (corePrefab == null) { return; }
            for (int i = 0; i < count; ++i) {
                Vector3 pos = transform.position + Vector3.up * 0.5f + UnityEngine.Random.insideUnitSphere * 0.3f;
                Instantiate(corePrefab, pos, UnityEngine.Random.rotation);
            }
        }
    }

    /// <summary>
    /// Registry of active autosorter hubs plus the cached station to containers lookup used by the
    /// crafting Harmony patches.
    /// </summary>
    internal static class ContainerNetwork {
        internal static readonly List<AutomationHub> Hubs = new List<AutomationHub>();

        // Cached map rebuilt only when a hub scans / (un)registers - not on every crafting check.
        private static readonly Dictionary<CraftingStation, List<Container>> StationToContainers =
            new Dictionary<CraftingStation, List<Container>>();
        // Scratch set reused across RebuildStationCache to dedup a station's containers in O(1).
        private static readonly HashSet<Container> RebuildSeen = new HashSet<Container>();

        // Every boat hold / cart bed any hub links, mapped to the vehicle carrying it. IsBusy asks this
        // for a pooled container whenever one is about to be asked for, so it is kept alongside the
        // station cache rather than worked out from the hierarchy on demand.
        private static readonly Dictionary<Container, MonoBehaviour> Carriers = new Dictionary<Container, MonoBehaviour>();

        // Also what both pool accessors hand back while the local player has craft-from-storage switched
        // off: every consumer - the two Harmony display paths, the requirement checks, consumption, and
        // Epic Loot's inventory provider - already treats an empty pool as "no autosorter here", so the
        // client-side switch needs no separate test anywhere else.
        private static readonly List<Container> Empty = new List<Container>();

        // Frame-memoized aggregate of item-name -> total stack across a container pool. Crafting/build
        // queries fan out across hundreds of recipes (panel refresh) and the selected recipe (every
        // frame); without this, each query rescans every chest's inventory and the game freezes once a
        // few hundred chests are linked. Rebuilt at most once per frame per pool reference.
        private static readonly Dictionary<string, int> AggCounts = new Dictionary<string, int>();
        private static int aggFrame = -1;
        private static List<Container> aggPool;

        // The same aggregate over only the chests this client owns - what can be spent without asking
        // anyone (see StorageOwnership). Kept apart so the two never evict each other within a frame.
        private static readonly Dictionary<string, int> SpendCounts = new Dictionary<string, int>();
        private static int spendFrame = -1;
        private static List<Container> spendPool;

        // Frame-memoized result of GetContainersNearPoint so the Hammer-build path stops reallocating
        // and re-deduping every frame, and returns a stable list reference the aggregate memo can key on.
        private static readonly List<Container> NearPointResult = new List<Container>();
        private static readonly HashSet<Container> NearPointSeen = new HashSet<Container>();
        private static int nearPointFrame = -1;
        private static Vector3 nearPointPos;

        internal static void Register(AutomationHub hub) {
            if (!Hubs.Contains(hub)) { Hubs.Add(hub); }
            RebuildStationCache();
        }

        internal static void Unregister(AutomationHub hub) {
            Hubs.Remove(hub);
            RebuildStationCache();
        }

        internal static void RebuildStationCache() {
            StationToContainers.Clear();
            Carriers.Clear();
            foreach (AutomationHub hub in Hubs) {
                if (hub == null) { continue; }
                foreach (KeyValuePair<Container, MonoBehaviour> link in hub.LinkedVehicles) {
                    Carriers[link.Key] = link.Value;
                }
                foreach (CraftingStation station in hub.LinkedStations) {
                    if (station == null) { continue; }
                    if (!StationToContainers.TryGetValue(station, out List<Container> list)) {
                        list = new List<Container>();
                        StationToContainers[station] = list;
                    }
                    // Dedup via a reused HashSet (seeded with what's already linked from earlier hubs)
                    // instead of List.Contains in a loop, which is O(containers^2).
                    RebuildSeen.Clear();
                    RebuildSeen.UnionWith(list);
                    foreach (Container container in hub.LinkedContainers) {
                        if (container != null && RebuildSeen.Add(container)) { list.Add(container); }
                    }
                }
            }
            // Chest membership changed: invalidate the frame-memoized aggregate / near-point caches.
            nearPointFrame = -1;
            InvalidateItemCounts();
        }

        /// <summary>
        /// Drops the frame-memoized item aggregate. Chest *membership* changes go through
        /// <see cref="RebuildStationCache"/>, but chest *contents* can change under us - auto-store moving
        /// items in, crafting taking them out - and the aggregate would otherwise serve counts from before
        /// the change for the rest of the frame.
        /// </summary>
        internal static void InvalidateItemCounts() {
            aggFrame = -1;
            aggPool = null;
            spendFrame = -1;
            spendPool = null;
            // Epic Loot's enchanting table reads a memo of the same pool, on the same per-frame basis.
            EpicLootIntegration.InvalidateItemCache();
        }

        /// <summary>The boat or cart carrying a linked container; false for a plain chest.</summary>
        internal static bool TryGetCarrier(Container container, out MonoBehaviour carrier) {
            return Carriers.TryGetValue(container, out carrier);
        }

        /// <summary>Containers linked to the given crafting station (station-crafting pool). O(1) lookup.</summary>
        internal static List<Container> GetContainersForStation(CraftingStation station) {
            if (!ValConfig.CraftFromStorageEnabled.Value) { return Empty; }
            if (station != null && StationToContainers.TryGetValue(station, out List<Container> list)) {
                return list;
            }
            return Empty;
        }

        /// <summary>
        /// Containers from every hub whose scan radius currently covers the point (Hammer-build pool).
        /// Frame-memoized: returns a stable, reused list within a frame for the same point so the
        /// per-frame build path neither reallocates nor re-dedups, and the aggregate memo can key on it.
        /// </summary>
        internal static List<Container> GetContainersNearPoint(Vector3 point) {
            if (!ValConfig.CraftFromStorageEnabled.Value) { return Empty; }
            if (nearPointFrame == Time.frameCount && nearPointPos == point) {
                return NearPointResult;
            }
            NearPointResult.Clear();
            NearPointSeen.Clear();
            foreach (AutomationHub hub in Hubs) {
                if (hub == null) { continue; }
                if (ValConfig.RequireCores.Value && hub.CoreCount == 0) { continue; }
                if (Vector3.Distance(hub.transform.position, point) > hub.EffectiveRadius) { continue; }
                foreach (Container container in hub.LinkedContainers) {
                    if (container != null && NearPointSeen.Add(container)) { NearPointResult.Add(container); }
                }
            }
            nearPointFrame = Time.frameCount;
            nearPointPos = point;
            return NearPointResult;
        }

        /// <summary>
        /// Total stack count of <paramref name="name"/> across the pool, matching vanilla
        /// <c>Inventory.CountItems(name, -1, matchWorldLevel: true)</c>. Backed by a per-pool aggregate
        /// rebuilt at most once per frame, turning each query into an O(1) dictionary lookup.
        /// </summary>
        internal static int CountInPool(List<Container> pool, string name) {
            if (pool == null || pool.Count == 0) { return 0; }
            if (aggFrame != Time.frameCount || !ReferenceEquals(aggPool, pool)) {
                BuildAggregate(pool, AggCounts, spendableOnly: false);
                aggFrame = Time.frameCount;
                aggPool = pool;
            }
            return AggCounts.TryGetValue(name, out int total) ? total : 0;
        }

        /// <summary>
        /// <see cref="CountInPool"/> over only the chests this client owns, each brought up to date with
        /// its ZDO first. What crafting can spend right now without asking anyone.
        /// </summary>
        internal static int CountSpendableInPool(List<Container> pool, string name) {
            if (pool == null || pool.Count == 0) { return 0; }
            if (spendFrame != Time.frameCount || !ReferenceEquals(spendPool, pool)) {
                BuildAggregate(pool, SpendCounts, spendableOnly: true);
                // Set after the build: a Sync inside it that reloads a grid invalidates the memo.
                spendFrame = Time.frameCount;
                spendPool = pool;
            }
            return SpendCounts.TryGetValue(name, out int total) ? total : 0;
        }

        private static void BuildAggregate(List<Container> pool, Dictionary<string, int> counts, bool spendableOnly) {
            bool debug = ValConfig.EnableDebugMode.Value;
            double startTime = debug ? Time.realtimeSinceStartupAsDouble : 0.0;

            counts.Clear();
            int worldLevel = Game.m_worldLevel;
            foreach (Container container in pool) {
                if (container == null || container.m_nview == null || !container.m_nview.IsValid()) { continue; }
                // A chest someone has open, or a boat with someone aboard, still counts as stock: it
                // cannot change hands (see CraftFromStoragePatches.IsBusy), but its owner can take the
                // materials out for this client (see StorageReserve).
                if (spendableOnly) {
                    if (!StorageOwnership.CanSpendLocally(container)) { continue; }
                    StorageOwnership.Sync(container);
                }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }
                foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                    // Mirror CountItems(name, -1, matchWorldLevel: true): any quality, world-level gated.
                    if (item.m_worldLevel < worldLevel) { continue; }
                    // Enchanted gear is not spendable as plain material, so it must not be counted as
                    // such either - otherwise a chest of legendaries reads as free crafting stock.
                    if (EpicLootIntegration.IsProtectedItem(item)) { continue; }
                    string itemName = item.m_shared.m_name;
                    counts.TryGetValue(itemName, out int cur);
                    counts[itemName] = cur + item.m_stack;
                }
            }

            if (debug) {
                double ms = (Time.realtimeSinceStartupAsDouble - startTime) * 1000.0;
                Logger.LogInfo($"[Autosorter] aggregated {pool.Count} chests{(spendableOnly ? " (owned only)" : "")}, {counts.Count} item types in {ms:F2}ms.");
            }
        }
    }
}
