using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_ForgeHopper piece. Attached to the prefab in Unity (not in code), next to
    /// the <see cref="CoreSockets"/> holding its six Surtling Cores.
    ///
    /// The hopper keeps a single shared inventory, reached through the hatch on its front face. On a
    /// timer it links every <see cref="Smelter"/> in range and services them from that one inventory:
    /// ore first, then the fuel that ore needs. Finished product is pulled back into the same inventory
    /// by the <c>Smelter.Spawn</c> prefix in <see cref="HopperPatches"/>, so the chain closes on itself -
    /// wood becomes coal in a kiln, and that coal is then spent as smelter fuel.
    ///
    /// Note that <see cref="Smelter"/> is not just the smelter: the windmill, spinning wheel, eitr
    /// refinery and plenty of modded stations use the same component too. Each hopper links only the
    /// station types it targets - <see cref="DefaultTargets"/> until a player picks otherwise in its [C]
    /// popup (<see cref="HopperTargetPanel"/>). The choice is saved on the hopper's ZDO, so every player
    /// sees and services the same set.
    ///
    /// Every item move happens on one client: the one that owns the hopper, and only for stations it
    /// also owns. It asks for the rest, so the stations follow the hopper. See
    /// <see cref="ServiceSmelters"/> for why.
    /// </summary>
    public class HopperHub : MonoBehaviour {
        // ---- Unity-wired references ----------------------------------------

        /// <summary>
        /// Shown while the hopper works. Follows <see cref="IsActive"/> rather than the cores, which is why
        /// it lives here and not on the <see cref="CoreSockets"/>: with "Require Cores" turned off the
        /// hopper works on zero cores, and a working hopper that never smokes reads as broken.
        /// </summary>
        public GameObject EnabledVisuals;

        /// <summary>
        /// Optional. Any transform listed here spins about its own local X while the hopper is working.
        /// Left empty for now: the drive gears are modelled two-to-an-object with their origins at the
        /// piece root, so spinning them would swing them around the wrong pivot. Split each gear onto
        /// its own object with the origin on the gear axis and they can simply be dropped in here.
        /// </summary>
        public Transform[] SpinningGears = new Transform[0];

        // ---- state ----------------------------------------------------------

        private const string NameToken = "$DA_hopper_name";

        // ZDO key holding the station types this hopper services, as comma-separated prefab names. Absent
        // until someone first changes the choice; a hopper without it services DefaultTargets. An empty
        // string is a real choice (service nothing) and is stored as such.
        private const string TargetsKey = "DA_hopper_targets";

        /// <summary>
        /// Prefab names of the stations a hopper services until someone picks otherwise: the charcoal
        /// kiln, smelter and blast furnace. Everything else on the Smelter component is opt-in.
        /// </summary>
        internal static readonly string[] DefaultTargets = { "charcoal_kiln", "smelter", "blastfurnace" };
        private static readonly string DefaultTargetList = string.Join(",", DefaultTargets);

        // The stored target list, parsed. Re-parsed only when the raw string changes.
        private readonly HashSet<string> targets = new HashSet<string>();
        private string targetsParsedFrom;

        private ZNetView nview;
        private CoreSockets sockets;
        private Coroutine loop;

        // Linking overlaps every collider in range and asks a ward about each station, so a tick only
        // relinks when something the last scan depended on has moved (see ScanDue). Past this many seconds
        // it relinks anyway: a ward's permissions can change without anything else doing so.
        private const float RescanSeconds = 30f;
        private int scannedStations = -1;
        private float scannedAt;
        private string scannedFor;

        // Rotating start index, so a run of smelters is served fairly instead of the first one soaking
        // up the whole per-tick budget.
        private int rotateStart;

        /// <summary>
        /// The piece's single shared inventory. Resolved from the hierarchy rather than serialized, so
        /// the prefab needs no extra Unity wiring.
        /// </summary>
        internal Container Store { get; private set; }

        /// <summary>Smelters linked by the most recent scan. Read by <see cref="HopperNetwork"/>.</summary>
        internal readonly List<Smelter> LinkedSmelters = new List<Smelter>();

        // The linked stations this client also owns - the only ones a service pass touches.
        private readonly List<Smelter> ownedSmelters = new List<Smelter>();

        /// <summary>Whether this client owns the hopper, and so is the only one allowed to write its store.</summary>
        internal bool IsOwner => nview != null && nview.IsValid() && nview.IsOwner();

        private void Awake() {
            nview = GetComponent<ZNetView>();
            // The only Container under the piece is the store; the root itself has none.
            Store = GetComponentInChildren<Container>(includeInactive: true);
            sockets = GetComponent<CoreSockets>();
            if (sockets != null) {
                sockets.SetHoverName(NameToken);
                sockets.StatusLine = CoreStatus;
                sockets.Changed += OnCoresChanged;
            }

            if (nview != null && nview.GetZDO() != null) {
                nview.Register<string, bool>("DA_HopperTargetRequest", RPC_TargetRequest);
            }

            // Refresh visuals shortly after load (and periodically) so a late ZDO sync still lights up.
            InvokeRepeating(nameof(UpdateVisuals), 1f, 4f);
        }

        private void OnDestroy() {
            if (sockets != null) { sockets.Changed -= OnCoresChanged; }
        }

        private void OnEnable() {
            HopperNetwork.Register(this);
            loop = StartCoroutine(TickRoutine());
        }

        private void OnDisable() {
            if (loop != null) {
                StopCoroutine(loop);
                loop = null;
            }
            LinkedSmelters.Clear();
            // Unregistering rebuilds the speed map, which restores m_secPerProduct on anything this hub
            // was the only one boosting.
            HopperNetwork.Unregister(this);
        }

        // ---- the tick -------------------------------------------------------

        private IEnumerator TickRoutine() {
            // Let the world finish loading before the first scan.
            yield return new WaitForSeconds(2f);
            while (true) {
                if (Player.m_localPlayer != null) {
                    // Linking and the core speed-up run on every client, not just the ZDO owner:
                    // Smelter.UpdateSmelter runs on the *smelter's* owner, and that may be someone else,
                    // so each client has to apply the boost to its own instances.
                    if (ScanDue()) { SafeScan(); }

                    // Moving items is owner-only. Two clients both feeding the same smelter would
                    // consume the stack twice.
                    if (nview != null && nview.IsValid() && nview.IsOwner()) { SafeService(); }
                }
                yield return new WaitForSeconds(Mathf.Max(1f, ValConfig.HopperInterval.Value));
            }
        }

        // try/catch cannot wrap a yield, so the guarded work lives in its own methods.
        private void SafeScan() {
            try {
                Scan();
            } catch (Exception ex) {
                Logger.LogError($"[Hopper] scan failed: {ex}");
            }
        }

        private void SafeService() {
            try {
                ServiceSmelters();
            } catch (Exception ex) {
                Logger.LogError($"[Hopper] servicing smelters failed: {ex}");
            }
        }

        /// <summary>
        /// Whether the links are worth rebuilding: a station has loaded or been built anywhere since the
        /// last scan, a linked one is gone, the hopper's cores, targets or settings changed, or
        /// <see cref="RescanSeconds"/> have passed.
        /// </summary>
        private bool ScanDue() {
            if (HopperNetwork.StationsLoaded != scannedStations) { return true; }
            if (Time.time - scannedAt >= RescanSeconds) { return true; }
            if (ScanInputs() != scannedFor) { return true; }
            foreach (Smelter smelter in LinkedSmelters) {
                if (smelter == null) { return true; }
            }
            return false;
        }

        // Everything a scan reads besides the stations themselves.
        private string ScanInputs() => $"{IsActive}|{EffectiveRadius}|{SpeedMultiplier}|{TargetList}";

        private void Scan() {
            scannedStations = HopperNetwork.StationsLoaded;
            scannedAt = Time.time;
            scannedFor = ScanInputs();
            LinkedSmelters.Clear();

            bool dormant = !ValConfig.HopperEnabled.Value
                           || (ValConfig.HopperRequireCores.Value && CoreCount == 0);
            if (dormant) {
                // Links nothing, and the rebuild below hands back any speed-up this hub was applying.
                HopperNetwork.RebuildSpeedMap();
                return;
            }

            foreach (Smelter smelter in StationsInRange()) {
                if (IsTarget(StationType(smelter))) { LinkedSmelters.Add(smelter); }
            }

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Hopper] linked {LinkedSmelters.Count} smelters within {EffectiveRadius}m.");
            }

            HopperNetwork.RebuildSpeedMap();
        }

        /// <summary>
        /// Every station on the Smelter component within the hopper's current reach that the player may
        /// use, whatever its type. The scan narrows this to the targets; the [C] popup offers all of it,
        /// which is why it ignores whether the hopper is dormant.
        /// </summary>
        internal List<Smelter> StationsInRange() {
            List<Smelter> found = new List<Smelter>();
            HashSet<Smelter> seen = new HashSet<Smelter>();
            Collider[] hits = StorageScan.OverlapPieces(transform.position, EffectiveRadius, out int count);
            for (int i = 0; i < count; ++i) {
                Collider hit = hits[i];
                if (hit == null) { continue; }
                Smelter smelter = hit.GetComponentInParent<Smelter>();
                if (smelter == null || !seen.Add(smelter)) { continue; }
                if (smelter.m_nview == null || !smelter.m_nview.IsValid()) { continue; }
                // A smelter inside a ward the player is not permitted in is none of our business.
                if (!PrivateArea.CheckAccess(smelter.transform.position, 0f, flash: false)) { continue; }
                found.Add(smelter);
            }
            StorageScan.ReleaseOverlap(count);
            return found;
        }

        // ---- targets --------------------------------------------------------

        /// <summary>A station's type, as the target list names it: its prefab name.</summary>
        internal static string StationType(Smelter smelter) => global::Utils.GetPrefabName(smelter.gameObject);

        /// <summary>
        /// The stored target list, raw. The popup compares it between frames to notice a change that
        /// arrived from another player.
        /// </summary>
        internal string TargetList =>
            nview != null && nview.IsValid() ? nview.GetZDO().GetString(TargetsKey, DefaultTargetList) : DefaultTargetList;

        /// <summary>Whether this hopper services stations of the given prefab.</summary>
        internal bool IsTarget(string stationType) {
            string list = TargetList;
            if (list != targetsParsedFrom) {
                targets.Clear();
                foreach (string part in list.Split(',')) {
                    string name = part.Trim();
                    if (name.Length > 0) { targets.Add(name); }
                }
                targetsParsedFrom = list;
            }
            return targets.Contains(stationType);
        }

        /// <summary>
        /// Turns one station type on or off for this hopper. Asked of the owner, like a core socket, so
        /// the list has a single writer. The player who has the hatch open normally is the owner, and then
        /// the change lands before this returns.
        /// </summary>
        internal void RequestTarget(string stationType, bool on) {
            if (nview == null || !nview.IsValid() || string.IsNullOrEmpty(stationType)) { return; }
            nview.InvokeRPC("DA_HopperTargetRequest", stationType, on);
        }

        private void RPC_TargetRequest(long sender, string stationType, bool on) {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            if (string.IsNullOrEmpty(stationType) || stationType.IndexOf(',') >= 0) { return; }
            if (IsTarget(stationType) == on) { return; }

            // IsTarget has just brought the parsed set up to date with the ZDO.
            List<string> next = new List<string>(targets);
            if (on) { next.Add(stationType); } else { next.Remove(stationType); }
            // Sorted so the stored string does not depend on click order.
            next.Sort(StringComparer.Ordinal);
            nview.GetZDO().Set(TargetsKey, string.Join(",", next));

            // The owner is the one that feeds and collects, so it relinks now. Every other client picks
            // the change up on its next tick: a broadcast would usually beat the ZDO there and rescan
            // against the old list, and for them linking only decides the speed-up anyway.
            if (Player.m_localPlayer != null) { SafeScan(); }
        }

        /// <summary>
        /// Collect finished product, queue ore, then fuel what is queued - in that order, and that order
        /// matters. Collecting first means coal a charcoal kiln finished this tick is already in the
        /// inventory and can be spent as smelter fuel in the same tick, which is what closes the wood ->
        /// coal -> bars chain without any special-casing. Ore before fuel because fuel is sized to the
        /// queue (see <see cref="FeedFuel"/>), so ore queued this tick is fuelled this tick too.
        ///
        /// Only stations this client owns are touched. Vanilla's RPC_AddOre / RPC_AddFuel are addressed
        /// to the sender's copy of the station's owner and silently do nothing on anyone else, so an item
        /// sent across the network while the station changes hands is simply gone - and the hopper sends
        /// them by the stack. Sent to a station this client owns, ZRoutedRpc runs the RPC right here, in
        /// the same frame the item left the store: no network hop, nothing to lose, and the room read
        /// from the ZDO is the real one rather than a lagging replica. A station owned by someone else is
        /// asked for instead (<see cref="HopperNetwork.AskForStation"/>) and fed once it has arrived.
        ///
        /// The stations come to the hopper rather than the other way round: handing the hopper to whoever
        /// owned most of its stations still left the others unfed whenever they ran on different games.
        /// </summary>
        private void ServiceSmelters() {
            if (Store == null || LinkedSmelters.Count == 0) { return; }

            ownedSmelters.Clear();
            foreach (Smelter smelter in LinkedSmelters) {
                if (smelter == null || smelter.m_nview == null || !smelter.m_nview.IsValid()) { continue; }
                if (smelter.m_nview.IsOwner()) {
                    ownedSmelters.Add(smelter);
                } else {
                    HopperNetwork.AskForStation(smelter);
                }
            }
            if (ownedSmelters.Count == 0) { return; }

            // Someone has the hatch open. Writing to the inventory under them would blank their panel
            // (see CraftFromStoragePatches.IsBusy), so the hopper idles until they close it.
            if (CraftFromStoragePatches.IsBusy(Store)) { return; }

            SyncStore();
            Inventory inv = Store.GetInventory();
            if (inv == null) { return; }

            FlushFinished();

            int budget = Mathf.Max(1, ValConfig.HopperItemsPerTick.Value);
            int moved = FeedOre(inv, budget);
            moved += FeedFuel(inv, budget - moved);

            // Advance the round robin so the next tick starts at a different smelter.
            rotateStart = (rotateStart + 1) % ownedSmelters.Count;

            if (moved > 0 && ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Hopper] moved {moved} items into {ownedSmelters.Count} smelters.");
            }
        }

        /// <summary>
        /// Brings the store's grid up to date with its ZDO before anything writes to it. Container only
        /// reloads on its once-a-second CheckForChanges, so for up to a second after this client becomes
        /// the owner - handed the hopper, or given it by the server - the grid it holds can be older than
        /// the one the previous owner last saved, and saving over that rolls the previous owner's work
        /// back. A no-op when nothing has changed.
        /// </summary>
        internal void SyncStore() {
            if (Store != null && !Store.IsInUse()) { Store.Load(); }
        }

        /// <summary>
        /// Vanilla only empties a smelter when its queue runs dry or its fuel does, so a continuously fed
        /// smelter would sit on a finished stack forever. Each item is normally collected as it joins the
        /// batch (see <see cref="HopperNetwork.CollectBatch"/>); this catches a batch that could not be
        /// taken then - the hatch was open, or the store was full. The RPC runs SpawnProcessed here and
        /// now (this client owns every station in the list), and the Smelter.Spawn prefix then routes the
        /// product into this inventory.
        /// </summary>
        private void FlushFinished() {
            if (!ValConfig.HopperCollectOutput.Value) { return; }
            foreach (Smelter smelter in ownedSmelters) {
                if (smelter.GetProcessedQueueSize() > 0) {
                    smelter.m_nview.InvokeRPC("RPC_EmptyProcessed");
                }
            }
        }

        /// <summary>
        /// Gives each station the fuel its queued ore will burn, not a full tank. Topping every linked
        /// station up to its cap parked 20 coal in each idle smelter and blast furnace, where it sat out
        /// of the hopper and out of reach of the station that actually had ore to melt. Vanilla burns
        /// <c>m_fuelPerProduct</c> per item at any speed, so the queue times that is the whole bill. If
        /// the rounding of the boosted burn leaves a station a step short on its last item, it pauses
        /// until the next pass tops it up.
        /// </summary>
        private int FeedFuel(Inventory inv, int budget) {
            if (budget <= 0) { return 0; }
            int moved = 0;
            int count = ownedSmelters.Count;
            for (int i = 0; i < count && moved < budget; ++i) {
                Smelter smelter = ownedSmelters[(rotateStart + i) % count];
                if (smelter.m_maxFuel <= 0 || smelter.m_fuelItem == null) { continue; }

                // Measured once, before anything is added, and each add is counted against it.
                float fuel = smelter.GetFuel();
                int room = Mathf.FloorToInt(smelter.m_maxFuel - fuel);
                int add = Mathf.Min(room, Mathf.CeilToInt(FuelWanted(smelter) - fuel));
                if (add <= 0) { continue; }

                string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;
                int have = inv.CountItems(fuelName, -1, matchWorldLevel: false);
                add = Mathf.Min(add, have, budget - moved);
                for (int k = 0; k < add; ++k) {
                    // Not world-level based, to match the count above: otherwise fuel from before a
                    // world level change is counted but never removed, and the station is fed for free.
                    inv.RemoveItem(fuelName, 1, -1, worldLevelBased: false);
                    smelter.m_nview.InvokeRPC("RPC_AddFuel");
                }
                moved += add;
            }
            return moved;
        }

        /// <summary>
        /// The fuel a station should hold: enough for everything in its queue, and none when the queue is
        /// empty. A station without an ore queue (m_maxOre 0, only ever a modded one) burns fuel with no
        /// input at all, so it is still filled to the cap.
        /// </summary>
        private static float FuelWanted(Smelter smelter) {
            if (smelter.m_maxOre <= 0) { return smelter.m_maxFuel; }
            int queued = smelter.GetQueueSize();
            if (queued <= 0) { return 0f; }
            // A station that burns nothing per item still will not run on an empty tank.
            return Mathf.Max(1, queued * smelter.m_fuelPerProduct);
        }

        private int FeedOre(Inventory inv, int budget) {
            if (budget <= 0) { return 0; }
            int moved = 0;
            int count = ownedSmelters.Count;
            for (int i = 0; i < count && moved < budget; ++i) {
                Smelter smelter = ownedSmelters[(rotateStart + i) % count];
                if (smelter.m_maxOre <= 0) { continue; }

                // Measured once, before anything is added, and each add is counted against it.
                int room = smelter.m_maxOre - smelter.GetQueueSize();
                if (room <= 0) { continue; }

                // GetAllItems hands back the live backing list and emptied stacks drop out of it.
                foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(inv.GetAllItems())) {
                    if (room <= 0 || moved >= budget) { break; }
                    if (item == null || item.m_shared == null || item.m_dropPrefab == null) { continue; }
                    if (!smelter.IsItemAllowed(item.m_dropPrefab.name)) { continue; }
                    // Enchanted gear shares m_shared.m_name with its mundane counterpart. Never melt one.
                    if (EpicLootIntegration.IsProtectedItem(item)) { continue; }

                    int take = Mathf.Min(item.m_stack, Mathf.Min(room, budget - moved));
                    if (take <= 0) { continue; }

                    string prefabName = item.m_dropPrefab.name;
                    bool cheated = item.m_cheated;
                    // Remove first, then queue. This client owns the smelter, so each RPC runs
                    // synchronously here: the units are never in both places, or in neither.
                    inv.RemoveItem(item, take);
                    for (int k = 0; k < take; ++k) {
                        smelter.m_nview.InvokeRPC("RPC_AddOre", prefabName, cheated);
                    }
                    room -= take;
                    moved += take;
                }
            }
            return moved;
        }

        // ---- Surtling Core slots --------------------------------------------

        /// <summary>Number of inserted cores (0-6), held by the piece's <see cref="CoreSockets"/>.</summary>
        internal int CoreCount => sockets != null ? sockets.Count : 0;

        private int SlotCount => sockets != null ? sockets.SlotCount : 0;

        /// <summary>The hopper works once at least one core is in, or immediately if cores are optional.</summary>
        internal bool IsActive {
            get {
                if (!ValConfig.HopperEnabled.Value) { return false; }
                return !ValConfig.HopperRequireCores.Value || CoreCount > 0;
            }
        }

        internal float EffectiveRadius =>
            ValConfig.HopperRadius.Value + CoreCount * ValConfig.HopperRangePerCore.Value;

        /// <summary>
        /// How much faster linked smelters run. Applied by dividing <c>Smelter.m_secPerProduct</c>, which
        /// leaves fuel-per-product untouched: vanilla burns <c>m_fuelPerProduct / m_secPerProduct</c> per
        /// second across <c>m_secPerProduct</c> seconds, so the cost of a bar is unchanged and only the
        /// wait shrinks.
        /// </summary>
        internal float SpeedMultiplier => 1f + CoreCount * ValConfig.HopperSpeedPerCore.Value;

        // Runs on every client: range and speed changed for everyone, not just the player who turned the core.
        private void OnCoresChanged() {
            UpdateVisuals();
            if (Player.m_localPlayer != null) { SafeScan(); }
        }

        private string CoreStatus() {
            return Localization.instance.Localize(
                "$DA_hopper_cores_status", CoreCount.ToString(), SlotCount.ToString(),
                Mathf.RoundToInt(EffectiveRadius).ToString(),
                Mathf.RoundToInt((SpeedMultiplier - 1f) * 100f).ToString());
        }

        private void UpdateVisuals() {
            if (nview == null || !nview.IsValid()) { return; }
            // Rides this timer because it has to be repeated, not because it is a visual.
            MultiUserChestIntegration.KeepExclusive(nview);
            if (EnabledVisuals != null && EnabledVisuals.activeSelf != IsActive) { EnabledVisuals.SetActive(IsActive); }
        }
    }
}
