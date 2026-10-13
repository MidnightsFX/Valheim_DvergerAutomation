using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Brings a Grinder's hopper over to the client running the Grinder, and removes it with the Grinder.
    ///
    /// A batch takes an item out of the hopper and puts what it returns into the results chest, so whoever
    /// grinds has to own both: only a container's owner may write it (see <see cref="StorageOwnership"/>).
    /// The results chest lives on the Grinder's own ZDO. The hopper is a separate object, and opening it
    /// hands it to the player who opened it, as any vanilla chest does. So once that player has closed it,
    /// the Grinder's owner asks for it back (see <see cref="OwnerHandover"/>). Never while it is open.
    /// </summary>
    internal static class GrinderOps {
        private const string FeedDisposeRpc = "DA_GrinderFeedDispose";

        // A hopper still open is asked for again at this pace: the lever's pull waits on it, so there is no
        // backing off.
        private const float RequestInterval = 2f;

        private static readonly OwnerHandover FeedHandover = new OwnerHandover("DA_GrinderFeedRequest", "Grinder", KeepFeed, RequestInterval);

        internal static void RegisterRpc(ZRoutedRpc rpc) {
            FeedHandover.Register(rpc);
            rpc.Register<ZDOID>(FeedDisposeRpc, RPC_FeedDispose);
        }

        /// <summary>
        /// Asks the hopper's owner to hand it to this client. Rate-limited per hopper, so it is cheap to call
        /// every tick.
        /// </summary>
        internal static void AskForFeed(GrinderFeed feed) {
            if (feed == null || !feed.IsLive) { return; }
            FeedHandover.Ask(feed.GetComponent<ZNetView>());
        }

        // Someone has it open; pulling it away would blank their panel. Asked again later.
        private static bool KeepFeed(GameObject root) {
            GrinderFeed feed = root.GetComponent<GrinderFeed>();
            return feed == null || feed.Box == null || StorageOwnership.IsBusy(feed.Box);
        }

        /// <summary>
        /// The Grinder is being taken down: its hopper empties where it stands and goes with it. Done here when
        /// this client owns the hopper (or nobody does); otherwise its owner is asked, since only the owner's
        /// destroy reaches everyone. Should the message be lost, the hopper notices on its own once the
        /// Grinder is gone (<see cref="GrinderFeed"/>). Also how a Grinder's owner removes a second hopper.
        /// </summary>
        internal static void DisposeFeed(GrinderFeed feed) {
            if (feed == null || !feed.IsLive) { return; }
            ZNetView nview = feed.GetComponent<ZNetView>();
            ZDO zdo = nview.GetZDO();
            if (zdo.GetOwner() == 0L) { nview.ClaimOwnership(); }
            if (feed.IsOwner) {
                feed.Dispose();
                return;
            }
            if (ZRoutedRpc.instance == null) { return; }
            ZRoutedRpc.instance.InvokeRoutedRPC(zdo.GetOwner(), FeedDisposeRpc, zdo.m_uid);
        }

        // Only for the Grinder's own owner, or once the Grinder is already gone here.
        private static void RPC_FeedDispose(long sender, ZDOID id) {
            try {
                if (ZDOMan.instance == null || ZNetScene.instance == null) { return; }
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsOwner()) { return; }
                GameObject root = ZNetScene.instance.FindInstance(id);
                GrinderFeed feed = root != null ? root.GetComponent<GrinderFeed>() : null;
                if (feed == null) { return; }
                ZDO grinder = feed.GrinderZdo();
                if (grinder != null && grinder.GetOwner() != sender) { return; }
                feed.Dispose();
            } catch (System.Exception ex) {
                Logger.LogError($"[Grinder] removing a hopper failed: {ex}");
            }
        }
    }
}
