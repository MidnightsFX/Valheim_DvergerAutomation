using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_Grinder piece, added to the prefab in Unity next to its
    /// <see cref="CoreSockets"/>. Items fed into the funnel wait in a queue on the piece's ZDO and are
    /// ground one craft's worth at a time into part of what they cost to make (<see cref="GrinderRecipes"/>),
    /// which collects in the results chest. Molten Cores in its power panel raise both the speed and the share returned.
    ///
    /// Feeding goes through the game's own item picker: the funnel answers vanilla's hover-menu interface,
    /// so the radial lists what the player carries that can be ground, and it stays open while there is
    /// room. Use opens that same picker; a hotbar key feeds the item directly, but only plain materials,
    /// since that key also draws weapons and eats food.
    ///
    /// Only the owner touches the queue or the chest (<see cref="GrinderOps"/>), on a one-second tick. The
    /// grind's progress is stored as a value at a time and a speed, so it carries on - and catches up after
    /// the area was unloaded, as a smelter does - without the ZDO being rewritten every second. It is
    /// written only when something happens: an item queued, a batch finished, a pause or a resume.
    /// </summary>
    public class GrinderHub : MonoBehaviour, IHasHoverMenuExtended {
        // ---- Unity-wired references ----------------------------------------

        /// <summary>The funnel's switch: feed with Use or a hotbar item, take back with Alt + Use.</summary>
        public Switch Funnel;

        /// <summary>Spun while grinding: the crusher rollers and their drive gears.</summary>
        public Transform[] Spinners = new Transform[0];
        /// <summary>Each spinner's axis, in its own local space.</summary>
        public Vector3[] SpinAxes = new Vector3[0];
        /// <summary>Each spinner's speed in degrees per second; the sign sets the direction.</summary>
        public float[] SpinSpeeds = new float[0];

        /// <summary>Shown while grinding: dust and the machine's sound.</summary>
        public GameObject WorkingVisuals;

        public EffectList FeedEffects = new EffectList();
        public EffectList ProduceEffects = new EffectList();

        // ---- state ----------------------------------------------------------

        internal enum State {
            Idle = 0,
            Working = 1,
            OutputFull = 2,
            /// <summary>Someone has the results chest open.</summary>
            Waiting = 3,
            Disabled = 4,
            /// <summary>Everything queued was made by a mod this game no longer has.</summary>
            Stuck = 5,
        }

        internal const string NameToken = "$DA_grinder_name";

        // Progress P (seconds of grinding done on the current batch) as of time T, accruing at speed S
        // while Working. Every other state holds P where it stopped.
        private const string ProgressKey = "DA_grinder_p";
        private const string TimeKey = "DA_grinder_t";
        private const string SpeedKey = "DA_grinder_s";
        private const string StateKey = "DA_grinder_state";

        // As vanilla's smelter: time away beyond an hour is not made up.
        private const double MaxCatchUpSeconds = 3600.0;

        private const string FxRpc = "DA_GrinderFx";
        private const int FxFeed = 0;
        private const int FxProduce = 1;

        internal static readonly List<GrinderHub> All = new List<GrinderHub>();

        /// <summary>
        /// True only while a pick made in the radial is being used on the hovered object. Set around
        /// <c>Humanoid.TryUseItemOnInteractable</c>, which only the radial calls (GrinderPatches).
        /// </summary>
        internal static bool FromRadial;

        private ZNetView nview;
        private CoreSockets sockets;

        // The owner's parsed queue, kept for as long as the ZDO holds the same array, so the item being
        // ground is decoded once rather than every tick.
        private byte[] queueRaw;
        private List<GrinderQueue.Entry> queue;

        /// <summary>The results chest. Resolved from the hierarchy, so the prefab needs no extra wiring.</summary>
        internal Container Store { get; private set; }

        internal bool IsLive => nview != null && nview.IsValid();

        internal ZDOID ZdoId => IsLive ? nview.GetZDO().m_uid : ZDOID.None;

        /// <summary>The Grinder whose results chest this is, if it is one.</summary>
        internal static GrinderHub For(Container container) {
            if (container == null) { return null; }
            GrinderHub hub = container.GetComponentInParent<GrinderHub>();
            return hub != null && hub.Store == container ? hub : null;
        }

        private void Awake() {
            nview = GetComponent<ZNetView>();
            Store = GetComponentInChildren<Container>(includeInactive: true);
            sockets = GetComponent<CoreSockets>();
            if (sockets != null) {
                sockets.SetHoverName(NameToken);
                sockets.StatusLine = CoreStatus;
                sockets.Changed += OnCoresChanged;
            }

            if (Funnel != null) {
                Funnel.m_onUse = OnFunnelUsed;
                Funnel.m_onHover = FunnelHover;
                Funnel.m_name = NameToken;
            }

            if (nview != null && nview.GetZDO() != null) {
                nview.Register<int>(FxRpc, RPC_Fx);
                WearNTear wnt = GetComponent<WearNTear>();
                if (wnt != null) { wnt.m_onDestroyed += OnDestroyedDropQueue; }
                InvokeRepeating(nameof(Tick), 2f, 1f);
                InvokeRepeating(nameof(UpdateVisuals), 1f, 1f);
            }
        }

        private void OnEnable() {
            if (!All.Contains(this)) { All.Add(this); }
        }

        private void OnDisable() {
            All.Remove(this);
        }

        private void OnDestroy() {
            if (sockets != null) { sockets.Changed -= OnCoresChanged; }
        }

        // ---- derived values -------------------------------------------------

        internal int CoreCount => sockets != null ? sockets.Count : 0;

        internal int SlotCount => sockets != null ? sockets.SlotCount : 0;

        /// <summary>The share of an item's cost returned, 0-1.</summary>
        internal float Yield =>
            Mathf.Clamp01((ValConfig.GrinderBaseYield.Value + CoreCount * ValConfig.GrinderYieldPerCore.Value) / 100f);

        internal float Speed => 1f + CoreCount * ValConfig.GrinderSpeedPerCore.Value;

        private static float SecondsPerItem => Mathf.Max(1f, ValConfig.GrinderSecondsPerItem.Value);

        /// <summary>
        /// Off for this server, or the world lets everyone craft for free - when grinding would turn
        /// nothing into materials.
        /// </summary>
        private static bool Disabled =>
            !ValConfig.GrinderEnabled.Value
            || (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost));

        private State CurrentState => IsLive ? (State)nview.GetZDO().GetInt(StateKey) : State.Idle;

        private int QueuedCount => IsLive ? nview.GetZDO().GetInt(GrinderQueue.CountKey) : 0;

        /// <summary>Seconds of grinding done on the current item, readable on every client.</summary>
        private float Progress() {
            if (!IsLive) { return 0f; }
            ZDO zdo = nview.GetZDO();
            float done = zdo.GetFloat(ProgressKey);
            if ((State)zdo.GetInt(StateKey) != State.Working || ZNet.instance == null) { return done; }
            long since = zdo.GetLong(TimeKey);
            double elapsed = (ZNet.instance.GetTime().Ticks - since) / (double)TimeSpan.TicksPerSecond;
            elapsed = Math.Max(0.0, Math.Min(MaxCatchUpSeconds, elapsed));
            return done + (float)(elapsed * zdo.GetFloat(SpeedKey, 1f));
        }

        /// <summary>Room left in the queue as this client sees it, counting its own feeds still in flight.</summary>
        private int QueueRoom() {
            return ValConfig.GrinderQueueSize.Value - QueuedCount - GrinderOps.UnansweredFor(ZdoId);
        }

        // ---- feeding: the funnel ----------------------------------------------

        private bool OnFunnelUsed(Switch caller, Humanoid user, ItemDrop.ItemData item) {
            if (!(user is Player player) || player != Player.m_localPlayer || !IsLive) { return false; }

            if (item == null) {
                // Flashes the ward, as any locked piece does.
                if (!PrivateArea.CheckAccess(transform.position)) { return false; }
                if (AltHeld()) {
                    GrinderOps.TakeBack(this);
                    return true;
                }
                return OpenFeedMenu(player);
            }

            // A hotbar key tries whatever the player is looking at first, so pressing 1 to draw a sword at
            // the funnel would grind it - and hotbar keys still work while the radial is open. So only a
            // pick made in the radial itself (FromRadial) takes gear and food; from the hotbar only plain
            // materials are taken, and anything that can be worn or eaten is left to vanilla.
            if (!FromRadial && (item.IsEquipable() || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable)) {
                return false;
            }
            if (!PrivateArea.CheckAccess(transform.position)) {
                player.UseIemBlockkMessage();
                return false;
            }
            if (!CanFeed(player, sendErrorMessage: true)) {
                player.UseIemBlockkMessage();
                return false;
            }
            GrinderRecipes.Refusal refusal = GrinderRecipes.Check(item);
            if (refusal != GrinderRecipes.Refusal.None) {
                player.Message(MessageHud.MessageType.Center, GrinderRecipes.RefusalMessage(refusal));
                player.UseIemBlockkMessage();
                return false;
            }
            return GrinderOps.Feed(this, player, item, item.m_stack);
        }

        // Vanilla's own test for the alternative-use modifier (Player.Update), which Switch.Interact drops.
        private static bool AltHeld() {
            return ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive()
                ? ZInput.GetButton("JoyAltKeys")
                : ZInput.GetButton("AltPlace") || ZInput.GetButton("JoyAltPlace");
        }

        /// <summary>
        /// Opens the radial the vanilla radial key would, which finds the hovered funnel and lists what can
        /// be fed. The radial refuses to open again after its first close until its own key re-arms it, so
        /// that is done here. Only when there is something to list: otherwise vanilla falls back to the
        /// main radial.
        /// </summary>
        private bool OpenFeedMenu(Player player) {
            Hud hud = Hud.instance;
            if (hud == null || hud.m_radialMenu == null || hud.m_config == null) { return false; }
            if (Hud.InRadial() || InventoryGui.IsVisible() || Hud.IsPieceSelectionVisible() || player.InPlaceMode()) { return false; }
            if (!CanFeed(player, sendErrorMessage: true)) { return false; }
            if (GrindableNames(player).Count == 0) {
                player.Message(MessageHud.MessageType.Center, "$DA_grinder_nothing_to_feed");
                return false;
            }
            hud.m_radialMenu.CanOpen = true;
            hud.m_radialMenu.Open(hud.m_config);
            return true;
        }

        /// <summary>Whether this Grinder can take anything at all right now.</summary>
        private bool CanFeed(Player player, bool sendErrorMessage) {
            string why = null;
            if (Disabled) {
                why = ValConfig.GrinderEnabled.Value ? "$DA_grinder_nocraftcost" : "$DA_grinder_disabled";
            } else if (QueueRoom() <= 0) {
                why = "$DA_grinder_queue_full";
            }
            if (why == null) { return true; }
            if (sendErrorMessage) { player.Message(MessageHud.MessageType.Center, why); }
            return false;
        }

        /// <summary>Shared names of what the player carries that this Grinder would take, each once.</summary>
        private static List<string> GrindableNames(Player player) {
            List<string> names = new List<string>();
            HashSet<string> seen = new HashSet<string>();
            foreach (ItemDrop.ItemData item in player.GetInventory().GetAllItems()) {
                if (item == null || item.m_shared == null || seen.Contains(item.m_shared.m_name)) { continue; }
                if (GrinderRecipes.Check(item) != GrinderRecipes.Refusal.None) { continue; }
                seen.Add(item.m_shared.m_name);
                names.Add(item.m_shared.m_name);
            }
            return names;
        }

        // ---- IHasHoverMenuExtended: the radial ----------------------------------------

        public bool TryGetItems(Player player, Switch switchRef, out List<string> items) {
            items = new List<string>();
            // The chest and the core sockets get the ordinary radial.
            if (switchRef == null || switchRef != Funnel) { return false; }
            // As vanilla's smelter: an empty list closes the radial rather than opening the main one.
            if (!CanUseItems(player, switchRef)) { return true; }
            items = GrindableNames(player);
            return true;
        }

        public bool CanUseItems(Player player, Switch switchRef, bool sendErrorMessage = true) {
            if (switchRef == null || switchRef != Funnel || player == null) { return false; }
            if (!PrivateArea.CheckAccess(transform.position, 0f, sendErrorMessage)) { return false; }
            return CanFeed(player, sendErrorMessage);
        }

        // ---- hover text ---------------------------------------------------------------

        private string FunnelHover() {
            if (!IsLive) { return ""; }
            string text = Localization.instance.Localize("$DA_grinder_queue_status",
                QueuedCount.ToString(), ValConfig.GrinderQueueSize.Value.ToString());
            string status = StatusText();
            if (status.Length > 0) { text += "\n" + status; }

            if (!PrivateArea.CheckAccess(transform.position, 0f, flash: false)) {
                return Localization.instance.Localize(text + "\n$piece_noaccess");
            }
            text += "\n[<color=yellow><b>$KEY_Use</b></color>] $DA_grinder_feed";
            if (QueuedCount > 0) {
                text += ZInput.IsNonClassicFunctionality() && ZInput.IsGamepadActive()
                    ? "\n[<color=yellow><b>$KEY_AltKeys + $KEY_Use</b></color>] $DA_grinder_take_back"
                    : "\n[<color=yellow><b>$KEY_AltPlace + $KEY_Use</b></color>] $DA_grinder_take_back";
            }
            return Localization.instance.Localize(text);
        }

        private string StatusText() {
            if (Disabled) { return ValConfig.GrinderEnabled.Value ? "$DA_grinder_nocraftcost" : "$DA_grinder_disabled"; }
            switch (CurrentState) {
                case State.Working: {
                    string head = nview.GetZDO().GetString(GrinderQueue.HeadKey);
                    if (head.Length == 0) { return ""; }
                    float left = Mathf.Max(0f, SecondsPerItem - Progress()) / Mathf.Max(0.01f, Speed);
                    return Localization.instance.Localize("$DA_grinder_working",
                        Localization.instance.Localize(head), Mathf.CeilToInt(left).ToString());
                }
                case State.OutputFull: return "$DA_grinder_output_full";
                case State.Waiting: return "$DA_grinder_waiting";
                case State.Stuck: return "$DA_grinder_stuck";
                default: return QueuedCount > 0 ? "" : "$DA_grinder_idle";
            }
        }

        private string CoreStatus() {
            return Localization.instance.Localize("$DA_grinder_cores_status",
                CoreCount.ToString(), SlotCount.ToString(),
                Mathf.RoundToInt(Yield * 100f).ToString(),
                Mathf.RoundToInt((Speed - 1f) * 100f).ToString());
        }

        // ---- the owner ------------------------------------------------------------------

        private List<GrinderQueue.Entry> OwnerQueue(ZDO zdo) {
            byte[] raw = zdo.GetByteArray(GrinderQueue.QueueKey);
            if (queue == null || !ReferenceEquals(raw, queueRaw)) {
                queue = GrinderQueue.Parse(raw);
                queueRaw = raw;
            }
            return queue;
        }

        private void SaveQueue(ZDO zdo) {
            queueRaw = GrinderQueue.Save(zdo, queue);
        }

        // Starts the clock from here: P seconds done, accruing at the current speed from now on.
        private void Anchor(ZDO zdo, float progress, State state) {
            zdo.Set(ProgressKey, Mathf.Max(0f, progress));
            zdo.Set(TimeKey, ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L);
            zdo.Set(SpeedKey, Speed);
            zdo.Set(StateKey, (int)state);
        }

        /// <summary>
        /// Owner side of a feed. Checks again here, against the owner's own view: the requester's copy of
        /// the queue lags, and the config may have changed in between.
        /// </summary>
        internal GrinderOps.Reason OwnerEnqueue(ItemDrop.ItemData item) {
            if (!IsLive || !nview.IsOwner()) { return GrinderOps.Reason.NotGrinder; }
            if (!ValConfig.GrinderEnabled.Value) { return GrinderOps.Reason.Disabled; }
            if (Disabled) { return GrinderOps.Reason.NoCraftCost; }
            GrinderRecipes.Refusal refusal = GrinderRecipes.Check(item);
            if (refusal != GrinderRecipes.Refusal.None) { return (GrinderOps.Reason)refusal; }

            ZDO zdo = nview.GetZDO();
            List<GrinderQueue.Entry> entries = OwnerQueue(zdo);
            if (entries.Count >= ValConfig.GrinderQueueSize.Value) { return GrinderOps.Reason.QueueFull; }
            GrinderQueue.Entry entry = GrinderQueue.NewEntry(item);
            if (GrinderQueue.Bytes(entries) + entry.Blob.Length > GrinderQueue.MaxBytes) { return GrinderOps.Reason.QueueFull; }

            // Nothing the Grinder could work on was waiting - an empty queue, or only items it cannot
            // rebuild - so this one starts from nothing rather than from whatever time had piled up.
            bool idle = FirstReadable(entries) < 0;
            entries.Add(entry);
            SaveQueue(zdo);
            if (idle) { Anchor(zdo, 0f, State.Working); }
            // After the queue is saved: the feed has happened, whatever an effect does.
            PlayEffect(FxFeed);
            return GrinderOps.Reason.None;
        }

        /// <summary>
        /// Owner side of a take-back: removes the last item queued that this game can rebuild, and hands it
        /// over. When that was the item being ground, its progress starts over with the next.
        /// </summary>
        internal ItemDrop.ItemData OwnerTakeLast() {
            if (!IsLive || !nview.IsOwner()) { return null; }
            ZDO zdo = nview.GetZDO();
            List<GrinderQueue.Entry> entries = OwnerQueue(zdo);
            int head = FirstReadable(entries);
            for (int i = entries.Count - 1; i >= 0; --i) {
                ItemDrop.ItemData item = GrinderQueue.Decode(entries[i]);
                if (item == null) { continue; }
                entries.RemoveAt(i);
                SaveQueue(zdo);
                if (entries.Count == 0) {
                    Anchor(zdo, 0f, State.Idle);
                } else if (i == head) {
                    Anchor(zdo, 0f, CurrentState);
                }
                return item;
            }
            return null;
        }

        private static int FirstReadable(List<GrinderQueue.Entry> entries) {
            for (int i = 0; i < entries.Count; ++i) {
                if (GrinderQueue.Decode(entries[i]) != null) { return i; }
            }
            return -1;
        }

        // Not gated on a local player: a dedicated server owns every persistent object around the world's
        // centre (its reference position never moves from there), so a Grinder built near the spawn is
        // ground by the server.
        private void Tick() {
            if (!IsLive || !nview.IsOwner() || ObjectDB.instance == null) { return; }
            try {
                OwnerTick();
            } catch (Exception ex) {
                Logger.LogError($"[Grinder] tick failed: {ex}");
            }
        }

        private void OwnerTick() {
            ZDO zdo = nview.GetZDO();
            MultiUserChestIntegration.KeepExclusive(nview);
            List<GrinderQueue.Entry> entries = OwnerQueue(zdo);
            State state = CurrentState;

            if (entries.Count == 0) {
                if (state != State.Idle) { Anchor(zdo, 0f, State.Idle); }
                return;
            }
            if (Disabled) {
                if (state != State.Disabled) { Anchor(zdo, Progress(), State.Disabled); }
                return;
            }
            // Only items made by a mod this game no longer has: nothing to work on, and no time banked for
            // whatever is fed next.
            if (FirstReadable(entries) < 0) {
                if (state != State.Stuck) { Anchor(zdo, 0f, State.Stuck); }
                return;
            }

            float seconds = SecondsPerItem;
            float progress = Progress();
            if (progress < seconds) {
                // Coming back from a pause: the clock starts again from where it stopped.
                if (state != State.Working) { Anchor(zdo, progress, State.Working); }
                return;
            }

            // Someone has the chest open. Writing to it under them would blank their panel, so the
            // finished batch waits until they close it.
            if (Store == null || CraftFromStoragePatches.IsBusy(Store)) {
                if (state != State.Waiting) { Anchor(zdo, progress, State.Waiting); }
                return;
            }
            SyncStore();
            Inventory inv = Store.GetInventory();
            if (inv == null) { return; }

            Dictionary<string, long> owed = GrinderQueue.LoadRemainders(zdo);
            List<OutputFit.Output> outputs = new List<OutputFit.Output>();
            int batches = 0;
            int budget = Mathf.Max(1, ValConfig.GrinderBatchesPerTick.Value);
            State next = State.Working;
            State written = state;
            List<string> produced = new List<string>();

            while (progress >= seconds && batches < budget) {
                // Whatever is left may be unreadable; the next tick parks the Grinder as Stuck.
                int index = FirstReadable(entries);
                if (index < 0) { break; }
                GrinderQueue.Entry entry = entries[index];
                ItemDrop.ItemData item = GrinderQueue.Decode(entry);
                int units = Mathf.Clamp(GrinderRecipes.CraftUnit(item), 1, entry.Stack);

                Dictionary<string, long> trial = new Dictionary<string, long>(owed);
                outputs.Clear();
                if (!Payout(item, units, trial, outputs)) {
                    // Its recipe is gone since it was queued (a mod removed, or a recipe switched off).
                    // The whole entry goes into the chest as it is rather than vanishing.
                    units = entry.Stack;
                    ItemDrop.ItemData whole = item.Clone();
                    whole.m_stack = 1;
                    outputs.Add(new OutputFit.Output { Template = whole, Amount = units });
                }
                if (!OutputFit.Fits(inv, outputs)) {
                    next = State.OutputFull;
                    break;
                }
                OutputFit.Add(inv, outputs);
                owed = trial;
                produced.Add(entry.Name);

                if (units >= entry.Stack) {
                    entries.RemoveAt(index);
                } else {
                    GrinderQueue.SetStack(entry, item, entry.Stack - units);
                }
                progress -= seconds;
                ++batches;

                // The chest saved itself the moment the output went in, so the queue, what is owed and the
                // clock are committed with it, batch by batch: if the next batch throws, or the Grinder
                // changes hands, the ZDO still agrees with what is in the chest.
                GrinderQueue.SaveRemainders(zdo, owed);
                SaveQueue(zdo);
                written = entries.Count == 0 ? State.Idle : State.Working;
                Anchor(zdo, entries.Count == 0 ? 0f : progress, written);
                if (entries.Count == 0) { break; }
            }

            if (batches > 0) {
                PlayEffect(FxProduce);
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Grinder] ground {string.Join(", ", produced)} at {Mathf.RoundToInt(Yield * 100f)}%.");
                }
            }

            // Written only when something changed: a held batch is re-checked every tick, but stays put.
            if (entries.Count > 0 && written != next) {
                Anchor(zdo, progress, next);
            }
        }

        // Effects are cosmetic and come after the change they mark is saved, so one that throws can never
        // undo a feed or a batch, or make a requester refund an item that was in fact queued.
        private void PlayEffect(int kind) {
            try {
                nview.InvokeRPC(ZNetView.Everybody, FxRpc, kind);
            } catch (Exception ex) {
                Logger.LogWarning($"[Grinder] playing an effect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// What grinding <paramref name="units"/> of <paramref name="item"/> pays out now: its share of the
        /// cost goes onto what is owed, and every whole unit owed is taken off and paid. False when the item
        /// has no recipe to work from.
        /// </summary>
        private bool Payout(ItemDrop.ItemData item, int units, Dictionary<string, long> owed, List<OutputFit.Output> outputs) {
            Dictionary<string, long> gained = new Dictionary<string, long>();
            if (!GrinderRecipes.AddReturns(item, units, Yield, gained)) { return false; }

            foreach (KeyValuePair<string, long> entry in gained) {
                string key = GrinderQueue.RemainderKeyFor(entry.Key, item.m_cheated);
                owed.TryGetValue(key, out long have);
                owed[key] = have + entry.Value;
            }

            // No better than the item it came from, and no better than this world makes.
            int worldLevel = Mathf.Min(item.m_worldLevel, Game.m_worldLevel);
            foreach (string key in new List<string>(owed.Keys)) {
                long whole = owed[key] / 1000;
                if (whole <= 0) { continue; }
                GameObject prefab = ObjectDB.instance.GetItemPrefab(GrinderQueue.PrefabOf(key, out bool cheated));
                ItemDrop.ItemData template = OutputFit.Material(prefab, cheated, worldLevel);
                // A material that has since left the game stays owed rather than being paid as nothing.
                if (template == null) { continue; }
                outputs.Add(new OutputFit.Output { Template = template, Amount = (int)whole });
                owed[key] -= whole * 1000;
            }
            return true;
        }

        /// <summary>
        /// Brings the chest's grid up to date with its ZDO before anything writes to it, as the Hopper's
        /// <see cref="HopperHub.SyncStore"/> does, for the same reason: for up to a second after this
        /// client becomes the owner its grid can be older than the one the previous owner saved.
        /// </summary>
        private void SyncStore() {
            if (Store != null && !Store.IsInUse()) { Store.Load(); }
        }

        private void OnCoresChanged() {
            // The speed changed: bank what was ground at the old one before the new one applies.
            if (IsLive && nview.IsOwner() && CurrentState == State.Working) {
                Anchor(nview.GetZDO(), Progress(), State.Working);
            }
            UpdateVisuals();
        }

        // Taking the piece down hands back everything still waiting to be ground. The cores are dropped
        // by CoreSockets, and the chest drops its own contents.
        private void OnDestroyedDropQueue() {
            if (!IsLive || !nview.IsOwner()) { return; }
            foreach (GrinderQueue.Entry entry in OwnerQueue(nview.GetZDO())) {
                ItemDrop.ItemData item = GrinderQueue.Decode(entry);
                if (item == null) {
                    Logger.LogWarning($"[Grinder] a queued {entry.Name} could not be rebuilt by this game and was lost with the Grinder.");
                    continue;
                }
                Vector3 pos = transform.position + Vector3.up * 0.8f + UnityEngine.Random.insideUnitSphere * 0.3f;
                ItemDrop.DropItem(item, item.m_stack, pos, Quaternion.identity);
            }
        }

        // ---- visuals ----------------------------------------------------------------------

        private bool working;

        private void UpdateVisuals() {
            if (!IsLive) { return; }
            working = CurrentState == State.Working && QueuedCount > 0 && !Disabled;
            if (WorkingVisuals != null && WorkingVisuals.activeSelf != working) { WorkingVisuals.SetActive(working); }
        }

        private void Update() {
            if (!working || Spinners == null) { return; }
            float dt = Time.deltaTime;
            for (int i = 0; i < Spinners.Length; ++i) {
                Transform spinner = Spinners[i];
                if (spinner == null) { continue; }
                Vector3 axis = SpinAxes != null && i < SpinAxes.Length ? SpinAxes[i] : Vector3.right;
                float speed = SpinSpeeds != null && i < SpinSpeeds.Length ? SpinSpeeds[i] : 180f;
                spinner.Rotate(axis, speed * dt, Space.Self);
            }
        }

        private void RPC_Fx(long sender, int kind) {
            EffectList effects = kind == FxFeed ? FeedEffects : ProduceEffects;
            effects?.Create(transform.position + Vector3.up, transform.rotation);
            UpdateVisuals();
        }
    }
}
