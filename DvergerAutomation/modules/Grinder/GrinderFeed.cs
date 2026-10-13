using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// The Grinder's hopper: the container on top that items are put in to be ground. Opened with Use like
    /// any chest.
    ///
    /// It is a networked object of its own (prefab DA_GrinderFeed) rather than a second container on the
    /// Grinder, because a container keeps its inventory under one fixed key on its ZDO and registers its
    /// RPCs under fixed names: two on the same ZDO would overwrite each other. The Grinder spawns it into its
    /// hopper's mouth (<see cref="GrinderHub"/>'s EnsureFeed), and each finds the other by where it stands
    /// (<see cref="GrinderLink"/>), since ZDOIDs do not survive a restart.
    ///
    /// It is not a piece: it cannot be built, damaged or removed on its own. It goes when its Grinder does:
    /// the Grinder's owner asks for that as the Grinder comes down (<see cref="GrinderOps.DisposeFeed"/>).
    /// Should that be missed, whoever owns the hopper notices on its own - no Grinder where its Grinder
    /// should stand for <see cref="OrphanSeconds"/> - and drops what is inside and destroys this object too.
    /// </summary>
    public class GrinderFeed : MonoBehaviour {
        internal const string PrefabName = "DA_GrinderFeed";

        // How long the Grinder's ZDO may be missing before the hopper gives up on it. A client that has just
        // arrived can see this object before its Grinder's ZDO streams in.
        private const float OrphanSeconds = 30f;

        internal static readonly List<GrinderFeed> All = new List<GrinderFeed>();

        private ZNetView nview;
        private float missingSince = -1f;
        private GrinderHub grinder;
        private readonly List<ZDO> grinderScratch = new List<ZDO>();

        /// <summary>The hopper's inventory.</summary>
        internal Container Box { get; private set; }

        internal bool IsLive => nview != null && nview.IsValid();

        internal bool IsOwner => IsLive && nview.IsOwner();

        /// <summary>The Grinder this hopper belongs to, when it is loaded here: the one whose FeedPoint it stands at.</summary>
        internal GrinderHub Grinder {
            get {
                if (IsMine(grinder)) { return grinder; }
                grinder = null;
                foreach (GrinderHub hub in GrinderHub.All) {
                    if (IsMine(hub)) {
                        grinder = hub;
                        break;
                    }
                }
                return grinder;
            }
        }

        private bool IsMine(GrinderHub hub) {
            return hub != null && hub.IsLive && GrinderLink.Near(hub.FeedPosition, transform.position);
        }

        /// <summary>The Grinder's ZDO, loaded or not; null when there is none where it should stand, or that cannot be worked out yet.</summary>
        internal ZDO GrinderZdo() {
            if (!GrinderLink.GrinderPosition(transform, out Vector3 at)) { return null; }
            grinderScratch.Clear();
            GrinderLink.ZdosAt(at, GrinderLink.GrinderHash, grinderScratch);
            ZDO found = grinderScratch.Count > 0 ? grinderScratch[0] : null;
            grinderScratch.Clear();
            return found;
        }

        private void Awake() {
            nview = GetComponent<ZNetView>();
            Box = GetComponent<Container>();
            if (nview != null && nview.GetZDO() != null) {
                InvokeRepeating(nameof(Check), 5f, 5f);
            }
        }

        private void OnEnable() {
            if (!All.Contains(this)) { All.Add(this); }
        }

        private void OnDisable() {
            All.Remove(this);
        }

        private void Check() {
            if (!IsOwner || ZDOMan.instance == null) { return; }
            // Written to by the Grinder's owner while players fill it, so never several players at once.
            MultiUserChestIntegration.KeepExclusive(nview);

            // Where its Grinder should stand cannot be worked out until the Grinder prefab is registered.
            if (!GrinderLink.GrinderPosition(transform, out _)) { return; }
            if (GrinderZdo() != null) {
                missingSince = -1f;
                return;
            }
            if (missingSince < 0f) { missingSince = Time.time; }
            if (Time.time - missingSince <= OrphanSeconds) { return; }

            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Grinder] a hopper outlived its Grinder; dropping what was in it.");
            }
            Dispose();
        }

        /// <summary>
        /// Drops everything in the hopper where it stands and destroys it. Owner only: the destroy has to come
        /// from the owner to reach every client, and the drop from the copy everyone else reloads from.
        /// </summary>
        internal void Dispose() {
            if (!IsOwner || ZNetScene.instance == null) { return; }
            if (Box != null && Box.GetInventory() != null) {
                // The copy everyone else reloads from: up to a second old just after a handover.
                StorageOwnership.Sync(Box);
                Box.DropAllItems();
            }
            ZNetScene.instance.Destroy(gameObject);
        }
    }
}
