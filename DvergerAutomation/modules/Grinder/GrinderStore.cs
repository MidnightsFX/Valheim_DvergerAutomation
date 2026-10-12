using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Finishes the Grinder's results chest on the freshly loaded bundle prefab. Must run before the piece
    /// is ever instantiated, because <c>Container.Awake</c> reads these fields to build its inventory and
    /// bind its network view. The same job <see cref="HopperStore"/> does for the Hopper, for the same
    /// reasons; see there for why each field is set.
    /// </summary>
    internal static class GrinderStore {
        // Child path of the results chest inside the DA_Grinder prefab.
        private const string StorePath = "Grinder_Body/GrinderStore";

        internal const int MinRows = 1;
        internal const int MaxRows = 8;
        internal const int DefaultRows = 2;

        // Fixed, as for the Hopper: Inventory.Load silently drops anything in a column a narrowed grid
        // no longer has.
        internal const int Width = 8;

        private static Container prefabStore;

        internal static void ConfigurePrefab(GameObject prefab) {
            if (prefab == null) {
                Logger.LogWarning("Grinder: DA_Grinder prefab missing; the results chest will not work.");
                return;
            }

            Transform store = prefab.transform.Find(StorePath);
            Container box = store != null ? store.GetComponent<Container>() : null;
            if (box == null) { box = prefab.GetComponentInChildren<Container>(includeInactive: true); }
            if (box == null) {
                Logger.LogWarning($"Grinder: no Container at '{StorePath}' on {prefab.name}; the results chest will not work.");
                return;
            }

            box.m_rootObjectOverride = prefab.GetComponent<ZNetView>();
            box.m_name = "$DA_grinder_store_name";
            box.m_checkGuardStone = true;
            box.m_privacy = Container.PrivacySetting.Public;
            box.m_autoDestroyEmpty = false;
            box.m_discoverStat = PlayerStatType.None;

            prefabStore = box;
            ApplySize(box);
            ValConfig.GrinderStoreRows.SettingChanged += OnRowsChanged;
        }

        internal static void ApplySize(Container box) {
            if (box == null) { return; }
            box.m_width = Width;
            box.m_height = Mathf.Clamp(ValConfig.GrinderStoreRows.Value, MinRows, MaxRows);

            Inventory inv = box.GetInventory();
            if (inv == null) { return; }   // prefab, or an instance whose Awake has not run yet
            inv.m_width = Width;
            inv.SetHeight(box.m_height);
        }

        private static void OnRowsChanged(object sender, System.EventArgs e) {
            ApplySize(prefabStore);
            foreach (GrinderHub hub in GrinderHub.All) {
                if (hub == null || hub.Store == null || hub.Store.IsInUse()) { continue; }
                ApplySize(hub.Store);
            }
        }
    }
}
