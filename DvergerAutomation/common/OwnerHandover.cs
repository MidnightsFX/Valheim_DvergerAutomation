using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// Asks the owner of a networked object to hand it to this client, and hands one over when asked. For
    /// the machines that bring what they work on to the client running them: the Hopper its stations, the
    /// Lighter its fires, the Grinder its hopper. Each only ever writes what it owns, so the thing it wants
    /// comes to it rather than being written from a lagging copy.
    ///
    /// Handed over the way vanilla hands a chest to the player opening it - <c>ForceSendZDO</c>, then
    /// <c>SetOwner</c>, from the owner's side - so its latest state travels with it and the old owner stops
    /// running it at that moment. Each user says when its owner should keep it instead, typically because a
    /// machine on that client works it too: two machines covering one object would otherwise pass it back and
    /// forth. A refusal is silent; the asker asks again, less and less often while the same owner keeps it.
    /// </summary>
    internal sealed class OwnerHandover {
        private const float MinInterval = 2f;
        // An object not asked about for this long has arrived, or stopped being wanted: the next ask starts
        // the wait over.
        private const float ResetSeconds = 10f;

        private sealed class Asked {
            internal long Owner;
            internal float Seen;
            internal float Next;
            internal float Interval;
        }

        private readonly string rpcName;
        private readonly string tag;
        private readonly Func<GameObject, bool> keepHere;
        private readonly float maxInterval;
        private readonly Dictionary<ZDOID, Asked> asked = new Dictionary<ZDOID, Asked>();
        private readonly List<ZDOID> pruneScratch = new List<ZDOID>();

        /// <param name="keepHere">Owner side: true when this client should keep the object, or it is not one this handover is for.</param>
        /// <param name="maxInterval">The longest wait between two asks of an owner who keeps refusing.</param>
        internal OwnerHandover(string rpcName, string tag, Func<GameObject, bool> keepHere, float maxInterval) {
            this.rpcName = rpcName;
            this.tag = tag;
            this.keepHere = keepHere;
            this.maxInterval = Mathf.Max(MinInterval, maxInterval);
        }

        /// <summary>Once per session, on that session's <c>ZRoutedRpc</c>.</summary>
        internal void Register(ZRoutedRpc rpc) {
            rpc.Register<ZDOID>(rpcName, OnRequest);
            asked.Clear();
        }

        /// <summary>
        /// Asks for the object behind <paramref name="view"/>. One nobody owns is simply taken - there is no
        /// one to race. Rate-limited per object, so it is cheap to call every pass.
        /// </summary>
        internal void Ask(ZNetView view) {
            if (view == null || !view.IsValid()) { return; }
            ZDO zdo = view.GetZDO();
            long owner = zdo.GetOwner();
            if (owner == 0L) {
                view.ClaimOwnership();
                return;
            }

            float now = Time.time;
            if (!asked.TryGetValue(zdo.m_uid, out Asked entry) || entry.Owner != owner || now - entry.Seen > ResetSeconds) {
                entry = new Asked { Owner = owner, Next = now, Interval = MinInterval };
                asked[zdo.m_uid] = entry;
                Prune(now);
            }
            entry.Seen = now;
            if (now < entry.Next) { return; }
            entry.Next = now + entry.Interval;
            entry.Interval = Mathf.Min(entry.Interval * 2f, maxInterval);
            if (ZRoutedRpc.instance != null) { ZRoutedRpc.instance.InvokeRoutedRPC(owner, rpcName, zdo.m_uid); }
        }

        private void Prune(float now) {
            if (asked.Count < 128) { return; }
            pruneScratch.Clear();
            foreach (KeyValuePair<ZDOID, Asked> entry in asked) {
                if (now - entry.Value.Seen > ResetSeconds) { pruneScratch.Add(entry.Key); }
            }
            foreach (ZDOID id in pruneScratch) { asked.Remove(id); }
            pruneScratch.Clear();
        }

        private void OnRequest(long sender, ZDOID id) {
            try {
                if (ZDOMan.instance == null || ZNetScene.instance == null) { return; }
                if (sender == ZDOMan.GetSessionID()) { return; }
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsOwner()) { return; }
                GameObject root = ZNetScene.instance.FindInstance(id);
                if (root == null || keepHere(root)) {
                    if (root != null && ValConfig.EnableDebugMode.Value) {
                        Logger.LogInfo($"[{tag}] kept {root.name}: it is worked here too.");
                    }
                    return;
                }

                ZDOMan.instance.ForceSendZDO(sender, id);
                zdo.SetOwner(sender);
                if (ValConfig.EnableDebugMode.Value) {
                    Logger.LogInfo($"[{tag}] handed {root.name} to {sender}.");
                }
            } catch (Exception ex) {
                Logger.LogError($"[{tag}] handing over {id} failed: {ex}");
            }
        }
    }
}
