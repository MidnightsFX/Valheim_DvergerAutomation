using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>One item an entry stands for: in a chest, or lying on the ground.</summary>
    internal struct SeekSource {
        internal Container Chest;
        /// <summary>The item lying on the ground; null for one in a chest.</summary>
        internal ItemDrop Drop;
        /// <summary>
        /// Set for a ground item. Kept apart from <see cref="Drop"/>, which compares equal to null as soon as
        /// the item is picked up or despawns.
        /// </summary>
        internal bool Ground;
        internal ItemDrop.ItemData Item;
    }

    /// <summary>
    /// One cell in the Seeking Table's grid: every unit of one kind of item across the chests in range,
    /// and on the ground when that is switched on. Stackables add up by vanilla's stacking rule; identical
    /// gear adds up too; an Epic Loot magic item is always a cell of its own.
    /// </summary>
    internal sealed class SeekEntry {
        /// <summary>One of the items the entry stands for, as it sits in its chest or on the ground. Shown, and sent as the prototype when asking for more.</summary>
        internal ItemDrop.ItemData Sample;
        /// <summary>Units across all sources, less what is already on its way to the player.</summary>
        internal int Total;
        internal readonly List<SeekSource> Sources = new List<SeekSource>();
        internal int Chests;
        /// <summary>How many separate items on the ground the entry includes.</summary>
        internal int GroundDrops;
        internal DepositCategory Category;
        internal int CategoryOrder;
        internal bool Magic;
        internal string Name = "";
        internal string Description = "";
        internal IReadOnlyList<string> EnchantmentNames;
    }

    /// <summary>
    /// What the open Seeking Table can see: the chests in its range and, grouped, what is in them - plus,
    /// when the player has switched it on, the items lying on the ground in the same range.
    ///
    /// Nothing here is pushed: every chest already reloads its own copy of its contents from the network
    /// (see <c>Container.CheckForChanges</c>), and this reads those copies. A chest's items version (see
    /// <see cref="StorageRegistry.Version"/>) moves exactly when its copy here changes - a reload that
    /// brought something new, or this client saving a chest it owns - so comparing those is all it takes to
    /// know when to regroup; a boat bobbing at the dock does not count. The chest list itself is redrawn
    /// as soon as a container has loaded or gone anywhere, and now and then regardless, since a ward's
    /// permissions can change without anything else doing so.
    ///
    /// The ground is looked over once a second, and only while a table's window is open with the switch on:
    /// every dropped item in the loaded world is checked against the range, which is cheap once but not
    /// something to do every frame.
    ///
    /// Takes in flight (see <see cref="SeekingRetrieval"/>) are held off the totals until the chest or item
    /// they were taken from changes, so a quick second click is never offered what the first already took.
    /// </summary>
    internal static class SeekingIndex {
        // How often the table checks whether a container has come or gone, and the longest it goes without
        // redrawing its chest list anyway.
        private const float RescanCheckInterval = 1f;
        // Short: a boat or cart moving out of range changes nothing the registry counts, and the walk is cheap.
        private const float RescanInterval = 3f;
        private const float RefreshInterval = 0.25f;
        private const float GroundScanInterval = 1f;
        // How long a take is held off a total at most, should what it came from never change.
        private const float InFlightSeconds = 5f;

        internal static readonly List<Container> Chests = new List<Container>();
        internal static readonly List<ItemDrop> Ground = new List<ItemDrop>();
        internal static readonly List<SeekEntry> Entries = new List<SeekEntry>();
        /// <summary>Bumped on every regroup, so the panel knows to refilter.</summary>
        internal static int Version { get; private set; }
        internal static float Radius { get; private set; }

        /// <summary>Ground items are listed: the server allows it and the player has switched it on.</summary>
        internal static bool GroundEnabled => ValConfig.SeekAllowGround.Value && ValConfig.SeekGroundItems.Value;

        /// <summary>Part of a take that is out: one source's units, held off its entry's total.</summary>
        internal sealed class InFlight {
            internal Container Chest;
            internal ItemDrop Drop;
            internal bool Ground;
            internal ItemDrop.ItemData Item;
            internal int Amount;
            // A chest's items version, or a ground item's stack, as it was when the take went out.
            internal int Revision;
            internal float Since;
        }

        private static readonly List<InFlight> Taking = new List<InFlight>();
        private static readonly List<Container> VehicleScratch = new List<Container>();
        private static readonly Dictionary<string, SeekEntry> Groups = new Dictionary<string, SeekEntry>();
        private static readonly HashSet<Container> ChestsScratch = new HashSet<Container>();
        private static readonly StringBuilder KeyBuilder = new StringBuilder();

        private static SeekingTable table;
        private static float nextRescan;
        private static float nextRescanCheck;
        private static int rescannedAt;
        private static float nextRefresh;
        private static float nextGroundScan;
        private static int signature;
        private static bool dirty;
        private static int magicIndex;

        internal static void Open(SeekingTable opened) {
            table = opened;
            Taking.Clear();
            Ground.Clear();
            nextRescan = 0f;
            nextRescanCheck = 0f;
            nextRefresh = 0f;
            nextGroundScan = 0f;
            dirty = true;
            Tick();
        }

        internal static void Close() {
            table = null;
            Chests.Clear();
            Ground.Clear();
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
            nextGroundScan = 0f;
            MarkDirty();
        }

        /// <summary>
        /// The ground switch, or the server's permission for it, changed: look at the ground now, or stop
        /// listing it. Hooked to both config entries.
        /// </summary>
        internal static void GroundSettingChanged(object sender = null, EventArgs e = null) {
            nextGroundScan = 0f;
            MarkDirty();
        }

        /// <summary>Called every frame while the table is open.</summary>
        internal static void Tick() {
            if (table == null) { return; }
            float now = Time.time;
            if (now >= nextRescanCheck) {
                nextRescanCheck = now + RescanCheckInterval;
                if (now >= nextRescan || StorageRegistry.Changes != rescannedAt) {
                    nextRescan = now + RescanInterval;
                    Rescan();
                }
            }
            if (now >= nextGroundScan) {
                nextGroundScan = now + GroundScanInterval;
                ScanGround();
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
            if (!table.IsAwake) {
                rescannedAt = StorageRegistry.Changes;
                return;
            }

            Radius = table.EffectiveRadius;
            VehicleScratch.Clear();
            StorageScan.Collect(table.transform.position, Radius, StorageScan.LocalPlayerId(),
                Chests, ValConfig.SeekFromHoppers.Value,
                VehicleScratch, ValConfig.SeekFromBoats.Value, ValConfig.SeekFromCarts.Value);
            // Read after the walk, which sweeps out what has been unloaded and counts that as a change.
            rescannedAt = StorageRegistry.Changes;
            Chests.AddRange(VehicleScratch);
            VehicleScratch.Clear();
        }

        // Every dropped item in the loaded world is in vanilla's own list, so there is nothing to overlap:
        // the list is walked and measured against the range.
        private static void ScanGround() {
            if (!GroundEnabled || !table.IsAwake) {
                Ground.Clear();
                return;
            }
            Ground.Clear();
            Vector3 center = table.transform.position;
            float reach = Radius * Radius;
            foreach (ItemDrop drop in ItemDrop.s_instances) {
                if (drop == null || (drop.transform.position - center).sqrMagnitude > reach) { continue; }
                if (!CanTakeFromGround(drop)) { continue; }
                // The copy of an item someone else owns is only refreshed when it is picked up, so it is
                // brought up to its ZDO here. A revision compare when nothing changed.
                drop.Load();
                Ground.Add(drop);
            }
        }

        /// <summary>
        /// Whether a dropped item is loot the player could walk up and pick up. Not one placed as a piece
        /// (an item set down as decoration), not a fish still swimming - vanilla only lets those be picked up
        /// once landed - and not one the game would refuse for having no icon.
        /// </summary>
        internal static bool CanTakeFromGround(ItemDrop drop) {
            ZNetView nview = drop.m_nview;
            if (nview == null || !nview.IsValid()) { return false; }
            if (nview.GetZDO().GetBool(ZDOVars.s_piece)) { return false; }
            ItemDrop.ItemData item = drop.m_itemData;
            if (item == null || item.m_shared == null || item.m_dropPrefab == null) { return false; }
            if (item.m_shared.m_icons == null || item.m_shared.m_icons.Length == 0) { return false; }
            Fish fish = drop.GetComponent<Fish>();
            return fish == null || fish.IsOutOfWater();
        }

        // Brings each chest's copy up to date with the network before it is read. Load does nothing when
        // nothing changed, which is nearly always. Ground items that were
        // picked up or despawned since the last look drop out here rather than waiting for it.
        private static void Refresh() {
            for (int i = Chests.Count - 1; i >= 0; --i) {
                Container chest = Chests[i];
                if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid()) {
                    Chests.RemoveAt(i);
                    continue;
                }
                StorageOwnership.Sync(chest);
            }
            for (int i = Ground.Count - 1; i >= 0; --i) {
                ItemDrop drop = Ground[i];
                if (drop == null || drop.m_nview == null || !drop.m_nview.IsValid()) {
                    Ground.RemoveAt(i);
                    dirty = true;
                }
            }

            float now = Time.time;
            for (int i = Taking.Count - 1; i >= 0; --i) {
                InFlight take = Taking[i];
                if (Moved(take) || now - take.Since > InFlightSeconds) {
                    Taking.RemoveAt(i);
                    dirty = true;
                }
            }
        }

        // What a take came from has changed since, so the totals read from it are current again. For an
        // item on the ground that is its stack: its revision also moves while it drifts or settles.
        private static bool Moved(InFlight take) {
            if (take.Ground) {
                if (take.Drop == null || take.Drop.m_nview == null || !take.Drop.m_nview.IsValid()) { return true; }
                return take.Drop.m_itemData.m_stack != take.Revision;
            }
            return take.Chest == null || StorageRegistry.Version(take.Chest) != take.Revision;
        }

        // Order-independent, so the same chests in another order - the registry is walked in load order,
        // which a rescan can change - read as unchanged. Which chests are listed and how far their items
        // have moved are kept apart: items versions only ever go up, so their sum rises with any change
        // and two chests changing at once cannot cancel out. A ground item's stack can go either way, so
        // each item and its stack are hashed together before they are added up.
        private static int Signature() {
            unchecked {
                int listed = Chests.Count * 31 + Ground.Count;
                int versions = 0;
                foreach (Container chest in Chests) {
                    listed += chest.GetInstanceID() * 397;
                    versions += StorageRegistry.Version(chest);
                }
                int ground = 0;
                foreach (ItemDrop drop in Ground) {
                    ground += Mix(drop.GetInstanceID() * 397 + drop.m_itemData.m_stack);
                }
                return (listed * 486187739 + versions) * 486187739 + ground;
            }
        }

        // A full avalanche (MurmurHash3's finaliser), so nearby inputs land far apart.
        private static int Mix(int value) {
            unchecked {
                uint h = (uint)value;
                h ^= h >> 16;
                h *= 0x85EBCA6B;
                h ^= h >> 13;
                h *= 0xC2B2AE35;
                h ^= h >> 16;
                return (int)h;
            }
        }

        // ---- grouping ------------------------------------------------------------

        private static void Regroup() {
            Entries.Clear();
            Groups.Clear();
            magicIndex = 0;

            foreach (Container chest in Chests) {
                Inventory inv = chest.GetInventory();
                if (inv == null) { continue; }
                foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                    Add(new SeekSource { Chest = chest, Item = item });
                }
            }
            foreach (ItemDrop drop in Ground) {
                if (drop == null) { continue; }
                Add(new SeekSource { Drop = drop, Ground = true, Item = drop.m_itemData });
            }

            foreach (SeekEntry entry in Entries) {
                ChestsScratch.Clear();
                int ground = 0;
                foreach (SeekSource source in entry.Sources) {
                    if (source.Ground) { ++ground; } else { ChestsScratch.Add(source.Chest); }
                }
                entry.Chests = ChestsScratch.Count;
                entry.GroundDrops = ground;
            }
            ChestsScratch.Clear();

            Entries.Sort(Compare);
            ++Version;
        }

        private static void Add(SeekSource source) {
            ItemDrop.ItemData item = source.Item;
            if (item == null || item.m_shared == null) { return; }
            // Only something that can be rebuilt on arrival can be fetched (see StorageOps.TakeExact).
            if (item.m_dropPrefab == null) { return; }
            int available = item.m_stack - InFlightOn(item);
            if (available <= 0) { return; }

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
            entry.Sources.Add(source);
        }

        // Category order first, as the tabs are laid out, then name, then the better quality first.
        private static int Compare(SeekEntry a, SeekEntry b) {
            int byCategory = a.CategoryOrder.CompareTo(b.CategoryOrder);
            if (byCategory != 0) { return byCategory; }
            int byName = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
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
            ItemIdentity.AppendCustomData(KeyBuilder, item);
            return KeyBuilder.ToString();
        }

        // ---- takes in flight --------------------------------------------------------

        /// <summary>
        /// Units of this very item held off by takes still out. By instance: each item in a chest, and each
        /// item on the ground, is its own; a chest reload makes new instances, but it also ends the holds.
        /// </summary>
        internal static int InFlightOn(ItemDrop.ItemData item) {
            int amount = 0;
            foreach (InFlight take in Taking) {
                if (take.Item == item) { amount += take.Amount; }
            }
            return amount;
        }

        /// <summary>Holds <paramref name="amount"/> of a source off the totals while its take is out.</summary>
        internal static InFlight HoldOff(SeekSource source, int amount) {
            InFlight take = new InFlight {
                Chest = source.Chest,
                Drop = source.Drop,
                Ground = source.Ground,
                Item = source.Item,
                Amount = amount,
                Revision = source.Ground ? source.Drop.m_itemData.m_stack : StorageRegistry.Version(source.Chest),
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

        /// <summary>A settled take, done or not: totals read from what it came from from now on.</summary>
        internal static void Release(InFlight take) {
            Taking.Remove(take);
            MarkDirty();
        }
    }
}
