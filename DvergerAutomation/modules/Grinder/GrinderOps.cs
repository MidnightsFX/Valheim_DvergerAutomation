using System;
using System.Collections.Generic;

namespace DvergerAutomation {
    /// <summary>
    /// Feeding a Grinder and taking an item back out, as requests to its owner through
    /// <see cref="StorageRpc"/>. The queue lives on the piece's ZDO beside the results chest, and only the
    /// owner writes either.
    ///
    /// Feeding takes the item out of the pack first and keeps a copy; anything the owner does not queue
    /// comes back from that copy. An answer that never arrives is settled the same way, so an item is
    /// never lost to a dropped message - at worst, if the owner did queue it after all, it exists twice.
    /// The request carries the time it was sent, and the owner turns away one older than
    /// <see cref="MaxRequestAgeSeconds"/>, well inside the requester's own give-up time, which keeps
    /// that window narrow.
    /// </summary>
    internal static class GrinderOps {
        internal enum Reason : byte {
            None = 0,
            // 1-5 are GrinderRecipes.Refusal, passed through as is.
            QueueFull = 10,
            Disabled = 11,
            Stale = 12,
            Unreadable = 13,
            Empty = 14,
            NotGrinder = 15,
            NoCraftCost = 16,
        }

        private const float MaxRequestAgeSeconds = 20f;

        // Feeds sent from here and not answered yet, per Grinder. Counted against the queue's room, since
        // this client's copy of the queue lags behind the owner's.
        private static readonly Dictionary<ZDOID, int> Unanswered = new Dictionary<ZDOID, int>();

        internal static void Register() {
            StorageRpc.Register(StorageRpc.Op.GrinderFeed, OwnerFeed);
            StorageRpc.Register(StorageRpc.Op.GrinderTakeBack, OwnerTakeBack);
            Unanswered.Clear();
        }

        internal static int UnansweredFor(ZDOID id) {
            return Unanswered.TryGetValue(id, out int n) ? n : 0;
        }

        internal static string ReasonMessage(Reason reason) {
            switch (reason) {
                case Reason.QueueFull: return "$DA_grinder_queue_full";
                case Reason.Disabled: return "$DA_grinder_disabled";
                case Reason.NoCraftCost: return "$DA_grinder_nocraftcost";
                case Reason.Empty: return "$DA_grinder_nothing_queued";
                case Reason.Stale:
                case Reason.Unreadable:
                case Reason.NotGrinder:
                case Reason.None:
                    return "$DA_grinder_not_taken";
                default:
                    return GrinderRecipes.RefusalMessage((GrinderRecipes.Refusal)reason);
            }
        }

        // ---- the requester ------------------------------------------------------------------------

        /// <summary>
        /// Takes <paramref name="amount"/> of <paramref name="item"/> out of the player's pack and asks the
        /// owner to queue it. When this client is the owner it is queued before this returns.
        /// </summary>
        internal static bool Feed(GrinderHub hub, Player player, ItemDrop.ItemData item, int amount) {
            if (hub == null || hub.Store == null || player == null || item == null || amount <= 0) { return false; }
            ZDOID id = hub.ZdoId;
            ItemDrop.ItemData sent = item.Clone();
            sent.m_stack = amount;
            sent.m_equipped = false;
            if (!player.GetInventory().RemoveItem(item, amount)) { return false; }

            Unanswered.TryGetValue(id, out int n);
            Unanswered[id] = n + 1;
            long sentAt = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : 0L;
            StorageRpc.Send(hub.Store, StorageRpc.Op.GrinderFeed,
                args => {
                    args.Write(sentAt);
                    ItemCodec.Write(args, new[] { sent });
                },
                (status, reply, late) => OnFed(id, sent, status, reply, late),
                giveUpOnOwnerChange: false);
            return true;
        }

        private static void OnFed(ZDOID id, ItemDrop.ItemData sent, StorageRpc.Status status, ZPackage reply, bool late) {
            // A late answer was already settled as lost, and the item handed back then.
            if (late) { return; }
            if (Unanswered.TryGetValue(id, out int n)) {
                if (n <= 1) { Unanswered.Remove(id); } else { Unanswered[id] = n - 1; }
            }

            Player player = Player.m_localPlayer;
            if (status == StorageRpc.Status.Ok) {
                player?.Message(MessageHud.MessageType.Center,
                    Localization.instance.Localize("$DA_grinder_added", Localization.instance.Localize(sent.m_shared.m_name)));
                return;
            }

            StorageReserve.HandToPlayer(sent);
            Reason reason = Reason.None;
            if (reply != null && reply.Size() > 0) { reason = (Reason)reply.ReadByte(); }
            player?.Message(MessageHud.MessageType.Center, status == StorageRpc.Status.Lost ? "$DA_grinder_lost" : ReasonMessage(reason));
        }

        /// <summary>Asks the owner for the item queued last. It lands in the player's pack, or at their feet.</summary>
        internal static void TakeBack(GrinderHub hub) {
            if (hub == null || hub.Store == null) { return; }
            StorageRpc.Send(hub.Store, StorageRpc.Op.GrinderTakeBack, null, OnTakenBack, giveUpOnOwnerChange: false);
        }

        private static void OnTakenBack(StorageRpc.Status status, ZPackage reply, bool late) {
            Player player = Player.m_localPlayer;
            if (status == StorageRpc.Status.Ok && reply != null) {
                // Delivered even when late: the owner has already taken it out of the queue, and this
                // reply is the only copy.
                List<ItemDrop.ItemData> items = ItemCodec.Read(reply, out bool intact);
                if (!intact) { Logger.LogWarning("[Grinder] an item taken back could not be rebuilt by this game."); }
                foreach (ItemDrop.ItemData item in items) {
                    StorageReserve.HandToPlayer(item);
                    player?.Message(MessageHud.MessageType.Center,
                        Localization.instance.Localize("$DA_grinder_taken_back", Localization.instance.Localize(item.m_shared.m_name)));
                }
                return;
            }
            if (late) { return; }
            Reason reason = Reason.None;
            if (reply != null && reply.Size() > 0) { reason = (Reason)reply.ReadByte(); }
            player?.Message(MessageHud.MessageType.Center, status == StorageRpc.Status.Lost ? "$DA_grinder_takeback_lost" : ReasonMessage(reason));
        }

        // ---- the owner ----------------------------------------------------------------------------

        private static StorageRpc.Status OwnerFeed(Container container, long sender, ZPackage args, ZPackage reply) {
            GrinderHub hub = GrinderHub.For(container);
            if (hub == null) {
                reply.Write((byte)Reason.NotGrinder);
                return StorageRpc.Status.Refused;
            }

            long sentAt = args.ReadLong();
            if (ZNet.instance != null && sentAt > 0L
                && ZNet.instance.GetTime().Ticks - sentAt > (long)(MaxRequestAgeSeconds * TimeSpan.TicksPerSecond)) {
                reply.Write((byte)Reason.Stale);
                return StorageRpc.Status.Changed;
            }

            List<ItemDrop.ItemData> items = ItemCodec.Read(args, out bool intact);
            if (!intact || items.Count != 1) {
                reply.Write((byte)Reason.Unreadable);
                return StorageRpc.Status.Refused;
            }

            Reason reason = hub.OwnerEnqueue(items[0]);
            if (reason != Reason.None) {
                reply.Write((byte)reason);
                return StorageRpc.Status.Refused;
            }
            return StorageRpc.Status.Ok;
        }

        private static StorageRpc.Status OwnerTakeBack(Container container, long sender, ZPackage args, ZPackage reply) {
            GrinderHub hub = GrinderHub.For(container);
            if (hub == null) {
                reply.Write((byte)Reason.NotGrinder);
                return StorageRpc.Status.Refused;
            }
            ItemDrop.ItemData item = hub.OwnerTakeLast();
            if (item == null) {
                reply.Write((byte)Reason.Empty);
                return StorageRpc.Status.Changed;
            }
            ItemCodec.Write(reply, new[] { item });
            return StorageRpc.Status.Ok;
        }
    }
}
