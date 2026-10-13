using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Registry of active AutoSorter hubs, plus the cached lookups the crafting Harmony patches read: which
    /// chests feed a station, which feed a point, and how many of each material those pools hold.
    /// </summary>
    internal static class ContainerNetwork {
        private static readonly List<AutomationHub> Hubs = new List<AutomationHub>();

        // Cached map rebuilt only after a hub scans / (un)registers - not on every crafting check - and
        // then lazily, on the next lookup, so several hubs scanning in a row rebuild it once.
        private static readonly Dictionary<CraftingStation, List<Container>> StationToContainers =
            new Dictionary<CraftingStation, List<Container>>();
        private static bool linksChanged = true;
        // Scratch set reused across EnsureStationCache to dedup a station's containers in O(1).
        private static readonly HashSet<Container> RebuildSeen = new HashSet<Container>();

        // Also what both pool accessors hand back while the AutoSorter is off - the server's switch or the
        // local player's: every consumer - the two Harmony display paths, the requirement checks,
        // consumption, and Epic Loot's inventory provider - already treats an empty pool as "no autosorter
        // here", so neither switch needs a separate test anywhere else.
        internal static readonly List<Container> Empty = new List<Container>();

        // Crafting and build queries fan out across hundreds of recipes (panel refresh) and the selected
        // recipe (every frame). Without these each query would rescan every chest's inventory, and the game
        // freezes once a few hundred chests are linked.
        private static readonly PoolCounts AllCounts = new PoolCounts(spendableOnly: false);
        private static readonly PoolCounts SpendCounts = new PoolCounts(spendableOnly: true);

        // Frame-memoized result of GetContainersNearPoint so the Hammer-build path stops reallocating
        // and re-deduping every frame, and returns a stable list reference the counts can key on.
        private static readonly List<Container> NearPointResult = new List<Container>();
        private static readonly HashSet<Container> NearPointSeen = new HashSet<Container>();
        private static int nearPointFrame = -1;
        private static Vector3 nearPointPos;

        internal static void Register(AutomationHub hub) {
            if (!Hubs.Contains(hub)) { Hubs.Add(hub); }
            LinksChanged();
        }

        internal static void Unregister(AutomationHub hub) {
            Hubs.Remove(hub);
            LinksChanged();
        }

        /// <summary>
        /// A hub's links changed. The station cache is rebuilt on its next lookup, and the near-point and
        /// count caches look again.
        /// </summary>
        internal static void LinksChanged() {
            linksChanged = true;
            nearPointFrame = -1;
            InvalidateItemCounts();
        }

        private static void EnsureStationCache() {
            if (!linksChanged) { return; }
            linksChanged = false;
            StationToContainers.Clear();
            foreach (AutomationHub hub in Hubs) {
                if (hub == null) { continue; }
                foreach (CraftingStation station in hub.LinkedStations) {
                    if (station == null) { continue; }
                    if (!StationToContainers.TryGetValue(station, out List<Container> list)) {
                        list = new List<Container>();
                        StationToContainers[station] = list;
                    }
                    // Dedup via a reused HashSet (seeded with what's already linked from earlier hubs)
                    // instead of List.Contains in a loop, which is O(containers^2).
                    RebuildSeen.Clear();
                    RebuildSeen.UnionWith(list);
                    foreach (Container container in hub.LinkedContainers) {
                        if (container != null && RebuildSeen.Add(container)) { list.Add(container); }
                    }
                }
            }
        }

        /// <summary>
        /// Makes the item counts check their chests again before they are next trusted: something may have
        /// changed them this frame - auto-store moving items in, crafting taking them out. Cheap: a chest
        /// whose items have not changed is not read again.
        /// </summary>
        internal static void InvalidateItemCounts() {
            AllCounts.Restamp();
            SpendCounts.Restamp();
            // Epic Loot's enchanting table reads a memo of the same pool, on the same per-frame basis.
            EpicLootIntegration.InvalidateItemCache();
        }

        private static bool Available => ValConfig.AutomationEnabled.Value && ValConfig.CraftFromStorageEnabled.Value;

        /// <summary>Containers linked to the given crafting station (station-crafting pool). O(1) lookup.</summary>
        internal static List<Container> GetContainersForStation(CraftingStation station) {
            if (!Available) { return Empty; }
            EnsureStationCache();
            if (station != null && StationToContainers.TryGetValue(station, out List<Container> list)) {
                return list;
            }
            return Empty;
        }

        /// <summary>
        /// Containers from every hub whose scan radius currently covers the point (Hammer-build pool).
        /// Frame-memoized: returns a stable, reused list within a frame for the same point so the
        /// per-frame build path neither reallocates nor re-dedups, and the counts can key on it.
        /// </summary>
        internal static List<Container> GetContainersNearPoint(Vector3 point) {
            if (!Available) { return Empty; }
            if (nearPointFrame == Time.frameCount && nearPointPos == point) {
                return NearPointResult;
            }
            NearPointResult.Clear();
            NearPointSeen.Clear();
            foreach (AutomationHub hub in Hubs) {
                if (hub == null || hub.IsDormant) { continue; }
                if (Vector3.Distance(hub.transform.position, point) > hub.EffectiveRadius) { continue; }
                foreach (Container container in hub.LinkedContainers) {
                    if (container != null && NearPointSeen.Add(container)) { NearPointResult.Add(container); }
                }
            }
            nearPointFrame = Time.frameCount;
            nearPointPos = point;
            return NearPointResult;
        }

        /// <summary>
        /// The pool a player crafts or builds from where they stand: their station's chests at a station
        /// (Epic Loot's enchanting table is one), otherwise every hub whose radius covers them.
        /// </summary>
        internal static List<Container> PoolFor(Player player) {
            if (player == null) { return Empty; }
            CraftingStation station = player.GetCurrentCraftingStation();
            return station != null ? GetContainersForStation(station) : GetContainersNearPoint(player.transform.position);
        }

        /// <summary>
        /// Total stack count of <paramref name="name"/> across the pool, matching vanilla
        /// <c>Inventory.CountItems(name, -1, matchWorldLevel: true)</c>.
        /// </summary>
        internal static int CountInPool(List<Container> pool, string name) {
            return AllCounts.Count(pool, name);
        }

        /// <summary>
        /// <see cref="CountInPool"/> over only the chests this client owns, each brought up to date with
        /// its ZDO first. What crafting can spend right now without asking anyone.
        /// </summary>
        internal static int CountSpendableInPool(List<Container> pool, string name) {
            return SpendCounts.Count(pool, name);
        }

        /// <summary>
        /// Item name to total stack across a pool. Checked at most once per frame per pool, and rebuilt only
        /// when the pool or one of its chests has changed since: the same chests in the same order, each with
        /// the items version (see <see cref="StorageRegistry.Version"/>) it was counted at - and, for the
        /// spendable counts, still owned or still not owned here.
        /// </summary>
        private sealed class PoolCounts {
            // A chest the counts skip: unloaded, or (spendable only) not this client's.
            private const int Skipped = int.MinValue;

            private readonly bool spendableOnly;
            private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            private readonly List<Container> sources = new List<Container>();
            private readonly List<int> stamps = new List<int>();
            private List<Container> pool;
            private int frame = -1;
            private int worldLevel;
            private bool built;

            internal PoolCounts(bool spendableOnly) {
                this.spendableOnly = spendableOnly;
            }

            internal void Restamp() {
                frame = -1;
            }

            internal int Count(List<Container> pool, string name) {
                if (pool == null || pool.Count == 0) { return 0; }
                if (frame != Time.frameCount || !ReferenceEquals(this.pool, pool)) {
                    if (!Current(pool)) { Build(pool); }
                    frame = Time.frameCount;
                    this.pool = pool;
                }
                return counts.TryGetValue(name, out int total) ? total : 0;
            }

            private int Stamp(Container container) {
                if (container == null || container.m_nview == null || !container.m_nview.IsValid() || container.GetInventory() == null) { return Skipped; }
                // A chest someone has open, or a boat with someone aboard, still counts as stock: it cannot
                // change hands (see StorageOwnership.IsBusy), but its owner can take the materials out for
                // this client (see StorageReserve).
                if (spendableOnly && !StorageOwnership.CanSpendLocally(container)) { return Skipped; }
                return StorageRegistry.Version(container);
            }

            private bool Current(List<Container> pool) {
                if (!built || worldLevel != Game.m_worldLevel || pool.Count != sources.Count) { return false; }
                for (int i = 0; i < pool.Count; ++i) {
                    if (!ReferenceEquals(pool[i], sources[i]) || Stamp(pool[i]) != stamps[i]) { return false; }
                }
                return true;
            }

            private void Build(List<Container> pool) {
                bool debug = ValConfig.EnableDebugMode.Value;
                double startTime = debug ? Time.realtimeSinceStartupAsDouble : 0.0;

                counts.Clear();
                sources.Clear();
                stamps.Clear();
                worldLevel = Game.m_worldLevel;
                foreach (Container container in pool) {
                    // A handoff that has just landed is read in before it is counted as spendable.
                    if (spendableOnly && Stamp(container) != Skipped) { StorageOwnership.Sync(container); }
                    int stamp = Stamp(container);
                    sources.Add(container);
                    stamps.Add(stamp);
                    if (stamp == Skipped) { continue; }
                    foreach (ItemDrop.ItemData item in container.GetInventory().GetAllItems()) {
                        // Mirror CountItems(name, -1, matchWorldLevel: true): any quality, world-level gated.
                        if (item.m_worldLevel < worldLevel) { continue; }
                        // Enchanted gear is not spendable as plain material, so it must not be counted as
                        // such either - otherwise a chest of legendaries reads as free crafting stock.
                        if (EpicLootIntegration.IsProtectedItem(item)) { continue; }
                        string itemName = item.m_shared.m_name;
                        counts.TryGetValue(itemName, out int cur);
                        counts[itemName] = cur + item.m_stack;
                    }
                }
                built = true;

                if (debug) {
                    double ms = (Time.realtimeSinceStartupAsDouble - startTime) * 1000.0;
                    Logger.LogInfo($"[Autosorter] aggregated {pool.Count} chests{(spendableOnly ? " (owned only)" : "")}, {counts.Count} item types in {ms:F2}ms.");
                }
            }
        }
    }
}
