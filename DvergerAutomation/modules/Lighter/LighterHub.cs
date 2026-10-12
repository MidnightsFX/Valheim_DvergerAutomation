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

        /// <summary>Shown while the Lighter has fuel for something in range: smoke from its chimney.</summary>
        public GameObject ActiveVisuals;

        // ---- state ----------------------------------------------------------

        internal const string NameToken = "$DA_lighter_name";

        internal static readonly List<LighterHub> All = new List<LighterHub>();

        private ZNetView nview;
        private float nextTick;

        // Reused by every pass, so a tick does not allocate.
        private readonly List<Component> inRange = new List<Component>();
        private readonly List<Component> toFill = new List<Component>();
        private readonly Dictionary<string, int> stock = new Dictionary<string, int>();

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

        private static readonly HashSet<string> ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string ignoredParsedFrom;

        /// <summary>Whether the server has told Lighters to leave this piece alone.</summary>
        internal static bool IsIgnored(string prefabName) {
            string list = ValConfig.LighterIgnoredPieces.Value ?? "";
            if (list != ignoredParsedFrom) {
                ignored.Clear();
                foreach (string part in list.Split(',')) {
                    string name = part.Trim();
                    if (name.Length > 0) { ignored.Add(name); }
                }
                ignoredParsedFrom = list;
            }
            return ignored.Count > 0 && ignored.Contains(prefabName);
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
            if (CraftFromStoragePatches.IsBusy(Store)) { return; }
            SyncStore();
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

            int pieces = 0;
            int units = 0;
            foreach (Component burner in toFill) {
                string fuel = FuelBurners.FuelName(burner);
                int add = Mathf.Min(FuelBurners.Room(burner), Stock(inv, fuel));
                if (add <= 0) { continue; }
                // Out of the tank first, then into the piece. This client owns both, so nothing in between
                // can see the fuel in both places or in neither. Not world-level based, to match the count:
                // otherwise fuel from before a world level change is counted but never removed, and the
                // piece is fed for free.
                inv.RemoveItem(fuel, add, -1, worldLevelBased: false);
                stock[fuel] -= add;
                FuelBurners.Add(burner, add);
                ++pieces;
                units += add;
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
        /// Brings the tank's grid up to date with its ZDO before anything writes to it, as the Hopper's
        /// <see cref="HopperHub.SyncStore"/> does, for the same reason: for up to a second after this client
        /// becomes the owner its grid can be older than the one the previous owner saved.
        /// </summary>
        private void SyncStore() {
            if (Store != null && !Store.IsInUse()) { Store.Load(); }
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

        private void Survey() {
            if (Time.time - surveyedAt < SurveySeconds) { return; }
            surveyedAt = Time.time;
            surveyTended = 0;
            surveyHasFuel = false;
            surveyMissing.Clear();
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
        }

        private void UpdateVisuals() {
            if (!IsLive) { return; }
            Survey();
            if (ActiveVisuals != null && ActiveVisuals.activeSelf != surveyHasFuel) { ActiveVisuals.SetActive(surveyHasFuel); }
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
                if (surveyMissing.Count > 0) {
                    List<string> names = new List<string>();
                    foreach (string fuel in surveyMissing) { names.Add(Localization.instance.Localize(fuel)); }
                    text += "\n" + Localization.instance.Localize("$DA_lighter_needs", string.Join(", ", names));
                }
            }
            if (!PrivateArea.CheckAccess(transform.position, 0f, flash: false)) {
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
