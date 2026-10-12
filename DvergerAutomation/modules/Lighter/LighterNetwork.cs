using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Brings a fire, torch, oven or hot tub over to the client running the Lighter that wants to fill it,
    /// as <see cref="HopperNetwork"/> brings smelters to their hopper: the Lighter only ever fills a piece it
    /// owns (see <see cref="LighterHub"/>), so a piece running on another player's game is asked for, and
    /// filled on a later pass once it has arrived.
    /// </summary>
    internal static class LighterNetwork {
        private const string RequestRpc = "DA_LighterPieceRequest";

        // Minimum gap between two requests for the same piece. A handoff takes a round trip, and a piece
        // whose owner keeps it for its own Lighter is asked again at this pace, not every pass.
        private const float RequestInterval = 2f;

        private static readonly Dictionary<ZDOID, float> lastRequest = new Dictionary<ZDOID, float>();

        // ZRoutedRpc is rebuilt with every session, and registering twice on the same one throws.
        private static ZRoutedRpc registeredOn;

        internal static void RegisterRpc() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == registeredOn) { return; }
            rpc.Register<ZDOID>(RequestRpc, RPC_Request);
            registeredOn = rpc;
            lastRequest.Clear();
        }

        /// <summary>
        /// Asks the owner of a piece this client's Lighter wants to fill to hand it over. A piece nobody
        /// owns is simply taken - there is no one to race. Rate-limited per piece, so it is cheap to call
        /// every pass.
        /// </summary>
        internal static void AskFor(ZNetView view) {
            if (view == null || !view.IsValid()) { return; }
            ZDO zdo = view.GetZDO();
            long owner = zdo.GetOwner();
            if (owner == 0L) {
                view.ClaimOwnership();
                return;
            }

            float now = Time.time;
            if (lastRequest.TryGetValue(zdo.m_uid, out float last) && now - last < RequestInterval) { return; }
            lastRequest[zdo.m_uid] = now;
            if (ZRoutedRpc.instance == null) { return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(owner, RequestRpc, zdo.m_uid);
        }

        /// <summary>
        /// Another player's Lighter wants a piece this client owns. Handed over the way vanilla hands a chest
        /// to the player opening it - <c>ForceSendZDO</c>, then <c>SetOwner</c>, from this side - so its
        /// latest fuel level travels with it and this client stops running it at that moment.
        ///
        /// Kept when a Lighter here would fill it too, or two Lighters covering one fire would pass it back
        /// and forth. A hot tub is also kept while a Hopper here feeds it (a Hopper can be told to): the
        /// Hopper hands over only for its own kind, so without this the two would trade it every few
        /// seconds. A refusal is silent; the requester asks again later.
        /// </summary>
        private static void RPC_Request(long sender, ZDOID id) {
            try {
                if (ZDOMan.instance == null || ZNetScene.instance == null) { return; }
                if (sender == ZDOMan.GetSessionID()) { return; }
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsOwner()) { return; }
                Component burner = FuelBurners.Find(ZNetScene.instance.FindInstance(id));
                if (burner == null) { return; }
                if (LighterHub.FedHere(burner) || FedByHopperHere(burner)) {
                    if (ValConfig.EnableDebugMode.Value) {
                        Logger.LogInfo($"[Lighter] kept {FuelBurners.PrefabName(burner)}: it is fed here too.");
                    }
                    return;
                }

                ZDOMan.instance.ForceSendZDO(sender, id);
                zdo.SetOwner(sender);
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Lighter] handed {FuelBurners.PrefabName(burner)} to {sender}, whose Lighter fills it.");
                }
            } catch (System.Exception ex) {
                Logger.LogError($"[Lighter] handing over a piece failed: {ex}");
            }
        }

        private static bool FedByHopperHere(Component burner) {
            if (!(burner is Smelter smelter)) { return false; }
            foreach (HopperHub hub in HopperNetwork.Hubs) {
                if (hub == null || !hub.IsOwner || !hub.IsActive) { continue; }
                if (hub.LinkedSmelters.Contains(smelter)) { return true; }
            }
            return false;
        }
    }

    /// <summary>
    /// Finishes the Lighter's fuel tank on the freshly loaded bundle prefab. Must run before the piece is
    /// ever instantiated, because <c>Container.Awake</c> reads these fields to build its inventory and bind
    /// its network view. The same job <see cref="HopperStore"/> does for the Hopper, for the same reasons;
    /// see there for why each field is set.
    /// </summary>
    internal static class LighterStore {
        // Child path of the tank inside the DA_Lighter prefab.
        private const string StorePath = "Lighter_Body/LighterStore";

        internal const int MinRows = 1;
        internal const int MaxRows = 8;
        internal const int DefaultRows = 2;

        // Fixed, as for the Hopper: Inventory.Load silently drops anything in a column a narrowed grid
        // no longer has.
        internal const int Width = 8;

        private static Container prefabStore;

        internal static void ConfigurePrefab(GameObject prefab) {
            if (prefab == null) {
                Logger.LogWarning("Lighter: DA_Lighter prefab missing; the fuel tank will not work.");
                return;
            }

            Transform store = prefab.transform.Find(StorePath);
            Container box = store != null ? store.GetComponent<Container>() : null;
            if (box == null) { box = prefab.GetComponentInChildren<Container>(includeInactive: true); }
            if (box == null) {
                Logger.LogWarning($"Lighter: no Container at '{StorePath}' on {prefab.name}; the fuel tank will not work.");
                return;
            }

            box.m_rootObjectOverride = prefab.GetComponent<ZNetView>();
            box.m_name = "$DA_lighter_store_name";
            box.m_checkGuardStone = true;
            box.m_privacy = Container.PrivacySetting.Public;
            box.m_autoDestroyEmpty = false;
            box.m_discoverStat = PlayerStatType.None;

            prefabStore = box;
            ApplySize(box);
            ValConfig.LighterStoreRows.SettingChanged += OnRowsChanged;
        }

        internal static void ApplySize(Container box) {
            if (box == null) { return; }
            box.m_width = Width;
            box.m_height = Mathf.Clamp(ValConfig.LighterStoreRows.Value, MinRows, MaxRows);

            Inventory inv = box.GetInventory();
            if (inv == null) { return; }   // prefab, or an instance whose Awake has not run yet
            inv.m_width = Width;
            inv.SetHeight(box.m_height);
        }

        private static void OnRowsChanged(object sender, System.EventArgs e) {
            ApplySize(prefabStore);
            foreach (LighterHub hub in LighterHub.All) {
                if (hub == null || hub.Store == null || hub.Store.IsInUse()) { continue; }
                ApplySize(hub.Store);
            }
        }
    }
}
