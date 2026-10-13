using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Brings a fire, torch, oven or hot tub over to the client running the Lighter that wants to fill it,
    /// as <see cref="HopperNetwork"/> brings smelters to their hopper: the Lighter only ever fills a piece it
    /// owns (see <see cref="LighterHub"/>), so a piece running on another player's game is asked for, and
    /// filled on a later pass once it has arrived.
    /// </summary>
    internal static class LighterNetwork {
        // A handoff takes a round trip, and a piece whose owner keeps it for its own Lighter is asked again
        // less and less often, up to this.
        private const float RequestMaxInterval = 30f;

        private static readonly OwnerHandover Handover = new OwnerHandover("DA_LighterPieceRequest", "Lighter", KeepPiece, RequestMaxInterval);

        internal static void RegisterRpc(ZRoutedRpc rpc) {
            Handover.Register(rpc);
        }

        /// <summary>
        /// Asks the owner of a piece this client's Lighter wants to fill to hand it over. Rate-limited per
        /// piece, so it is cheap to call every pass.
        /// </summary>
        internal static void AskFor(ZNetView view) {
            Handover.Ask(view);
        }

        // Kept when a Lighter here would fill it too, or two Lighters covering one fire would pass it back
        // and forth. A hot tub is also kept while a Hopper here feeds it (a Hopper can be told to): the
        // Hopper hands over only for its own kind, so without this the two would trade it every few seconds.
        private static bool KeepPiece(GameObject root) {
            Component burner = FuelBurners.Find(root);
            if (burner == null) { return true; }
            return LighterHub.FedHere(burner) || (burner is Smelter tub && HopperNetwork.FedHere(tub));
        }
    }
}
