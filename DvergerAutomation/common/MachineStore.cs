using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Finishes a machine's storage - the Hopper's store, the Lighter's tank, the Grinder's results chest
    /// and its hopper - on the freshly loaded bundle prefab, and keeps their size on the server's setting.
    /// Must run before the piece is ever instantiated, because <c>Container.Awake</c> reads these fields to
    /// build its inventory and bind its network view.
    /// </summary>
    internal static class MachineStore {
        // Fixed at the player's own inventory width. Deliberately NOT configurable: Inventory.Load re-adds
        // every item at its saved grid position and the positional AddItem rejects x >= m_width outright,
        // silently dropping anything in a column a narrowed grid no longer has. Rows are safe -
        // Container.UpdateRows grows the grid to fit - so only the height is exposed.
        internal const int Width = 8;

        private static readonly List<Container> Scratch = new List<Container>();

        /// <summary>
        /// Finishes the Container at <paramref name="path"/> under the prefab - or the prefab's own, or failing
        /// both any under it, so a rename in Unity is survivable - and sizes it to <paramref name="rows"/>,
        /// now and whenever the setting changes. A missing prefab or container is reported, not thrown.
        /// </summary>
        internal static void Setup(GameObject prefab, string path, string nameToken, ConfigEntry<int> rows, int minRows, int maxRows, StorageRegistry.Kind kind) {
            if (prefab == null) {
                Logger.LogWarning($"{nameToken}: its prefab is missing from the asset bundle; it will not work.");
                return;
            }
            Transform at = path != null ? prefab.transform.Find(path) : prefab.transform;
            Container box = at != null ? at.GetComponent<Container>() : null;
            if (box == null) { box = prefab.GetComponentInChildren<Container>(includeInactive: true); }
            if (box == null) {
                Logger.LogWarning($"{nameToken}: no Container at '{path}' on {prefab.name}; it will not work.");
                return;
            }

            // A Container on a child has no ZNetView of its own, so it has to be pointed at the piece's.
            // Without this Container.Awake NREs on the very next line (m_nview.GetZDO()).
            ZNetView root = prefab.GetComponent<ZNetView>();
            Finish(box, box.GetComponent<ZNetView>() == null ? root : null, nameToken);

            ApplySize(box, rows.Value, minRows, maxRows);
            rows.SettingChanged += (sender, e) => {
                ApplySize(box, rows.Value, minRows, maxRows);
                Scratch.Clear();
                StorageRegistry.OfKind(kind, Scratch);
                foreach (Container live in Scratch) {
                    // Rebuilding the grid under a player's open panel would reset their selection and any
                    // drag in progress. The next change, or the next load, picks the size up.
                    if (!live.IsInUse()) { ApplySize(live, rows.Value, minRows, maxRows); }
                }
                Scratch.Clear();
            };
        }

        /// <summary>
        /// The fields every machine's container needs, each of which breaks the piece in a way that is not
        /// obvious from the Unity inspector:
        ///  - Private calls m_piece.GetCreator(), and m_piece is null on a child object.
        ///  - autoDestroyEmpty would nview.Destroy() the SHARED root view, deleting the whole piece the moment
        ///    the container ran empty.
        ///  - any discover stat other than None invokes an RPC vanilla registers under a different name
        ///    ("RPC_Discovered"), logging an unknown-RPC error on every open.
        /// </summary>
        internal static void Finish(Container box, ZNetView rootOverride, string nameToken) {
            if (rootOverride != null) { box.m_rootObjectOverride = rootOverride; }
            box.m_name = nameToken;
            box.m_checkGuardStone = true;
            box.m_privacy = Container.PrivacySetting.Public;
            box.m_autoDestroyEmpty = false;
            box.m_discoverStat = PlayerStatType.None;
        }

        private static void ApplySize(Container box, int rows, int minRows, int maxRows) {
            box.m_width = Width;
            box.m_height = Mathf.Clamp(rows, minRows, maxRows);

            Inventory inv = box.GetInventory();
            if (inv == null) { return; }   // prefab, or an instance whose Awake has not run yet
            // Never fewer rows than its items sit in: a lowered setting would hide them, and the store read
            // as full, until a reload grew the grid back - and an unchanged one is not reloaded.
            int height = box.m_height;
            foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                if (item != null) { height = Mathf.Max(height, item.m_gridPos.y + 1); }
            }
            inv.m_width = Width;
            inv.SetHeight(height);
        }
    }
}
