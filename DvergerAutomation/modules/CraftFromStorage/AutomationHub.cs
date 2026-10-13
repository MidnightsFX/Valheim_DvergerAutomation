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

        // The boat holds and cart beds among LinkedContainers. Vehicles come and go far faster than chests
        // get built, so they are relinked on a short cycle of their own rather than waiting out the chest scan.
        private readonly HashSet<Container> linkedVehicles = new HashSet<Container>();
        private const float VehicleRelinkInterval = 2f;

        // What a scan finds, before it is compared with the links it may replace.
        private readonly HashSet<CraftingStation> stationScratch = new HashSet<CraftingStation>();
        private readonly List<Container> chestScratch = new List<Container>();
        private readonly List<Container> vehicleScratch = new List<Container>();

        // What the last scan had seen loaded, so a rescan can be skipped when nothing has come since.
        private int scannedContainers = -1;
        private int scannedStations = -1;

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
        // not count until the next one. With Fast Initial Scan on, the hub looks every second through
        // this window instead, and rescans when a container or crafting station has loaded since its
        // last scan - which, once the base is in, it has not.
        private const float InitialScanInterval = 1f;
        private const float InitialScanWindow = 60f;
        private static readonly WaitForSeconds InitialScanWait = new WaitForSeconds(InitialScanInterval);
        private static readonly WaitForSeconds VehicleRelinkWait = new WaitForSeconds(VehicleRelinkInterval);
        private static readonly WaitForSeconds SettleWait = new WaitForSeconds(2f);

        // Live pieces only - the placement ghost has no ZDO, nothing to link and no inventory to protect.
        private bool IsLive => nview != null && nview.GetZDO() != null;

        private void Awake() {
            nview = GetComponent<ZNetView>();
            // The only Container under the piece is the deposit box; the root itself has none.
            DepositBox = GetComponentInChildren<Container>(includeInactive: true);
            sockets = GetComponent<CoreSockets>();
            if (sockets != null) {
                sockets.SetHoverName(NameToken);
                sockets.Changed += OnCoresChanged;
            }
            if (IsLive) {
                AutoStore.ApplySize(DepositBox);
                // Has to be repeated: only the owner may mark the ZDO, and this client may become it later.
                InvokeRepeating(nameof(KeepExclusive), 1f, 4f);
            }
        }

        private void OnEnable() {
            if (!IsLive) { return; }
            ContainerNetwork.Register(this);
            scanLoop = StartCoroutine(ScanLoopRoutine());
            vehicleLoop = StartCoroutine(VehicleLoopRoutine());
        }

        private void OnDisable() {
            if (!IsLive) { return; }
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
            linkedVehicles.Clear();
            scannedContainers = -1;
            ContainerNetwork.Unregister(this);
            // Unity stops the coroutines without running their finally blocks.
            AutoStore.HubDisabled(this);
        }

        private void OnDestroy() {
            if (sockets != null) { sockets.Changed -= OnCoresChanged; }
        }

        private IEnumerator ScanLoopRoutine() {
            // Let the world finish loading before the first scan.
            yield return SettleWait;
            // Seconds of the initial window used up.
            float initialElapsed = 0f;
            while (true) {
                bool initial = ValConfig.FastInitialScan.Value && initialElapsed < InitialScanWindow;
                if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) {
                    if (!initial) {
                        Scan();
                    } else {
                        ScanIfChanged();
                        // A portal holds the player in its loading screen for several seconds while the
                        // base streams in, so only time spent back in the world uses up the window.
                        if (!Player.m_localPlayer.IsTeleporting()) { initialElapsed += InitialScanInterval; }
                    }
                }
                if (initial) {
                    yield return InitialScanWait;
                } else {
                    yield return new WaitForSeconds(Mathf.Max(1f, ValConfig.ScanInterval.Value));
                }
            }
        }

        // A cart pulled up to the workshop should count within moments, not after the next chest scan,
        // and one pulled away should stop counting just as quickly. Cheap: it only looks at the loaded
        // boats and carts, and rebuilds the station cache only when the set actually changed.
        private IEnumerator VehicleLoopRoutine() {
            yield return SettleWait;
            while (true) {
                yield return VehicleRelinkWait;
                if (ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) {
                    RelinkVehicles();
                }
            }
        }

        /// <summary>
        /// Relinks if a container or crafting station has loaded or gone since the last scan. Used by the
        /// auto-store pass, so a chest built since the last scan tick is still a valid destination.
        /// </summary>
        internal void ScanIfChanged() {
            if (StorageRegistry.Changes != scannedContainers || CraftingStation.m_allStations.Count != scannedStations) { Scan(); }
        }

        private void Scan() {
            scannedStations = CraftingStation.m_allStations.Count;
            stationScratch.Clear();
            chestScratch.Clear();
            vehicleScratch.Clear();

            // With no cores inserted the hub links nothing (feature off) when they are required.
            if (!IsDormant) {
                float radius = EffectiveRadius;
                Vector3 pos = transform.position;
                // Crafting stations: the game's global station list, distance-checked (no physics needed).
                foreach (CraftingStation station in CraftingStation.m_allStations) {
                    if (station != null && Vector3.Distance(pos, station.transform.position) <= radius) {
                        stationScratch.Add(station);
                    }
                }
                // Every chest the player could open, the Hopper's store among them - crafting stock like any
                // chest; AutoStore keeps it out of the sort targets itself - and the boats and carts.
                StorageScan.Collect(pos, radius, StorageScan.LocalPlayerId(), chestScratch, hopperStores: true,
                    vehicleScratch, ValConfig.CraftFromBoats.Value, ValConfig.CraftFromCarts.Value);
            }
            // Read after the walk, which sweeps out what has been unloaded and counts that as a change.
            scannedContainers = StorageRegistry.Changes;
            // After the chests, so crafting spends out of this hub's chests before a boat's or cart's load.
            chestScratch.AddRange(vehicleScratch);

            // An unchanged result is the usual one once the base has loaded, and changing nothing keeps
            // every cache built on the links.
            if (LinkedStations.SetEquals(stationScratch) && SameOrder(LinkedContainers, chestScratch)) { return; }

            LinkedStations.Clear();
            LinkedStations.UnionWith(stationScratch);
            LinkedContainers.Clear();
            LinkedContainers.AddRange(chestScratch);
            linkedVehicles.Clear();
            linkedVehicles.UnionWith(vehicleScratch);

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Autosorter] scan linked {LinkedStations.Count} stations, {LinkedContainers.Count - linkedVehicles.Count} chests, {linkedVehicles.Count} boats/carts within {EffectiveRadius}m.");
            }
            ContainerNetwork.LinksChanged();
        }

        // The vehicle half of Scan on its own: swaps this hub's boat and cart links for whatever is in
        // range right now, leaving its chests alone.
        private void RelinkVehicles() {
            vehicleScratch.Clear();
            if (!IsDormant) {
                StorageScan.Collect(transform.position, EffectiveRadius, StorageScan.LocalPlayerId(), null, false,
                    vehicleScratch, ValConfig.CraftFromBoats.Value, ValConfig.CraftFromCarts.Value);
            }
            if (linkedVehicles.SetEquals(vehicleScratch)) { return; }

            LinkedContainers.RemoveAll(linkedVehicles.Contains);
            linkedVehicles.Clear();
            linkedVehicles.UnionWith(vehicleScratch);
            LinkedContainers.AddRange(vehicleScratch);
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Autosorter] relinked {linkedVehicles.Count} boats/carts.");
            }
            ContainerNetwork.LinksChanged();
        }

        private static bool SameOrder(List<Container> a, List<Container> b) {
            if (a.Count != b.Count) { return false; }
            for (int i = 0; i < a.Count; ++i) {
                if (!ReferenceEquals(a[i], b[i])) { return false; }
            }
            return true;
        }

        // ---- Surtling Core slots --------------------------------------------

        /// <summary>Number of inserted cores (0-4), held by the piece's <see cref="CoreSockets"/>.</summary>
        internal int CoreCount => sockets != null ? sockets.Count : 0;

        /// <summary>Whether the hub links nothing because it needs a core and has none.</summary>
        internal bool IsDormant => ValConfig.RequireCores.Value && CoreCount == 0;

        /// <summary>Base link radius plus the configured bonus for each inserted core.</summary>
        internal float EffectiveRadius => ValConfig.ScanRadius.Value + CoreCount * ValConfig.RangePerCore.Value;

        // Runs on every client: each copy relinks at once, so the new range or active state applies
        // without waiting for the scan tick.
        private void OnCoresChanged() {
            if (IsLive && ValConfig.AutomationEnabled.Value && Player.m_localPlayer != null) { Scan(); }
        }

        private void KeepExclusive() {
            MultiUserChestIntegration.KeepExclusive(nview);
        }
    }
}
