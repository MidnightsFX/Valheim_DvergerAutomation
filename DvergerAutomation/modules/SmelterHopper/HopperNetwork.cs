using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Registry of live hoppers and of every loaded station on the Smelter component, plus the core-driven
    /// speed-up applied to the smelters the hoppers link, and the request that brings a linked station over
    /// to the client running its hopper.
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
        // What the speed map is rebuilt into; kept so a rebuild does not allocate.
        private static readonly Dictionary<Smelter, float> Wanted = new Dictionary<Smelter, float>();

        private static readonly List<Smelter> Scratch = new List<Smelter>();

        // Every station on the Smelter component that has loaded or been built, found by walking this
        // rather than overlapping every collider within a hopper's reach.
        private static readonly List<Smelter> AllStations = new List<Smelter>();

        // What each station melts: the shared name its items carry, to the prefab name it is queued under.
        private static readonly Dictionary<Smelter, Dictionary<string, string>> FeedableByStation = new Dictionary<Smelter, Dictionary<string, string>>();

        // A handoff takes a round trip, and a station whose owner keeps it for its own hopper is asked again
        // less and less often, up to this.
        private const float StationRequestMaxInterval = 30f;

        private static readonly OwnerHandover StationHandover = new OwnerHandover("DA_HopperStationRequest", "Hopper", KeepStation, StationRequestMaxInterval);

        /// <summary>
        /// Moves on every time a station on the Smelter component is loaded or built, anywhere. A hopper
        /// compares it against the value at its last scan to tell whether a station may have appeared in
        /// its range since (see <see cref="HopperHub"/>'s ScanDue).
        /// </summary>
        internal static int StationsLoaded { get; private set; }

        internal static void RegisterRpc(ZRoutedRpc rpc) {
            StationHandover.Register(rpc);
        }

        /// <summary>A station has woken. A placement ghost has no ZDO and is left out.</summary>
        internal static void StationLoaded(Smelter smelter) {
            if (smelter.m_nview == null || smelter.m_nview.GetZDO() == null) { return; }
            AllStations.Add(smelter);
            ++StationsLoaded;
        }

        /// <summary>Every loaded station, the unloaded ones swept out on the way.</summary>
        internal static List<Smelter> Stations() {
            AllStations.RemoveAll(smelter => smelter == null);
            return AllStations;
        }

        /// <summary>
        /// What a station melts, by the shared name an item carries, mapped to the prefab name it is queued
        /// under: the station's conversions, read once rather than for every item on every pass.
        /// </summary>
        internal static Dictionary<string, string> Feedable(Smelter smelter) {
            if (FeedableByStation.TryGetValue(smelter, out Dictionary<string, string> feedable)) { return feedable; }
            if (FeedableByStation.Count > AllStations.Count + 64) {
                Scratch.Clear();
                foreach (Smelter known in FeedableByStation.Keys) {
                    if (known == null) { Scratch.Add(known); }
                }
                foreach (Smelter gone in Scratch) { FeedableByStation.Remove(gone); }
                Scratch.Clear();
            }
            feedable = new Dictionary<string, string>();
            foreach (Smelter.ItemConversion conversion in smelter.m_conversion) {
                if (conversion.m_from == null || conversion.m_from.m_itemData == null) { continue; }
                feedable[conversion.m_from.m_itemData.m_shared.m_name] = conversion.m_from.gameObject.name;
            }
            FeedableByStation[smelter] = feedable;
            return feedable;
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
            Wanted.Clear();
            foreach (HopperHub hub in Hubs) {
                if (hub == null || !hub.IsActive) { continue; }
                float multiplier = hub.SpeedMultiplier;
                if (multiplier <= 1f) { continue; }
                foreach (Smelter smelter in hub.LinkedSmelters) {
                    if (smelter == null) { continue; }
                    if (!Wanted.TryGetValue(smelter, out float best) || multiplier > best) {
                        Wanted[smelter] = multiplier;
                    }
                }
            }

            // Hand back anything no longer covered - and anything that has since been destroyed, whose
            // entry would otherwise sit in these dictionaries for the rest of the session.
            Scratch.Clear();
            foreach (KeyValuePair<Smelter, float> entry in Applied) {
                if (entry.Key == null || !Wanted.ContainsKey(entry.Key)) { Scratch.Add(entry.Key); }
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
            Scratch.Clear();

            foreach (KeyValuePair<Smelter, float> entry in Wanted) {
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
            Wanted.Clear();
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
                if (StorageOwnership.IsBusy(store)) { continue; }
                StorageOwnership.Sync(store);
                Inventory inv = store.GetInventory();
                if (inv != null && inv.CanAddItem(product, stack)) { return inv; }
            }
            return null;
        }

        // ---- bringing stations to the hopper ------------------------------------

        /// <summary>
        /// Asks the owner of a station this client's hopper links to hand it over, so the hopper can feed
        /// it. Feeding only ever touches stations this client owns (see <see cref="HopperHub"/>), and a
        /// hopper whose stations ran on several players' games used to feed only its own owner's share:
        /// the rest sat idle, and which ones ran shifted as players came and went. Bringing the stations
        /// to the hopper keeps every one of them fed by one client. Rate-limited per station, so it is
        /// cheap to call every pass.
        /// </summary>
        internal static void AskForStation(Smelter smelter) {
            if (smelter == null) { return; }
            StationHandover.Ask(smelter.m_nview);
        }

        // The owner keeps a station when a hopper it runs feeds it as well: two hoppers covering one
        // station would otherwise pass it back and forth.
        private static bool KeepStation(GameObject root) {
            Smelter smelter = root.GetComponentInChildren<Smelter>();
            return smelter == null || FedHere(smelter);
        }

        /// <summary>Whether a hopper this client runs feeds the station.</summary>
        internal static bool FedHere(Smelter smelter) {
            foreach (HopperHub hub in Hubs) {
                if (hub == null || !hub.IsOwner || !hub.IsActive) { continue; }
                if (hub.LinkedSmelters.Contains(smelter)) { return true; }
            }
            return false;
        }
    }
}
