using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Every container loaded on this client, with what the mod needs to know about each worked out once,
    /// as it wakes: what kind of storage it is, and a version that moves only when its items do.
    ///
    /// The kind is what every feature asks before touching a container - whether crafting may draw on it,
    /// auto-store file into it, the Seeking Table list it, another player be handed it - so one lookup here
    /// stands in for walking the hierarchy each time.
    ///
    /// The version exists because a container's <c>m_lastRevision</c> cannot say whether its items changed.
    /// It follows the ZDO's data revision, and that also moves whenever the object does: a boat bobbing at
    /// the dock or a cart being pulled bumps it many times a second. Vanilla reloads on it, re-reading the
    /// whole hold and building every item afresh, which throws away anything holding on to those items -
    /// Epic Loot's table addresses them by reference - and every cache keyed on the revision. So a reload
    /// whose items are byte for byte the ones already loaded or saved is skipped (only the revision is
    /// caught up), and <see cref="Version"/> counts the loads and saves that did change something.
    /// </summary>
    internal static class StorageRegistry {
        internal enum Kind : byte {
            /// <summary>Nothing the mod draws on: a tombstone, a creature's pack, anything that is not built storage.</summary>
            Other,
            /// <summary>A built storage piece.</summary>
            Chest,
            /// <summary>A boat's hold or a cart's bed. Its carrier is the Ship or Vagon.</summary>
            Vehicle,
            /// <summary>An AutoSorter's deposit box: items waiting to be sorted.</summary>
            DepositBox,
            /// <summary>A Hopper's store, which feeds its stations.</summary>
            HopperStore,
            /// <summary>A Lighter's fuel tank.</summary>
            LighterTank,
            /// <summary>A Grinder's results chest.</summary>
            GrinderStore,
            /// <summary>A Grinder's hopper: items waiting to be ground.</summary>
            GrinderFeed,
        }

        private sealed class Entry {
            internal Kind Kind;
            // The machine holding the container (an AutomationHub, HopperHub, LighterHub, GrinderHub or
            // GrinderFeed), or the Ship or Vagon carrying it.
            internal MonoBehaviour Holder;
            internal int Version;
            // The items bytes the inventory was last loaded from or saved as, when Known.
            internal byte[] Items;
            internal bool Known;
        }

        private static readonly Dictionary<Container, Entry> Entries = new Dictionary<Container, Entry>();
        private static readonly List<Container> Live = new List<Container>();

        /// <summary>
        /// Moves every time a container is registered or found gone. A scan that saw one value has seen
        /// every container loaded at the time, so the same value later means none has come or gone since.
        /// </summary>
        internal static int Changes { get; private set; }

        internal static void Register(Container container) {
            if (Entries.ContainsKey(container)) { return; }
            Entry entry = new Entry();
            Classify(container, entry);
            Entries[container] = entry;
            Live.Add(container);
            ++Changes;
        }

        // Run once, from Container.Awake. Every component on the piece exists by then, whichever has woken.
        private static void Classify(Container container, Entry entry) {
            entry.Kind = Kind.Other;
            if (Take(entry, container.GetComponent<GrinderFeed>(), Kind.GrinderFeed)) { return; }
            if (Take(entry, container.GetComponentInParent<AutomationHub>(), Kind.DepositBox)) { return; }
            if (Take(entry, container.GetComponentInParent<HopperHub>(), Kind.HopperStore)) { return; }
            if (Take(entry, container.GetComponentInParent<LighterHub>(), Kind.LighterTank)) { return; }
            if (Take(entry, container.GetComponentInParent<GrinderHub>(), Kind.GrinderStore)) { return; }
            Vagon cart = container.m_wagon != null ? container.m_wagon : container.GetComponentInParent<Vagon>();
            if (Take(entry, cart, Kind.Vehicle)) { return; }
            if (Take(entry, container.GetComponentInParent<Ship>(), Kind.Vehicle)) { return; }
            if (container.m_nview.GetComponent<Piece>() != null) { entry.Kind = Kind.Chest; }
        }

        private static bool Take(Entry entry, MonoBehaviour holder, Kind kind) {
            if (holder == null) { return false; }
            entry.Kind = kind;
            entry.Holder = holder;
            return true;
        }

        /// <summary>What kind of storage a container is. <see cref="Kind.Other"/> for one not registered: a placement ghost.</summary>
        internal static Kind KindOf(Container container) {
            return container != null && Entries.TryGetValue(container, out Entry entry) ? entry.Kind : Kind.Other;
        }

        /// <summary>
        /// A Hopper's store, a Lighter's tank or a Grinder's results chest. Each shares its machine's ZDO, so
        /// handing it to another player would hand them the machine; and each is a working buffer, so
        /// auto-store never files into one. Crafting reaches them through their owner instead.
        /// </summary>
        internal static bool IsMachineStore(Container container) {
            Kind kind = KindOf(container);
            return kind == Kind.HopperStore || kind == Kind.LighterTank || kind == Kind.GrinderStore;
        }

        /// <summary>
        /// The machine holding a container, or the vehicle carrying it: null for anything else, or when it is
        /// not a <typeparamref name="T"/>.
        /// </summary>
        internal static T HolderOf<T>(Container container) where T : MonoBehaviour {
            return container != null && Entries.TryGetValue(container, out Entry entry) ? entry.Holder as T : null;
        }

        /// <summary>
        /// Moves exactly when this client's copy of the container's items changes: a load that brought
        /// something new, a save, or an edit of a copy this client does not own. Stands in for
        /// <c>m_lastRevision</c> wherever the question is whether the items changed.
        /// </summary>
        internal static int Version(Container container) {
            return container != null && Entries.TryGetValue(container, out Entry entry) ? entry.Version : -1;
        }

        /// <summary>
        /// Adds every live registered container within <paramref name="radius"/> of <paramref name="center"/>
        /// to <paramref name="into"/>, sweeping out the ones that have been unloaded on the way.
        /// </summary>
        internal static void InRange(Vector3 center, float radius, List<Container> into) {
            Sweep();
            float sqr = radius * radius;
            foreach (Container container in Live) {
                if ((container.transform.position - center).sqrMagnitude <= sqr) { into.Add(container); }
            }
        }

        /// <summary>Every live container of one kind, for the config changes that resize them all.</summary>
        internal static void OfKind(Kind kind, List<Container> into) {
            Sweep();
            foreach (Container container in Live) {
                if (Entries[container].Kind == kind) { into.Add(container); }
            }
        }

        // Drops the containers that have been unloaded, keeping the rest in load order. A destroyed
        // container still hashes by its instance id, so its entry can be removed after it is gone.
        private static void Sweep() {
            int kept = 0;
            for (int i = 0; i < Live.Count; ++i) {
                Container container = Live[i];
                if (container == null) {
                    Entries.Remove(container);
                    ++Changes;
                    continue;
                }
                Live[kept++] = container;
            }
            Live.RemoveRange(kept, Live.Count - kept);
        }

        // ---- the items version ----------------------------------------------------

        // Load, before vanilla's own work: true when the reload would bring nothing new and the inventory
        // need not be read again.
        internal static bool SkipsLoad(Container container) {
            ZNetView nview = container.m_nview;
            if (nview == null || !nview.IsValid()) { return false; }
            ZDO zdo = nview.GetZDO();
            // Vanilla bails out on both of these itself.
            if (zdo.DataRevision == container.m_lastRevision || container.m_inUse) { return false; }
            if (!Entries.TryGetValue(container, out Entry entry) || !entry.Known) { return false; }
            byte[] items = zdo.GetByteArray(ZDOVars.s_items);
            if (!SameBytes(entry.Items, items)) { return false; }
            container.m_lastRevision = zdo.DataRevision;
            // The newest array holding these bytes, so the next comparison is usually a reference match.
            entry.Items = items;
            return true;
        }

        // Load went ahead, or Save ran: the inventory now matches the ZDO's items.
        internal static void Matched(Container container) {
            if (!Entries.TryGetValue(container, out Entry entry)) { return; }
            ZNetView nview = container.m_nview;
            // No items array at all is a real answer too: nothing was ever stored.
            entry.Known = nview != null && nview.IsValid();
            entry.Items = entry.Known ? nview.GetZDO().GetByteArray(ZDOVars.s_items) : null;
            ++entry.Version;
        }

        // The inventory changed on a client that does not own it, so it no longer matches the ZDO and the
        // next reload has to go ahead to put it right.
        internal static void Diverged(Container container) {
            if (!Entries.TryGetValue(container, out Entry entry)) { return; }
            entry.Known = false;
            entry.Items = null;
            ++entry.Version;
        }

        private static bool SameBytes(byte[] a, byte[] b) {
            if (ReferenceEquals(a, b)) { return true; }
            if (a == null || b == null || a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; ++i) {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Container), "Awake")]
    internal static class Container_Awake_StorageRegistry_Patch {
        // A placement ghost has no ZDO, and Container.Awake builds no inventory for it.
        private static void Postfix(Container __instance) {
            if (__instance.m_nview == null || __instance.m_nview.GetZDO() == null) { return; }
            StorageRegistry.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), "Load")]
    internal static class Container_Load_StorageRegistry_Patch {
        // First, so a skipped reload is skipped before anything else prepares for it.
        //
        // A skipped reload still answers true, as vanilla's would: the ZDO did move, and its caller
        // (CheckForChanges) only then redraws the open lid and lets an emptied cargo crate go - opening or
        // closing a chest changes nothing but its in-use flag. The postfix leaves the version alone.
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Container __instance, ref bool __result, out bool __state) {
            __state = false;
            if (StorageRegistry.SkipsLoad(__instance)) {
                __result = true;
                return false;
            }
            __state = true;
            return true;
        }

        private static void Postfix(Container __instance, bool __result, bool __state) {
            if (__state && __result) { StorageRegistry.Matched(__instance); }
        }
    }

    [HarmonyPatch(typeof(Container), "Save")]
    internal static class Container_Save_StorageRegistry_Patch {
        private static void Postfix(Container __instance) {
            StorageRegistry.Matched(__instance);
        }
    }

    [HarmonyPatch(typeof(Container), "OnContainerChanged")]
    internal static class Container_OnContainerChanged_StorageRegistry_Patch {
        // The owner's change is saved, and Save records it. Anyone else's is not.
        private static void Prefix(Container __instance) {
            if (__instance.m_loading || __instance.m_nview == null || !__instance.m_nview.IsValid() || __instance.m_nview.IsOwner()) { return; }
            StorageRegistry.Diverged(__instance);
        }
    }
}
