using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Finds the chests around a point that a player could use, for every piece that draws on nearby
    /// storage: the AutoSorter's crafting pool and the Seeking Table's listing.
    ///
    /// Chests are found by overlapping the piece layers rather than walking every loaded Container: a
    /// base has far more pieces than chests, but the overlap is answered by the physics scene's own
    /// spatial index, and only the colliders in range are looked at. Boats and carts are not on those
    /// layers and are collected separately (see <see cref="VehicleStorage"/>). The Hopper finds its
    /// stations with the same overlap (<see cref="OverlapPieces"/>).
    /// </summary>
    internal static class StorageScan {
        private static int pieceMask;
        // Reused across scans and doubled whenever an overlap fills it, so a big base settles on a buffer
        // that fits it after a scan or two and the scans stop allocating.
        private static Collider[] hits = new Collider[512];
        private static readonly HashSet<Container> Seen = new HashSet<Container>();

        /// <summary>
        /// Every collider on the piece layers within <paramref name="radius"/> of <paramref name="center"/>:
        /// the first <paramref name="count"/> entries of the returned buffer. The buffer is shared by every
        /// scan, so read it before the next overlap and hand it back with <see cref="ReleaseOverlap"/>.
        /// </summary>
        internal static Collider[] OverlapPieces(Vector3 center, float radius, out int count) {
            if (pieceMask == 0) { pieceMask = LayerMask.GetMask("piece", "piece_nonsolid"); }
            while (true) {
                count = Physics.OverlapSphereNonAlloc(center, radius, hits, pieceMask);
                if (count < hits.Length) { return hits; }
                hits = new Collider[hits.Length * 2];
            }
        }

        /// <summary>
        /// The buffer keeps its references until they are overwritten; letting go of them once a scan is
        /// done means it does not pin destroyed pieces.
        /// </summary>
        internal static void ReleaseOverlap(int count) {
            System.Array.Clear(hits, 0, count);
        }

        /// <summary>
        /// Adds to <paramref name="into"/> every chest within <paramref name="radius"/> of
        /// <paramref name="center"/> that <paramref name="playerId"/> may open and that no ward keeps them
        /// out of. An AutoSorter's deposit box is never one: it holds items waiting to be sorted, not
        /// storage.
        ///
        /// <paramref name="requireResolvable"/> keeps only containers whose owner will serve a request for
        /// them (see <see cref="StorageOwnership.TryResolveStorage"/>), which rules out anything that is not
        /// a built piece. <paramref name="includeHopperStores"/> false leaves out the Hopper's storage.
        /// </summary>
        internal static void CollectChests(Vector3 center, float radius, long playerId, List<Container> into, bool requireResolvable, bool includeHopperStores) {
            Collider[] overlap = OverlapPieces(center, radius, out int count);
            Seen.Clear();
            for (int i = 0; i < count; ++i) {
                Collider hit = overlap[i];
                if (hit == null) { continue; }
                Container container = hit.GetComponentInParent<Container>();
                if (container == null || !Seen.Add(container)) { continue; }
                // An autosorter's own deposit box holds items waiting to be sorted, not storage: never a
                // crafting source, and never a sort target - it holds the very items being sorted, so it
                // would match everything and file them straight back into itself.
                if (container.GetComponentInParent<AutomationHub>() != null) { continue; }
                if (!includeHopperStores && HopperNetwork.IsStore(container)) { continue; }
                if (!AutomationHub.IsAccessible(container, playerId)) { continue; }
                if (requireResolvable && !IsResolvable(container, playerId)) { continue; }
                into.Add(container);
            }
            ReleaseOverlap(count);
        }

        // The owner checks exactly this before carrying out a request, so a container that fails it here
        // would only ever be refused.
        private static bool IsResolvable(Container container, long playerId) {
            if (container.m_nview == null) { return false; }
            return StorageOwnership.TryResolveStorage(container.m_nview.gameObject, playerId, out Container resolved)
                && resolved == container;
        }
    }
}
