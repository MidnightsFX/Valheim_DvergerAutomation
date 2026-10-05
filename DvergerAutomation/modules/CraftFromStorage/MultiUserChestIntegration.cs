using BepInEx.Bootstrap;

namespace DvergerAutomation {
    /// <summary>
    /// Living alongside MultiUserChest, which lets several players have one chest open at once: the
    /// first to open it stays its owner, and everyone after is a guest whose moves are sent to that owner.
    ///
    /// Nothing of MultiUserChest's is called. Crafting from a chest its players have open goes through
    /// this mod's own request to the owner (see <see cref="StorageReserve"/>), which works whether or not
    /// it is installed. What it does need to be told is which containers to stay out of: the AutoSorter's
    /// deposit box and the Hopper's store are each written by code that runs only for their owner - the
    /// sort on closing, the deposit buttons, the Hopper's feed - and a guest in either would be looking at
    /// a panel half of whose buttons do nothing. MultiUserChest treats a container whose ZDO carries
    /// <c>MUC_Ignore</c> as vanilla: one player at a time.
    /// </summary>
    internal static class MultiUserChestIntegration {
        internal const string MultiUserChestGUID = "com.maxsch.valheim.MultiUserChest";

        private const string IgnoreKey = "MUC_Ignore";

        // Resolved once: plugins do not come and go.
        private static bool? present;

        internal static bool Present {
            get {
                if (!present.HasValue) { present = Chainloader.PluginInfos.ContainsKey(MultiUserChestGUID); }
                return present.Value;
            }
        }

        /// <summary>
        /// Marks a container's ZDO as one MultiUserChest should leave to a single player. Cheap enough to
        /// call on a timer, and it has to be: only the ZDO's owner may write it, so a piece gets marked
        /// whenever a client that owns it next comes by.
        /// </summary>
        internal static void KeepExclusive(ZNetView nview) {
            if (!Present || nview == null || !nview.IsValid() || !nview.IsOwner()) { return; }
            ZDO zdo = nview.GetZDO();
            if (!zdo.GetBool(IgnoreKey)) { zdo.Set(IgnoreKey, true); }
        }
    }
}
