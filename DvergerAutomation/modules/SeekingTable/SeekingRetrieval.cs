using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Takes items out of the chests around a Seeking Table for the local player.
    ///
    /// Every take is asked of the chest's owner (<see cref="StorageRpc.Op.TakeExact"/>), the same way
    /// crafting asks for materials from a chest someone else has open: the owner removes the items from
    /// its own copy and sends them back, so nothing is ever written to a chest by anyone but its owner
    /// (see <see cref="StorageOwnership"/>). A chest this client owns answers on the spot.
    ///
    /// What comes back goes into the pack, overflow at the player's feet - including a reply that turns
    /// up after the request was given up on, since by then the items have already left the chest.
    ///
    /// Items lying on the ground are taken the way vanilla picks one up: the item is asked of whoever
    /// simulates it (<c>ItemDrop.RequestOwn</c>, which its owner answers by handing it over), and once it
    /// is this client's, the units are taken off it and it is removed or shrunk. Unlike a vanilla pickup,
    /// part of a pile can be taken.
    /// </summary>
    internal static class SeekingRetrieval {
        // One take's worth of units still to arrive, so a second click is not offered room the first has
        // already spoken for.
        private sealed class Delivery {
            internal ItemDrop.ItemData Prototype;
            internal int Amount;
        }

        private static readonly List<Delivery> Deliveries = new List<Delivery>();
        private static readonly Dictionary<Container, List<SeekSource>> ByChest = new Dictionary<Container, List<SeekSource>>();
        private static readonly List<Container> ChestOrder = new List<Container>();

        /// <summary>
        /// How many units of <paramref name="item"/> the pack can still take: room on matching stacks and
        /// in empty slots, less what takes already out will fill.
        /// </summary>
        internal static int Room(ItemDrop.ItemData item) {
            Player player = Player.m_localPlayer;
            if (player == null) { return 0; }
            int max = Mathf.Max(1, item.m_shared.m_maxStackSize);
            Inventory inv = player.GetInventory();
            int room = SlotModLimit(inv, item, InventoryOps.FreeSpaceFor(inv, item));
            foreach (Delivery delivery in Deliveries) {
                if (max > 1 && ItemIdentity.SameStack(delivery.Prototype, item)) {
                    room -= delivery.Amount;
                } else {
                    // Something else on its way takes whole slots, which would otherwise have held a stack
                    // of this.
                    int itsMax = Mathf.Max(1, delivery.Prototype.m_shared.m_maxStackSize);
                    room -= ((delivery.Amount + itsMax - 1) / itsMax) * max;
                }
            }
            return Mathf.Max(0, room);
        }

        // An inventory mod's quick and equipment slots are extra rows of the same grid (see
        // InventorySlotsIntegration). Each of the three already answers GetEmptySlots for the pack alone, plus
        // its free quick slots, which a take does fill: their AddItem moves on to them once the pack is full.
        // AzuEPI's also counts free equipment slots, which only take gear, so asking for that much wood would
        // land the rest at the player's feet. The mod's CanAddItem - the question vanilla's pickup asks, and
        // which each one answers for the item at hand - has the last word.
        private static int SlotModLimit(Inventory inv, ItemDrop.ItemData item, int room) {
            if (room <= 0 || !InventorySlotsIntegration.Active || inv.CanAddItem(item, room)) { return room; }
            // CanAddItem reads a stack of 0 as "the item's own stack", so the search never asks for none.
            int fits = 0;
            int low = 1;
            int high = room - 1;
            while (low <= high) {
                int mid = low + (high - low) / 2;
                if (inv.CanAddItem(item, mid)) {
                    fits = mid;
                    low = mid + 1;
                } else {
                    high = mid - 1;
                }
            }
            return fits;
        }

        /// <summary>The most of <paramref name="entry"/> one take may ask for.</summary>
        internal static int MaxTake(SeekEntry entry) {
            return Mathf.Min(entry.Total, Room(entry.Sample));
        }

        /// <summary>
        /// Takes up to <paramref name="amount"/> units of <paramref name="entry"/>, clamped to what is there
        /// and what the pack can hold. Anything on the ground goes first, nearest first: it is what the
        /// player switched the ground on for, and unlike a chest it can despawn. Then chests this client
        /// owns, since they answer at once, then the nearest.
        /// </summary>
        internal static void Take(SeekingTable table, SeekEntry entry, int amount) {
            Player player = Player.m_localPlayer;
            if (player == null || table == null || entry == null) { return; }
            int room = Room(entry.Sample);
            if (room <= 0) {
                player.Message(MessageHud.MessageType.Center, "$inventory_full");
                return;
            }
            amount = Mathf.Min(amount, Mathf.Min(entry.Total, room));
            if (amount <= 0) { return; }

            Vector3 origin = table.transform.position;
            int remaining = TakeFromGround(table, entry, amount, origin);

            // One request per chest: the owner finds every matching unit in it itself.
            ByChest.Clear();
            ChestOrder.Clear();
            foreach (SeekSource source in entry.Sources) {
                if (source.Ground || source.Chest == null) { continue; }
                if (!ByChest.TryGetValue(source.Chest, out List<SeekSource> list)) {
                    list = new List<SeekSource>();
                    ByChest[source.Chest] = list;
                    ChestOrder.Add(source.Chest);
                }
                list.Add(source);
            }
            ChestOrder.Sort((a, b) => {
                bool ownsA = a.m_nview != null && a.m_nview.IsOwner();
                bool ownsB = b.m_nview != null && b.m_nview.IsOwner();
                if (ownsA != ownsB) { return ownsA ? -1 : 1; }
                return (a.transform.position - origin).sqrMagnitude.CompareTo((b.transform.position - origin).sqrMagnitude);
            });

            foreach (Container chest in ChestOrder) {
                if (remaining <= 0) { break; }
                List<SeekingIndex.InFlight> held = new List<SeekingIndex.InFlight>();
                int fromChest = 0;
                SeekSource first = default;
                foreach (SeekSource source in ByChest[chest]) {
                    int available = source.Item.m_stack - SeekingIndex.InFlightOn(source.Item);
                    int take = Mathf.Min(available, remaining - fromChest);
                    if (take <= 0) { continue; }
                    if (fromChest == 0) { first = source; }
                    held.Add(SeekingIndex.HoldOff(source, take));
                    fromChest += take;
                }
                if (fromChest <= 0) { continue; }
                remaining -= fromChest;
                Request(table, entry.Sample, first, fromChest, held, retried: false);
            }
            ByChest.Clear();
            ChestOrder.Clear();
        }

        // ---- the ground -------------------------------------------------------------------

        // A ground item being taken, waiting on the item to become this client's.
        private sealed class Pickup {
            internal SeekingTable Table;
            internal ItemDrop Drop;
            internal ItemDrop.ItemData Prototype;
            internal int Amount;
            internal SeekingIndex.InFlight Held;
            internal Delivery Delivery;
            internal float Since;
        }

        private static readonly List<Pickup> Pickups = new List<Pickup>();
        private static readonly List<SeekSource> GroundScratch = new List<SeekSource>();
        // The owner hands an item over within a round trip; vanilla's own retry backs off from 0.2s.
        private const float PickupWaitSeconds = 3f;

        /// <summary>Starts taking what the entry has lying on the ground, nearest first. Returns how much is still to take.</summary>
        private static int TakeFromGround(SeekingTable table, SeekEntry entry, int remaining, Vector3 origin) {
            GroundScratch.Clear();
            foreach (SeekSource source in entry.Sources) {
                // Picked up or despawned since the list was drawn: there is nothing left to ask for.
                if (!source.Ground || source.Drop == null || source.Drop.m_nview == null || !source.Drop.m_nview.IsValid()) { continue; }
                GroundScratch.Add(source);
            }
            GroundScratch.Sort((a, b) =>
                (a.Drop.transform.position - origin).sqrMagnitude.CompareTo((b.Drop.transform.position - origin).sqrMagnitude));

            foreach (SeekSource source in GroundScratch) {
                if (remaining <= 0) { break; }
                int take = Mathf.Min(source.Item.m_stack - SeekingIndex.InFlightOn(source.Item), remaining);
                if (take <= 0) { continue; }
                remaining -= take;

                Pickup pickup = new Pickup {
                    Table = table,
                    Drop = source.Drop,
                    // What it was when clicked, to check it is still that once it is ours.
                    Prototype = source.Item.Clone(),
                    Amount = take,
                    Held = SeekingIndex.HoldOff(source, take),
                    Delivery = new Delivery { Prototype = source.Item.Clone(), Amount = take },
                    Since = Time.time,
                };
                Deliveries.Add(pickup.Delivery);
                if (!TryFinish(pickup)) { Pickups.Add(pickup); }
            }
            GroundScratch.Clear();
            return remaining;
        }

        /// <summary>
        /// Takes the units once the item is this client's, asking for it again until then. True when the
        /// pickup is settled either way.
        /// </summary>
        private static bool TryFinish(Pickup pickup) {
            ItemDrop drop = pickup.Drop;
            if (drop == null || drop.m_nview == null || !drop.m_nview.IsValid()) {
                Settle(pickup);
                Report(StorageRpc.Status.NotFound);
                return true;
            }
            if (!drop.CanPickup(autoPickupDelay: false)) {
                if (Time.time - pickup.Since > PickupWaitSeconds) {
                    Settle(pickup);
                    Report(StorageRpc.Status.NotOwner);
                    return true;
                }
                // Its own back-off decides whether this actually asks again.
                drop.RequestOwn();
                return false;
            }

            // Ours now, and possibly changed on the way: someone took part of it, or it merged with a pile
            // beside it. Read it fresh and check it is still what was clicked.
            drop.Load();
            ItemDrop.ItemData item = drop.m_itemData;
            Player player = Player.m_localPlayer;
            if (player == null || !ItemIdentity.Interchangeable(pickup.Prototype, item)) {
                Settle(pickup);
                Report(StorageRpc.Status.Changed);
                return true;
            }
            // The one rule vanilla's pickup has that a chest does not: a quest item the player already has.
            if (item.m_shared.m_questItem && player.HaveUniqueKey(item.m_shared.m_name)) {
                Settle(pickup);
                player.Message(MessageHud.MessageType.Center, "$msg_cantpickup");
                return true;
            }

            int take = Mathf.Min(pickup.Amount, item.m_stack);
            ItemDrop.ItemData slice = item.Clone();
            slice.m_stack = take;
            slice.m_equipped = false;
            Vector3 where = drop.transform.position;
            // Off the ground first, then into the pack, as a chest's owner does it: the units never exist
            // twice.
            if (take >= item.m_stack) {
                ZNetScene.instance.Destroy(drop.gameObject);
            } else {
                drop.SetStack(item.m_stack - take);
            }
            Settle(pickup);
            PlayerDelivery.Give(slice);
            player.ShowPickupMessage(slice, take);
            if (pickup.Table != null) { pickup.Table.ShowRetrieval(where); }
            SeekingIndex.MarkDirty();
            return true;
        }

        private static void Settle(Pickup pickup) {
            Deliveries.Remove(pickup.Delivery);
            SeekingIndex.Release(pickup.Held);
        }

        // ---- chests -------------------------------------------------------------------------

        private static void Request(SeekingTable table, ItemDrop.ItemData prototype, SeekSource first, int amount, List<SeekingIndex.InFlight> held, bool retried) {
            Container chest = first.Chest;
            if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid()) {
                SeekingIndex.Release(held);
                Report(StorageRpc.Status.NotFound);
                return;
            }
            Vector2i hint = first.Item.m_gridPos;
            Delivery delivery = new Delivery { Prototype = prototype.Clone(), Amount = amount };
            Deliveries.Add(delivery);
            bool settled = false;
            // Who the request goes to, so a "not mine any more" can wait for the chest to be someone else's.
            long sentTo = chest.m_nview.GetZDO().GetOwner();

            StorageRpc.Send(chest, StorageRpc.Op.TakeExact,
                args => StorageOps.WriteTakeExact(args, prototype, hint, amount),
                (status, reply, late) => {
                    // The first verdict settles the room this take spoke for; a late reply only delivers.
                    if (!settled) {
                        settled = true;
                        Deliveries.Remove(delivery);
                    }

                    int delivered = 0;
                    bool intact = true;
                    ItemDrop.ItemData shown = null;
                    if ((status == StorageRpc.Status.Ok || status == StorageRpc.Status.Partial) && reply != null) {
                        List<ItemDrop.ItemData> items = ItemCodec.Read(reply, out intact);
                        foreach (ItemDrop.ItemData item in items) {
                            delivered += item.m_stack;
                            shown = shown ?? item;
                            PlayerDelivery.Give(item);
                        }
                    }
                    if (!intact) {
                        // The owner has already taken them out; they existed only in the reply.
                        Logger.LogWarning($"[Seeking Table] {prototype.m_shared.m_name} came back from {(chest != null ? chest.name : "a chest")} as something this game has no prefab for, and was lost. Is the same set of mods installed for everyone?");
                        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "$DA_seek_unreadable");
                    }

                    if (delivered > 0) {
                        Player player = Player.m_localPlayer;
                        if (player != null && shown != null) { player.ShowPickupMessage(shown, delivered); }
                        if (!late && table != null && chest != null) {
                            table.ShowRetrieval(chest.transform.position + Vector3.up * 0.5f);
                        }
                        if (status == StorageRpc.Status.Partial) { SeekingIndex.Release(held); }
                        SeekingIndex.MarkDirty();
                        return;
                    }
                    if (late) { return; }

                    SeekingIndex.Release(held);
                    if (!intact) { return; }
                    // The chest changed hands between the click and the request arriving. Its new owner is
                    // asked once the handover reaches this client - the refusal travels faster than the
                    // chest does, so asking straight away would only reach the old owner again.
                    if (status == StorageRpc.Status.NotOwner && !retried) {
                        Retries.Add(new Retry {
                            Table = table, Prototype = prototype, First = first, Amount = amount,
                            Held = Rehold(held), StaleOwner = sentTo, Since = Time.time,
                        });
                        return;
                    }
                    Report(status);
                });
        }

        private sealed class Retry {
            internal SeekingTable Table;
            internal ItemDrop.ItemData Prototype;
            internal SeekSource First;
            internal int Amount;
            internal List<SeekingIndex.InFlight> Held;
            internal long StaleOwner;
            internal float Since;
        }

        private static readonly List<Retry> Retries = new List<Retry>();
        private const float RetryWaitSeconds = 3f;

        /// <summary>
        /// Finishes ground pickups whose item has become this client's, and sends the retries whose chest
        /// has reached its new owner. Run every frame.
        /// </summary>
        internal static void Tick() {
            for (int i = Pickups.Count - 1; i >= 0; --i) {
                if (TryFinish(Pickups[i])) { Pickups.RemoveAt(i); }
            }
            for (int i = Retries.Count - 1; i >= 0; --i) {
                Retry retry = Retries[i];
                Container chest = retry.First.Chest;
                bool live = chest != null && chest.m_nview != null && chest.m_nview.IsValid();
                if (live && chest.m_nview.GetZDO().GetOwner() != retry.StaleOwner) {
                    Retries.RemoveAt(i);
                    Request(retry.Table, retry.Prototype, retry.First, retry.Amount, retry.Held, retried: true);
                } else if (!live || Time.time - retry.Since > RetryWaitSeconds) {
                    Retries.RemoveAt(i);
                    SeekingIndex.Release(retry.Held);
                    Report(live ? StorageRpc.Status.NotOwner : StorageRpc.Status.NotFound);
                }
            }
        }

        private static List<SeekingIndex.InFlight> Rehold(List<SeekingIndex.InFlight> released) {
            List<SeekingIndex.InFlight> held = new List<SeekingIndex.InFlight>(released.Count);
            foreach (SeekingIndex.InFlight take in released) {
                if (take.Chest == null) { continue; }
                held.Add(SeekingIndex.HoldOff(new SeekSource { Chest = take.Chest, Item = take.Item }, take.Amount));
            }
            return held;
        }

        private static void Report(StorageRpc.Status status) {
            Player player = Player.m_localPlayer;
            if (player == null) { return; }
            switch (status) {
                case StorageRpc.Status.Changed:
                case StorageRpc.Status.NotFound:
                    player.Message(MessageHud.MessageType.Center, "$DA_seek_gone");
                    break;
                case StorageRpc.Status.Lost:
                    player.Message(MessageHud.MessageType.Center, "$DA_seek_lost");
                    break;
                case StorageRpc.Status.NotOwner:
                    player.Message(MessageHud.MessageType.Center, "$DA_seek_busy");
                    break;
                default:
                    player.Message(MessageHud.MessageType.Center, "$DA_seek_refused");
                    break;
            }
        }

        /// <summary>Forgets room spoken for by takes still out. For a new session, where none can still answer.</summary>
        internal static void Reset() {
            Deliveries.Clear();
            Retries.Clear();
            Pickups.Clear();
        }
    }
}
