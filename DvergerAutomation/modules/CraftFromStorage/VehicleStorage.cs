using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Boat holds and cart beds as AutoSorter crafting sources. The hub's chest scan never finds them: a
    /// boat's hold sits on the "vehicle" layer and a cart's bed on "item", and widening the overlap to
    /// those would sweep every dropped item in range. Both vehicle types keep a live instance list, so
    /// they are enumerated directly instead.
    ///
    /// Vehicles only ever lend to crafting - auto-store never files into one (see AutoStore.Sort).
    /// </summary>
    internal static class VehicleStorage {
        // Reused across ships by Collect; GetComponentsInChildren clears it first.
        private static readonly List<Container> HoldScratch = new List<Container>();

        /// <summary>
        /// Adds every boat hold and cart bed within <paramref name="radius"/> of <paramref name="center"/>
        /// that the player could open, mapped to the vehicle carrying it. Each type is gated by its own
        /// server setting.
        /// </summary>
        internal static void Collect(Vector3 center, float radius, long playerId, Dictionary<Container, MonoBehaviour> into) {
            if (ValConfig.CraftFromBoats.Value) {
                foreach (IMonoUpdater instance in Ship.Instances) {
                    Ship ship = instance as Ship;
                    if (ship == null) { continue; }
                    // Every vanilla boat has one hold, but nothing stops a modded one carrying more.
                    ship.GetComponentsInChildren(HoldScratch);
                    foreach (Container hold in HoldScratch) {
                        Consider(hold, ship, center, radius, playerId, into);
                    }
                }
                HoldScratch.Clear();
            }
            if (ValConfig.CraftFromCarts.Value) {
                foreach (Vagon cart in Vagon.m_instances) {
                    if (cart == null) { continue; }
                    Consider(cart.m_container, cart, center, radius, playerId, into);
                }
            }
        }

        private static void Consider(Container container, MonoBehaviour carrier, Vector3 center, float radius, long playerId, Dictionary<Container, MonoBehaviour> into) {
            if (container == null) { return; }
            if (Vector3.Distance(center, container.transform.position) > radius) { return; }
            if (!AutomationHub.IsAccessible(container, playerId)) { return; }
            into[container] = carrier;
        }

        /// <summary>
        /// True when spending from this container would take a vehicle out from under someone. A hold
        /// shares its ZDO with the vehicle it rides on, so the <c>ClaimOwnership</c> that spending needs
        /// hands the whole vehicle's simulation to this client: a cart being pulled detaches from the
        /// player pulling it, and a boat with people aboard stutters until <c>Ship.UpdateOwner</c> hands
        /// it back. False for anything that is not vehicle storage.
        /// </summary>
        internal static bool InUse(Container container) {
            if (!ContainerNetwork.TryGetCarrier(container, out MonoBehaviour carrier)) { return false; }
            // Already simulated here, so claiming it is a no-op and there is no one to disturb - this is
            // what lets you craft out of the cart you are pulling yourself.
            if (container.m_nview.IsOwner()) { return false; }
            if (carrier == null) { return true; }
            if (carrier is Ship ship) { return ship.HasPlayerOnboard(); }
            if (carrier is Vagon cart) { return cart.IsAttached() || (cart.m_chair != null && cart.m_chair.IsInUse()); }
            return false;
        }
    }
}
