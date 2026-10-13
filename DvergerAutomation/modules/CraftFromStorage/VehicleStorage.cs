using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Boat holds and cart beds as storage. They are found like any chest (see <see cref="StorageScan"/>),
    /// and lend to crafting and the Seeking Table only - auto-store never files into one. What sets them
    /// apart is that each shares its ZDO with the vehicle carrying it.
    /// </summary>
    internal static class VehicleStorage {
        /// <summary>
        /// True when taking this container would take a vehicle out from under someone. A hold shares its
        /// ZDO with the vehicle it rides on, so owning the hold hands the whole vehicle's simulation to
        /// this client: a cart being pulled detaches from the player pulling it, and a boat with people
        /// aboard stutters until <c>Ship.UpdateOwner</c> hands it back. The owner refuses to hand over a
        /// vehicle in use too (see <see cref="StorageOwnership"/>); this just saves asking. Crafting and
        /// building get at such a hold without moving it, by having its owner take the materials out
        /// (see <see cref="StorageReserve"/>). False for anything that is not vehicle storage.
        /// </summary>
        internal static bool InUse(Container container) {
            if (StorageRegistry.KindOf(container) != StorageRegistry.Kind.Vehicle) { return false; }
            // Already simulated here, so claiming it is a no-op and there is no one to disturb - this is
            // what lets you craft out of the cart you are pulling yourself.
            if (container.m_nview.IsOwner()) { return false; }
            MonoBehaviour carrier = StorageRegistry.HolderOf<MonoBehaviour>(container);
            return carrier == null || CarrierInUse(carrier);
        }

        /// <summary>Whether someone is aboard a boat, or pulling or riding a cart.</summary>
        internal static bool CarrierInUse(MonoBehaviour carrier) {
            if (carrier is Ship ship) { return ship.HasPlayerOnboard(); }
            if (carrier is Vagon cart) { return cart.IsAttached() || (cart.m_chair != null && cart.m_chair.IsInUse()); }
            return false;
        }
    }
}
