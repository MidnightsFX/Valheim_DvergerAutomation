using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// MonoBehaviour for the DA_SeekingTable piece, added to the prefab in Unity next to its
    /// <see cref="CoreSockets"/>. Using it opens the player's inventory with the table's panel where a
    /// chest's would be (<see cref="SeekingTablePanel"/>): everything in the chests around it the player
    /// could open themselves, to search and take from. Black Cores in its pillars reach further.
    ///
    /// The table owns nothing and writes nothing: the listing is read from each chest's own copy, and a
    /// take is asked of each chest's owner (see <see cref="SeekingRetrieval"/>). Its only network traffic of
    /// its own is the retrieval line, broadcast so everyone nearby sees where an item came from.
    /// </summary>
    public class SeekingTable : MonoBehaviour, Hoverable, Interactable {
        /// <summary>Where the orb floats; the retrieval line starts here.</summary>
        public Transform OrbPoint;
        /// <summary>The orb and anything else shown while the table is awake.</summary>
        public GameObject EnabledVisuals;

        private const string BeamRpc = "DA_SeekBeam";
        private const string NameToken = "$DA_seek_name";

        private ZNetView nview;
        private CoreSockets sockets;

        private void Awake() {
            nview = GetComponent<ZNetView>();
            sockets = GetComponent<CoreSockets>();
            if (sockets != null) {
                sockets.SetHoverName(NameToken);
                sockets.StatusLine = StatusLine;
                sockets.Changed += OnCoresChanged;
            }
            if (nview != null && nview.GetZDO() != null) {
                nview.Register<Vector3>(BeamRpc, RPC_Beam);
            }
            InvokeRepeating(nameof(UpdateVisuals), 1f, 4f);
        }

        private void OnDestroy() {
            if (sockets != null) { sockets.Changed -= OnCoresChanged; }
        }

        internal int CoreCount => sockets != null ? sockets.Count : 0;

        internal int SlotCount => sockets != null ? sockets.SlotCount : 0;

        /// <summary>Base radius plus the configured bonus for each inserted Black Core.</summary>
        internal float EffectiveRadius => ValConfig.SeekRadius.Value + CoreCount * ValConfig.SeekRangePerCore.Value;

        /// <summary>Lists anything at all: switched on, and fed a core when the server asks for one.</summary>
        internal bool IsAwake => ValConfig.SeekEnabled.Value && (!ValConfig.SeekRequireCores.Value || CoreCount > 0);

        internal bool IsLive => nview != null && nview.IsValid();

        internal Vector3 BeamOrigin => OrbPoint != null ? OrbPoint.position : transform.position + Vector3.up * 1.9f;

        private void OnCoresChanged() {
            UpdateVisuals();
            SeekingIndex.RescanSoon(this);
        }

        private void UpdateVisuals() {
            if (!IsLive) { return; }
            if (EnabledVisuals != null && EnabledVisuals.activeSelf != IsAwake) { EnabledVisuals.SetActive(IsAwake); }
        }

        // ---- Hoverable / Interactable ---------------------------------------------

        public string GetHoverText() {
            if (!IsLive) { return ""; }
            string text = StatusLine();
            if (!PrivateArea.CheckAccess(transform.position, 0f, flash: false)) {
                return Localization.instance.Localize(text + "\n$piece_noaccess");
            }
            text += IsAwake
                ? "\n[<color=yellow><b>$KEY_Use</b></color>] $DA_seek_use"
                : "\n$DA_seek_dormant";
            return Localization.instance.Localize(text);
        }

        public string GetHoverName() {
            return Localization.instance.Localize(NameToken);
        }

        public float GetHoverOffset() {
            return 0f;
        }

        public bool Interact(Humanoid user, bool hold, bool alt) {
            if (hold || !IsLive) { return false; }
            if (!(user is Player player) || player != Player.m_localPlayer) { return false; }
            // Flashes the ward, as any locked piece does.
            if (!PrivateArea.CheckAccess(transform.position)) { return false; }
            if (!IsAwake) {
                player.Message(MessageHud.MessageType.Center, ValConfig.SeekEnabled.Value ? "$DA_seek_dormant" : "$DA_seek_disabled");
                return false;
            }
            SeekingTablePanel.Open(this);
            return true;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) {
            return false;
        }

        private string StatusLine() {
            return Localization.instance.Localize("$DA_seek_status",
                CoreCount.ToString(), SlotCount.ToString(), Mathf.RoundToInt(EffectiveRadius).ToString());
        }

        // ---- the retrieval line ------------------------------------------------------

        /// <summary>Draws the line from the orb to <paramref name="target"/> for everyone nearby.</summary>
        internal void ShowRetrieval(Vector3 target) {
            if (!IsLive) { return; }
            nview.InvokeRPC(ZNetView.Everybody, BeamRpc, target);
        }

        private void RPC_Beam(long sender, Vector3 target) {
            if (!ValConfig.SeekShowBeam.Value) { return; }
            RetrievalBeam.Play(BeamOrigin, target, ValConfig.SeekBeamSeconds.Value);
        }
    }
}
