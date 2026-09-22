using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Registry of live hoppers, plus the core-driven speed-up applied to the smelters they link.
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

        // Charcoal kiln, smelter, blast furnace... all share the Smelter component, so name the station.
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
        /// </summary>
        internal static bool TryCollect(Smelter smelter, string ore, int stack) {
            if (smelter == null || stack <= 0) { return false; }
            if (!ValConfig.HopperEnabled.Value || !ValConfig.HopperCollectOutput.Value) { return false; }

            Smelter.ItemConversion conversion = smelter.GetItemConversion(ore);
            if (conversion == null || conversion.m_to == null) { return false; }
            GameObject prefab = conversion.m_to.gameObject;

            foreach (HopperHub hub in Hubs) {
                if (hub == null || !hub.IsActive) { continue; }
                if (!hub.LinkedSmelters.Contains(smelter)) { continue; }

                Container store = hub.Store;
                if (store == null) { continue; }
                Inventory inv = store.GetInventory();
                if (inv == null) { continue; }
                // Someone has the hatch open; leave their panel alone.
                if (CraftFromStoragePatches.IsBusy(store)) { continue; }
                if (!inv.CanAddItem(prefab, stack)) { continue; }

                // Smelter.Spawn runs on the *smelter's* owner, which need not own this hopper, and
                // Container.Save is a no-op for non-owners - the write would silently revert.
                CraftFromStoragePatches.ClaimOwnership(store);
                if (!inv.AddItem(prefab, stack)) { continue; }

                // Vanilla plays this inside Spawn; skipping the original would otherwise swallow it.
                smelter.m_produceEffects.Create(smelter.transform.position, smelter.transform.rotation);

                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Hopper] collected {stack}x {prefab.name} from {smelter.m_name}.");
                }
                return true;
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
