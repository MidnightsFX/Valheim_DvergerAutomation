using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Materials taken out of other players' chests for a craft or a build this client is about to make.
    ///
    /// A chest another player has open cannot be handed over, so its owner takes the materials out
    /// instead (see <see cref="StorageOps"/>) and sends them here. Vanilla hands out the crafted item or
    /// places the piece before it spends anything, so the materials have to be in hand before the action
    /// starts: they are fetched first, held here, and spent from here when the action goes through.
    ///
    /// Whatever is not spent goes back. Each item remembers when something last wanted it, and one that
    /// has gone unwanted for <see cref="GraceSeconds"/> is returned to the chest it came from, or failing
    /// that to the player's pack, or failing that to the ground at their feet. That one rule covers a
    /// cancelled craft, a closed panel, a changed recipe and a put-away Hammer without a hook on any of
    /// them.
    ///
    /// The reserve lives in memory only. A crash while it holds something loses it; leaving the game the
    /// ordinary way does not (see <see cref="FlushToPlayer"/>). It never holds more than one action's
    /// shortfall, for about as long as that action takes.
    /// </summary>
    internal static class StorageReserve {
        internal enum Readiness {
            /// <summary>Everything the pool has to supply can be spent right now.</summary>
            Covered,
            /// <summary>The pool has enough, but some of it is still on its way here.</summary>
            Pending,
            /// <summary>The pool as a whole is short. Vanilla turns the action down by itself.</summary>
            Short,
        }

        private const float GraceSeconds = 3f;

        // How long a closed chest gets to be handed over before its owner is asked to take the materials
        // out instead. Its owner may be crafting from it too, and a refused handoff is silent.
        private const float HandoverPatience = 1f;

        // A chest that came back short is left alone until this client's copy of it has caught up.
        private const float RetrySeconds = 1f;

        private const int RefundAttempts = 3;
        private const float TickInterval = 0.25f;

        private sealed class Entry {
            internal ZDOID Source;
            internal ItemDrop.ItemData Item;
            internal float LastWanted;
            internal int Attempts;
        }

        private struct PendingTake {
            internal ZDOID Chest;
            internal string Name;
            internal int Amount;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<int, PendingTake> Takes = new Dictionary<int, PendingTake>();
        private static readonly Dictionary<ZDOID, float> RetryAt = new Dictionary<ZDOID, float>();
        private static readonly List<Entry> RefundScratch = new List<Entry>();
        private static int nextToken;
        private static float nextTick;

        /// <summary>A new session: nothing held or asked for in the last one means anything in this one.</summary>
        internal static void Reset() {
            Entries.Clear();
            Takes.Clear();
            RetryAt.Clear();
        }

        // ---- reading and spending ---------------------------------------------------

        /// <summary>How many of a material are held here. Asked per ingredient row every frame, so free when empty.</summary>
        internal static int Count(string name) {
            if (Entries.Count == 0) { return 0; }
            int total = 0;
            foreach (Entry entry in Entries) {
                if (CraftFromStoragePatches.Matches(entry.Item, name, -1)) { total += entry.Item.m_stack; }
            }
            return total;
        }

        /// <summary>Spends up to <paramref name="amount"/> of a material held here and returns how many it was.</summary>
        internal static int Spend(string name, int amount, int quality) {
            int spent = 0;
            for (int i = Entries.Count - 1; i >= 0 && amount > 0; --i) {
                ItemDrop.ItemData item = Entries[i].Item;
                if (!CraftFromStoragePatches.Matches(item, name, quality)) { continue; }
                int take = Mathf.Min(item.m_stack, amount);
                item.m_stack -= take;
                amount -= take;
                spent += take;
                if (item.m_stack <= 0) { Entries.RemoveAt(i); }
            }
            return spent;
        }

        // ---- fetching ---------------------------------------------------------------

        /// <summary>
        /// Checks, before anything is spent, that everything the storage pool has to supply is either in
        /// chests this client owns or already held here - and sets about fetching whatever is not. Closed
        /// chests are asked for whole, as before; open ones, vehicles in use and chests whose owner will
        /// not let go are asked for just the materials. Cheap to call every frame while an action waits:
        /// nothing is asked for twice.
        /// </summary>
        internal static Readiness Ensure(List<Container> pool, List<KeyValuePair<string, int>> shortfalls) {
            if (shortfalls.Count == 0) { return Readiness.Covered; }

            bool waiting = false;
            foreach (KeyValuePair<string, int> shortfall in shortfalls) {
                string name = shortfall.Key;
                Touch(name);
                int reserved = Count(name);
                int ready = ContainerNetwork.CountSpendableInPool(pool, name) + reserved;
                if (ready >= shortfall.Value) { continue; }

                int inFlight = InFlight(name);
                if (ContainerNetwork.CountInPool(pool, name) + reserved + inFlight < shortfall.Value) { return Readiness.Short; }

                waiting = true;
                int missing = shortfall.Value - ready - inFlight;
                if (missing > 0) { Ask(pool, name, missing); }
            }
            if (!waiting) { return Readiness.Covered; }

            // A chest nobody owned was simply taken (see StorageOwnership.Request), so the gap may have
            // closed on the spot.
            ContainerNetwork.InvalidateItemCounts();
            foreach (KeyValuePair<string, int> shortfall in shortfalls) {
                if (ContainerNetwork.CountSpendableInPool(pool, shortfall.Key) + Count(shortfall.Key) < shortfall.Value) {
                    return Readiness.Pending;
                }
            }
            return Readiness.Covered;
        }

        // In pool order, the same order RemoveFromContainers spends in.
        private static void Ask(List<Container> pool, string name, int missing) {
            float now = Time.time;
            foreach (Container container in pool) {
                if (missing <= 0) { return; }
                if (container == null || container.m_nview == null || !container.m_nview.IsValid()) { continue; }
                // Its stock already counts as ready.
                if (container.m_nview.IsOwner()) { continue; }
                int held = Held(container, name);
                if (held <= 0) { continue; }

                // Handing a closed chest over whole is the cheaper way: everything after the first unit is
                // then spent without asking anyone.
                if (StorageOwnership.CanAskHandover(container)) {
                    StorageOwnership.Want(container);
                    if (StorageOwnership.WaitedFor(container) < HandoverPatience) {
                        missing -= held;
                        continue;
                    }
                }

                ZDOID chest = container.m_nview.GetZDO().m_uid;
                if (RetryAt.TryGetValue(chest, out float at) && at > now) { continue; }
                // This client's copy still shows what an earlier request is already bringing.
                if (HasInFlight(chest, name)) { continue; }

                int amount = Mathf.Min(held, missing);
                SendTake(container, chest, name, amount);
                missing -= amount;
            }
        }

        private static void SendTake(Container container, ZDOID chest, string name, int amount) {
            int token = ++nextToken;
            Takes[token] = new PendingTake { Chest = chest, Name = name, Amount = amount };
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Storage] asking the owner of {container.name} for {amount} {name}.");
            }
            StorageRpc.Send(container, StorageRpc.Op.Take,
                args => StorageOps.WriteTake(args, name, -1, amount),
                (status, reply, late) => OnTaken(token, chest, status, reply));
        }

        // Also runs for a reply that arrives after the request was given up on: the items it carries
        // have left their chest either way, and from here they find their way back.
        private static void OnTaken(int token, ZDOID chest, StorageRpc.Status status, ZPackage reply) {
            Takes.Remove(token);
            if (reply != null && (status == StorageRpc.Status.Ok || status == StorageRpc.Status.Partial)) {
                List<ItemDrop.ItemData> items = ItemCodec.Read(reply, out bool intact);
                if (!intact) {
                    Logger.LogWarning("[Storage] some materials sent from another player's chest could not be rebuilt here and are lost.");
                }
                float now = Time.time;
                foreach (ItemDrop.ItemData item in items) {
                    Entries.Add(new Entry { Source = chest, Item = item, LastWanted = now });
                }
            }
            if (status != StorageRpc.Status.Ok) { RetryAt[chest] = Time.time + RetrySeconds; }
        }

        private static void Touch(string name) {
            if (Entries.Count == 0) { return; }
            float now = Time.time;
            foreach (Entry entry in Entries) {
                if (entry.Item.m_shared.m_name == name) { entry.LastWanted = now; }
            }
        }

        private static int InFlight(string name) {
            if (Takes.Count == 0) { return 0; }
            int total = 0;
            foreach (PendingTake take in Takes.Values) {
                if (take.Name == name) { total += take.Amount; }
            }
            return total;
        }

        private static bool HasInFlight(ZDOID chest, string name) {
            foreach (PendingTake take in Takes.Values) {
                if (take.Chest == chest && take.Name == name) { return true; }
            }
            return false;
        }

        private static int Held(Container container, string name) {
            Inventory inv = container.GetInventory();
            if (inv == null) { return 0; }
            int held = 0;
            foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                if (CraftFromStoragePatches.Matches(item, name, -1)) { held += item.m_stack; }
            }
            return held;
        }

        // ---- giving back ------------------------------------------------------------

        /// <summary>Returns what nothing has wanted for a while. Run every frame; does its work a few times a second.</summary>
        internal static void Tick() {
            if (Entries.Count == 0 || Time.time < nextTick) { return; }
            nextTick = Time.time + TickInterval;
            if (RetryAt.Count > 256) { RetryAt.Clear(); }

            float now = Time.time;
            // A craft in progress has been paid for out of here and has not spent it yet. Its bar can run
            // far longer than the grace period.
            bool crafting = InventoryGui.instance != null && InventoryGui.instance.m_craftTimer >= 0f;
            RefundScratch.Clear();
            for (int i = Entries.Count - 1; i >= 0; --i) {
                Entry entry = Entries[i];
                if (crafting) {
                    entry.LastWanted = now;
                    continue;
                }
                if (now - entry.LastWanted < GraceSeconds) { continue; }
                Entries.RemoveAt(i);
                RefundScratch.Add(entry);
            }

            // One request per chest.
            while (RefundScratch.Count > 0) {
                ZDOID source = RefundScratch[RefundScratch.Count - 1].Source;
                List<Entry> group = new List<Entry>();
                for (int i = RefundScratch.Count - 1; i >= 0; --i) {
                    if (RefundScratch[i].Source != source) { continue; }
                    group.Add(RefundScratch[i]);
                    RefundScratch.RemoveAt(i);
                }
                GiveBack(source, group);
            }
        }

        private static void GiveBack(ZDOID source, List<Entry> group) {
            Container container = StorageOwnership.FindStorage(source);
            if (container == null) {
                ToPlayer(group);
                return;
            }

            List<ItemDrop.ItemData> items = new List<ItemDrop.ItemData>(group.Count);
            foreach (Entry entry in group) { items.Add(entry.Item); }
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[Storage] returning {items.Count} unused stacks to {container.name}.");
            }
            // The owner answers with amounts, not items: the group stays here as the copy to restore from.
            StorageRpc.Send(container, StorageRpc.Op.Give,
                args => ItemCodec.Write(args, items),
                (status, reply, late) => OnGiven(group, status, reply, late),
                giveUpOnOwnerChange: false);
        }

        private static void OnGiven(List<Entry> group, StorageRpc.Status status, ZPackage reply, bool late) {
            // The group went to the player when no answer came. If the owner did apply it after all, the
            // items now exist twice - the price of never losing them.
            if (late) { return; }

            switch (status) {
                case StorageRpc.Status.Ok:
                    return;
                case StorageRpc.Status.Partial:
                    // Amounts in the order sent. A count that does not line up cannot be trusted.
                    if (reply != null && reply.ReadInt() == group.Count) {
                        foreach (Entry entry in group) { entry.Item.m_stack -= reply.ReadInt(); }
                        group.RemoveAll(entry => entry.Item.m_stack <= 0);
                    }
                    ToPlayer(group);
                    return;
                case StorageRpc.Status.NotOwner:
                    // The chest changed hands on the way. Back on the pile, already overdue, so the next
                    // tick sends them to whoever has it now.
                    for (int i = group.Count - 1; i >= 0; --i) {
                        Entry entry = group[i];
                        if (++entry.Attempts >= RefundAttempts) { continue; }
                        entry.LastWanted = Time.time - GraceSeconds;
                        Entries.Add(entry);
                        group.RemoveAt(i);
                    }
                    ToPlayer(group);
                    return;
                default:
                    ToPlayer(group);
                    return;
            }
        }

        private static void ToPlayer(List<Entry> group) {
            if (group.Count == 0) { return; }
            Player player = Player.m_localPlayer;
            if (player == null) {
                // Dead or between worlds: no pack to put them in and no feet to drop them at. Held until
                // there is.
                float now = Time.time;
                foreach (Entry entry in group) {
                    entry.LastWanted = now;
                    Entries.Add(entry);
                }
                return;
            }
            foreach (Entry entry in group) { GiveToPlayer(player, entry.Item); }
        }

        private static void GiveToPlayer(Player player, ItemDrop.ItemData item) {
            int rest = item.m_stack - AutoStore.AddMeasured(player.GetInventory(), item, item.m_stack);
            if (rest <= 0) { return; }
            if (item.m_dropPrefab == null) {
                Logger.LogWarning($"[Storage] {rest} {item.m_shared.m_name} had nowhere to go back to and could not be dropped.");
                return;
            }
            ItemDrop.DropItem(item, rest, player.transform.position + player.transform.forward + Vector3.up, Quaternion.identity);
        }

        /// <summary>
        /// Gives the player an item that has nowhere else to go back to: into their pack, overflow at
        /// their feet. With no player to give it to it is kept here until there is one.
        /// </summary>
        internal static void HandToPlayer(ItemDrop.ItemData item) {
            Player player = Player.m_localPlayer;
            if (player != null) {
                GiveToPlayer(player, item);
                return;
            }
            // No chest to go back to, so the refund pass hands it straight to the player once one exists.
            Entries.Add(new Entry { Source = ZDOID.None, Item = item, LastWanted = Time.time });
        }

        /// <summary>
        /// Empties the reserve into the player's pack, overflow at their feet. For leaving the game: there
        /// is no time left for a round trip to the chests, and the pack is saved with the character.
        /// </summary>
        internal static void FlushToPlayer(Player player) {
            if (Entries.Count == 0 || player == null) { return; }
            foreach (Entry entry in Entries) { GiveToPlayer(player, entry.Item); }
            Entries.Clear();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
    internal static class Game_Shutdown_StorageReserve_Patch {
        // A prefix: Shutdown saves the character first thing.
        private static void Prefix(Game __instance) {
            if (__instance.m_shuttingDown) { return; }
            try {
                StorageReserve.FlushToPlayer(Player.m_localPlayer);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] emptying the storage reserve on the way out failed: {ex}");
            }
        }
    }
}
