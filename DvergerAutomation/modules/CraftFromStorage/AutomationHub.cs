using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_Autosorter piece. Attached to the prefab in Unity (not in code), next to
    /// the <see cref="CoreSockets"/> holding its four Surtling Cores.
    /// On a timer it links nearby crafting stations and nearby accessible storage chests, turning the
    /// chests into a shared material pool. The actual "use chest items while crafting/building" behaviour
    /// is applied by the Harmony patches in CraftFromStoragePatches, which read this hub's links via
    /// <see cref="ContainerNetwork"/>.
    /// </summary>
    public class AutomationHub : MonoBehaviour {
        private const string NameToken = "$DA_autosorter_name";

        private ZNetView nview;
        private CoreSockets sockets;

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
            sockets = GetComponent<CoreSockets>();
            if (sockets != null) {
                sockets.SetHoverName(NameToken);
                sockets.Changed += OnCoresChanged;
            }
            if (nview != null && nview.GetZDO() != null) {
                // Live pieces only - the placement ghost has no ZDO and no inventory to size or protect.
                // Registered here rather than in OnEnable because Container.Load can run for the box as soon as
                // its first CheckForChanges tick, and the load guard has to recognise it by then.
                AutoStore.RegisterDepositBox(DepositBox);
            }

            // Has to be repeated: only the owner may mark the ZDO, and this client may become it later.
            InvokeRepeating(nameof(KeepExclusive), 1f, 4f);
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
            if (sockets != null) { sockets.Changed -= OnCoresChanged; }
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
                ContainerNetwork.LinksChanged();
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

            // Containers: every chest on the piece layers the player could open. The Hopper's store is
            // crafting stock like any chest; AutoStore keeps it out of the sort targets itself.
            StorageScan.CollectChests(pos, radius, playerId, LinkedContainers, requireResolvable: false, includeHopperStores: true);

            // After the chests, so crafting spends out of this hub's chests before a boat's or cart's load.
            VehicleStorage.Collect(pos, radius, playerId, LinkedVehicles);
            LinkedContainers.AddRange(LinkedVehicles.Keys);

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Autosorter] scan linked {LinkedStations.Count} stations, {LinkedContainers.Count - LinkedVehicles.Count} chests, {LinkedVehicles.Count} boats/carts within {radius}m.");
            }

            ContainerNetwork.LinksChanged();
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
            ContainerNetwork.LinksChanged();
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

        /// <summary>Number of inserted cores (0-4), held by the piece's <see cref="CoreSockets"/>.</summary>
        internal int CoreCount => sockets != null ? sockets.Count : 0;

        /// <summary>Base link radius plus the configured bonus for each inserted core.</summary>
        internal float EffectiveRadius => ValConfig.ScanRadius.Value + CoreCount * ValConfig.RangePerCore.Value;

        // Runs on every client: each copy relinks at once, so the new range or active state applies
        // without waiting for the scan tick.
        private void OnCoresChanged() {
            if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) { Scan(); }
        }

        private void KeepExclusive() {
            MultiUserChestIntegration.KeepExclusive(nview);
        }
    }

    /// <summary>
    /// Registry of active autosorter hubs plus the cached station to containers lookup used by the
    /// crafting Harmony patches.
    /// </summary>
    internal static class ContainerNetwork {
        internal static readonly List<AutomationHub> Hubs = new List<AutomationHub>();

        // Cached map rebuilt only after a hub scans / (un)registers - not on every crafting check - and
        // then lazily, on the next lookup, so several hubs scanning in a row rebuild it once.
        private static readonly Dictionary<CraftingStation, List<Container>> StationToContainers =
            new Dictionary<CraftingStation, List<Container>>();
        private static bool linksChanged = true;
        // Scratch set reused across EnsureStationCache to dedup a station's containers in O(1).
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

        // Aggregate of item-name -> total stack across a container pool. Crafting/build queries fan out
        // across hundreds of recipes (panel refresh) and the selected recipe (every frame); without this,
        // each query rescans every chest's inventory and the game freezes once a few hundred chests are
        // linked. Checked at most once per frame per pool reference, and only rebuilt when the pool or
        // one of its chests has changed since (see AggregateCurrent).
        private static readonly Dictionary<string, int> AggCounts = new Dictionary<string, int>();
        private static int aggFrame = -1;
        private static List<Container> aggPool;
        // What the aggregate was built from: each chest in pool order, with the revision of its grid.
        private static readonly List<Container> AggSources = new List<Container>();
        private static readonly List<long> AggRevisions = new List<long>();
        private static int aggWorldLevel;
        private static bool aggBuilt;

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
            LinksChanged();
        }

        internal static void Unregister(AutomationHub hub) {
            Hubs.Remove(hub);
            LinksChanged();
        }

        /// <summary>
        /// A hub's links changed. The station cache is rebuilt on its next lookup, and the near-point and
        /// count caches start over now.
        /// </summary>
        internal static void LinksChanged() {
            linksChanged = true;
            nearPointFrame = -1;
            InvalidateItemCounts();
        }

        private static void EnsureStationCache() {
            if (!linksChanged) { return; }
            linksChanged = false;
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
        }

        /// <summary>
        /// Drops the item aggregates. Chest *membership* changes go through <see cref="LinksChanged"/>,
        /// but chest *contents* can change under us - auto-store moving items in, crafting taking them out
        /// - and the aggregate would otherwise serve counts from before the change for the rest of the
        /// frame.
        /// </summary>
        internal static void InvalidateItemCounts() {
            aggBuilt = false;
            aggFrame = -1;
            aggPool = null;
            spendFrame = -1;
            spendPool = null;
            // Epic Loot's enchanting table reads a memo of the same pool, on the same per-frame basis.
            EpicLootIntegration.InvalidateItemCache();
        }

        /// <summary>The boat or cart carrying a linked container; false for a plain chest.</summary>
        internal static bool TryGetCarrier(Container container, out MonoBehaviour carrier) {
            EnsureStationCache();
            return Carriers.TryGetValue(container, out carrier);
        }

        /// <summary>Containers linked to the given crafting station (station-crafting pool). O(1) lookup.</summary>
        internal static List<Container> GetContainersForStation(CraftingStation station) {
            if (!ValConfig.CraftFromStorageEnabled.Value) { return Empty; }
            EnsureStationCache();
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
        /// <c>Inventory.CountItems(name, -1, matchWorldLevel: true)</c>. Backed by an aggregate that is
        /// only rebuilt when the pool or a chest in it has changed, turning each query into an O(1)
        /// dictionary lookup.
        /// </summary>
        internal static int CountInPool(List<Container> pool, string name) {
            if (pool == null || pool.Count == 0) { return 0; }
            if (aggFrame != Time.frameCount || !ReferenceEquals(aggPool, pool)) {
                if (!AggregateCurrent(pool)) {
                    BuildAggregate(pool, AggCounts, spendableOnly: false);
                    AggSources.Clear();
                    AggRevisions.Clear();
                    foreach (Container container in pool) {
                        AggSources.Add(container);
                        AggRevisions.Add(GridRevision(container));
                    }
                    aggWorldLevel = Game.m_worldLevel;
                    aggBuilt = true;
                }
                aggFrame = Time.frameCount;
                aggPool = pool;
            }
            return AggCounts.TryGetValue(name, out int total) ? total : 0;
        }

        /// <summary>
        /// Whether the aggregate still describes the pool: the same chests in the same order, none of
        /// whose grids has changed here since. A chest's <c>m_lastRevision</c> moves exactly when its copy
        /// here does - this client saving it, or reloading it from the ZDO - so comparing one number per
        /// chest stands in for re-reading every item in it.
        /// </summary>
        private static bool AggregateCurrent(List<Container> pool) {
            if (!aggBuilt || aggWorldLevel != Game.m_worldLevel || pool.Count != AggSources.Count) { return false; }
            for (int i = 0; i < pool.Count; ++i) {
                if (!ReferenceEquals(pool[i], AggSources[i]) || GridRevision(pool[i]) != AggRevisions[i]) { return false; }
            }
            return true;
        }

        // -1 for a chest the aggregate skips, so one coming back counts as a change.
        private static long GridRevision(Container container) {
            if (container == null || container.m_nview == null || !container.m_nview.IsValid() || container.GetInventory() == null) { return -1; }
            return container.m_lastRevision;
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
