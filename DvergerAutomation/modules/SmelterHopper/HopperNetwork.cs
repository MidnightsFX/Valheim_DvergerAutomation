using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Registry of live hoppers, plus the core-driven speed-up applied to the smelters they link, and the
    /// request that brings a linked station over to the client running its hopper.
    ///
    /// The speed-up is a straight write to <c>Smelter.m_secPerProduct</c>. That field is a plain
    /// instance field on the MonoBehaviour, copied fresh from the prefab on every instantiation, so
    /// the change never persists into a save - but it does have to be handed back when a hopper stops
    /// covering a smelter, or that smelter stays fast for the rest of the session.
    /// </summary>
    internal static class HopperNetwork {
        internal static readonly List<HopperHub> Hubs = new List<HopperHub>();

        // The untouched m_secPerProduct for every smelter we have ever boosted.
        private static readonly Dictionary<Smelter, float> BaseSpeed = new Dictionary<Smelter, float>();
        // The multiplier currently in force per smelter, so an unchanged one is not rewritten.
        private static readonly Dictionary<Smelter, float> Applied = new Dictionary<Smelter, float>();

        private static readonly List<Smelter> Scratch = new List<Smelter>();

        private const string StationRequestRpc = "DA_HopperStationRequest";

        // Minimum gap between two requests for the same station. A handoff takes a round trip, and a
        // station whose owner keeps it for its own hopper is asked again at this pace, not every pass.
        private const float StationRequestInterval = 2f;

        private static readonly Dictionary<ZDOID, float> lastStationRequest = new Dictionary<ZDOID, float>();

        // ZRoutedRpc is rebuilt with every session, and registering twice on the same one throws.
        private static ZRoutedRpc registeredOn;

        /// <summary>
        /// Moves on every time a station on the Smelter component is loaded or built, anywhere. A hopper
        /// compares it against the value at its last scan to tell whether a station may have appeared in
        /// its range since (see <see cref="HopperHub"/>'s ScanDue).
        /// </summary>
        internal static int StationsLoaded { get; private set; }

        internal static void NoteStationLoaded() { ++StationsLoaded; }

        internal static void RegisterRpc() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == registeredOn) { return; }
            rpc.Register<ZDOID>(StationRequestRpc, RPC_StationRequest);
            registeredOn = rpc;
            lastStationRequest.Clear();
        }

        internal static void Register(HopperHub hub) {
            if (hub != null && !Hubs.Contains(hub)) { Hubs.Add(hub); }
            RebuildSpeedMap();
        }

        internal static void Unregister(HopperHub hub) {
            Hubs.Remove(hub);
            RebuildSpeedMap();
        }

        /// <summary>
        /// Recomputes which smelters should be running fast and by how much, then applies the difference.
        ///
        /// Two hoppers covering the same smelter do not stack - the best multiplier wins - otherwise
        /// ringing a smelter with hoppers would multiply without limit.
        /// </summary>
        internal static void RebuildSpeedMap() {
            Dictionary<Smelter, float> want = new Dictionary<Smelter, float>();

            foreach (HopperHub hub in Hubs) {
                if (hub == null || !hub.IsActive) { continue; }
                float multiplier = hub.SpeedMultiplier;
                if (multiplier <= 1f) { continue; }
                foreach (Smelter smelter in hub.LinkedSmelters) {
                    if (smelter == null) { continue; }
                    if (!want.TryGetValue(smelter, out float best) || multiplier > best) {
                        want[smelter] = multiplier;
                    }
                }
            }

            // Hand back anything no longer covered - and anything that has since been destroyed, whose
            // entry would otherwise sit in these dictionaries for the rest of the session.
            Scratch.Clear();
            foreach (KeyValuePair<Smelter, float> entry in Applied) {
                if (entry.Key == null || !want.ContainsKey(entry.Key)) { Scratch.Add(entry.Key); }
            }
            foreach (Smelter smelter in Scratch) {
                if (smelter != null && BaseSpeed.TryGetValue(smelter, out float original)) {
                    smelter.m_secPerProduct = original;
                    if (ValConfig.EnableDebugMode.Value) {
                        Logger.LogInfo($"[Hopper] {StationName(smelter)} back to base speed: {original:0.#}s per item.");
                    }
                }
                Applied.Remove(smelter);
                BaseSpeed.Remove(smelter);
            }

            foreach (KeyValuePair<Smelter, float> entry in want) {
                Smelter smelter = entry.Key;
                if (smelter == null) { continue; }
                if (!BaseSpeed.TryGetValue(smelter, out float original)) {
                    original = smelter.m_secPerProduct;
                    BaseSpeed[smelter] = original;
                }
                if (original <= 0f) { continue; }
                if (Applied.TryGetValue(smelter, out float current) && Mathf.Approximately(current, entry.Value)) {
                    continue;
                }
                smelter.m_secPerProduct = original / entry.Value;
                Applied[smelter] = entry.Value;
                // Only logged when the multiplier changes (unchanged ones are skipped above), so this does
                // not repeat every scan tick.
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Hopper] {StationName(smelter)} running at {entry.Value:0.##}x: {original:0.#}s -> {smelter.m_secPerProduct:0.#}s per item.");
                }
            }
        }

        // Charcoal kiln, smelter and blast furnace all share the Smelter component, so name the station.
        private static string StationName(Smelter smelter) {
            string display = Localization.instance != null ? Localization.instance.Localize(smelter.m_name) : smelter.m_name;
            return $"{display} ({smelter.gameObject.name.Replace("(Clone)", "")})";
        }

        /// <summary>
        /// Diverts a smelter's finished product into a linked hopper's inventory instead of dropping it
        /// on the ground. Returns true when the product was taken, in which case the caller must not run
        /// vanilla's spawn.
        ///
        /// Deliberately all-or-nothing: a prefix cannot spawn "the remainder", so a stack that does not
        /// fit is left entirely to vanilla and lands on the ground, which is also the clearest possible
        /// signal that the hopper is full.
        ///
        /// Only a hopper this client owns will take it. Spawn runs on the smelter's owner, and when that
        /// is someone else, collecting would mean claiming the hopper from its owner mid-service. ZDO
        /// data syncs as one revision per object, so under lag the two saves race and one rolls the other
        /// back: the bars vanish, or ore that was already fed reappears. The product drops at the smelter
        /// instead, and the hopper's owner asks for the station on its next pass (<see cref="AskForStation"/>).
        /// </summary>
        internal static bool TryCollect(Smelter smelter, string ore, int stack) {
            Inventory inv = StoreWithRoom(smelter, ore, stack, out GameObject product);
            if (inv == null || !inv.AddItem(product, stack)) { return false; }

            // Vanilla plays this inside Spawn; skipping the original would otherwise swallow it.
            smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Hopper] collected {stack}x {product.name} from {smelter.m_name}.");
            }
            return true;
        }

        /// <summary>
        /// Stations that stack their output - the smelter, kiln and blast furnace among them - do not spawn
        /// an item when it finishes. They add it to a batch kept on their ZDO and spawn the batch only once
        /// it makes a full stack, the ore changes, or the queue or fuel runs out, so a hopper feeding one
        /// steadily only ever got the product when its own pass flushed the station. Run as each item joins
        /// the batch: when a hopper here can take the whole batch it is spawned on the spot, and the
        /// <c>Smelter.Spawn</c> prefix carries it into that hopper. Otherwise it is left to grow as vanilla
        /// would.
        /// </summary>
        internal static void CollectBatch(Smelter smelter) {
            if (smelter == null || smelter.m_nview == null || !smelter.m_nview.IsValid()) { return; }
            ZDO zdo = smelter.m_nview.GetZDO();
            int batch = zdo.GetInt(ZDOVars.s_spawnAmount);
            if (batch <= 0) { return; }
            if (StoreWithRoom(smelter, zdo.GetString(ZDOVars.s_spawnOre), batch, out _) == null) { return; }
            smelter.SpawnProcessed();
        }

        /// <summary>
        /// The inventory of a hopper this client runs that links the station and has room right now for
        /// <paramref name="stack"/> of what <paramref name="ore"/> becomes; null when there is none.
        /// </summary>
        private static Inventory StoreWithRoom(Smelter smelter, string ore, int stack, out GameObject product) {
            product = null;
            if (smelter == null || stack <= 0) { return null; }
            if (!ValConfig.HopperEnabled.Value || !ValConfig.HopperCollectOutput.Value) { return null; }

            Smelter.ItemConversion conversion = smelter.GetItemConversion(ore);
            if (conversion == null || conversion.m_to == null) { return null; }
            product = conversion.m_to.gameObject;

            foreach (HopperHub hub in Hubs) {
                if (hub == null || !hub.IsActive || !hub.IsOwner) { continue; }
                if (!hub.LinkedSmelters.Contains(smelter)) { continue; }

                Container store = hub.Store;
                if (store == null) { continue; }
                // Someone has the hatch open; leave their panel alone.
                if (CraftFromStoragePatches.IsBusy(store)) { continue; }
                hub.SyncStore();
                Inventory inv = store.GetInventory();
                if (inv != null && inv.CanAddItem(product, stack)) { return inv; }
            }
            return null;
        }

        /// <summary>Whether the container is a hopper's store.</summary>
        internal static bool IsStore(Container container) {
            if (container == null) { return false; }
            HopperHub hub = container.GetComponentInParent<HopperHub>();
            return hub != null && hub.Store == container;
        }

        // ---- bringing stations to the hopper ------------------------------------

        /// <summary>
        /// Asks the owner of a station this client's hopper links to hand it over, so the hopper can feed
        /// it. Feeding only ever touches stations this client owns (see <see cref="HopperHub"/>), and a
        /// hopper whose stations ran on several players' games used to feed only its own owner's share:
        /// the rest sat idle, and which ones ran shifted as players came and went. Bringing the stations
        /// to the hopper keeps every one of them fed by one client. A station nobody owns is simply
        /// taken - there is no one to race. Rate-limited per station, so it is cheap to call every pass.
        /// </summary>
        internal static void AskForStation(Smelter smelter) {
            if (smelter == null || smelter.m_nview == null || !smelter.m_nview.IsValid()) { return; }
            ZDO zdo = smelter.m_nview.GetZDO();
            long owner = zdo.GetOwner();
            if (owner == 0L) {
                smelter.m_nview.ClaimOwnership();
                return;
            }

            float now = Time.time;
            if (lastStationRequest.TryGetValue(zdo.m_uid, out float last) && now - last < StationRequestInterval) { return; }
            lastStationRequest[zdo.m_uid] = now;
            if (ZRoutedRpc.instance == null) { return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(owner, StationRequestRpc, zdo.m_uid);
        }

        /// <summary>
        /// Another player's hopper wants a station this client owns. Handed over the way vanilla hands a
        /// chest to the player opening it - <c>ForceSendZDO</c>, then <c>SetOwner</c>, from this side - so
        /// the station's latest queue and fuel travel with it and this client stops running it at that
        /// moment. Kept when a hopper this client runs feeds it as well: two hoppers covering one station
        /// would otherwise pass it back and forth. A refusal is silent; the requester asks again later.
        /// </summary>
        private static void RPC_StationRequest(long sender, ZDOID id) {
            try {
                if (ZDOMan.instance == null || ZNetScene.instance == null) { return; }
                if (sender == ZDOMan.GetSessionID()) { return; }
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsOwner()) { return; }
                GameObject root = ZNetScene.instance.FindInstance(id);
                Smelter smelter = root != null ? root.GetComponentInChildren<Smelter>() : null;
                if (smelter == null) { return; }
                if (FedHere(smelter)) {
                    if (ValConfig.EnableDebugMode.Value) {
                        Logger.LogInfo($"[Hopper] kept {StationName(smelter)}: a hopper here feeds it too.");
                    }
                    return;
                }

                ZDOMan.instance.ForceSendZDO(sender, id);
                zdo.SetOwner(sender);
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Hopper] handed {StationName(smelter)} to {sender}, whose hopper feeds it.");
                }
            } catch (System.Exception ex) {
                Logger.LogError($"[Hopper] handing over a station failed: {ex}");
            }
        }

        // A dedicated server never scans (see HopperHub's tick), so it always hands its stations on.
        private static bool FedHere(Smelter smelter) {
            foreach (HopperHub hub in Hubs) {
                if (hub == null || !hub.IsOwner || !hub.IsActive) { continue; }
                if (hub.LinkedSmelters.Contains(smelter)) { return true; }
            }
            return false;
        }
    }

    /// <summary>
    /// Finishes the hopper's Container on the freshly loaded bundle prefab. Must run before the piece is
    /// ever instantiated, because <c>Container.Awake</c> reads these fields to build its inventory and
    /// bind its network view.
    /// </summary>
    internal static class HopperStore {
        // Child path of the store inside the DA_ForgeHopper prefab.
        private const string StorePath = "Hopper_Body/HopperStore";

        internal const int MinRows = 2;
        internal const int MaxRows = 8;
        internal const int DefaultRows = 4;

        // Fixed at the player's own inventory width. Deliberately NOT configurable: Inventory.Load
        // re-adds every item at its saved grid position and the positional AddItem rejects x >= m_width
        // outright, silently dropping anything in a column a narrowed grid no longer has. Rows are safe -
        // Container.UpdateRows grows the grid to fit - so only the height is exposed.
        internal const int Width = 8;

        private static Container prefabStore;

        internal static void ConfigurePrefab(GameObject prefab) {
            if (prefab == null) {
                Logger.LogWarning("Hopper: DA_ForgeHopper prefab missing; the store will not work.");
                return;
            }

            Transform store = prefab.transform.Find(StorePath);
            Container box = store != null ? store.GetComponent<Container>() : null;
            if (box == null) {
                // Fall back to any Container under the piece, so a rename in Unity is survivable.
                box = prefab.GetComponentInChildren<Container>(includeInactive: true);
            }
            if (box == null) {
                Logger.LogWarning($"Hopper: no Container at '{StorePath}' on {prefab.name}; the store will not work.");
                return;
            }

            // There is no ZNetView on the child, so the Container has to be pointed at the piece's own.
            // Without this Container.Awake NREs on the very next line (m_nview.GetZDO()).
            box.m_rootObjectOverride = prefab.GetComponent<ZNetView>();
            box.m_name = "$DA_hopper_store_name";
            box.m_checkGuardStone = true;

            // Each of these would break the piece in a way that is not obvious from the Unity inspector:
            //  - Private calls m_piece.GetCreator(), and m_piece is null on a child object.
            //  - autoDestroyEmpty would nview.Destroy() the SHARED root view, deleting the whole piece
            //    the moment the store ran empty.
            //  - any discover stat other than None invokes an RPC vanilla registers under a different
            //    name ("RPC_Discovered"), logging an unknown-RPC error on every open.
            box.m_privacy = Container.PrivacySetting.Public;
            box.m_autoDestroyEmpty = false;
            box.m_discoverStat = PlayerStatType.None;

            prefabStore = box;
            ApplySize(box);
            ValConfig.HopperStoreRows.SettingChanged += OnRowsChanged;
        }

        internal static void ApplySize(Container box) {
            if (box == null) { return; }
            box.m_width = Width;
            box.m_height = Mathf.Clamp(ValConfig.HopperStoreRows.Value, MinRows, MaxRows);

            Inventory inv = box.GetInventory();
            if (inv == null) { return; }   // prefab, or an instance whose Awake has not run yet
            inv.m_width = Width;
            inv.SetHeight(box.m_height);
        }

        private static void OnRowsChanged(object sender, System.EventArgs e) {
            ApplySize(prefabStore);
            foreach (HopperHub hub in HopperNetwork.Hubs) {
                if (hub == null || hub.Store == null || hub.Store.IsInUse()) { continue; }
                ApplySize(hub.Store);
            }
        }
    }
}
