using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// A guest's side of a shared chest (see <see cref="SharedChests"/>): turns each move into a request
    /// to the chest's owner, and deals with the answer.
    ///
    /// The guest's copy of the chest is never written here. What leaves the player's own inventory -
    /// an item put into the chest, or one offered in trade for a chest item - leaves at once, and a copy
    /// of it stays in the request so it can be put back if the owner turns the request down, sends part
    /// of it back, or never answers. What comes out of the chest is added when the owner's answer
    /// brings it. Until then <see cref="SharedChestPreview"/> draws both sides as they are about to be.
    ///
    /// A chest can change hands while a request is on its way - its owner walked off, or it was handed
    /// to someone crafting from it. Whoever receives the request then answers that the chest is no
    /// longer theirs, and the request is sent again to the new owner once this client knows who that is.
    ///
    /// Nothing waits on a slot staying free: an item coming back to a slot that has since been filled
    /// goes to any free slot instead, and to the ground at the player's feet if there is none. So no
    /// request, answered or not, can leave the player's inventory locked.
    /// </summary>
    internal static class SharedChestRequests {
        internal enum Kind { Add, Remove, Move }

        /// <summary>Where an item taken out of the chest is headed.</summary>
        internal enum Destination { Slot, Any, Ground, Consume }

        internal sealed class Pending {
            internal Kind Kind;
            internal Container Chest;
            /// <summary>The inventory on the player's side: where an added item came from, or a removed one is going.</summary>
            internal Inventory Other;
            /// <summary>A copy of what is moving; its stack is the amount asked for.</summary>
            internal ItemDrop.ItemData Item;
            /// <summary>Add: the slot aimed for, negative for anywhere. Remove and Move: the slot it is leaving.</summary>
            internal Vector2i ChestSlot;
            /// <summary>Add: the slot it left. Remove: the slot it is headed for. Move: the slot in the chest it is headed for.</summary>
            internal Vector2i OtherSlot;
            internal bool WholeStack;
            /// <summary>Add: the chest's item expected back in trade. Remove: the player's item sent in trade.</summary>
            internal ItemDrop.ItemData Swap;
            internal Destination Destination;
            /// <summary>Who asked. A player who has died and come back since has a different inventory to deliver to.</summary>
            internal Player Requester;
            /// <summary>The revision the guest's copy of the chest was at when this was sent.</summary>
            internal uint RevisionAtSend;
            internal bool Answered;
            internal float AnsweredAt;

            // Sending, and sending again.
            internal Action Send;
            internal StorageRpc.ResultHandler Finish;
            internal long AskedOf;
            internal int Resends;
            /// <summary>Set while waiting to learn the chest's new owner; zero otherwise.</summary>
            internal float ResendBy;
        }

        // An answered request stays drawn until the guest's copy of the chest reloads with the result
        // in it. This is how long it waits for that before giving the copy the last word.
        private const float SettleSeconds = 1f;

        // The answer "not mine any more" arrives ahead of the news of whose it is now. This is how long a
        // request waits for that news, and how many times it will chase a chest that keeps moving.
        private const float ResendSeconds = 2f;
        private const int MaxResends = 2;

        private static readonly Vector2i Anywhere = new Vector2i(-1, -1);

        internal static readonly List<Pending> All = new List<Pending>();
        private static readonly List<Pending> Scratch = new List<Pending>();

        // Items that came back while there was no player to give them to.
        private static readonly List<ItemDrop.ItemData> Orphans = new List<ItemDrop.ItemData>();

        // The crafting panel needs redrawing after the pack changes, which rebuilds its whole recipe
        // list. Take All brings one answer per stack, so it is done once a frame, not once an answer.
        private static bool panelStale;

        /// <summary>A unit of food or mead is on its way out of a chest to be consumed; one at a time.</summary>
        internal static bool ConsumePending { get; private set; }

        internal static void Reset() {
            All.Clear();
            Orphans.Clear();
            ConsumePending = false;
            panelStale = false;
        }

        // ---- asking -----------------------------------------------------------------

        /// <summary>
        /// Puts up to <paramref name="amount"/> of <paramref name="item"/> from <paramref name="source"/>
        /// into the chest at <paramref name="to"/>, or anywhere when that is negative. Returns how many
        /// were sent: fewer than asked when the chest has room for only part, and none - with nothing
        /// asked of the owner - when it plainly has room for none.
        /// </summary>
        internal static int Add(Container chest, Inventory source, ItemDrop.ItemData item, int amount, Vector2i to) {
            amount = Mathf.Min(amount, item.m_stack);
            if (amount <= 0 || !source.ContainsItem(item)) { return 0; }
            // Cannot be rebuilt on the owner's side, so it would leave here and arrive nowhere.
            if (item.m_dropPrefab == null) { return 0; }

            // Judged against the chest as it will be once everything already asked for has been done:
            // that is the chest the owner will see when this request reaches it.
            Inventory predicted = SharedChestPreview.Chest(chest);
            bool wholeStack = amount == item.m_stack;
            ItemDrop.ItemData sent = item.Clone();
            sent.m_equipped = false;
            sent.m_stack = amount;

            ItemDrop.ItemData inSlot = null;
            ItemDrop.ItemData expectedBack = null;
            if (to.x >= 0 && to.y >= 0) {
                inSlot = predicted.GetItemAt(to.x, to.y);
                if (inSlot != null) {
                    if (SharedChestOps.ShouldSwap(inSlot, sent, wholeStack)) {
                        expectedBack = inSlot.Clone();
                    } else {
                        amount = Mathf.Min(SharedChestOps.StackSpace(inSlot, sent), amount);
                    }
                }
            } else {
                to = Anywhere;
                amount = AutoStore.AddMeasured(predicted, sent, amount);
            }
            if (amount <= 0) { return 0; }
            sent.m_stack = amount;

            Pending pending = new Pending {
                Kind = Kind.Add,
                Chest = chest,
                Other = source,
                Item = sent,
                ChestSlot = to,
                OtherSlot = item.m_gridPos,
                WholeStack = wholeStack,
                Swap = expectedBack,
                Requester = Player.m_localPlayer,
                RevisionAtSend = chest.m_lastRevision,
            };
            int expect = SharedChestOps.Fingerprint(inSlot);
            All.Add(pending);
            source.RemoveItem(item, amount);
            Dispatch(pending, StorageRpc.Op.ChestAdd,
                args => SharedChestOps.WriteAdd(args, to, wholeStack, expect, sent),
                (status, reply, late) => OnAdded(pending, status, reply, late));
            return amount;
        }

        /// <summary>
        /// Takes up to <paramref name="amount"/> of a chest item and sends it on its way to
        /// <paramref name="destination"/>. <paramref name="swap"/>, when given, is an item in
        /// <paramref name="target"/> that goes into the chest slot in its place.
        /// </summary>
        internal static bool Remove(Container chest, ItemDrop.ItemData item, int amount, Destination destination, Inventory target, Vector2i to, ItemDrop.ItemData swap) {
            amount = Mathf.Min(amount, item.m_stack);
            if (amount <= 0) { return false; }
            if (swap != null && swap.m_dropPrefab == null) { return false; }

            ItemDrop.ItemData wanted = item.Clone();
            wanted.m_stack = amount;
            ItemDrop.ItemData swapSent = null;
            if (swap != null) {
                swapSent = swap.Clone();
                swapSent.m_equipped = false;
            }

            Pending pending = new Pending {
                Kind = Kind.Remove,
                Chest = chest,
                Other = target,
                Item = wanted,
                ChestSlot = item.m_gridPos,
                OtherSlot = to,
                WholeStack = amount == item.m_stack,
                Swap = swapSent,
                Destination = destination,
                Requester = Player.m_localPlayer,
                RevisionAtSend = chest.m_lastRevision,
            };
            Vector2i from = item.m_gridPos;
            int expect = SharedChestOps.Fingerprint(item);
            All.Add(pending);
            if (swap != null) { target.RemoveItem(swap); }
            if (destination == Destination.Consume) { ConsumePending = true; }
            Dispatch(pending, StorageRpc.Op.ChestRemove,
                args => SharedChestOps.WriteRemove(args, from, amount, expect, swapSent),
                (status, reply, late) => OnRemoved(pending, status, reply, late));
            return true;
        }

        /// <summary>Moves up to <paramref name="amount"/> of a chest item to another slot of the same chest.</summary>
        internal static bool Move(Container chest, ItemDrop.ItemData item, Vector2i to, int amount) {
            amount = Mathf.Min(amount, item.m_stack);
            if (amount <= 0) { return false; }
            Vector2i from = item.m_gridPos;
            // Dropped back where it was picked up.
            if (to == from) { return true; }

            Inventory predicted = SharedChestPreview.Chest(chest);
            ItemDrop.ItemData inSlot = predicted.GetItemAt(to.x, to.y);
            bool wholeStack = amount == item.m_stack;
            if (inSlot != null && !SharedChestOps.ShouldSwap(inSlot, item, wholeStack) && SharedChestOps.StackSpace(inSlot, item) <= 0) {
                return false;
            }

            ItemDrop.ItemData moving = item.Clone();
            moving.m_stack = amount;
            Pending pending = new Pending {
                Kind = Kind.Move,
                Chest = chest,
                Item = moving,
                ChestSlot = from,
                OtherSlot = to,
                WholeStack = wholeStack,
                Requester = Player.m_localPlayer,
                RevisionAtSend = chest.m_lastRevision,
            };
            int expectFrom = SharedChestOps.Fingerprint(item);
            int expectTo = SharedChestOps.Fingerprint(inSlot);
            All.Add(pending);
            Dispatch(pending, StorageRpc.Op.ChestMove,
                args => SharedChestOps.WriteMove(args, from, to, amount, expectFrom, expectTo),
                (status, reply, late) => OnMoved(pending, status, late));
            return true;
        }

        private static void Dispatch(Pending pending, StorageRpc.Op op, Action<ZPackage> writeArgs, StorageRpc.ResultHandler finish) {
            pending.Finish = finish;
            pending.Send = () => {
                pending.AskedOf = OwnerOf(pending.Chest);
                StorageRpc.Send(pending.Chest, op, writeArgs, finish);
            };
            pending.Send();
        }

        private static long OwnerOf(Container chest) {
            if (chest == null || chest.m_nview == null || !chest.m_nview.IsValid()) { return 0L; }
            return chest.m_nview.GetZDO().GetOwner();
        }

        // ---- the answers ------------------------------------------------------------
        // A late answer is one for a request that was already given up on, when everything the player
        // had put into it was handed back. Anything the answer itself carries has still left the chest
        // and is still taken in.

        private static void OnAdded(Pending pending, StorageRpc.Status status, ZPackage reply, bool late) {
            int accepted = 0;
            ItemDrop.ItemData swappedOut = null;
            if (reply != null && (status == StorageRpc.Status.Ok || status == StorageRpc.Status.Partial)) {
                accepted = reply.ReadInt();
                List<ItemDrop.ItemData> back = ItemCodec.Read(reply, out _);
                if (back.Count > 0) { swappedOut = back[0]; }
            }

            if (swappedOut != null) { DeliverHome(pending, swappedOut, toSlot: true); }
            if (late || WaitsToResend(pending, status)) { return; }

            int rest = pending.Item.m_stack - accepted;
            if (rest > 0) {
                ItemDrop.ItemData back = pending.Item.Clone();
                back.m_stack = rest;
                DeliverHome(pending, back, toSlot: true);
            }
            if (accepted <= 0) { Tell(status); }
            Answer(pending, accepted > 0);
        }

        private static void OnRemoved(Pending pending, StorageRpc.Status status, ZPackage reply, bool late) {
            ItemDrop.ItemData got = null;
            if (reply != null && (status == StorageRpc.Status.Ok || status == StorageRpc.Status.Partial)) {
                List<ItemDrop.ItemData> back = ItemCodec.Read(reply, out _);
                if (back.Count > 0) { got = back[0]; }
            }

            if (!late) {
                if (got == null && WaitsToResend(pending, status)) { return; }
                if (pending.Destination == Destination.Consume) { ConsumePending = false; }
                // The owner did not take the item offered in trade, so it comes home.
                if (pending.Swap != null && got == null) { DeliverHome(pending, pending.Swap, toSlot: true); }
                if (got == null) { Tell(status); }
                Answer(pending, got != null);
            }
            if (got == null) { return; }

            switch (pending.Destination) {
                case Destination.Slot:
                    DeliverHome(pending, got, toSlot: true);
                    break;
                case Destination.Any:
                    DeliverHome(pending, got, toSlot: false);
                    break;
                case Destination.Ground:
                    DropAtFeet(got);
                    break;
                case Destination.Consume:
                    Consume(got);
                    break;
            }
        }

        private static void OnMoved(Pending pending, StorageRpc.Status status, bool late) {
            if (late || WaitsToResend(pending, status)) { return; }
            if (status != StorageRpc.Status.Ok) { Tell(status); }
            Answer(pending, status == StorageRpc.Status.Ok);
        }

        // The chest changed hands before the request got there. Nothing is put back yet: the request
        // stays outstanding, and Tick sends it to the new owner or gives up on it.
        private static bool WaitsToResend(Pending pending, StorageRpc.Status status) {
            if (status != StorageRpc.Status.NotOwner || pending.Resends >= MaxResends) { return false; }
            ++pending.Resends;
            pending.ResendBy = Time.time + ResendSeconds;
            return true;
        }

        private static void Answer(Pending pending, bool succeeded) {
            pending.Answered = true;
            pending.AnsweredAt = Time.time;
            // An answered request goes on being drawn until the guest's copy reloads with the result in
            // it. There is nothing to wait for when the chest did not change, when the change was made
            // right here, or when the copy has already reloaded since the request went out: the answer
            // usually beats the reload, but when it does not, the copy already shows the result and
            // drawing the request on top would show it twice.
            Container chest = pending.Chest;
            bool settled = !succeeded || chest == null || chest.m_nview == null || !chest.m_nview.IsValid()
                || chest.m_nview.IsOwner() || chest.m_lastRevision != pending.RevisionAtSend;
            if (settled) { All.Remove(pending); }
            RefreshPanel();
        }

        // Only for a move that came to nothing. A chest that was simply full says nothing in vanilla either.
        private static void Tell(StorageRpc.Status status) {
            if (status == StorageRpc.Status.Ok || status == StorageRpc.Status.Partial) { return; }
            Player player = Player.m_localPlayer;
            if (player == null) { return; }
            player.Message(MessageHud.MessageType.Center, status == StorageRpc.Status.Changed ? "$DA_shared_changed" : "$DA_shared_failed");
        }

        /// <summary>The guest's copy of a chest reloaded: what it shows now includes every answered request.</summary>
        internal static void OnChestLoaded(Inventory inventory) {
            if (All.Count == 0) { return; }
            All.RemoveAll(pending => pending.Answered && pending.Chest != null && pending.Chest.m_inventory == inventory);
        }

        internal static void Tick() {
            if (All.Count > 0) {
                float now = Time.time;
                All.RemoveAll(pending => pending.Answered && now - pending.AnsweredAt > SettleSeconds);
                Resend(now);
            }
            if (Orphans.Count > 0 && Player.m_localPlayer != null) {
                List<ItemDrop.ItemData> waiting = new List<ItemDrop.ItemData>(Orphans);
                Orphans.Clear();
                foreach (ItemDrop.ItemData item in waiting) { Deliver(item, null, Anywhere); }
            }
            if (panelStale) {
                panelStale = false;
                InventoryGui gui = InventoryGui.m_instance;
                if (gui != null && Player.m_localPlayer != null) { gui.UpdateCraftingPanel(); }
            }
        }

        // Sending again can settle a request on the spot - this client may be the new owner - which edits
        // the list, so the ones waiting are collected first.
        private static void Resend(float now) {
            Scratch.Clear();
            foreach (Pending pending in All) {
                if (pending.ResendBy > 0f) { Scratch.Add(pending); }
            }
            foreach (Pending pending in Scratch) {
                long owner = OwnerOf(pending.Chest);
                bool gone = pending.Chest == null || pending.Chest.m_nview == null || !pending.Chest.m_nview.IsValid();
                if (!gone && owner != pending.AskedOf) {
                    pending.ResendBy = 0f;
                    pending.Send();
                } else if (gone || now > pending.ResendBy) {
                    // Finished as the refusal it was, with no more chasing.
                    pending.ResendBy = 0f;
                    pending.Resends = MaxResends;
                    pending.Finish(gone ? StorageRpc.Status.NotFound : StorageRpc.Status.NotOwner, null, false);
                }
            }
            Scratch.Clear();
        }

        // ---- getting an item to the player ------------------------------------------

        // Back to the inventory the request was made from - unless the player who made it is gone. An
        // answer can outlast them (a request is only given up on after a while), and the inventory it
        // remembers is then a dead player's: the item goes to whoever the player is now instead.
        private static void DeliverHome(Pending pending, ItemDrop.ItemData item, bool toSlot) {
            bool samePlayer = pending.Requester != null && pending.Requester == Player.m_localPlayer;
            Deliver(item, samePlayer ? pending.Other : null, samePlayer && toSlot ? pending.OtherSlot : Anywhere);
        }

        /// <summary>
        /// Gives the player an item: into <paramref name="slot"/> of <paramref name="preferred"/> if it
        /// fits there, then anywhere in it, then anywhere in their pack, then the ground.
        /// </summary>
        private static void Deliver(ItemDrop.ItemData item, Inventory preferred, Vector2i slot) {
            Player player = Player.m_localPlayer;
            if (player == null) {
                Orphans.Add(item);
                return;
            }

            Inventory pack = player.GetInventory();
            Inventory first = preferred ?? pack;
            int left = item.m_stack;
            if (slot.x >= 0 && slot.y >= 0) {
                ItemDrop.ItemData copy = item.Clone();
                copy.m_stack = left;
                int landed = SharedChestOps.ApplyAdd(first, copy, slot, wholeStack: false, live: true, out _);
                // Topping up a stack edits it in place, which tells nobody: not the weight, not the hotbar.
                if (landed > 0) { first.Changed(); }
                left -= landed;
            }
            if (left > 0) { left -= AddAnywhere(first, item, left); }
            if (left > 0 && first != pack) { left -= AddAnywhere(pack, item, left); }
            if (left > 0) { DropAtFeet(player, item, left); }
            RefreshPanel();
        }

        private static int AddAnywhere(Inventory inv, ItemDrop.ItemData item, int amount) {
            ItemDrop.ItemData copy = item.Clone();
            copy.m_stack = amount;
            return AutoStore.AddMeasured(inv, copy, amount);
        }

        private static void DropAtFeet(ItemDrop.ItemData item) {
            Player player = Player.m_localPlayer;
            if (player == null) {
                Orphans.Add(item);
                return;
            }
            DropAtFeet(player, item, item.m_stack);
        }

        // The tail of vanilla's Humanoid.DropItem, from the point where the item has left its inventory.
        private static void DropAtFeet(Player player, ItemDrop.ItemData item, int amount) {
            if (amount <= 0 || item.m_dropPrefab == null) { return; }
            Transform at = player.transform;
            ItemDrop drop = ItemDrop.DropItem(item, amount, at.position + at.forward + at.up, at.rotation);
            drop.OnPlayerDrop();
            Rigidbody body = drop.GetComponent<Rigidbody>();
            if (body != null) { body.linearVelocity = (at.forward + Vector3.up) * (item.GetWeight() >= 300f ? 0.5f : 5f); }
            player.m_zanim.SetTrigger("interact");
            player.m_dropEffects.Create(at.position, Quaternion.identity, null, 1f, -1, player.GetZDOID());
            player.Message(MessageHud.MessageType.TopLeft, "$msg_dropped " + drop.m_itemData.m_shared.m_name, drop.m_itemData.m_stack, drop.m_itemData.GetIcon());
        }

        // Eaten through vanilla's own path, out of a one-slot inventory holding just the unit that
        // arrived. Whatever stops the player eating it now - they filled up in the meantime - leaves it
        // in that inventory, and it goes to their pack instead.
        private static void Consume(ItemDrop.ItemData item) {
            Player player = Player.m_localPlayer;
            if (player == null) {
                Orphans.Add(item);
                return;
            }
            Inventory scratch = new Inventory("da", null, 1, 1);
            item.m_gridPos = Vector2i.zero;
            scratch.m_inventory.Add(item);
            player.UseItem(scratch, item, fromInventoryGui: true);
            foreach (ItemDrop.ItemData uneaten in new List<ItemDrop.ItemData>(scratch.m_inventory)) {
                Deliver(uneaten, null, Anywhere);
            }
        }

        // The crafting panel counts what the player carries. Redrawn from Tick.
        private static void RefreshPanel() {
            panelStale = true;
        }
    }
}
