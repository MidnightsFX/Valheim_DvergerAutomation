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

        // Surtling Core prefab name (used to remove/refund/drop cores).
        private const string SurtlingCorePrefab = "SurtlingCore";

        private ZNetView nview;

        // Links produced by the most recent scan. Read by ContainerNetwork.
        internal readonly HashSet<CraftingStation> LinkedStations = new HashSet<CraftingStation>();
        internal readonly List<Container> LinkedContainers = new List<Container>();

        private Coroutine scanLoop;
        private static int pieceMask = 0;

        private void Awake() {
            nview = GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null) {
                nview.Register("DA_RefreshCores", new Action<long>(RPC_RefreshCores));
                WearNTear wnt = GetComponent<WearNTear>();
                if (wnt != null) { wnt.m_onDestroyed += OnDestroyedDropCores; }
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
        }

        private void OnDisable() {
            if (scanLoop != null) {
                StopCoroutine(scanLoop);
                scanLoop = null;
            }
            LinkedStations.Clear();
            LinkedContainers.Clear();
            ContainerNetwork.Unregister(this);
        }

        private IEnumerator ScanLoopRoutine() {
            // Let the world finish loading before the first scan.
            yield return new WaitForSeconds(2f);
            while (true) {
                if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) {
                    Scan();
                }
                yield return new WaitForSeconds(Mathf.Max(1f, ValConfig.ScanInterval.Value));
            }
        }

        private void Scan() {
            LinkedStations.Clear();
            LinkedContainers.Clear();

            // Gated inactive: with no cores inserted the hub links nothing (feature off) when required.
            if (ValConfig.RequireCores.Value && CoreCount == 0) {
                ContainerNetwork.RebuildStationCache();
                return;
            }

            float radius = EffectiveRadius;
            Vector3 pos = transform.position;
            long playerId = (Game.instance != null && Game.instance.GetPlayerProfile() != null)
                ? Game.instance.GetPlayerProfile().GetPlayerID()
                : 0L;

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
                if (IsAccessible(container, playerId)) {
                    LinkedContainers.Add(container);
                }
            }

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Autosorter] scan linked {LinkedStations.Count} stations, {LinkedContainers.Count} chests within {radius}m.");
            }

            ContainerNetwork.RebuildStationCache();
        }

        // A chest is usable only if the local player can freely access it: not Private/Group-locked to
        // someone else, and not inside a ward the player is not permitted in (the player's own ward passes).
        private static bool IsAccessible(Container container, long playerId) {
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

        // Persists a new core bitmask: take ownership, write the ZDO, tell every client to refresh its
        // visuals, and relink locally so the new range/active state applies immediately for this client.
        private void SetCoreMask(int mask) {
            if (nview == null || !nview.IsValid()) { return; }
            if (!nview.IsOwner()) { nview.ClaimOwnership(); }
            nview.GetZDO().Set(CoreMaskKey, mask);
            nview.InvokeRPC(ZNetView.Everybody, "DA_RefreshCores");
            UpdateVisuals();
            // Relink immediately so the new range / active state applies without waiting for the scan tick.
            if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) { Scan(); }
        }

        private void RPC_RefreshCores(long sender) {
            UpdateVisuals();
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

            GameObject corePrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(SurtlingCorePrefab) : null;
            if (corePrefab == null) { return false; }
            string coreName = corePrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;

            int mask = GetCoreMask();
            bool filled = (mask & (1 << slot)) != 0;

            if (filled) {
                if (!player.GetInventory().AddItem(corePrefab, 1)) {
                    user.Message(MessageHud.MessageType.Center, "$inventory_full");
                    return false;
                }
                SetCoreMask(mask & ~(1 << slot));
                user.Message(MessageHud.MessageType.Center, "$DA_Remove_Core");
            } else {
                if (player.GetInventory().CountItems(coreName) <= 0) {
                    user.Message(MessageHud.MessageType.Center, "$DA_Need_Core");
                    return false;
                }
                player.GetInventory().RemoveItem(coreName, 1);
                SetCoreMask(mask | (1 << slot));
                user.Message(MessageHud.MessageType.Center, "$DA_Add_Core");
            }
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
            GameObject corePrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(SurtlingCorePrefab) : null;
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

        private static readonly List<Container> Empty = new List<Container>();

        // Frame-memoized aggregate of item-name -> total stack across a container pool. Crafting/build
        // queries fan out across hundreds of recipes (panel refresh) and the selected recipe (every
        // frame); without this, each query rescans every chest's inventory and the game freezes once a
        // few hundred chests are linked. Rebuilt at most once per frame per pool reference.
        private static readonly Dictionary<string, int> AggCounts = new Dictionary<string, int>();
        private static int aggFrame = -1;
        private static List<Container> aggPool;

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
            foreach (AutomationHub hub in Hubs) {
                if (hub == null) { continue; }
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
            aggFrame = -1;
            aggPool = null;
            nearPointFrame = -1;
        }

        /// <summary>Containers linked to the given crafting station (station-crafting pool). O(1) lookup.</summary>
        internal static List<Container> GetContainersForStation(CraftingStation station) {
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
                BuildAggregate(pool);
            }
            return AggCounts.TryGetValue(name, out int total) ? total : 0;
        }

        private static void BuildAggregate(List<Container> pool) {
            bool debug = ValConfig.EnableDebugMode.Value;
            double startTime = debug ? Time.realtimeSinceStartupAsDouble : 0.0;

            AggCounts.Clear();
            int worldLevel = Game.m_worldLevel;
            foreach (Container container in pool) {
                if (container == null) { continue; }
                Inventory inv = container.GetInventory();
                if (inv == null) { continue; }
                foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                    // Mirror CountItems(name, -1, matchWorldLevel: true): any quality, world-level gated.
                    if (item.m_worldLevel < worldLevel) { continue; }
                    // Enchanted gear is not spendable as plain material, so it must not be counted as
                    // such either - otherwise a chest of legendaries reads as free crafting stock.
                    if (EpicLootIntegration.IsProtectedItem(item)) { continue; }
                    string itemName = item.m_shared.m_name;
                    AggCounts.TryGetValue(itemName, out int cur);
                    AggCounts[itemName] = cur + item.m_stack;
                }
            }
            aggFrame = Time.frameCount;
            aggPool = pool;

            if (debug) {
                double ms = (Time.realtimeSinceStartupAsDouble - startTime) * 1000.0;
                Logger.LogInfo($"[Autosorter] aggregated {pool.Count} chests, {AggCounts.Count} item types in {ms:F2}ms.");
            }
        }
    }
}
