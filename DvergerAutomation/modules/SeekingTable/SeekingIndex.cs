using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>One item in a chest that an entry stands for.</summary>
    internal struct SeekSource {
        internal Container Chest;
        internal ItemDrop.ItemData Item;
    }

    /// <summary>
    /// One cell in the Seeking Table's grid: every unit of one kind of item across the chests in range.
    /// Stackables add up by vanilla's stacking rule; identical gear adds up too; an Epic Loot magic item
    /// is always a cell of its own.
    /// </summary>
    internal sealed class SeekEntry {
        /// <summary>One of the items the entry stands for, as it sits in its chest. Shown, and sent as the prototype when asking for more.</summary>
        internal ItemDrop.ItemData Sample;
        /// <summary>Units across all sources, less what is already on its way to the player.</summary>
        internal int Total;
        internal readonly List<SeekSource> Sources = new List<SeekSource>();
        internal int Chests;
        internal DepositCategory Category;
        internal int CategoryOrder;
        internal bool Magic;
        internal string Name = "";
        internal string Description = "";
        internal IReadOnlyList<string> EnchantmentNames;
    }

    /// <summary>
    /// What the open Seeking Table can see: the chests in its range and, grouped, what is in them.
    ///
    /// Nothing here is pushed: every chest already reloads its own copy of its contents from the network
    /// (see <c>Container.CheckForChanges</c>), and this reads those copies. A chest's
    /// <c>m_lastRevision</c> moves exactly when its copy here changes - a reload, or this client saving a
    /// chest it owns - so comparing those is all it takes to know when to regroup. The chest list itself is
    /// redrawn every few seconds, which is when a newly built or newly loaded chest shows up.
    ///
    /// Takes in flight (see <see cref="SeekingRetrieval"/>) are held off the totals until the chest they
    /// were taken from changes, so a quick second click is never offered what the first already took.
    /// </summary>
    internal static class SeekingIndex {
        private const float RescanInterval = 3f;
        private const float RefreshInterval = 0.25f;
        // How long a take is held off a total at most, should the chest it came from never change.
        private const float InFlightSeconds = 5f;

        internal static readonly List<Container> Chests = new List<Container>();
        internal static readonly List<SeekEntry> Entries = new List<SeekEntry>();
        /// <summary>Bumped on every regroup, so the panel knows to refilter.</summary>
        internal static int Version { get; private set; }
        internal static float Radius { get; private set; }

        /// <summary>Part of a take that is out: one source's units, held off its entry's total.</summary>
        internal sealed class InFlight {
            internal Container Chest;
            internal ItemDrop.ItemData Item;
            internal int Amount;
            internal uint Revision;
            internal float Since;
        }

        private static readonly List<InFlight> Taking = new List<InFlight>();
        private static readonly Dictionary<Container, MonoBehaviour> VehicleScratch = new Dictionary<Container, MonoBehaviour>();
        private static readonly Dictionary<string, SeekEntry> Groups = new Dictionary<string, SeekEntry>();
        private static readonly HashSet<Container> ChestsScratch = new HashSet<Container>();
        private static readonly StringBuilder KeyBuilder = new StringBuilder();
        private static readonly List<string> KeyScratch = new List<string>();

        private static SeekingTable table;
        private static float nextRescan;
        private static float nextRefresh;
        private static int signature;
        private static bool dirty;

        internal static void Open(SeekingTable opened) {
            table = opened;
            Taking.Clear();
            nextRescan = 0f;
            nextRefresh = 0f;
            dirty = true;
            Tick();
        }

        internal static void Close() {
            table = null;
            Chests.Clear();
            Entries.Clear();
            Groups.Clear();
            Taking.Clear();
            SeekingSearch.ClearCaches();
        }

        /// <summary>Forces a regroup on the next tick, after something this client did.</summary>
        internal static void MarkDirty() {
            dirty = true;
            nextRefresh = 0f;
        }

        /// <summary>Redraws the chest list on the next tick: the table's range or awake state changed.</summary>
        internal static void RescanSoon(SeekingTable changed) {
            if (changed != table) { return; }
            nextRescan = 0f;
            MarkDirty();
        }

        internal static bool IsOpenOn(SeekingTable candidate) => table != null && table == candidate;

        /// <summary>Called every frame while the table is open.</summary>
        internal static void Tick() {
            if (table == null) { return; }
            float now = Time.time;
            if (now >= nextRescan) {
                nextRescan = now + RescanInterval;
                Rescan();
            }
            if (now < nextRefresh && !dirty) { return; }
            nextRefresh = now + RefreshInterval;

            Refresh();
            int current = Signature();
            if (current != signature || dirty) {
                signature = current;
                dirty = false;
                Regroup();
            }
        }

        private static void Rescan() {
            Chests.Clear();
            if (!table.IsAwake) { return; }

            Radius = table.EffectiveRadius;
            Vector3 center = table.transform.position;
            long playerId = AutomationHub.LocalPlayerId();
            StorageScan.CollectChests(center, Radius, playerId, Chests, requireResolvable: true, includeHopperStores: ValConfig.SeekFromHoppers.Value);

            VehicleScratch.Clear();
            VehicleStorage.Collect(center, Radius, playerId, VehicleScratch, ValConfig.SeekFromBoats.Value, ValConfig.SeekFromCarts.Value);
            Chests.AddRange(VehicleScratch.Keys);
            VehicleScratch.Clear();
        }

        // Brings each chest's copy up to date with the network before it is read. Load is a revision
        // compare and does nothing when nothing changed, which is nearly always.
        private static void Refresh() {
            for (int i = Chests.Count - 1; i >= 0; --i) {
                Container chest = Chests[i];
                if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid()) {
                    Chests.RemoveAt(i);
                    continue;
                }
                StorageOwnership.Sync(chest);
            }

            float now = Time.time;
            for (int i = Taking.Count - 1; i >= 0; --i) {
                InFlight take = Taking[i];
                if (take.Chest == null || take.Chest.m_lastRevision != take.Revision || now - take.Since > InFlightSeconds) {
                    Taking.RemoveAt(i);
                    dirty = true;
                }
            }
        }

        private static int Signature() {
            unchecked {
                int hash = 17;
                foreach (Container chest in Chests) {
                    hash = hash * 31 + chest.GetInstanceID();
                    hash = hash * 31 + (int)chest.m_lastRevision;
                }
                return hash;
            }
        }

        // ---- grouping ------------------------------------------------------------

        private static void Regroup() {
            Entries.Clear();
            Groups.Clear();
            int magicIndex = 0;

            foreach (Container chest in Chests) {
                Inventory inv = chest.GetInventory();
                if (inv == null) { continue; }
                foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                    if (item == null || item.m_shared == null) { continue; }
                    // Only something that can be rebuilt on arrival can be fetched (see StorageOps.TakeExact).
                    if (item.m_dropPrefab == null) { continue; }
                    int available = item.m_stack - InFlightOn(chest, item);
                    if (available <= 0) { continue; }

                    bool magic = EpicLootIntegration.IsMagicItem(item);
                    string key = magic ? "magic#" + (magicIndex++) : GroupKey(item);
                    if (!Groups.TryGetValue(key, out SeekEntry entry)) {
                        entry = new SeekEntry { Sample = item, Magic = magic };
                        Categorize(entry);
                        SeekingSearch.Describe(entry);
                        Groups[key] = entry;
                        Entries.Add(entry);
                    }
                    entry.Total += available;
                    entry.Sources.Add(new SeekSource { Chest = chest, Item = item });
                }
            }

            foreach (SeekEntry entry in Entries) {
                ChestsScratch.Clear();
                foreach (SeekSource source in entry.Sources) { ChestsScratch.Add(source.Chest); }
                entry.Chests = ChestsScratch.Count;
            }
            ChestsScratch.Clear();

            Entries.Sort(Compare);
            ++Version;
        }

        // Category order first, as the tabs are laid out, then name, then the better quality first.
        private static int Compare(SeekEntry a, SeekEntry b) {
            int byCategory = a.CategoryOrder.CompareTo(b.CategoryOrder);
            if (byCategory != 0) { return byCategory; }
            int byName = string.Compare(a.Name, b.Name, System.StringComparison.CurrentCultureIgnoreCase);
            if (byName != 0) { return byName; }
            return b.Sample.m_quality.CompareTo(a.Sample.m_quality);
        }

        private static void Categorize(SeekEntry entry) {
            DepositCategory[] all = DepositCategories.All;
            for (int i = 0; i < all.Length; ++i) {
                if (!DepositCategories.Covers(all[i], entry.Sample)) { continue; }
                entry.Category = all[i];
                entry.CategoryOrder = i;
                return;
            }
            entry.CategoryOrder = all.Length;
        }

        // Stackables by vanilla's stacking rule, gear by everything a save keeps (see ItemIdentity).
        private static string GroupKey(ItemDrop.ItemData item) {
            KeyBuilder.Clear();
            KeyBuilder.Append(item.m_shared.m_name).Append('|').Append(item.m_quality)
                .Append('|').Append(item.m_worldLevel).Append('|').Append(item.m_cheated);
            if (item.m_shared.m_maxStackSize > 1) { return KeyBuilder.ToString(); }

            KeyBuilder.Append('|').Append(item.m_variant)
                .Append('|').Append(item.m_crafterID).Append('|').Append(item.m_crafterName)
                .Append('|').Append(item.m_dropPrefab != null ? item.m_dropPrefab.name : "")
                // Hundredths, the precision a save keeps (see ItemIdentity).
                .Append('|').Append(Mathf.RoundToInt(item.m_durability * 100f));
            KeyScratch.Clear();
            KeyScratch.AddRange(item.m_customData.Keys);
            KeyScratch.Sort(System.StringComparer.Ordinal);
            foreach (string field in KeyScratch) {
                KeyBuilder.Append('|').Append(field).Append('=').Append(item.m_customData[field]);
            }
            return KeyBuilder.ToString();
        }

        // ---- takes in flight --------------------------------------------------------

        internal static int InFlightOn(Container chest, ItemDrop.ItemData item) {
            int amount = 0;
            foreach (InFlight take in Taking) {
                if (take.Chest == chest && take.Item == item) { amount += take.Amount; }
            }
            return amount;
        }

        /// <summary>Holds <paramref name="amount"/> of a source off the totals while its take is out.</summary>
        internal static InFlight HoldOff(SeekSource source, int amount) {
            InFlight take = new InFlight {
                Chest = source.Chest,
                Item = source.Item,
                Amount = amount,
                Revision = source.Chest.m_lastRevision,
                Since = Time.time,
            };
            Taking.Add(take);
            MarkDirty();
            return take;
        }

        /// <summary>Takes that came back empty: what they held off is back on offer.</summary>
        internal static void Release(List<InFlight> takes) {
            foreach (InFlight take in takes) { Taking.Remove(take); }
            MarkDirty();
        }
    }
}
