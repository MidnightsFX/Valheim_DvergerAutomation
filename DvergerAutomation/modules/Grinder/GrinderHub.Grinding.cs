using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    // The Grinder's owner: keeping its hopper, loading the queue when the lever is pulled, and grinding
    // through it. Every method here runs only on the client that owns the Grinder.
    public partial class GrinderHub {
        // How long the hopper's ZDO may be missing (rather than destroyed) before a new one is spawned. A
        // client that has just arrived can load the Grinder before its hopper streams in.
        private const float FeedGraceSeconds = 10f;

        // With its hopper loaded at the FeedPoint, how often the Grinder still looks through the zone for a
        // second one. The look copies every object in the zone, so it is not made every tick.
        private const float FeedSurveySeconds = 30f;

        private float feedMissingSince = -1f;
        private float nextFeedSurvey;
        private bool warnedNoFeedPrefab;
        private readonly List<ZDO> feedScratch = new List<ZDO>();

        // The owner's parsed queue, kept for as long as the ZDO holds the same array, so the item being
        // ground is decoded once rather than every tick.
        private byte[] queueRaw;
        private List<GrinderQueue.Entry> queue;

        // Not gated on a local player: a dedicated server owns every persistent object around the world's
        // centre (its reference position never moves from there), so a Grinder built near the spawn is
        // ground by the server.
        private void Tick() {
            if (!IsLive || !nview.IsOwner() || ObjectDB.instance == null || ZDOMan.instance == null) { return; }
            try {
                OwnerTick();
            } catch (Exception ex) {
                // Read the queue back off the ZDO next time, whatever this left in the cached copy.
                ForgetQueue();
                Logger.LogError($"[Grinder] tick failed: {ex}");
            }
        }

        private void OwnerTick() {
            ZDO zdo = nview.GetZDO();
            MultiUserChestIntegration.KeepExclusive(nview);
            EnsureFeed();
            // A pull that had to wait for the hopper.
            TryLoad(zdo);

            List<GrinderQueue.Entry> entries = OwnerQueue(zdo);
            State state = CurrentState;
            if (entries.Count == 0) {
                if (state != State.Idle) { Anchor(zdo, 0f, State.Idle); }
                return;
            }
            if (Disabled) {
                if (state != State.Disabled) { Anchor(zdo, Progress(), State.Disabled); }
                return;
            }
            // Only items made by a mod this game no longer has: nothing to work on, and no time banked for
            // whatever comes next.
            int head = FirstReadable(entries);
            if (head < 0) {
                if (state != State.Stuck) { Anchor(zdo, 0f, State.Stuck); }
                return;
            }

            float seconds = SecondsPerItem;
            float progress = Progress();
            // What it cannot grind goes straight through, without waiting for a batch's time.
            bool headGrinds = GrinderRecipes.CanGrind(GrinderQueue.Decode(entries[head]));
            if (progress < seconds && headGrinds) {
                // Coming back from a pause: the clock starts again from where it stopped.
                if (state != State.Working) { Anchor(zdo, progress, State.Working); }
                return;
            }

            // Someone has the chest open. Writing to it under them would blank their panel, so the batch
            // waits until they close it.
            if (Store == null || StorageOwnership.IsBusy(Store)) {
                if (state != State.Waiting) { Anchor(zdo, progress, State.Waiting); }
                return;
            }
            // For up to a second after this client becomes the owner its grid can be older than the one the
            // previous owner saved.
            StorageOwnership.Sync(Store);
            Inventory inv = Store.GetInventory();
            if (inv == null) { return; }

            State written = state;
            State next = GrindBatches(zdo, entries, inv, head, headGrinds, seconds, ref progress, ref written);

            // Written only when something changed: a held batch is re-checked every tick, but stays put.
            if (entries.Count > 0 && written != next) {
                Anchor(zdo, progress, next);
            }
        }

        /// <summary>
        /// Grinds batches off the front of the queue, up to the per-tick budget, for as long as the clock has
        /// time banked for them and the results chest has room. Returns the state the Grinder should be left
        /// in; <paramref name="written"/> is the state last saved with a batch, and <paramref name="progress"/>
        /// the clock after the batches taken.
        /// </summary>
        private State GrindBatches(ZDO zdo, List<GrinderQueue.Entry> entries, Inventory inv, int head, bool headGrinds, float seconds, ref float progress, ref State written) {
            Dictionary<string, long> owed = GrinderRemainders.Load(zdo);
            List<OutputFit.Output> outputs = new List<OutputFit.Output>();
            List<string> produced = new List<string>();
            int budget = Mathf.Max(1, ValConfig.GrinderBatchesPerTick.Value);
            State next = State.Working;

            for (int batches = 0; batches < budget; ++batches) {
                // The first batch is the head the caller has already looked at.
                int index = batches == 0 ? head : FirstReadable(entries);
                if (index < 0) { break; }
                GrinderQueue.Entry entry = entries[index];
                ItemDrop.ItemData item = GrinderQueue.Decode(entry);
                bool grind = batches == 0 ? headGrinds : GrinderRecipes.CanGrind(item);
                if (grind && progress < seconds) { break; }

                // One craft's worth at a time: one sword, or the twenty arrows a craft makes.
                int units = Mathf.Clamp(GrinderRecipes.CraftUnit(item), 1, entry.Stack);
                Dictionary<string, long> trial = new Dictionary<string, long>(owed);
                outputs.Clear();
                if (grind && Payout(item, units, trial, outputs)) {
                    progress -= seconds;
                } else {
                    // Not something this Grinder breaks down - or no longer, since it was queued. It comes out
                    // in the results chest as it went in, and takes no time.
                    grind = false;
                    units = entry.Stack;
                    ItemDrop.ItemData whole = item.Clone();
                    whole.m_stack = 1;
                    outputs.Add(new OutputFit.Output { Template = whole, Amount = units });
                }
                if (!OutputFit.Fits(inv, outputs)) {
                    if (grind) { progress += seconds; }
                    next = State.OutputFull;
                    break;
                }
                OutputFit.Add(inv, outputs);
                if (grind) {
                    owed = trial;
                    produced.Add(entry.Name);
                }
                if (units >= entry.Stack) {
                    entries.RemoveAt(index);
                } else {
                    GrinderQueue.SetStack(entry, item, entry.Stack - units);
                }

                // The chest saved itself the moment the output went in, so the queue, what is owed and the
                // clock are committed with it, batch by batch: if the next batch throws, or the Grinder
                // changes hands, the ZDO still agrees with what is in the chest.
                GrinderRemainders.Save(zdo, owed);
                SaveQueue(zdo);
                written = entries.Count == 0 ? State.Idle : State.Working;
                Anchor(zdo, entries.Count == 0 ? 0f : progress, written);
                if (entries.Count == 0) { break; }
            }

            if (produced.Count > 0) {
                PlayEffect(FxProduce);
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Grinder] ground {string.Join(", ", produced)} at {Mathf.RoundToInt(Yield * 100f)}%.");
                }
            }
            return next;
        }

        /// <summary>
        /// What grinding <paramref name="units"/> of <paramref name="item"/> pays out now: its share of the
        /// cost goes onto what is owed, and every whole unit owed is taken off and paid. False when the item
        /// has no recipe to work from.
        /// </summary>
        private bool Payout(ItemDrop.ItemData item, int units, Dictionary<string, long> owed, List<OutputFit.Output> outputs) {
            Dictionary<string, long> gained = new Dictionary<string, long>();
            if (!GrinderRecipes.AddReturns(item, units, Yield, gained)) { return false; }

            foreach (KeyValuePair<string, long> entry in gained) {
                string key = GrinderRemainders.KeyFor(entry.Key, item.m_cheated);
                owed.TryGetValue(key, out long have);
                owed[key] = have + entry.Value;
            }

            // No better than the item it came from, and no better than this world makes.
            int worldLevel = Mathf.Min(item.m_worldLevel, Game.m_worldLevel);
            foreach (string key in new List<string>(owed.Keys)) {
                long whole = owed[key] / 1000;
                if (whole <= 0) { continue; }
                GameObject prefab = ObjectDB.instance.GetItemPrefab(GrinderRemainders.PrefabOf(key, out bool cheated));
                ItemDrop.ItemData template = OutputFit.Material(prefab, cheated, worldLevel);
                // A material that has since left the game stays owed rather than being paid as nothing.
                if (template == null) { continue; }
                outputs.Add(new OutputFit.Output { Template = template, Amount = (int)whole });
                owed[key] -= whole * 1000;
            }
            return true;
        }

        // ---- the queue -------------------------------------------------------------------

        private List<GrinderQueue.Entry> OwnerQueue(ZDO zdo) {
            byte[] raw = zdo.GetByteArray(GrinderQueue.QueueKey);
            if (queue == null || !ReferenceEquals(raw, queueRaw)) {
                queue = GrinderQueue.Parse(raw);
                queueRaw = raw;
            }
            return queue;
        }

        private void ForgetQueue() {
            queue = null;
            queueRaw = null;
        }

        private void SaveQueue(ZDO zdo) {
            queueRaw = GrinderQueue.Save(zdo, queue);
        }

        private static int FirstReadable(List<GrinderQueue.Entry> entries) {
            for (int i = 0; i < entries.Count; ++i) {
                if (GrinderQueue.Decode(entries[i]) != null) { return i; }
            }
            return -1;
        }

        // Starts the clock from here: P seconds done, accruing at the current speed from now on.
        private void Anchor(ZDO zdo, float progress, State state) {
            zdo.Set(ProgressKey, Mathf.Max(0f, progress));
            zdo.Set(TimeKey, ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L);
            zdo.Set(SpeedKey, Speed);
            zdo.Set(StateKey, (int)state);
        }

        /// <summary>
        /// Owner side of a pulled lever: moves everything in the hopper, in grid order, into the queue - as
        /// much as there is room for; the rest stays in the hopper for the next pull. Waits, with the pull
        /// still pending, while someone has the hopper open or it is on its way here.
        /// </summary>
        private void TryLoad(ZDO zdo) {
            if (!zdo.GetBool(PullKey)) { return; }
            GrinderFeed hopper = Feed;
            if (hopper == null || hopper.Box == null || StorageOwnership.IsBusy(hopper.Box)) { return; }
            if (!hopper.IsOwner) {
                GrinderOps.AskForFeed(hopper);
                return;
            }
            StorageOwnership.Sync(hopper.Box);
            Inventory inv = hopper.Box.GetInventory();
            if (inv == null) { return; }

            List<GrinderQueue.Entry> entries = OwnerQueue(zdo);
            bool wasEmpty = entries.Count == 0;
            int room = Mathf.Max(0, ValConfig.GrinderQueueSize.Value - entries.Count);
            int bytes = GrinderQueue.Bytes(entries);
            List<GrinderQueue.Entry> added = new List<GrinderQueue.Entry>();
            List<ItemDrop.ItemData> taken = new List<ItemDrop.ItemData>();
            foreach (ItemDrop.ItemData item in InGridOrder(inv)) {
                if (taken.Count >= room) { break; }
                GrinderQueue.Entry entry = GrinderQueue.NewEntry(item);
                if (bytes + entry.Blob.Length > GrinderQueue.MaxBytes) { break; }
                bytes += entry.Blob.Length;
                added.Add(entry);
                taken.Add(item);
            }
            zdo.Set(PullKey, false);
            if (taken.Count == 0) { return; }

            // Into the queue only once every entry is built, so one that fails to build leaves the queue as
            // the ZDO has it - a half-loaded queue kept here would be loaded again on the next pull, and the
            // items saved twice. Saved before the hopper lets go, so a failure in between leaves an item in
            // both rather than in neither.
            entries.AddRange(added);
            SaveQueue(zdo);
            foreach (ItemDrop.ItemData item in taken) { inv.RemoveItem(item); }
            if (wasEmpty) { Anchor(zdo, 0f, State.Working); }
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Grinder] loaded {taken.Count} stacks from the hopper; {entries.Count} queued.");
            }
        }

        private static List<ItemDrop.ItemData> InGridOrder(Inventory inv) {
            List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>(inv.GetAllItems());
            items.Sort((a, b) => a.m_gridPos.y != b.m_gridPos.y ? a.m_gridPos.y.CompareTo(b.m_gridPos.y) : a.m_gridPos.x.CompareTo(b.m_gridPos.x));
            return items;
        }

        // ---- the hopper ------------------------------------------------------------------

        /// <summary>
        /// Spawns the hopper when the Grinder has none - just built, or its hopper is gone. A hopper whose ZDO
        /// has not arrived yet is given a moment before it is counted as gone.
        /// </summary>
        private void EnsureFeed() {
            // A hopper loaded here at the FeedPoint is the answer, without looking through the zone.
            if (Feed != null && Time.time < nextFeedSurvey) {
                feedMissingSince = -1f;
                return;
            }
            nextFeedSurvey = Time.time + FeedSurveySeconds;

            GrinderLink.ZdosAt(FeedPosition, GrinderLink.FeedHash, feedScratch);
            int found = feedScratch.Count;
            if (found > 1) { RemoveExtraFeeds(feedScratch); }
            feedScratch.Clear();
            if (found > 0) {
                feedMissingSince = -1f;
                return;
            }
            // Missing: looked for again every tick until it turns up or is replaced.
            nextFeedSurvey = 0f;
            if (feedMissingSince < 0f) { feedMissingSince = Time.time; }
            if (Time.time - feedMissingSince < FeedGraceSeconds) { return; }

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(GrinderFeed.PrefabName) : null;
            if (prefab == null) {
                if (!warnedNoFeedPrefab) {
                    Logger.LogWarning($"[Grinder] the {GrinderFeed.PrefabName} prefab is not registered; the Grinder has no hopper.");
                    warnedNoFeedPrefab = true;
                }
                return;
            }
            GameObject go = Instantiate(prefab, FeedPosition, transform.rotation);
            ZNetView feedView = go.GetComponent<ZNetView>();
            if (feedView == null || feedView.GetZDO() == null) {
                Destroy(go);
                return;
            }
            feed = go.GetComponent<GrinderFeed>();
            feedMissingSince = -1f;
            if (ValConfig.EnableDebugMode.Value) { Logger.LogInfo("[Grinder] spawned a hopper."); }
        }

        /// <summary>
        /// More than one hopper at this Grinder - an earlier build linked the two by ZDOID, lost the link on a
        /// restart and spawned another. The one holding the most stays; the rest empty onto the ground and go.
        /// Waits until every one of them is loaded here and none is open.
        /// </summary>
        private void RemoveExtraFeeds(List<ZDO> hoppers) {
            List<GrinderFeed> loaded = new List<GrinderFeed>();
            foreach (ZDO zdo in hoppers) {
                ZNetView view = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo) : null;
                GrinderFeed hopper = view != null ? view.GetComponent<GrinderFeed>() : null;
                if (hopper == null || hopper.Box == null || hopper.Box.GetInventory() == null) { return; }
                if (StorageOwnership.IsBusy(hopper.Box)) { return; }
                loaded.Add(hopper);
            }
            GrinderFeed keep = null;
            foreach (GrinderFeed hopper in loaded) {
                if (keep == null || hopper.Box.GetInventory().NrOfItems() > keep.Box.GetInventory().NrOfItems()) { keep = hopper; }
            }
            foreach (GrinderFeed hopper in loaded) {
                if (hopper == keep) { continue; }
                Logger.LogWarning($"[Grinder] found a second hopper on one Grinder; removing it and dropping the {hopper.Box.GetInventory().NrOfItems()} stacks in it.");
                GrinderOps.DisposeFeed(hopper);
            }
            feed = keep;
        }
    }
}
