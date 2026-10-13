using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// How a Grinder and its hopper find each other: by where they stand, never by ZDOID.
    ///
    /// Valheim gives every object a new ZDOID each time the world is loaded (<c>ZDO.Load</c>), so an ID one
    /// object keeps of another points at nothing after a restart. Vanilla re-links its own pairs (portals,
    /// spawners) through ZDO connections, but those hold one link per object and are rebuilt only for the
    /// kinds vanilla knows. A Grinder's hopper always stands at the Grinder's FeedPoint, turned the same
    /// way, and neither ever moves, so each finds the other there: the hopper at the FeedPoint, the Grinder
    /// at the hopper less the FeedPoint's offset. The FeedPoint is straight above the Grinder's origin, so
    /// the two are always in the same zone, and a client that has one's ZDO has the other's.
    /// </summary>
    internal static class GrinderLink {
        internal const string GrinderPrefab = "DA_Grinder";

        // Metres: far closer than two Grinders' hoppers can ever stand to each other.
        private const float Tolerance = 0.25f;

        internal static readonly int GrinderHash = GrinderPrefab.GetStableHashCode();
        internal static readonly int FeedHash = GrinderFeed.PrefabName.GetStableHashCode();

        private static readonly List<ZDO> scratch = new List<ZDO>();
        private static readonly HashSet<ZoneSystem.SectorIndex> visited = new HashSet<ZoneSystem.SectorIndex>();
        private static bool haveOffset;
        private static Vector3 feedOffset;

        internal static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < Tolerance * Tolerance;

        /// <summary>
        /// Adds to <paramref name="into"/> the ZDOs of one prefab that this client has at a point. Looks
        /// through every object in the point's zone, so callers keep it off the per-tick path.
        /// </summary>
        internal static void ZdosAt(Vector3 at, int prefabHash, List<ZDO> into) {
            if (ZDOMan.instance == null) { return; }
            scratch.Clear();
            visited.Clear();
            ZDOMan.instance.FindObjects(ZoneSystem.GetZone(at), scratch, visited);
            foreach (ZDO zdo in scratch) {
                if (zdo != null && zdo.GetPrefab() == prefabHash && Near(zdo.GetPosition(), at)) { into.Add(zdo); }
            }
            scratch.Clear();
        }

        /// <summary>
        /// Where a hopper's Grinder stands, from the hopper's own position and rotation and the FeedPoint's
        /// place in the Grinder prefab. False while the prefab is not registered.
        /// </summary>
        internal static bool GrinderPosition(Transform hopper, out Vector3 at) {
            at = Vector3.zero;
            if (!haveOffset) {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(GrinderHash) : null;
                GrinderHub hub = prefab != null ? prefab.GetComponent<GrinderHub>() : null;
                if (hub == null || hub.FeedPoint == null) { return false; }
                feedOffset = prefab.transform.InverseTransformPoint(hub.FeedPoint.position);
                haveOffset = true;
            }
            at = hopper.position - hopper.rotation * feedOffset;
            return true;
        }
    }
}
