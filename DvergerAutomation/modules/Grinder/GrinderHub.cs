using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_Grinder piece, added to the prefab in Unity next to its
    /// <see cref="CoreSockets"/>.
    ///
    /// Players put what they want broken down into its hopper (<see cref="GrinderFeed"/>) and pull the
    /// lever on its left flank. The lever empties the hopper into a processing queue on this piece's ZDO
    /// (<see cref="GrinderQueue"/>), and the Grinder works through it one craft's worth at a time, returning
    /// part of what each item cost (<see cref="GrinderRecipes"/>) into the results chest below. Anything it
    /// cannot break down comes out in the results chest as it went in. Molten Cores in its power panel raise
    /// both the speed and the share returned. The lever stands thrown while there is work, and drops back
    /// when the queue runs out.
    ///
    /// The hopper is a linked object of its own, which this piece spawns into its mouth (see
    /// <see cref="GrinderFeed"/> for why). Only the Grinder's owner touches the queue and the results chest,
    /// and it loads the hopper only once it owns that too: it asks for the hopper (<see cref="GrinderOps"/>)
    /// after whoever last opened it closes it, and the pull waits until then. That owner's side lives in
    /// GrinderHub.Grinding.cs.
    ///
    /// The grind's progress is stored as a value at a time and a speed, so it carries on - and catches up
    /// after the area was unloaded, as a smelter does - without the ZDO being rewritten every second. It is
    /// written only when something happens: the lever pulled, a batch finished, a pause or a resume.
    /// </summary>
    public partial class GrinderHub : MonoBehaviour {
        // ---- Unity-wired references ----------------------------------------

        /// <summary>Where the hopper is spawned: the middle of the hopper's mouth.</summary>
        public Transform FeedPoint;

        /// <summary>The start lever's switch, on the left flank.</summary>
        public Switch Lever;
        /// <summary>The lever's moving arm, which swings about <see cref="LeverAxis"/> (in its own space).</summary>
        public Transform LeverArm;
        public Vector3 LeverAxis = Vector3.right;
        /// <summary>Degrees from the arm as modelled (standing up): leaning back when idle, thrown forward while running.</summary>
        public float LeverIdleAngle = -30f;
        public float LeverRunAngle = 110f;
        public EffectList LeverEffects = new EffectList();

        /// <summary>Spun while grinding: the crusher rollers and their drive gears.</summary>
        public Transform[] Spinners = new Transform[0];
        /// <summary>Each spinner's axis, in its own local space.</summary>
        public Vector3[] SpinAxes = new Vector3[0];
        /// <summary>Each spinner's speed in degrees per second; the sign sets the direction.</summary>
        public float[] SpinSpeeds = new float[0];

        /// <summary>Shown while grinding: the dust the crusher throws up.</summary>
        public GameObject WorkingVisuals;

        /// <summary>Played at the hopper as each item is broken down.</summary>
        public EffectList ProduceEffects = new EffectList();

        /// <summary>
        /// Ashlands' lava bubbling, built into the top of the lava tank and replayed every few seconds. Part of
        /// the piece rather than spawned like vanilla's, so it can be shrunk to the tank: its systems scale by
        /// their own transforms only.
        /// </summary>
        public ParticleSystem LavaBubble;

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

        // Set by the lever, cleared once the hopper has been loaded into the queue.
        private static readonly int PullKey = "DA_grinder_pull".GetStableHashCode();

        // Progress P (seconds of grinding done on the current batch) as of time T, accruing at speed S
        // while Working. Every other state holds P where it stopped.
        private static readonly int ProgressKey = "DA_grinder_p".GetStableHashCode();
        private static readonly int TimeKey = "DA_grinder_t".GetStableHashCode();
        private static readonly int SpeedKey = "DA_grinder_s".GetStableHashCode();
        private static readonly int StateKey = "DA_grinder_state".GetStableHashCode();

        // As vanilla's smelter: time away beyond an hour is not made up.
        private const double MaxCatchUpSeconds = 3600.0;

        private const string LeverRpc = "DA_GrinderLever";
        private const string FxRpc = "DA_GrinderFx";
        private const int FxProduce = 0;
        private const int FxLever = 1;

        // Metres within which a player sees the lava tank bubble.
        private const float BubbleRange = 60f;

        // Degrees per second the lever swings at.
        private const float LeverSwing = 300f;

        internal static readonly List<GrinderHub> All = new List<GrinderHub>();

        private ZNetView nview;
        private CoreSockets sockets;
        private GrinderFeed feed;
        private float nextBubble;
        private Quaternion leverRest;
        private float leverAngle;
        // Whether the crusher is turning, as of the last visuals pass.
        private bool working;

        /// <summary>The results chest. Resolved from the hierarchy, so the prefab needs no extra wiring.</summary>
        internal Container Store { get; private set; }

        internal bool IsLive => nview != null && nview.IsValid();

        /// <summary>The status line for a Grinder's hopper or results chest; null for any other container.</summary>
        internal static string StatusFor(Container container) {
            GrinderHub hub;
            switch (StorageRegistry.KindOf(container)) {
                case StorageRegistry.Kind.GrinderStore:
                    hub = StorageRegistry.HolderOf<GrinderHub>(container);
                    break;
                case StorageRegistry.Kind.GrinderFeed:
                    GrinderFeed hopper = StorageRegistry.HolderOf<GrinderFeed>(container);
                    hub = hopper != null ? hopper.Grinder : null;
                    break;
                default:
                    return null;
            }
            return hub != null && hub.IsLive ? Localization.instance.Localize(hub.StatusText()) : null;
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
            if (Lever != null) {
                Lever.m_onUse = OnLeverUsed;
                Lever.m_onHover = LeverHover;
                Lever.m_name = NameToken;
            }
            if (LeverArm != null) {
                leverRest = LeverArm.localRotation;
                leverAngle = LeverIdleAngle;
                LeverArm.localRotation = leverRest * Quaternion.AngleAxis(leverAngle, LeverAxis);
            }

            if (nview != null && nview.GetZDO() != null) {
                nview.Register(LeverRpc, new Action<long>(RPC_Lever));
                nview.Register<int>(FxRpc, RPC_Fx);
                WearNTear wnt = GetComponent<WearNTear>();
                if (wnt != null) { wnt.m_onDestroyed += OnDestroyedDropContents; }
                InvokeRepeating(nameof(Tick), 1f, 1f);
                InvokeRepeating(nameof(UpdateVisuals), 1f, 1f);
                nextBubble = Time.time + UnityEngine.Random.Range(1f, 4f);
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

        // Why, when it is.
        private static string DisabledMessage => ValConfig.GrinderEnabled.Value ? "$DA_grinder_nocraftcost" : "$DA_grinder_disabled";

        private State CurrentState => IsLive ? (State)nview.GetZDO().GetInt(StateKey) : State.Idle;

        private int QueuedCount => IsLive ? nview.GetZDO().GetInt(GrinderQueue.CountKey) : 0;

        private bool PullPending => IsLive && nview.GetZDO().GetBool(PullKey);

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

        // ---- the hopper -----------------------------------------------------------------

        /// <summary>Where the hopper stands: the middle of the hopper's mouth.</summary>
        internal Vector3 FeedPosition => FeedPoint != null ? FeedPoint.position : transform.position + Vector3.up;

        /// <summary>This Grinder's hopper, when it is loaded here: the one standing at its FeedPoint (<see cref="GrinderLink"/>).</summary>
        internal GrinderFeed Feed {
            get {
                if (IsFeedHere(feed)) { return feed; }
                feed = null;
                if (!IsLive) { return null; }
                foreach (GrinderFeed hopper in GrinderFeed.All) {
                    if (IsFeedHere(hopper)) {
                        feed = hopper;
                        break;
                    }
                }
                return feed;
            }
        }

        private bool IsFeedHere(GrinderFeed hopper) {
            return hopper != null && hopper.IsLive && GrinderLink.Near(hopper.transform.position, FeedPosition);
        }

        private Inventory FeedInventory() {
            GrinderFeed hopper = Feed;
            return hopper != null && hopper.Box != null ? hopper.Box.GetInventory() : null;
        }

        // ---- the lever --------------------------------------------------------------------

        private bool OnLeverUsed(Switch caller, Humanoid user, ItemDrop.ItemData item) {
            // A hotbar key at the lever is left to vanilla, which uses the item as it would anywhere else.
            if (item != null) { return false; }
            if (!(user is Player player) || player != Player.m_localPlayer || !IsLive) { return false; }
            // Flashes the ward, as any locked piece does.
            if (!PrivateArea.CheckAccess(transform.position)) { return false; }
            if (Disabled) {
                player.Message(MessageHud.MessageType.Center, DisabledMessage);
                return false;
            }
            Inventory hopper = FeedInventory();
            if (hopper == null || hopper.NrOfItems() == 0) {
                player.Message(MessageHud.MessageType.Center, "$DA_grinder_hopper_empty");
                return false;
            }
            if (QueuedCount >= ValConfig.GrinderQueueSize.Value) {
                player.Message(MessageHud.MessageType.Center, "$DA_grinder_queue_full");
                return false;
            }
            // The owner loads the hopper (TryLoad): it alone writes the queue, and needs the hopper as well.
            nview.InvokeRPC(LeverRpc);
            return true;
        }

        private void RPC_Lever(long sender) {
            if (!IsLive || !nview.IsOwner()) { return; }
            ZDO zdo = nview.GetZDO();
            zdo.Set(PullKey, true);
            PlayEffect(FxLever);
            try {
                TryLoad(zdo);
            } catch (Exception ex) {
                Logger.LogError($"[Grinder] loading the hopper failed: {ex}");
            }
        }

        // ---- hover text ---------------------------------------------------------------

        private string LeverHover() {
            if (!IsLive) { return ""; }
            string text = NameToken + "\n" + StatusText();
            text += StorageScan.Permits(transform.position, StorageScan.LocalPlayerId())
                ? "\n[<color=yellow><b>$KEY_Use</b></color>] $DA_grinder_lever_use"
                : "\n$piece_noaccess";
            return Localization.instance.Localize(text);
        }

        internal string StatusText() {
            if (Disabled) { return DisabledMessage; }
            int queued = QueuedCount;
            if (queued == 0) {
                if (PullPending) { return "$DA_grinder_loading"; }
                Inventory hopper = FeedInventory();
                return hopper != null && hopper.NrOfItems() > 0 ? "$DA_grinder_pull_lever" : "$DA_grinder_idle";
            }
            switch (CurrentState) {
                case State.OutputFull: return "$DA_grinder_output_full";
                case State.Waiting: return "$DA_grinder_waiting";
                case State.Stuck: return "$DA_grinder_stuck";
                default: {
                    // The time left on the item being ground; with more than one queued, how many there are
                    // rather than which is first.
                    string left = Mathf.CeilToInt(Mathf.Max(0f, SecondsPerItem - Progress()) / Mathf.Max(0.01f, Speed)).ToString();
                    int items = nview.GetZDO().GetInt(GrinderQueue.ItemsKey);
                    if (items > 1) {
                        return Localization.instance.Localize("$DA_grinder_working_many", items.ToString(), left);
                    }
                    string head = nview.GetZDO().GetString(GrinderQueue.HeadKey);
                    return Localization.instance.Localize("$DA_grinder_working", Localization.instance.Localize(head), left);
                }
            }
        }

        private string CoreStatus() {
            return Localization.instance.Localize("$DA_grinder_cores_status",
                CoreCount.ToString(), SlotCount.ToString(),
                Mathf.RoundToInt(Yield * 100f).ToString(),
                Mathf.RoundToInt((Speed - 1f) * 100f).ToString());
        }

        private void OnCoresChanged() {
            // The speed changed: bank what was ground at the old one before the new one applies.
            if (IsLive && nview.IsOwner() && CurrentState == State.Working) {
                Anchor(nview.GetZDO(), Progress(), State.Working);
            }
            UpdateVisuals();
        }

        // Taking the Grinder down - broken or removed with the Hammer - drops everything still queued where
        // the hopper stands, intact, and empties and removes the hopper (GrinderOps.DisposeFeed). The cores
        // are dropped by CoreSockets, and the results chest drops its own contents.
        private void OnDestroyedDropContents() {
            if (!IsLive || !nview.IsOwner()) { return; }
            Vector3 at = FeedPosition;
            foreach (GrinderQueue.Entry entry in OwnerQueue(nview.GetZDO())) {
                ItemDrop.ItemData item = GrinderQueue.Decode(entry);
                if (item == null) {
                    Logger.LogWarning($"[Grinder] a queued {entry.Name} could not be rebuilt by this game and was lost with the Grinder.");
                    continue;
                }
                ItemDrop.DropItem(item, item.m_stack, at + UnityEngine.Random.insideUnitSphere * 0.3f, Quaternion.identity);
            }
            GrinderOps.DisposeFeed(Feed);
        }

        // ---- visuals ----------------------------------------------------------------------

        // Effects are cosmetic and come after the change they mark is saved, so one that throws can never
        // undo a batch.
        private void PlayEffect(int kind) {
            try {
                nview.InvokeRPC(ZNetView.Everybody, FxRpc, kind);
            } catch (Exception ex) {
                Logger.LogWarning($"[Grinder] playing an effect failed: {ex.Message}");
            }
        }

        private void UpdateVisuals() {
            if (!IsLive) { return; }
            working = CurrentState == State.Working && QueuedCount > 0 && !Disabled;
            if (WorkingVisuals != null && WorkingVisuals.activeSelf != working) { WorkingVisuals.SetActive(working); }
        }

        private void Update() {
            float dt = Time.deltaTime;

            // The lever stands thrown while there is work, read from the ZDO so every client agrees.
            if (LeverArm != null && IsLive) {
                bool running = !Disabled && (QueuedCount > 0 || PullPending);
                float target = running ? LeverRunAngle : LeverIdleAngle;
                if (!Mathf.Approximately(leverAngle, target)) {
                    leverAngle = Mathf.MoveTowards(leverAngle, target, LeverSwing * dt);
                    LeverArm.localRotation = leverRest * Quaternion.AngleAxis(leverAngle, LeverAxis);
                }
            }

            // The lava tank bubbles all the time, on every client near enough to see it: purely for the look,
            // never synced. A dedicated server has no player, so never plays it.
            if (LavaBubble != null && IsLive && Time.time >= nextBubble) {
                nextBubble = Time.time + UnityEngine.Random.Range(3f, 6f);
                Player player = Player.m_localPlayer;
                if (player != null && (player.transform.position - LavaBubble.transform.position).sqrMagnitude < BubbleRange * BubbleRange) {
                    // Starts the burst over; anything still in the air from the last one finishes as it was.
                    LavaBubble.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    LavaBubble.Play(true);
                }
            }

            if (!working || Spinners == null) { return; }
            for (int i = 0; i < Spinners.Length; ++i) {
                Transform spinner = Spinners[i];
                if (spinner == null) { continue; }
                Vector3 axis = SpinAxes != null && i < SpinAxes.Length ? SpinAxes[i] : Vector3.right;
                float speed = SpinSpeeds != null && i < SpinSpeeds.Length ? SpinSpeeds[i] : 180f;
                spinner.Rotate(axis, speed * dt, Space.Self);
            }
        }

        private void RPC_Fx(long sender, int kind) {
            if (kind == FxLever) {
                Vector3 at = LeverArm != null ? LeverArm.position : transform.position;
                LeverEffects?.Create(at, transform.rotation);
            } else {
                ProduceEffects?.Create(FeedPosition, transform.rotation);
            }
            UpdateVisuals();
        }
    }
}
