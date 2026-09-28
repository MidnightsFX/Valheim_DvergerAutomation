using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// The Surtling Core as the AutoSorter's and the Hopper's core sockets use it.
    ///
    /// Both sockets live on their piece's ZDO beside a container, and ZDO data syncs as one revision per
    /// object, so a player claiming the piece to flip a socket would race the owner's container writes and
    /// one save would roll the other back. So both follow the same exchange: the player using the socket
    /// asks the owner, the owner flips it and answers, and only then does the core move
    /// (<see cref="Settle"/>). A request that is lost or finds the socket already changed costs nothing.
    /// </summary>
    internal static class SurtlingCore {
        private const string PrefabName = "SurtlingCore";

        internal static GameObject Prefab => ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(PrefabName) : null;

        internal static string SharedName(GameObject prefab) {
            return prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
        }

        /// <summary>
        /// Requester side, once the owner has changed the socket: an insert takes the core from the pack;
        /// a removal returns it there, or at the player's feet if the pack filled up in the meantime.
        /// </summary>
        internal static void Settle(bool inserted) {
            Player player = Player.m_localPlayer;
            GameObject prefab = Prefab;
            if (player == null || prefab == null) { return; }

            if (inserted) {
                player.GetInventory().RemoveItem(SharedName(prefab), 1);
                player.Message(MessageHud.MessageType.Center, "$DA_Add_Core");
            } else {
                if (!player.GetInventory().AddItem(prefab, 1)) {
                    Vector3 pos = player.transform.position + Vector3.up * 0.5f;
                    ItemDrop.OnCreateNew(Object.Instantiate(prefab, pos, Quaternion.identity));
                }
                player.Message(MessageHud.MessageType.Center, "$DA_Remove_Core");
            }
        }
    }
}
