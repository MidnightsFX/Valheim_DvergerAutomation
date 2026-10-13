using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Finishes the Grinder's two containers on their bundle prefabs (see <see cref="MachineStore"/>): the
    /// results chest under the Grinder, and the hopper, which sits on its own root with its own ZNetView.
    /// </summary>
    internal static class GrinderStore {
        // Child path of the results chest inside the DA_Grinder prefab.
        private const string StorePath = "Grinder_Body/GrinderStore";

        /// <summary>Rows in either container, as the server's settings allow them.</summary>
        internal const int MinRows = 1;
        internal const int MaxRows = 8;
        internal const int DefaultRows = 4;

        internal static void ConfigurePrefab(GameObject prefab) {
            MachineStore.Setup(prefab, StorePath, "$DA_grinder_store_name", ValConfig.GrinderStoreRows, MinRows, MaxRows, StorageRegistry.Kind.GrinderStore);
        }

        internal static void ConfigureFeedPrefab(GameObject prefab) {
            MachineStore.Setup(prefab, null, "$DA_grinder_feed_name", ValConfig.GrinderFeedRows, MinRows, MaxRows, StorageRegistry.Kind.GrinderFeed);
        }
    }
}
