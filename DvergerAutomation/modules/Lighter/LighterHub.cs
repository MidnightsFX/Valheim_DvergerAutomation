using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_Lighter piece, added to the prefab in Unity. Its fuel tank is a chest
    /// behind the hatch on its stand; on a timer it tops up every fire, torch, oven and hot tub within
    /// range (<see cref="FuelBurners"/>) from what is in there, emptiest first.
    ///
    /// Only the Lighter's owner moves fuel, and only into pieces it also owns, as the Hopper does with its
    /// smelters: the fuel RPCs then run on this client in the same frame the fuel leaves the tank, and the
    /// fuel level read before adding is the real one rather than a lagging copy. The oven and the hot tub
    /// do not check their cap when fuel arrives (only the player adding it does), so filling one from
    /// another client's copy could overfill it. A piece owned by someone else is asked for instead
    /// (<see cref="LighterNetwork.AskFor"/>) and filled once it has arrived.
    ///
    /// The tick is not gated on a local player: a dedicated server owns every persistent object around
    /// the world's centre, so a Lighter built near the spawn is run by the server.
    /// </summary>
    public class LighterHub : MonoBehaviour, Hoverable, Interactable {
        // ---- Unity-wired references ----------------------------------------

        /// <summary>
        /// Shown while the Lighter is switched on and has fuel for something in range: wisps of smoke from
        /// all three of its upward pipes.
        /// </summary>
        public GameObject ActiveVisuals;

        /// <summary>
        /// Under <see cref="ActiveVisuals"/>: the centre column's <see cref="SmokeSpawner"/>, which puffs
        /// out the same smoke a fire does. Shown only while the server allows it (Physical Smoke).
        /// </summary>
        public GameObject PhysicalSmoke;

        // ---- state ----------------------------------------------------------

        internal const string NameToken = "$DA_lighter_name";

        /// <summary>Rows in the fuel tank, as the server's Storage Rows setting allows them.</summary>
        internal const int MinStoreRows = 1;
        internal const int MaxStoreRows = 8;
        internal const int DefaultStoreRows = 2;

        internal static readonly List<LighterHub> All = new List<LighterHub>();

        private ZNetView nview;
        private float nextTick;

        // Reused by every pass, so a tick does not allocate.
        private readonly List<Component> inRange = new List<Component>();
        private readonly List<Component> toFill = new List<Component>();
        private readonly List<int> amounts = new List<int>();
        private readonly Dictionary<string, int> stock = new Dictionary<string, int>();
        private readonly Dictionary<string, int> taken = new Dictionary<string, int>();

        /// <summary>The fuel tank. Resolved from the hierarchy, so the prefab needs no extra wiring.</summary>
        internal Container Store { get; private set; }

        internal bool IsLive => nview != null && nview.IsValid();

        internal bool IsOwner => IsLive && nview.IsOwner();

        internal static float Radius => ValConfig.LighterRadius.Value;

        private void Awake() {
            nview = GetComponent<ZNetView>();
            Store = GetComponentInChildren<Container>(includeInactive: true);
            if (nview != null && nview.GetZDO() != null) {
                InvokeRepeating(nameof(Tick), 2f, 1f);
                InvokeRepeating(nameof(UpdateVisuals), 1f, 2f);
            }
        }

        private void OnEnable() {
            if (!All.Contains(this)) { All.Add(this); }
        }

        private void OnDisable() {
            All.Remove(this);
        }

        // ---- the server's ignore list -------------------------------------------

        private static readonly NameSet Ignored = new NameSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether the server has told Lighters to leave this piece alone.</summary>
        internal static bool IsIgnored(string prefabName) {
            return Ignored.Contains(ValConfig.LighterIgnoredPieces.Value, prefabName);
        }

        // ---- the owner ----------------------------------------------------------

        private void Tick() {
            if (!IsOwner || ObjectDB.instance == null) { return; }
            // Rides this timer because it has to be repeated: the tank is one player at a time.
            MultiUserChestIntegration.KeepExclusive(nview);
            if (Time.time < nextTick) { return; }
            nextTick = Time.time + Mathf.Max(1f, ValConfig.LighterInterval.Value);
            try {
                Refuel();
            } catch (Exception ex) {
                Logger.LogError($"[Lighter] refuelling failed: {ex}");
            }
        }

        /// <summary>
        /// One pass: every piece in range that has room for a whole unit of a fuel the tank holds gets
        /// as much as fits, the emptiest first, so a short tank keeps the most fires burning. Pieces this
        /// client does not own are asked for, and filled on a later pass.
        /// </summary>
        private void Refuel() {
            if (!ValConfig.LighterEnabled.Value || Store == null) { return; }
            // Someone has the tank open. Writing to it under them would blank their panel, so the Lighter
            // waits until they close it.
            if (StorageOwnership.IsBusy(Store)) { return; }
            // For up to a second after this client becomes the owner its grid can be older than the one
            // the previous owner saved.
            StorageOwnership.Sync(Store);
            Inventory inv = Store.GetInventory();
            if (inv == null || inv.NrOfItems() == 0) { return; }

            FuelBurners.InRange(transform.position, Radius, inRange);
            stock.Clear();
            toFill.Clear();
            foreach (Component burner in inRange) {
                if (FuelBurners.Room(burner) <= 0) { continue; }
                string fuel = FuelBurners.FuelName(burner);
                if (fuel == null || Stock(inv, fuel) <= 0) { continue; }
                ZNetView view = FuelBurners.ViewOf(burner);
                if (view.IsOwner()) {
                    toFill.Add(burner);
                } else {
                    LighterNetwork.AskFor(view);
                }
            }
            if (toFill.Count == 0) { return; }
            toFill.Sort((a, b) => FuelBurners.Fill(a).CompareTo(FuelBurners.Fill(b)));

            // Shared out first, emptiest first, against what the tank holds.
            amounts.Clear();
            taken.Clear();
            foreach (Component burner in toFill) {
                string fuel = FuelBurners.FuelName(burner);
                int add = Mathf.Min(FuelBurners.Room(burner), Stock(inv, fuel));
                amounts.Add(add);
                if (add <= 0) { continue; }
                stock[fuel] -= add;
                taken.TryGetValue(fuel, out int sofar);
                taken[fuel] = sofar + add;
            }
            // Then out of the tank, one removal per fuel - each one saves the whole tank - and into the
            // pieces. This client owns all of them and nothing runs in between, so nothing can see the
            // fuel in both places or in neither. Not world-level based, to match the count: otherwise fuel
            // from before a world level change is counted but never removed, and the piece is fed for free.
            foreach (KeyValuePair<string, int> fuel in taken) {
                inv.RemoveItem(fuel.Key, fuel.Value, -1, worldLevelBased: false);
            }
            int pieces = 0;
            int units = 0;
            for (int i = 0; i < toFill.Count; ++i) {
                if (amounts[i] <= 0) { continue; }
                FuelBurners.Add(toFill[i], amounts[i]);
                ++pieces;
                units += amounts[i];
            }

            if (pieces > 0 && ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Lighter] added {units} fuel to {pieces} pieces within {Radius}m.");
            }
        }

        // What the tank holds of one fuel, counted once per pass and then kept in step with what is taken.
        private int Stock(Inventory inv, string fuel) {
            if (!stock.TryGetValue(fuel, out int have)) {
                have = inv.CountItems(fuel, -1, matchWorldLevel: false);
                stock[fuel] = have;
            }
            return have;
        }

        /// <summary>
        /// Whether a Lighter this client runs would fill <paramref name="burner"/> now: one that covers it
        /// and holds what it burns. The owner of a piece another player's Lighter asks for keeps it then,
        /// or two Lighters covering one fire would pass it back and forth.
        /// </summary>
        internal static bool FedHere(Component burner) {
            if (!ValConfig.LighterEnabled.Value) { return false; }
            string fuel = FuelBurners.FuelName(burner);
            if (fuel == null) { return false; }
            float sqr = Radius * Radius;
            foreach (LighterHub hub in All) {
                if (hub == null || !hub.IsOwner || hub.Store == null) { continue; }
                if ((burner.transform.position - hub.transform.position).sqrMagnitude > sqr) { continue; }
                Inventory inv = hub.Store.GetInventory();
                if (inv != null && inv.CountItems(fuel, -1, matchWorldLevel: false) > 0) { return true; }
            }
            return false;
        }

        // ---- what every client shows ---------------------------------------------

        // Read from this client's copy of the tank and the pieces around it, so every player sees the same
        // thing. Hover text is asked for every frame, so the survey is kept for a moment.
        private const float SurveySeconds = 1f;
        private float surveyedAt = -10f;
        private int surveyTended;
        private bool surveyHasFuel;
        private readonly List<string> surveyMissing = new List<string>();
        // The hover's line naming what is missing, built with the survey rather than every frame.
        private string surveyNeeds = "";

        private void Survey() {
            if (Time.time - surveyedAt < SurveySeconds) { return; }
            surveyedAt = Time.time;
            surveyTended = 0;
            surveyHasFuel = false;
            surveyMissing.Clear();
            surveyNeeds = "";
            if (!IsLive || !ValConfig.LighterEnabled.Value) { return; }

            Inventory inv = Store != null ? Store.GetInventory() : null;
            FuelBurners.InRange(transform.position, Radius, inRange);
            surveyTended = inRange.Count;
            foreach (Component burner in inRange) {
                string fuel = FuelBurners.FuelName(burner);
                if (fuel == null) { continue; }
                bool have = inv != null && inv.CountItems(fuel, -1, matchWorldLevel: false) > 0;
                if (have) {
                    surveyHasFuel = true;
                } else if (FuelBurners.Room(burner) > 0 && !surveyMissing.Contains(fuel)) {
                    surveyMissing.Add(fuel);
                }
            }
            if (surveyMissing.Count > 0) {
                List<string> names = new List<string>();
                foreach (string fuel in surveyMissing) { names.Add(Localization.instance.Localize(fuel)); }
                surveyNeeds = "\n" + Localization.instance.Localize("$DA_lighter_needs", string.Join(", ", names));
            }
        }

        // Read again every pass rather than on a setting change, so a value synced from the server is picked
        // up within a couple of seconds wherever it arrives from.
        private void UpdateVisuals() {
            // A dedicated server draws nothing.
            if (!IsLive || ZNet.instance == null || ZNet.instance.IsDedicated()) { return; }
            Survey();
            SetActiveSafe(ActiveVisuals, surveyHasFuel);
            SetActiveSafe(PhysicalSmoke, ValConfig.LighterPhysicalSmoke.Value);
        }

        private static void SetActiveSafe(GameObject go, bool active) {
            if (go != null && go.activeSelf != active) { go.SetActive(active); }
        }

        // ---- Hoverable / Interactable: the body opens the tank ----------------------------

        public string GetHoverText() {
            if (!IsLive) { return ""; }
            Survey();
            string text = NameToken;
            if (!ValConfig.LighterEnabled.Value) {
                text += "\n$DA_lighter_disabled";
            } else {
                string range = Mathf.RoundToInt(Radius).ToString();
                text += "\n" + (surveyTended > 0
                    ? Localization.instance.Localize("$DA_lighter_status", surveyTended.ToString(), range)
                    : Localization.instance.Localize("$DA_lighter_nothing", range));
                text += surveyNeeds;
            }
            if (!StorageScan.Permits(transform.position, StorageScan.LocalPlayerId())) {
                return Localization.instance.Localize(text + "\n$piece_noaccess");
            }
            return Localization.instance.Localize(text + "\n[<color=yellow><b>$KEY_Use</b></color>] $DA_lighter_open");
        }

        public string GetHoverName() {
            return Localization.instance.Localize(NameToken);
        }

        public float GetHoverOffset() {
            return 0f;
        }

        // The hatch is small; using any part of the machine opens the tank, with the chest's own checks.
        public bool Interact(Humanoid user, bool hold, bool alt) {
            if (hold || Store == null) { return false; }
            return Store.Interact(user, hold, alt);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
            return false;
        }
    }
}
