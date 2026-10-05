using System;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Marks a Harmony patch class as part of shared chests. These are left unapplied when
    /// MultiUserChest is installed: it does the same job, and both mods rewrite the same call in
    /// <c>InventoryGui.UpdateContainer</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class SharedChestPatchAttribute : Attribute { }

    /// <summary>
    /// Several players in one chest at once.
    ///
    /// Vanilla gives a chest to whoever opens it and turns everyone else away while it is open. Here
    /// the first player to open a chest still becomes its owner, but anyone who opens it after that is
    /// let in as a guest: their panel shows the chest, kept up to date from its ZDO, and every move they
    /// make in it is sent to the owner to carry out (see <see cref="SharedChestRequests"/> and
    /// <see cref="SharedChestOps"/>). Ownership never moves because of a guest, so there is only ever
    /// one copy being written.
    ///
    /// Credit for the idea goes to MultiUserChest by MSchmoecker, which showed that a chest could be
    /// shared this way and was the reference for how one should behave. This module is written
    /// separately and works differently underneath:
    ///  - a guest's moves are picked up at the handful of methods the inventory screen funnels them
    ///    through, which say outright what is moving, from where and to which slot. MultiUserChest
    ///    listens lower down, on <c>Inventory.AddItem</c>, and works the move out from there;
    ///  - anything else that tries to write a guest's copy of the chest is refused outright (see
    ///    <see cref="SharedChestGuard"/>) rather than let through to be overwritten by the next reload;
    ///  - requests travel on <see cref="StorageRpc"/>, the same channel crafting from storage uses,
    ///    which notices an owner that has left and puts back whatever was on its way;
    ///  - a request that names a slot says what it expects to find there, and items are packed by the
    ///    game's own inventory serialiser rather than field by field.
    /// </summary>
    internal static class SharedChests {
        // MultiUserChest's opt-out flag, honoured here too so one marking keeps a container exclusive
        // under either mod.
        private static readonly int IgnoreHash = "MUC_Ignore".GetStableHashCode();

        // The part of IsShareable that depends only on how the piece is built, for the one container
        // that is asked about every frame while its panel is open.
        private static Container lastChecked;
        private static bool lastBuiltToShare;

        /// <summary>On for everyone or no one: the setting is the server's, and MultiUserChest takes over when present.</summary>
        internal static bool Active => !MultiUserChestIntegration.Present && ValConfig.SharedChestsEnabled.Value;

        /// <summary>
        /// Whether a container may have guests. Storage pieces, boat holds and cart beds; not the
        /// AutoSorter's deposit box or the Hopper's store, which are worked by code that runs only for
        /// their owner; and not a networked object carrying more than one container, where a request
        /// could not say which it meant.
        /// </summary>
        internal static bool IsShareable(Container container) {
            if (container == null) { return false; }
            ZNetView nview = container.m_nview;
            if (nview == null || !nview.IsValid()) { return false; }
            if (!ReferenceEquals(container, lastChecked)) {
                lastChecked = container;
                lastBuiltToShare = BuiltToShare(container, nview);
            }
            return lastBuiltToShare && !nview.GetZDO().GetBool(IgnoreHash);
        }

        private static bool BuiltToShare(Container container, ZNetView nview) {
            GameObject root = nview.gameObject;
            if (root.GetComponent<Piece>() == null && root.GetComponent<Ship>() == null && root.GetComponent<Vagon>() == null) { return false; }
            if (container.GetComponentInParent<AutomationHub>() != null || container.GetComponentInParent<HopperHub>() != null) { return false; }
            int sharing = 0;
            foreach (Container other in root.GetComponentsInChildren<Container>(includeInactive: true)) {
                if (other.m_nview == nview) { ++sharing; }
            }
            return sharing == 1;
        }

        /// <summary>Stands in for <c>Container.IsOwner</c> where the inventory screen decides whether to draw the chest.</summary>
        internal static bool CanView(Container container) {
            return container.IsOwner() || (Active && IsShareable(container));
        }

        /// <summary>The chest the local player has open as a guest, or null: none open, or it is theirs.</summary>
        internal static Container GuestContainer() {
            InventoryGui gui = InventoryGui.m_instance;
            if ((object)gui == null) { return null; }
            Container container = gui.m_currentContainer;
            if ((object)container == null || !Active) { return null; }
            if (!IsShareable(container) || container.m_nview.IsOwner()) { return null; }
            return container;
        }

        /// <summary>
        /// Whether an inventory is the local player's read-only copy of a chest someone else owns. Asked
        /// on every inventory write in the game, so it turns nearly all of them away on one comparison.
        /// </summary>
        internal static bool IsGuestInventory(Inventory inventory) {
            InventoryGui gui = InventoryGui.m_instance;
            if ((object)gui == null) { return false; }
            Container container = gui.m_currentContainer;
            if ((object)container == null || container.m_inventory != inventory) { return false; }
            return GuestContainer() == container;
        }
    }
}
