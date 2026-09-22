using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_ForgeHopper piece. Attached to the prefab in Unity (not in code).
    ///
    /// The hopper keeps a single shared inventory, reached through the hatch on its front face. On a
    /// timer it links every <see cref="Smelter"/> in range and services them from that one inventory:
    /// fuel first, then ore. Finished product is pulled back into the same inventory by the
    /// <c>Smelter.Spawn</c> prefix in <see cref="HopperPatches"/>, so the chain closes on itself -
    /// wood becomes coal in a kiln, and that coal is then spent as smelter fuel.
    ///
    /// Note that <see cref="Smelter"/> is not just the smelter: the charcoal kiln, blast furnace,
    /// windmill, spinning wheel and eitr refinery all use the same component, so one piece automates
    /// every one of them.
    /// </summary>
    public class HopperHub : MonoBehaviour {
        // ---- Unity-wired references ----------------------------------------

        /// <summary>
        /// The six core sockets, in AddCore order: 1-3 low-to-high on the +X flank, 4-6 low-to-high on
        /// the -X flank. Each is paired with its crystal by position at prefab build time, not by name.
        /// </summary>
        public Switch[] CoreSwitches = new Switch[SlotCount];

        /// <summary>Per-slot crystal, shown only while that slot holds a core. Same order as the switches.</summary>
        public GameObject[] CoreVisuals = new GameObject[SlotCount];

        /// <summary>Shown while at least one core is inserted.</summary>
        public GameObject EnabledVisuals;

        /// <summary>
        /// Optional. Any transform listed here spins about its own local X while the hopper is working.
        /// Left empty for now: the drive gears are modelled two-to-an-object with their origins at the
        /// piece root, so spinning them would swing them around the wrong pivot. Split each gear onto
        /// its own object with the origin on the gear axis and they can simply be dropped in here.
        /// </summary>
        public Transform[] SpinningGears = new Transform[0];

        // ---- state ----------------------------------------------------------

        internal const int SlotCount = 6;

        // ZDO key holding the inserted-core bitmask (bit n set => slot n filled). Deliberately not the
        // autosorter's key: the two pieces have different slot counts and must not read each other's mask.
        private const string CoreMaskKey = "DA_hopper_cores";

        private const string SurtlingCorePrefab = "SurtlingCore";

        private ZNetView nview;
        private Coroutine loop;
        private static int pieceMask;

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

        private void Awake() {
            nview = GetComponent<ZNetView>();
            // The only Container under the piece is the store; the root itself has none.
            Store = GetComponentInChildren<Container>(includeInactive: true);

            if (nview != null && nview.GetZDO() != null) {
                nview.Register("DA_HopperCores", new Action<long>(RPC_RefreshCores));
                WearNTear wnt = GetComponent<WearNTear>();
                if (wnt != null) { wnt.m_onDestroyed += OnDestroyedDropCores; }
            }

            // Switch callbacks are C# delegates and cannot be assigned in Unity, so wire them here.
            for (int slot = 0; slot < SlotCount; ++slot) { WireSwitch(slot); }

            // Refresh visuals shortly after load (and periodically) so a late ZDO sync still lights up.
            InvokeRepeating(nameof(UpdateVisuals), 1f, 4f);
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
                    SafeScan();

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

        private void Scan() {
            LinkedSmelters.Clear();

            bool dormant = !ValConfig.HopperEnabled.Value
                           || (ValConfig.HopperRequireCores.Value && CoreCount == 0);
            if (dormant) {
                // Links nothing, and the rebuild below hands back any speed-up this hub was applying.
                HopperNetwork.RebuildSpeedMap();
                return;
            }

            if (pieceMask == 0) { pieceMask = LayerMask.GetMask("piece", "piece_nonsolid"); }

            float radius = EffectiveRadius;
            Collider[] hits = Physics.OverlapSphere(transform.position, radius, pieceMask);
            HashSet<Smelter> seen = new HashSet<Smelter>();
            foreach (Collider hit in hits) {
                if (hit == null) { continue; }
                Smelter smelter = hit.GetComponentInParent<Smelter>();
                if (smelter == null || !seen.Add(smelter)) { continue; }
                if (smelter.m_nview == null || !smelter.m_nview.IsValid()) { continue; }
                // A smelter inside a ward the player is not permitted in is none of our business.
                if (!PrivateArea.CheckAccess(smelter.transform.position, 0f, flash: false)) { continue; }
                LinkedSmelters.Add(smelter);
            }

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Hopper] linked {LinkedSmelters.Count} smelters within {radius}m.");
            }

            HopperNetwork.RebuildSpeedMap();
        }

        /// <summary>
        /// Collect finished product, top up fuel, then queue ore - in that order, and that order matters.
        /// Collecting first means coal a charcoal kiln finished this tick is already in the inventory and
        /// can be spent as smelter fuel in the same tick, which is what closes the wood -> coal -> bars
        /// chain without any special-casing. Fuel before ore means that if some modded station ever makes
        /// one item both a valid fuel and a valid conversion input, keeping fires lit wins.
        /// </summary>
        private void ServiceSmelters() {
            if (Store == null || LinkedSmelters.Count == 0) { return; }
            Inventory inv = Store.GetInventory();
            if (inv == null) { return; }

            // Someone has the hatch open. Writing to the inventory under them would blank their panel
            // (see CraftFromStoragePatches.IsBusy), so the hopper idles until they close it.
            if (CraftFromStoragePatches.IsBusy(Store)) { return; }

            CraftFromStoragePatches.ClaimOwnership(Store);

            FlushFinished();

            int budget = Mathf.Max(1, ValConfig.HopperItemsPerTick.Value);
            int moved = FeedFuel(inv, budget);
            moved += FeedOre(inv, budget - moved);

            // Advance the round robin so the next tick starts at a different smelter.
            if (LinkedSmelters.Count > 0) { rotateStart = (rotateStart + 1) % LinkedSmelters.Count; }

            if (moved > 0 && ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Hopper] moved {moved} items into {LinkedSmelters.Count} smelters.");
            }
        }

        /// <summary>
        /// Vanilla only empties a smelter when its queue runs dry or its fuel does, so a continuously fed
        /// smelter would sit on a finished stack forever. Poking the public RPC makes the owner run
        /// SpawnProcessed, and the Smelter.Spawn prefix then routes the product into this inventory.
        /// </summary>
        private void FlushFinished() {
            if (!ValConfig.HopperCollectOutput.Value) { return; }
            foreach (Smelter smelter in LinkedSmelters) {
                if (smelter == null || smelter.m_nview == null || !smelter.m_nview.IsValid()) { continue; }
                if (smelter.GetProcessedQueueSize() > 0) {
                    smelter.m_nview.InvokeRPC("RPC_EmptyProcessed");
                }
            }
        }

        private int FeedFuel(Inventory inv, int budget) {
            int moved = 0;
            int count = LinkedSmelters.Count;
            for (int i = 0; i < count && moved < budget; ++i) {
                Smelter smelter = LinkedSmelters[(rotateStart + i) % count];
                if (smelter == null || smelter.m_maxFuel <= 0 || smelter.m_fuelItem == null) { continue; }
                if (smelter.m_nview == null || !smelter.m_nview.IsValid()) { continue; }

                // Room is measured once, before anything is added. GetFuel reads the ZDO, and the ZDO
                // does not change until the RPC has landed on the owner and replicated back - so topping
                // up in a loop that re-reads it would massively overshoot m_maxFuel.
                int room = Mathf.FloorToInt(smelter.m_maxFuel - smelter.GetFuel());
                if (room <= 0) { continue; }

                string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;
                int have = inv.CountItems(fuelName, -1, matchWorldLevel: false);
                int add = Mathf.Min(room, have, budget - moved);
                for (int k = 0; k < add; ++k) {
                    inv.RemoveItem(fuelName, 1);
                    smelter.m_nview.InvokeRPC("RPC_AddFuel");
                }
                moved += add;
            }
            return moved;
        }

        private int FeedOre(Inventory inv, int budget) {
            if (budget <= 0) { return 0; }
            int moved = 0;
            int count = LinkedSmelters.Count;
            for (int i = 0; i < count && moved < budget; ++i) {
                Smelter smelter = LinkedSmelters[(rotateStart + i) % count];
                if (smelter == null || smelter.m_maxOre <= 0) { continue; }
                if (smelter.m_nview == null || !smelter.m_nview.IsValid()) { continue; }

                // Same reasoning as the fuel pass: measure the queue once, then fill it.
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
                    // Remove first, then queue: no yield or RPC runs between the two, so the units can
                    // never exist in both the inventory and the smelter.
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

        private int GetCoreMask() {
            if (nview == null || !nview.IsValid()) { return 0; }
            return nview.GetZDO().GetInt(CoreMaskKey, 0);
        }

        /// <summary>Number of inserted cores (0-6).</summary>
        internal int CoreCount {
            get {
                int mask = GetCoreMask();
                int count = 0;
                for (int slot = 0; slot < SlotCount; ++slot) {
                    if ((mask & (1 << slot)) != 0) { ++count; }
                }
                return count;
            }
        }

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

        private void SetCoreMask(int mask) {
            if (nview == null || !nview.IsValid()) { return; }
            if (!nview.IsOwner()) { nview.ClaimOwnership(); }
            nview.GetZDO().Set(CoreMaskKey, mask);
            nview.InvokeRPC(ZNetView.Everybody, "DA_HopperCores");
            UpdateVisuals();
            // Apply the new range / speed without waiting for the next tick.
            if (Player.m_localPlayer != null) { SafeScan(); }
        }

        private void RPC_RefreshCores(long sender) {
            UpdateVisuals();
            // Range and speed changed for everyone, not just the player who turned the core.
            if (Player.m_localPlayer != null) { SafeScan(); }
        }

        private void WireSwitch(int slot) {
            Switch sw = (CoreSwitches != null && slot < CoreSwitches.Length) ? CoreSwitches[slot] : null;
            if (sw == null) { return; }
            sw.m_onUse = OnCoreSwitchUsed;
            sw.m_onHover = () => GetSwitchHover(slot);
            sw.m_name = "$DA_hopper_name";
        }

        private int SlotForSwitch(Switch caller) {
            if (CoreSwitches == null) { return -1; }
            for (int slot = 0; slot < CoreSwitches.Length; ++slot) {
                if (CoreSwitches[slot] == caller) { return slot; }
            }
            return -1;
        }

        private bool OnCoreSwitchUsed(Switch caller, Humanoid user, ItemDrop.ItemData item) {
            int slot = SlotForSwitch(caller);
            if (slot < 0) { return false; }
            if (!(user is Player player)) { return false; }
            if (!PrivateArea.CheckAccess(transform.position)) { return false; }
            if (nview == null || !nview.IsValid()) { return false; }

            GameObject corePrefab = ObjectDB.instance != null
                ? ObjectDB.instance.GetItemPrefab(SurtlingCorePrefab) : null;
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
            int cores = CoreCount;
            string status = Localization.instance.Localize(
                "$DA_hopper_cores_status", cores.ToString(), SlotCount.ToString(),
                Mathf.RoundToInt(EffectiveRadius).ToString(),
                Mathf.RoundToInt((SpeedMultiplier - 1f) * 100f).ToString());
            return Localization.instance.Localize(
                status + "\n[<color=yellow><b>$KEY_Use</b></color>] " + action);
        }

        private void UpdateVisuals() {
            if (nview == null || !nview.IsValid()) { return; }
            int mask = GetCoreMask();
            if (CoreVisuals != null) {
                for (int slot = 0; slot < CoreVisuals.Length && slot < SlotCount; ++slot) {
                    SetActiveSafe(CoreVisuals[slot], (mask & (1 << slot)) != 0);
                }
            }
            // Follows IsActive, not the raw mask: with "Require Cores" turned off the hopper works on
            // zero cores, and a working hopper that never smokes reads as broken.
            SetActiveSafe(EnabledVisuals, IsActive);
        }

        private static void SetActiveSafe(GameObject go, bool active) {
            if (go != null && go.activeSelf != active) { go.SetActive(active); }
        }

        // On destroy/deconstruct, drop the inserted cores so they are not lost.
        private void OnDestroyedDropCores() {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            int count = CoreCount;
            if (count <= 0) { return; }
            GameObject corePrefab = ObjectDB.instance != null
                ? ObjectDB.instance.GetItemPrefab(SurtlingCorePrefab) : null;
            if (corePrefab == null) { return; }
            for (int i = 0; i < count; ++i) {
                Vector3 pos = transform.position + Vector3.up * 0.5f + UnityEngine.Random.insideUnitSphere * 0.3f;
                Instantiate(corePrefab, pos, UnityEngine.Random.rotation);
            }
        }
    }
}
