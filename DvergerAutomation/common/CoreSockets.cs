using System;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// A row of item sockets on a piece - each a <see cref="Switch"/> that takes one core and hands it back
    /// - kept as a bitmask on the piece's ZDO. Added to the prefab in Unity, next to the piece's own
    /// component, which reads <see cref="Count"/> and listens to <see cref="Changed"/>. Every piece with
    /// cores uses it: the AutoSorter's and the Hopper's Surtling Cores, the Seeking Table's Black Cores and
    /// the Grinder's Molten Cores.
    ///
    /// The mask shares the piece's ZDO with whatever else its owner writes there - a container's items, a
    /// queue - and ZDO data syncs as one revision per object, so a player claiming the piece to flip a
    /// socket would race the owner's writes and one save would roll the other back. So the player using a
    /// socket asks the piece's owner, the owner flips the bit and answers, and only then does the core move
    /// (<see cref="Settle"/>). A request that is lost or finds the socket already changed costs nothing. On
    /// top of that, an insert whose core turns out to be gone from the pack by the time the answer lands -
    /// dropped or spent while the request was out - is taken back without handing a core out.
    /// </summary>
    public class CoreSockets : MonoBehaviour {
        public Switch[] Switches;
        /// <summary>Shown while the socket of the same index holds a core: the core itself, typically.</summary>
        public GameObject[] SlotVisuals;
        /// <summary>A second visual per socket, for one that lives elsewhere in the hierarchy - an effect reaching out to it.</summary>
        public GameObject[] SlotEffects;
        /// <summary>Shown while at least one socket is filled.</summary>
        public GameObject ActiveVisuals;

        /// <summary>The item prefab a socket takes.</summary>
        public string ItemPrefab = "BlackCore";
        /// <summary>
        /// The ZDO key holding the bitmask; the RPC names are built from it, so it must be unique per piece
        /// type. Never renamed once a piece has shipped: worlds keep their cores under it.
        /// </summary>
        public string MaskKey = "DA_seek_cores";
        /// <summary>The switches' hover name, normally the piece's own name token.</summary>
        public string HoverName = "";
        public string AddMessage = "$DA_Add_BlackCore";
        public string RemoveMessage = "$DA_Remove_BlackCore";
        public string NeedMessage = "$DA_Need_BlackCore";

        /// <summary>Raised on every client whenever a socket changes, after the visuals are updated.</summary>
        internal event Action Changed;

        /// <summary>A line about the piece shown above each socket's action; set by the piece.</summary>
        internal Func<string> StatusLine;

        private ZNetView nview;
        // MaskKey hashed once: the mask is read every frame wherever a piece shows whether it is awake.
        private int maskHash;
        private string requestRpc;
        private string resultRpc;
        private string revokeRpc;
        private string refreshRpc;

        private void Awake() {
            nview = GetComponentInParent<ZNetView>();
            maskHash = MaskKey.GetStableHashCode();
            requestRpc = MaskKey + "_request";
            resultRpc = MaskKey + "_result";
            revokeRpc = MaskKey + "_revoke";
            refreshRpc = MaskKey + "_refresh";

            if (nview != null && nview.GetZDO() != null) {
                nview.Register<int, bool>(requestRpc, RPC_Request);
                nview.Register<int, bool>(resultRpc, RPC_Result);
                nview.Register<int>(revokeRpc, RPC_Revoke);
                nview.Register(refreshRpc, new Action<long>(RPC_Refresh));
                WearNTear wnt = GetComponentInParent<WearNTear>();
                if (wnt != null) { wnt.m_onDestroyed += OnDestroyedDropCores; }
            }

            // The Switch callbacks are C# delegates and cannot be assigned in Unity, so wire them here.
            if (Switches != null) {
                for (int slot = 0; slot < Switches.Length; ++slot) { WireSwitch(slot); }
            }

            // Shortly after load, and now and then, so a late ZDO sync still lights the sockets.
            InvokeRepeating(nameof(UpdateVisuals), 1f, 4f);
        }

        internal int SlotCount => Switches != null ? Switches.Length : 0;

        private int Mask {
            get {
                if (nview == null || !nview.IsValid()) { return 0; }
                return nview.GetZDO().GetInt(maskHash, 0);
            }
        }

        /// <summary>How many sockets hold a core.</summary>
        internal int Count {
            get {
                int mask = Mask;
                int count = 0;
                for (int slot = 0; slot < SlotCount; ++slot) {
                    if ((mask & (1 << slot)) != 0) { ++count; }
                }
                return count;
            }
        }

        private bool IsFilled(int slot) => (Mask & (1 << slot)) != 0;

        private GameObject FindItemPrefab() {
            return ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(ItemPrefab) : null;
        }

        private static string SharedName(GameObject prefab) {
            return prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
        }

        // ---- the owner ----------------------------------------------------------

        // A request that finds the socket already as asked - two players at once - changes nothing and is
        // not answered.
        private void RPC_Request(long sender, int slot, bool insert) {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            if (slot < 0 || slot >= SlotCount) { return; }
            int mask = Mask;
            int bit = 1 << slot;
            if (((mask & bit) != 0) == insert) { return; }

            nview.GetZDO().Set(maskHash, insert ? mask | bit : mask & ~bit);
            nview.InvokeRPC(sender, resultRpc, slot, insert);
            // Everybody includes this client.
            nview.InvokeRPC(ZNetView.Everybody, refreshRpc);
        }

        // An insert the requester could not pay for. Emptied without an answer, so no core is handed out.
        private void RPC_Revoke(long sender, int slot) {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            if (slot < 0 || slot >= SlotCount || !IsFilled(slot)) { return; }
            nview.GetZDO().Set(maskHash, Mask & ~(1 << slot));
            nview.InvokeRPC(ZNetView.Everybody, refreshRpc);
        }

        // ---- the requester ------------------------------------------------------

        private void RPC_Result(long sender, int slot, bool inserted) {
            if (!Settle(inserted)) {
                nview.InvokeRPC(revokeRpc, slot);
            }
        }

        /// <summary>
        /// Once the owner has changed the socket: an insert takes the core from the pack; a removal returns
        /// it there, or at the player's feet if the pack filled up in the meantime. False only for an insert
        /// that could not be paid for - its core is no longer in the pack, or there is no player to take it
        /// from (they died while the request was out) - so the socket was filled for nothing and has to be
        /// emptied.
        /// </summary>
        private bool Settle(bool inserted) {
            Player player = Player.m_localPlayer;
            GameObject prefab = FindItemPrefab();
            if (prefab == null) { return true; }
            if (player == null) {
                if (inserted) { return false; }
                // Taken out of its socket for a player who has died since: it waits for them to come back.
                ItemDrop.ItemData core = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
                core.m_stack = 1;
                core.m_dropPrefab = prefab;
                core.m_worldLevel = Game.m_worldLevel;
                PlayerDelivery.Give(core);
                return true;
            }

            Inventory inv = player.GetInventory();
            if (inserted) {
                string name = SharedName(prefab);
                if (inv.CountItems(name) <= 0) { return false; }
                inv.RemoveItem(name, 1);
                player.Message(MessageHud.MessageType.Center, AddMessage);
            } else {
                if (!inv.AddItem(prefab, 1)) {
                    Vector3 pos = player.transform.position + Vector3.up * 0.5f;
                    ItemDrop.OnCreateNew(Instantiate(prefab, pos, Quaternion.identity));
                }
                player.Message(MessageHud.MessageType.Center, RemoveMessage);
            }
            return true;
        }

        private void RPC_Refresh(long sender) {
            UpdateVisuals();
            Changed?.Invoke();
        }

        private void WireSwitch(int slot) {
            Switch sw = Switches[slot];
            if (sw == null) { return; }
            sw.m_onUse = OnSwitchUsed;
            sw.m_onHover = () => GetSwitchHover(slot);
            sw.m_name = HoverName;
        }

        /// <summary>
        /// Names every socket after the piece. For a piece whose own Awake may run after this one's, which
        /// Unity does not order.
        /// </summary>
        internal void SetHoverName(string name) {
            HoverName = name;
            if (Switches == null) { return; }
            foreach (Switch sw in Switches) {
                if (sw != null) { sw.m_name = name; }
            }
        }

        private int SlotFor(Switch caller) {
            return Switches != null ? Array.IndexOf(Switches, caller) : -1;
        }

        // Use on an empty socket inserts a core from the pack; use on a filled one takes it out. Using the
        // core itself from the hotbar on an empty socket inserts it too. Any other item is not taken.
        private bool OnSwitchUsed(Switch caller, Humanoid user, ItemDrop.ItemData item) {
            int slot = SlotFor(caller);
            if (slot < 0 || !(user is Player player)) { return false; }
            if (!PrivateArea.CheckAccess(transform.position)) { return false; }
            if (nview == null || !nview.IsValid()) { return false; }

            GameObject prefab = FindItemPrefab();
            if (prefab == null) { return false; }
            string coreName = SharedName(prefab);

            bool filled = IsFilled(slot);
            if (item != null && (filled || item.m_shared.m_name != coreName)) { return false; }
            if (filled) {
                if (!player.GetInventory().CanAddItem(prefab, 1)) {
                    user.Message(MessageHud.MessageType.Center, "$inventory_full");
                    return false;
                }
            } else if (player.GetInventory().CountItems(coreName) <= 0) {
                user.Message(MessageHud.MessageType.Center, NeedMessage);
                return false;
            }

            // When this client is the owner both RPCs run synchronously.
            nview.InvokeRPC(requestRpc, slot, !filled);
            return true;
        }

        private string GetSwitchHover(int slot) {
            string action = IsFilled(slot) ? RemoveMessage : AddMessage;
            string status = StatusLine != null ? StatusLine() + "\n" : "";
            return Localization.instance.Localize(status + "[<color=yellow><b>$KEY_Use</b></color>] " + action);
        }

        private void UpdateVisuals() {
            if (nview == null || !nview.IsValid()) { return; }
            int mask = Mask;
            for (int slot = 0; slot < SlotCount; ++slot) {
                bool filled = (mask & (1 << slot)) != 0;
                if (SlotVisuals != null && slot < SlotVisuals.Length) { SetActiveSafe(SlotVisuals[slot], filled); }
                if (SlotEffects != null && slot < SlotEffects.Length) { SetActiveSafe(SlotEffects[slot], filled); }
            }
            SetActiveSafe(ActiveVisuals, mask != 0);
        }

        private static void SetActiveSafe(GameObject go, bool active) {
            if (go != null && go.activeSelf != active) { go.SetActive(active); }
        }

        // Taking the piece down hands the cores back where it stood.
        private void OnDestroyedDropCores() {
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            int count = Count;
            GameObject prefab = FindItemPrefab();
            if (count <= 0 || prefab == null) { return; }
            for (int i = 0; i < count; ++i) {
                Vector3 pos = transform.position + Vector3.up * 0.5f + UnityEngine.Random.insideUnitSphere * 0.3f;
                // OnCreateNew stamps the world's level on the core, as any freshly made item gets: without it
                // the core keeps the prefab's, and on a harder world no socket would count it as one again.
                ItemDrop.OnCreateNew(Instantiate(prefab, pos, UnityEngine.Random.rotation));
            }
        }
    }
}
