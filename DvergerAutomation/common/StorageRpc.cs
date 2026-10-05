using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Asks a container's owner to change it, and brings back what the owner did.
    ///
    /// Only a container's owner may write it (see <see cref="StorageOwnership"/>). A closed chest is
    /// simply handed over, but one its owner has open cannot be: taking it would pull the panel out from
    /// under them. For those the owner makes the change itself, on the copy everyone else reloads from,
    /// and reports back.
    ///
    /// A request goes to whoever owns the container when it is sent, and that client always answers -
    /// with a status saying why not, when it cannot help. The requester keeps its own copy of anything
    /// it handed over and restores from that; the owner never sends a requester's items back to it.
    ///
    /// A request is settled by its reply. If none comes it is given up on when the container has been
    /// someone else's for <see cref="OwnerGoneSeconds"/> (the owner left: the server drops a message to
    /// a peer that is gone, and hands their objects to someone else within a couple of seconds) or at
    /// <see cref="HardCapSeconds"/> regardless. A reply that turns up after that is still passed on,
    /// marked late, because it may be carrying items.
    /// </summary>
    internal static class StorageRpc {
        private const string OpRpc = "DA_StorageOp";
        private const string ResultRpc = "DA_StorageOpResult";

        private const float OwnerGoneSeconds = 5f;
        private const float HardCapSeconds = 30f;
        // How long a given-up request is remembered, so its reply can still be delivered.
        private const float LateSeconds = 60f;

        internal enum Op : byte {
            // Crafting and building (StorageOps).
            Take = 1,
            Give = 2,
            // A guest's moves in a chest several players have open (SharedChestOps).
            ChestAdd = 3,
            ChestRemove = 4,
            ChestMove = 5,
        }

        internal enum Status : byte {
            /// <summary>Done in full.</summary>
            Ok = 0,
            /// <summary>Done in part; the reply says how much.</summary>
            Partial = 1,
            /// <summary>The recipient no longer owns the container.</summary>
            NotOwner = 2,
            /// <summary>The recipient has no instance of the container loaded.</summary>
            NotFound = 3,
            /// <summary>Not storage, or not the asker's to open.</summary>
            Refused = 4,
            /// <summary>The container no longer looks the way the request assumed.</summary>
            Changed = 5,
            /// <summary>The request could not be read or carried out.</summary>
            Error = 6,
            /// <summary>No reply came. Never sent: the requester's own verdict.</summary>
            Lost = 7,
        }

        /// <summary>Carries out a request on a container this client owns, writing its answer into <paramref name="reply"/>.</summary>
        internal delegate Status OwnerHandler(Container container, long sender, ZPackage args, ZPackage reply);

        /// <summary>
        /// Receives a request's outcome. <paramref name="reply"/> is null for <see cref="Status.Lost"/>.
        /// <paramref name="late"/> means this request was already reported lost.
        /// </summary>
        internal delegate void ResultHandler(Status status, ZPackage reply, bool late);

        private sealed class Pending {
            internal int Id;
            internal ZDOID Chest;
            internal long Target;
            internal float SentAt;
            internal float ElsewhereSince = -1f;
            internal bool GiveUpOnOwnerChange;
            internal bool Lost;
            internal ResultHandler OnResult;
        }

        private struct DelayedReply {
            internal float SendAt;
            internal long Target;
            internal ZDOID Chest;
            internal ZPackage Reply;
        }

        private static readonly Dictionary<Op, OwnerHandler> Handlers = new Dictionary<Op, OwnerHandler>();
        private static readonly Dictionary<int, Pending> PendingById = new Dictionary<int, Pending>();
        private static readonly List<Pending> PendingScratch = new List<Pending>();
        private static readonly List<DelayedReply> Delayed = new List<DelayedReply>();

        // ZRoutedRpc is rebuilt with every session, and registering twice on the same one throws.
        private static ZRoutedRpc registeredOn;
        private static int nextId;

        internal static void Register(Op op, OwnerHandler handler) {
            Handlers[op] = handler;
        }

        internal static void RegisterRpc() {
            ZRoutedRpc rpc = ZRoutedRpc.instance;
            if (rpc == null || rpc == registeredOn) { return; }
            rpc.Register<ZDOID, ZPackage>(OpRpc, RPC_StorageOp);
            rpc.Register<ZDOID, ZPackage>(ResultRpc, RPC_StorageOpResult);
            registeredOn = rpc;
            PendingById.Clear();
            Delayed.Clear();
        }

        // ---- the requester's side -------------------------------------------------

        /// <summary>
        /// Sends a request to the container's owner. <paramref name="onResult"/> is called exactly once
        /// with the outcome, and a second time, marked late, if a reply arrives after the request was given
        /// up on. When this client is the owner the request is carried out on the spot and
        /// <paramref name="onResult"/> has already run by the time this returns.
        ///
        /// Pass <paramref name="giveUpOnOwnerChange"/> false for a request that hands items over: an owner
        /// that is still there may have applied it, and only its reply says so.
        /// </summary>
        internal static void Send(Container container, Op op, Action<ZPackage> writeArgs, ResultHandler onResult, bool giveUpOnOwnerChange = true) {
            if (container == null || container.m_nview == null || !container.m_nview.IsValid()) {
                onResult(Status.NotFound, null, false);
                return;
            }

            ZDO zdo = container.m_nview.GetZDO();
            ZPackage args = new ZPackage();
            writeArgs?.Invoke(args);

            // Nobody is simulating it, so there is no one to ask and no one to race (see
            // StorageOwnership.Request).
            if (zdo.GetOwner() == 0L) { container.m_nview.ClaimOwnership(); }

            if (zdo.IsOwner()) {
                ZPackage local = new ZPackage();
                args.SetPos(0);
                Status status = Run(op, container, ZDOMan.GetSessionID(), AutomationHub.LocalPlayerId(), args, ref local);
                local.SetPos(0);
                onResult(status, local, false);
                return;
            }

            if (ZRoutedRpc.instance == null) {
                onResult(Status.NotFound, null, false);
                return;
            }

            Pending pending = new Pending {
                Id = ++nextId,
                Chest = zdo.m_uid,
                Target = zdo.GetOwner(),
                SentAt = Time.time,
                GiveUpOnOwnerChange = giveUpOnOwnerChange,
                OnResult = onResult,
            };
            PendingById[pending.Id] = pending;

            ZPackage request = new ZPackage();
            request.Write(pending.Id);
            request.Write((byte)op);
            request.Write(AutomationHub.LocalPlayerId());
            request.Write(args);
            ZRoutedRpc.instance.InvokeRoutedRPC(pending.Target, OpRpc, zdo.m_uid, request);
        }

        private static void RPC_StorageOpResult(long sender, ZDOID id, ZPackage reply) {
            try {
                int requestId = reply.ReadInt();
                Status status = (Status)reply.ReadByte();
                ZPackage body = reply.ReadPackage();
                if (!PendingById.TryGetValue(requestId, out Pending pending) || pending.Target != sender) { return; }
                PendingById.Remove(requestId);
                pending.OnResult(status, body, pending.Lost);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] reading a storage reply failed: {ex}");
            }
        }

        /// <summary>Gives up on requests whose owner is gone, and forgets the ones given up on long ago.</summary>
        internal static void Tick() {
            FlushDelayed();
            if (PendingById.Count == 0) { return; }

            float now = Time.time;
            PendingScratch.Clear();
            PendingScratch.AddRange(PendingById.Values);
            foreach (Pending pending in PendingScratch) {
                if (pending.Lost) {
                    if (now - pending.SentAt > LateSeconds) { PendingById.Remove(pending.Id); }
                    continue;
                }

                bool lost = now - pending.SentAt > HardCapSeconds;
                if (!lost && pending.GiveUpOnOwnerChange) {
                    ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(pending.Chest) : null;
                    if (zdo != null && zdo.GetOwner() == pending.Target) {
                        pending.ElsewhereSince = -1f;
                    } else if (pending.ElsewhereSince < 0f) {
                        pending.ElsewhereSince = now;
                    } else if (now - pending.ElsewhereSince > OwnerGoneSeconds) {
                        lost = true;
                    }
                }
                if (!lost) { continue; }

                pending.Lost = true;
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[Storage] request {pending.Id} to {pending.Target} went unanswered.");
                }
                try {
                    pending.OnResult(Status.Lost, null, false);
                } catch (Exception ex) {
                    Logger.LogError($"[Storage] settling an unanswered storage request failed: {ex}");
                }
            }
        }

        // ---- the owner's side -----------------------------------------------------

        private static void RPC_StorageOp(long sender, ZDOID id, ZPackage request) {
            int requestId = 0;
            Status status = Status.Error;
            ZPackage body = new ZPackage();
            try {
                requestId = request.ReadInt();
                Op op = (Op)request.ReadByte();
                long playerId = request.ReadLong();
                ZPackage args = request.ReadPackage();

                status = Resolve(id, playerId, out Container container);
                if (status == Status.Ok) { status = Run(op, container, sender, playerId, args, ref body); }
            } catch (Exception ex) {
                Logger.LogError($"[Storage] reading a storage request failed: {ex}");
                status = Status.Error;
                body = new ZPackage();
            }
            Reply(sender, id, requestId, status, body);
        }

        private static Status Resolve(ZDOID id, long playerId, out Container container) {
            container = null;
            if (ZDOMan.instance == null || ZNetScene.instance == null) { return Status.NotFound; }
            ZDO zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null) { return Status.NotFound; }
            if (!zdo.IsOwner()) { return Status.NotOwner; }
            GameObject root = ZNetScene.instance.FindInstance(id);
            if (root == null) { return Status.NotFound; }
            return StorageOwnership.TryResolveStorage(root, playerId, out container) ? Status.Ok : Status.Refused;
        }

        // A handler that throws has its half-written answer thrown away with it: the requester is told
        // Error and restores from its own copy.
        private static Status Run(Op op, Container container, long sender, long playerId, ZPackage args, ref ZPackage reply) {
            if (!Handlers.TryGetValue(op, out OwnerHandler handler)) { return Status.Error; }
            if (!container.CheckAccess(playerId)) { return Status.Refused; }
            try {
                return handler(container, sender, args, reply);
            } catch (Exception ex) {
                Logger.LogError($"[Storage] carrying out {op} on {container.name} failed: {ex}");
                reply = new ZPackage();
                return Status.Error;
            }
        }

        private static void Reply(long target, ZDOID chest, int requestId, Status status, ZPackage body) {
            if (ZRoutedRpc.instance == null) { return; }
            ZPackage reply = new ZPackage();
            reply.Write(requestId);
            reply.Write((byte)status);
            reply.Write(body);

            // Debug mode only: hold replies back, or swallow them, to reproduce a slow or vanished owner.
            if (ValConfig.EnableDebugMode.Value) {
                if (ValConfig.DebugStorageDropReplies.Value) {
                    Logger.LogInfo($"[Storage] dropped the reply to request {requestId} ({status}).");
                    return;
                }
                float delay = ValConfig.DebugStorageReplyDelay.Value;
                if (delay > 0f) {
                    Delayed.Add(new DelayedReply { SendAt = Time.time + delay, Target = target, Chest = chest, Reply = reply });
                    return;
                }
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(target, ResultRpc, chest, reply);
        }

        private static void FlushDelayed() {
            if (Delayed.Count == 0 || ZRoutedRpc.instance == null) { return; }
            float now = Time.time;
            for (int i = Delayed.Count - 1; i >= 0; --i) {
                DelayedReply delayed = Delayed[i];
                if (delayed.SendAt > now) { continue; }
                Delayed.RemoveAt(i);
                ZRoutedRpc.instance.InvokeRoutedRPC(delayed.Target, ResultRpc, delayed.Chest, delayed.Reply);
            }
        }
    }

    // Game.Update rather than the local player's: requests still have to be settled, and what they
    // brought back refunded, while the player is dead and there is no Player to tick from.
    [HarmonyPatch(typeof(Game), nameof(Game.Update))]
    internal static class Game_Update_StorageRpc_Patch {
        private static void Postfix() {
            try {
                StorageRpc.Tick();
                StorageReserve.Tick();
                SharedChestRequests.Tick();
            } catch (Exception ex) {
                Logger.LogError($"[Storage] settling storage requests failed: {ex}");
            }
        }
    }
}
