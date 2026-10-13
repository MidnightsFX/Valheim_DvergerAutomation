using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// The AutoSorter's deposit box: the StoreGoods child of the DA_Autosorter piece is a vanilla
    /// <see cref="Container"/> the player drops goods into. Closing it distributes each stack into the
    /// hub's linked chests that already hold a matching item. Whatever has no home stays in the box
    /// and is tried again on the next close.
    ///
    /// The close is detected by the Harmony patches in AutoStorePatches.
    /// </summary>
    internal static class AutoStore {
        // Child path of the deposit box inside the DA_Autosorter prefab.
        private const string DepositPath = "AutoSorter/StoreGoods";

        // ---- box size -------------------------------------------------------

        internal const int MinSize = 2;
        // The player's own inventory size (Humanoid.m_inventory is a hard-coded 8x4).
        internal const int DefaultWidth = 8;
        internal const int DefaultHeight = 4;
        // InventoryGrid centres its columns on a container panel it never resizes, so anything past the
        // player's 8 columns spills off both sides. Rows are different: the grid is built to be taller
        // than its viewport and scrolls (see InventoryGrid.ResetView).
        internal const int MaxWidth = 8;
        internal const int MaxHeight = 8;

        // The bundle prefab's Container, kept so a live size change also applies to pieces placed later.
        private static Container prefabBox;

        /// <summary>
        /// Whether the container is an AutoSorter's deposit box. Container_Load_Patch asks this of every
        /// chest in the world once a second, so it is a registry lookup, not a hierarchy walk.
        /// </summary>
        internal static bool IsDepositBox(Container container) {
            return StorageRegistry.KindOf(container) == StorageRegistry.Kind.DepositBox;
        }

        /// <summary>
        /// Applies the configured size to a deposit box, but never shrinks its grid past what it already
        /// holds: a narrower grid does not hide the items in the cut-off columns, it deletes them the next
        /// time the box loads (see <see cref="Container_Load_Patch"/>). A box grown to fit its contents
        /// snaps back to the configured size once the items holding it open are sorted or taken out.
        /// </summary>
        internal static void ApplySize(Container box) {
            if (box == null) { return; }
            int width = Mathf.Clamp(ValConfig.DepositBoxWidth.Value, MinSize, MaxWidth);
            int height = Mathf.Clamp(ValConfig.DepositBoxHeight.Value, MinSize, MaxHeight);

            // m_width seeds the Inventory in Container.Awake; m_height is also the floor Container.UpdateRows
            // grows from on every load, so both have to track the config, not just the live inventory.
            box.m_width = width;
            box.m_height = height;

            // Null on the prefab, and on an instance whose Container.Awake has not run yet.
            Inventory inv = box.GetInventory();
            if (inv == null) { return; }

            foreach (ItemDrop.ItemData item in inv.GetAllItems()) {
                if (item == null) { continue; }
                width = Mathf.Max(width, item.m_gridPos.x + 1);
                height = Mathf.Max(height, item.m_gridPos.y + 1);
            }
            inv.m_width = Mathf.Min(width, MaxWidth);
            inv.SetHeight(height);
        }

        // Config sync or an admin edit: resize the prefab (pieces placed from now on) and every live box.
        private static void OnSizeChanged(object sender, System.EventArgs e) {
            ApplySize(prefabBox);
            List<Container> boxes = new List<Container>();
            StorageRegistry.OfKind(StorageRegistry.Kind.DepositBox, boxes);
            foreach (Container box in boxes) {
                // Rebuilding the grid under a player's open panel would reset their selection and any drag in
                // progress. Their close hook applies the size instead.
                if (box.IsInUse()) { continue; }
                ApplySize(box);
            }
        }

        // ---- prefab wiring --------------------------------------------------

        /// <summary>
        /// Finishes the deposit Container on the freshly loaded bundle prefab. Must run before the piece
        /// is ever instantiated, because <c>Container.Awake</c> reads these fields to build its inventory
        /// and bind its network view.
        /// </summary>
        internal static void ConfigureDepositPrefab(GameObject prefab) {
            if (prefab == null) {
                Logger.LogWarning("AutoStore: DA_Autosorter prefab missing; the deposit box will not work.");
                return;
            }

            Transform deposit = prefab.transform.Find(DepositPath);
            Container box = deposit != null ? deposit.GetComponent<Container>() : null;
            if (box == null) {
                Logger.LogWarning($"AutoStore: no Container at '{DepositPath}' on {prefab.name}; the deposit box will not work.");
                return;
            }

            // An earlier iteration used a Switch here and it is still on the same GameObject. Both it and
            // Container are Interactable/Hoverable, and Player.Interact takes the FIRST one
            // GetComponentInParent finds - the Switch - so the box would never open. Disabling is not
            // enough: GetComponentInParent still returns disabled components.
            Switch stale = deposit.GetComponent<Switch>();
            if (stale != null) {
                // Prefab surgery on a bundle asset: if Unity ever refuses, the box silently stops opening,
                // so say so rather than letting it look like the feature was never written.
                try {
                    Object.DestroyImmediate(stale, allowDestroyingAssets: true);
                } catch (System.Exception ex) {
                    Logger.LogError($"AutoStore: could not remove the stale Switch from '{DepositPath}' ({ex.Message}); the deposit box will not open.");
                    return;
                }
                if (deposit.GetComponent<Switch>() != null) {
                    Logger.LogError($"AutoStore: the stale Switch on '{DepositPath}' survived removal; the deposit box will not open.");
                    return;
                }
            }

            // Already correct in the prefab, re-asserted for the reasons MachineStore.Finish gives.
            MachineStore.Finish(box, prefab.GetComponent<ZNetView>(), "$DA_store_goods_name");

            // Size comes from config, not from whatever the prefab was saved with in Unity. Server sync and
            // admin edits arrive later as SettingChanged.
            prefabBox = box;
            ApplySize(box);
            ValConfig.DepositBoxWidth.SettingChanged += OnSizeChanged;
            ValConfig.DepositBoxHeight.SettingChanged += OnSizeChanged;
        }

        // ---- sorting --------------------------------------------------------

        /// <summary>What a <see cref="Sort"/> pass did, for a caller that reports it as part of a larger action.</summary>
        internal struct SortResult {
            internal int Stored;
            internal int Chests;
            internal int Leftover;
            // Some of the leftover has a chest to go to that another player still owns. It has been asked
            // for, and RetrySort files the rest once it arrives.
            internal bool Waiting;
        }

        // Hubs with a RetrySort running, so a burst of sorts does not stack up retries.
        private static readonly HashSet<AutomationHub> Retrying = new HashSet<AutomationHub>();
        private const int RetryAttempts = 3;
        private const float RetryDelay = 2f;

        // Reused by every pass: the linked chests that can receive sorted goods, grouped by the names of
        // what each already holds. Built once per pass, so each chest is read once rather than once per stack.
        private static readonly Dictionary<string, List<Container>> TargetsByName = new Dictionary<string, List<Container>>();
        private static readonly Stack<List<Container>> SpareLists = new Stack<List<Container>>();

        /// <summary>
        /// Distributes the deposit box's contents into the hub's linked chests. Anything left over stays
        /// put in the box. Local-player only: it runs off the inventory UI closing, or off the Deposit All
        /// button, which passes <paramref name="report"/> false so it can fold this into one message of
        /// its own instead of having a second centre message overwrite it.
        ///
        /// Items only go into chests this client owns. A matching chest another player owns is asked for
        /// (see <see cref="StorageOwnership"/>), the items it would take stay in the box, and a retry files
        /// them a moment later once the chest has been handed over. The exception is a chest another
        /// player has open, which cannot be handed over: its owner is sent the items to file itself (see
        /// <see cref="SendToOpenChest"/>).
        /// </summary>
        internal static SortResult Sort(AutomationHub hub, Player player, bool report = true) {
            SortResult result = default;
            if (hub == null || hub.DepositBox == null) { return result; }
            Inventory src = hub.DepositBox.GetInventory();
            if (src == null || src.NrOfItems() == 0) { return result; }

            // Opening the box made this client its owner (Container.RPC_RequestOpen does SetOwner). If that
            // no longer holds, the box is someone else's to write: emptying it here would never reach
            // Container.Save, CheckForChanges would reload the pre-sort contents off the ZDO, and the items
            // would be in the chests AND back in the box. Leave it for whoever has it.
            ZNetView boxView = hub.DepositBox.m_nview;
            if (boxView == null || !boxView.IsValid() || !boxView.IsOwner()) { return result; }

            // A chest built since the last scan tick should still be a valid destination.
            hub.ScanIfChanged();
            IndexTargets(hub);

            int stored = 0;
            HashSet<Container> usedChests = new HashSet<Container>();

            // GetAllItems hands back the live backing list and emptied stacks drop out of it, so iterate
            // a copy.
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(src.GetAllItems())) {
                if (!Sortable(item)) { continue; }
                if (!TargetsByName.TryGetValue(item.m_shared.m_name, out List<Container> holders)) { continue; }
                stored += FileItem(hub, src, item, holders, usedChests, ref result.Waiting);
            }
            ReleaseTargets();

            if (stored > 0) {
                // Chest contents changed outside a membership rebuild.
                ContainerNetwork.InvalidateItemCounts();
            }

            // Leftovers stay in the box rather than being handed back. The old hand-back (into the
            // player's pack, then onto the ground) duplicated items, and simply not moving them cannot.
            int leftover = src.CountItems(null, -1, matchWorldLevel: false);
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[AutoStore] stored {stored} items across {usedChests.Count} chests, {leftover} left in the box.");
            }
            if (report) { Report(player, stored, usedChests.Count, leftover, result.Waiting); }

            result.Stored = stored;
            result.Chests = usedChests.Count;
            result.Leftover = leftover;
            if (result.Waiting && leftover > 0) { StartRetry(hub); }
            return result;
        }

        // Enchanted gear shares m_shared.m_name with its mundane counterpart, so matching by type would
        // happily file a legendary into a chest of ordinary swords. Note this asks IsMagicItem, not
        // IsProtectedItem: the latter is additionally gated on Epic Loot's provider having registered, and
        // this switch has to hold either way.
        private static bool Sortable(ItemDrop.ItemData item) {
            if (item == null || item.m_shared == null) { return false; }
            return ValConfig.SortMagicItems.Value || !EpicLootIntegration.IsMagicItem(item);
        }

        /// <summary>
        /// Files one stack from the box into the chests already holding its kind, in link order. Returns how
        /// many units left the box. <paramref name="waiting"/> is set when part of it has to wait for a chest
        /// another player still owns.
        /// </summary>
        private static int FileItem(AutomationHub hub, Inventory src, ItemDrop.ItemData item, List<Container> holders, HashSet<Container> usedChests, ref bool waiting) {
            int remaining = item.m_stack;
            int stored = 0;
            bool asked = false;
            foreach (Container target in holders) {
                if (remaining <= 0) { break; }
                Inventory dst = target.GetInventory();
                if (dst == null || !HasMatching(dst, item)) { continue; }

                // Checked before asking, so a chest with no room is not handed over for nothing.
                if (InventoryOps.FreeSpaceFor(dst, item) <= 0) { continue; }

                if (StorageOwnership.IsBusy(target)) {
                    ZNetView view = target.m_nview;
                    // Unloaded, or open in this player's own panel: left alone.
                    if (view == null || !view.IsValid() || view.IsOwner()) { continue; }
                    // Open in another player's. It cannot change hands, so its owner files the items.
                    int sent = SendToOpenChest(hub, src, item, target, remaining);
                    if (sent <= 0) { continue; }
                    remaining -= sent;
                    stored += sent;
                    usedChests.Add(target);
                    continue;
                }

                // Only the chest's owner may write to it. One someone else owns is asked for, and this
                // item waits in the box for the retry.
                if (!StorageOwnership.TryAcquire(target)) {
                    asked = true;
                    continue;
                }
                // TryAcquire reloaded the grid, so ask again of what is actually in it now.
                if (!HasMatching(dst, item)) { continue; }

                int landed = InventoryOps.MoveMeasured(src, item, dst, remaining);
                if (landed <= 0) { continue; }
                remaining -= landed;
                stored += landed;
                usedChests.Add(target);
            }
            // Only waiting if an owned chest did not end up taking it all anyway.
            if (asked && remaining > 0) { waiting = true; }
            return stored;
        }

        /// <summary>
        /// Groups the hub's linked chests that can receive sorted goods by the names of what each already
        /// holds, in link order. Paired with <see cref="ReleaseTargets"/>.
        /// </summary>
        private static void IndexTargets(AutomationHub hub) {
            ReleaseTargets();
            foreach (Container target in hub.LinkedContainers) {
                if (!IsSortTarget(target)) { continue; }
                Inventory dst = target.GetInventory();
                if (dst == null) { continue; }
                foreach (ItemDrop.ItemData have in dst.GetAllItems()) {
                    if (have == null || have.m_shared == null) { continue; }
                    if (!TargetsByName.TryGetValue(have.m_shared.m_name, out List<Container> holders)) {
                        holders = SpareLists.Count > 0 ? SpareLists.Pop() : new List<Container>();
                        TargetsByName[have.m_shared.m_name] = holders;
                    }
                    // A chest holding several stacks of one thing is listed once.
                    if (holders.Count == 0 || holders[holders.Count - 1] != target) { holders.Add(target); }
                }
            }
        }

        private static void ReleaseTargets() {
            foreach (List<Container> holders in TargetsByName.Values) {
                holders.Clear();
                SpareLists.Push(holders);
            }
            TargetsByName.Clear();
        }

        /// <summary>
        /// Whether a linked container can receive sorted goods at all, before looking at what it holds.
        /// </summary>
        private static bool IsSortTarget(Container target) {
            if (target == null) { return false; }
            // Public chests only. The linked pool admits a Private chest when the local player placed it,
            // which is right for crafting out of your own storage, but a locked chest is a deliberate
            // choice about what goes in it - it is never a sort destination. Group chests never reach the
            // pool, but the rule is phrased as "Public or nothing" so it cannot quietly start admitting them.
            if (target.m_privacy != Container.PrivacySetting.Public) { return false; }
            // Plain chests only. A machine's store is a working buffer, not storage: a Hopper's already
            // holds whatever its smelters just produced and would soak up the whole haul, a Grinder's
            // results chest would fill up under it, and a Lighter's tank would take the coal meant for the
            // coal chest. Crafting still counts them. Boats and carts lend to crafting only too: they leave,
            // and anything filed into one would leave with them - a cart that happens to carry wood is not
            // the wood chest.
            return StorageRegistry.KindOf(target) == StorageRegistry.Kind.Chest;
        }

        /// <summary>
        /// While the player has a deposit box open, asks ahead for the chests its contents would be filed
        /// into, so they are this client's by the time the box closes and the sort runs.
        /// </summary>
        internal static void WantAhead(Container open) {
            if (open == null || !IsDepositBox(open)) { return; }
            AutomationHub hub = StorageRegistry.HolderOf<AutomationHub>(open);
            if (!CanSort(hub) || hub.DepositBox != open) { return; }
            Inventory src = open.GetInventory();
            if (src == null || src.NrOfItems() == 0) { return; }

            IndexTargets(hub);
            foreach (ItemDrop.ItemData item in src.GetAllItems()) {
                if (!Sortable(item)) { continue; }
                if (!TargetsByName.TryGetValue(item.m_shared.m_name, out List<Container> holders)) { continue; }
                foreach (Container target in holders) {
                    Inventory dst = target.GetInventory();
                    if (dst == null || StorageOwnership.IsBusy(target)) { continue; }
                    if (HasMatching(dst, item) && InventoryOps.FreeSpaceFor(dst, item) > 0) { StorageOwnership.Want(target); }
                }
            }
            ReleaseTargets();
        }

        /// <summary>The hub was switched off or unloaded; Unity stopped its retry without running its finally block.</summary>
        internal static void HubDisabled(AutomationHub hub) {
            Retrying.Remove(hub);
        }

        // ---- filing into a chest someone has open ----------------------------------

        /// <summary>
        /// Sends up to <paramref name="amount"/> of <paramref name="item"/> to the owner of a chest that
        /// is open in their panel, for them to put in (see <see cref="StorageOps"/>). Returns how many
        /// left the box.
        ///
        /// This is the one place a sort lets go of an item before it has landed. The copy sent stays with
        /// the request, and whatever the owner does not take - the chest filled up, changed hands, or
        /// never answered - comes back to the box.
        /// </summary>
        private static int SendToOpenChest(AutomationHub hub, Inventory src, ItemDrop.ItemData item, Container target, int amount) {
            // Cannot be rebuilt on the owner's side, so it would leave here and arrive nowhere.
            if (!ItemIdentity.CanTravel(item)) { return 0; }
            amount = Mathf.Min(amount, Mathf.Min(item.m_stack, InventoryOps.FreeSpaceFor(target.GetInventory(), item)));
            if (amount <= 0) { return 0; }

            ItemDrop.ItemData sent = item.Clone();
            sent.m_stack = amount;
            src.RemoveItem(item, amount);
            // Never given up on early: an owner that is still there may have put it in, and only its
            // answer says so.
            StorageRpc.Send(target, StorageRpc.Op.Give,
                args => ItemCodec.Write(args, new[] { sent }),
                (status, reply, late) => OnFiled(hub, sent, status, reply, late),
                giveUpOnOwnerChange: false);
            return amount;
        }

        private static void OnFiled(AutomationHub hub, ItemDrop.ItemData sent, StorageRpc.Status status, ZPackage reply, bool late) {
            // It went back in the box when no answer came. If the owner did put it in after all, it now
            // exists twice - the price of never losing it.
            if (late) { return; }

            int accepted = 0;
            if (status == StorageRpc.Status.Ok) {
                accepted = sent.m_stack;
            } else if (status == StorageRpc.Status.Partial && reply != null && reply.ReadInt() == 1) {
                accepted = reply.ReadInt();
            }
            int back = sent.m_stack - accepted;
            if (back <= 0) { return; }

            ItemDrop.ItemData rest = sent.Clone();
            rest.m_stack = back;
            ReturnToBox(hub, rest);
        }

        // Back where it came from when that is still this client's to write; otherwise to the player.
        private static void ReturnToBox(AutomationHub hub, ItemDrop.ItemData item) {
            int left = item.m_stack;
            Container box = hub != null ? hub.DepositBox : null;
            if (box != null && box.m_nview != null && box.m_nview.IsValid() && box.m_nview.IsOwner()) {
                StorageOwnership.Sync(box);
                left -= InventoryOps.AddMeasured(box.GetInventory(), item, left);
            }
            if (ValConfig.EnableDebugMode.Value) {
                Logger.LogInfo($"[AutoStore] {item.m_stack} {item.m_shared.m_name} came back from an open chest; {left} did not fit in the box.");
            }
            if (left > 0) {
                ItemDrop.ItemData rest = item.Clone();
                rest.m_stack = left;
                PlayerDelivery.Give(rest);
            }
            if (Player.m_localPlayer != null) {
                Player.m_localPlayer.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$DA_Sorted_returned", item.m_stack.ToString()));
            }
        }

        private static void StartRetry(AutomationHub hub) {
            if (!hub.isActiveAndEnabled || !Retrying.Add(hub)) { return; }
            hub.StartCoroutine(RetrySort(hub));
        }

        /// <summary>
        /// Files what a sort had to leave behind for chests that were still being handed over. Stops as
        /// soon as nothing is waiting, when the player opens the box again (closing it sorts anyway), or
        /// when the box is no longer this client's (Sort then does nothing and reports nothing waiting).
        /// </summary>
        private static IEnumerator RetrySort(AutomationHub hub) {
            try {
                for (int attempt = 0; attempt < RetryAttempts; ++attempt) {
                    yield return new WaitForSeconds(RetryDelay);
                    if (hub == null || hub.DepositBox == null || hub.DepositBox.IsInUse()) { yield break; }
                    if (!RetryOnce(hub)) { yield break; }
                }
            } finally {
                Retrying.Remove(hub);
            }
        }

        // One retry pass. True when something is still waiting on a chest.
        private static bool RetryOnce(AutomationHub hub) {
            try {
                SortResult result = Sort(hub, Player.m_localPlayer, report: false);
                // Quiet unless something moved: a retry that files nothing has nothing new to say.
                if (result.Stored > 0) { Report(Player.m_localPlayer, result.Stored, result.Chests, result.Leftover, result.Waiting); }
                return result.Waiting && result.Leftover > 0;
            } catch (System.Exception ex) {
                Logger.LogError($"AutoStore: retrying the sort failed: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Sorts <paramref name="box"/> if it is a deposit box whose hub sorts: what closing the box and the
        /// Deposit Selected button both do.
        /// </summary>
        internal static SortResult SortBox(Container box, Player player, bool report) {
            if (!IsDepositBox(box)) { return default; }
            AutomationHub hub = StorageRegistry.HolderOf<AutomationHub>(box);
            if (!CanSort(hub) || hub.DepositBox != box) { return default; }
            return Sort(hub, player, report);
        }

        /// <summary>
        /// Whether the hub sorts at all. Off, or a dormant hub - whose scan deliberately links nothing, so
        /// sorting would just hand everything straight back - and the box sits there as a plain chest.
        /// </summary>
        internal static bool CanSort(AutomationHub hub) {
            return hub != null && ValConfig.AutoStoreEnabled.Value && !hub.IsDormant;
        }

        /// <summary>True when the chest already holds an item that would stack with this one.</summary>
        private static bool HasMatching(Inventory inv, ItemDrop.ItemData item) {
            foreach (ItemDrop.ItemData have in inv.GetAllItems()) {
                // Vanilla's own stacking test: name + world level, and quality only for gear that has
                // quality levels. Variant is deliberately not compared, matching vanilla.
                if (have != null && have.IsSameType(item)) { return true; }
            }
            return false;
        }

        // One centre message: a second would just overwrite the first.
        private static void Report(Player player, int stored, int chests, int leftover, bool waiting) {
            if (player == null) { return; }
            if (stored > 0 && leftover > 0) {
                player.Message(MessageHud.MessageType.Center, Localization.instance.Localize(
                    "$DA_Sorted_partial", stored.ToString(), chests.ToString(), leftover.ToString()));
            } else if (stored > 0) {
                player.Message(MessageHud.MessageType.Center, Localization.instance.Localize(
                    "$DA_Sorted_items", stored.ToString(), chests.ToString()));
            } else if (leftover > 0) {
                // Nothing moved yet, but there are chests for it: they are on their way to this client.
                player.Message(MessageHud.MessageType.Center, waiting ? "$DA_Sort_waiting" : "$DA_Sort_nothing");
            }
        }
    }
}
