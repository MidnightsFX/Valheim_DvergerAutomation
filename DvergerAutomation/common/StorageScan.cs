using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Finds the storage around a point that a player could use, and answers whether they may, for every
    /// piece that draws on nearby storage: the AutoSorter's crafting pool and the Seeking Table's listing.
    ///
    /// Storage is found by walking <see cref="StorageRegistry"/>, which holds every loaded container with
    /// its kind already worked out, rather than by overlapping the physics scene: a base has far more
    /// colliders than chests, and each of those would have needed a hierarchy walk to tell.
    /// </summary>
    internal static class StorageScan {
        private static readonly List<Container> Nearby = new List<Container>();
        private static readonly List<Container> ViewScratch = new List<Container>();

        /// <summary>This client's player ID, or 0 where there is no profile (a dedicated server).</summary>
        internal static long LocalPlayerId() {
            return (Game.instance != null && Game.instance.GetPlayerProfile() != null)
                ? Game.instance.GetPlayerProfile().GetPlayerID()
                : 0L;
        }

        /// <summary>
        /// Whether no ward keeps <paramref name="playerId"/> out of <paramref name="point"/>: vanilla's
        /// <c>PrivateArea.CheckAccess</c> rule, for any player rather than only the local one. That makes it
        /// safe on a dedicated server, where vanilla's dereferences a local player there is not, and it
        /// allocates nothing. Checks that should flash the ward at the player keep vanilla's.
        /// </summary>
        internal static bool Permits(Vector3 point, long playerId) {
            bool covered = false;
            foreach (PrivateArea area in PrivateArea.m_allAreas) {
                if (area == null || !area.IsEnabled() || !area.IsInside(point, 0f)) { continue; }
                // Any one ward that lets the player in is enough, as in vanilla.
                if ((area.m_piece != null && area.m_piece.GetCreator() == playerId) || area.IsPermitted(playerId)) { return true; }
                covered = true;
            }
            return !covered;
        }

        /// <summary>
        /// A container is usable only if the player can freely access it: not Private/Group-locked to
        /// someone else, and not inside a ward they are not permitted in (their own ward passes).
        /// </summary>
        internal static bool IsAccessible(Container container, long playerId) {
            if (container.m_inventory == null) { return false; } // Container.Awake not run yet.
            if (!container.CheckAccess(playerId)) { return false; }
            return Permits(container.transform.position, playerId);
        }

        /// <summary>
        /// Adds the storage within <paramref name="radius"/> of <paramref name="center"/> that
        /// <paramref name="playerId"/> may open: chests to <paramref name="chests"/>, boat holds and cart
        /// beds to <paramref name="vehicles"/> (either list may be null to skip that half).
        ///
        /// Never an AutoSorter's deposit box or a Grinder's hopper, which hold items waiting to be sorted or
        /// ground rather than storage. A Hopper's store only with <paramref name="hopperStores"/>.
        /// </summary>
        internal static void Collect(Vector3 center, float radius, long playerId,
            List<Container> chests, bool hopperStores, List<Container> vehicles, bool boats, bool carts) {
            Nearby.Clear();
            StorageRegistry.InRange(center, radius, Nearby);
            foreach (Container container in Nearby) {
                switch (StorageRegistry.KindOf(container)) {
                    case StorageRegistry.Kind.Chest:
                    case StorageRegistry.Kind.LighterTank:
                    case StorageRegistry.Kind.GrinderStore:
                        if (chests != null && IsAccessible(container, playerId)) { chests.Add(container); }
                        break;
                    case StorageRegistry.Kind.HopperStore:
                        if (chests != null && hopperStores && IsAccessible(container, playerId)) { chests.Add(container); }
                        break;
                    case StorageRegistry.Kind.Vehicle:
                        if (vehicles == null) { break; }
                        MonoBehaviour carrier = StorageRegistry.HolderOf<MonoBehaviour>(container);
                        bool wanted = carrier is Ship ? boats : carrier is Vagon && carts;
                        if (wanted && IsAccessible(container, playerId)) { vehicles.Add(container); }
                        break;
                }
            }
            Nearby.Clear();
        }

        /// <summary>
        /// Finds the storage on a networked object, for a request made on behalf of
        /// <paramref name="playerId"/>. Only the kinds of storage the pools hold: storage pieces (chests,
        /// the machines' stores), boat holds and cart beds. Never a creature carrying a container, and never
        /// an AutoSorter, whose box is its user's alone. False too when the player could not open it.
        /// </summary>
        internal static bool TryResolveStorage(GameObject root, long playerId, out Container storage) {
            storage = null;
            if (root.GetComponent<Ship>() == null && root.GetComponent<Vagon>() == null && root.GetComponent<Piece>() == null) { return false; }
            if (root.GetComponent<AutomationHub>() != null) { return false; }

            ZNetView nview = root.GetComponent<ZNetView>();
            root.GetComponentsInChildren(true, ViewScratch);
            foreach (Container container in ViewScratch) {
                if (container.m_nview != nview) { continue; }
                // Vanilla's open request checks the same thing: a private chest goes only to its builder.
                if (!container.CheckAccess(playerId)) {
                    storage = null;
                    break;
                }
                if (storage == null) { storage = container; }
            }
            ViewScratch.Clear();
            return storage != null;
        }

        /// <summary>The storage with this ZDO, when it is loaded here and the local player could open it.</summary>
        internal static Container FindStorage(ZDOID id) {
            if (ZNetScene.instance == null) { return null; }
            GameObject root = ZNetScene.instance.FindInstance(id);
            if (root == null) { return null; }
            return TryResolveStorage(root, LocalPlayerId(), out Container storage) ? storage : null;
        }
    }
}
