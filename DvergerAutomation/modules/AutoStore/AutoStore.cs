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

        // Every live deposit box. Container_Load_Patch runs for every chest in the world once a second, so
        // telling a deposit box apart has to be a hash lookup, not a hierarchy walk.
        private static readonly HashSet<Container> DepositBoxes = new HashSet<Container>();

        internal static void RegisterDepositBox(Container box) {
            if (box == null) { return; }
            DepositBoxes.Add(box);
            ApplySize(box);
        }

        internal static void UnregisterDepositBox(Container box) {
            if (box != null) { DepositBoxes.Remove(box); }
        }

        internal static bool IsDepositBox(Container container) {
            return container != null && DepositBoxes.Contains(container);
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
            foreach (Container box in DepositBoxes) {
                // Rebuilding the grid under a player's open panel would reset their selection and any drag in
                // progress. Their close hook applies the size instead.
                if (box == null || box.IsInUse()) { continue; }
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

            // There is no ZNetView on the child, so the Container has to be pointed at the piece's own.
            // Without this Container.Awake NREs on the very next line (m_nview.GetZDO()).
            box.m_rootObjectOverride = prefab.GetComponent<ZNetView>();
            box.m_name = "$DA_store_goods_name";
            box.m_checkGuardStone = true;

            // Already correct in the prefab, re-asserted because each would break the piece in a way that
            // is not obvious from the Unity inspector:
            //  - Private calls m_piece.GetCreator(), and m_piece is null on a child object.
            //  - autoDestroyEmpty would nview.Destroy() the SHARED root view, deleting the whole piece
            //    the moment the box ran empty.
            //  - any discover stat other than None invokes an RPC vanilla registers under a different
            //    name ("RPC_Discovered"), logging an unknown-RPC error on every open.
            box.m_privacy = Container.PrivacySetting.Public;
            box.m_autoDestroyEmpty = false;
            box.m_discoverStat = PlayerStatType.None;

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
        }

        /// <summary>
        /// Distributes the deposit box's contents into the hub's linked chests. Anything left over stays
        /// put in the box. Local-player only: it runs off the inventory UI closing, or off the Deposit All
        /// button, which passes <paramref name="report"/> false so it can fold this into one message of
        /// its own instead of having a second centre message overwrite it.
        /// </summary>
        internal static SortResult Sort(AutomationHub hub, Player player, bool report = true) {
            SortResult result = default;
            if (hub == null || hub.DepositBox == null) { return result; }
            Inventory src = hub.DepositBox.GetInventory();
            if (src == null || src.NrOfItems() == 0) { return result; }

            // Opening the box already made this client the ZDO owner (Container.RPC_RequestOpen does
            // SetOwner), but re-claim rather than assume: emptying the box on a non-owner never reaches
            // Container.Save, and CheckForChanges reloads the pre-sort contents off the ZDO a second
            // later - the items would be in the chests AND back in the box.
            CraftFromStoragePatches.ClaimOwnership(hub.DepositBox);

            // A chest built since the last scan tick should still be a valid destination.
            hub.Rescan();

            int stored = 0;
            HashSet<Container> usedChests = new HashSet<Container>();

            // GetAllItems hands back the live backing list and emptied stacks drop out of it, so iterate
            // a copy.
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(src.GetAllItems())) {
                if (item == null || item.m_shared == null) { continue; }
                // Enchanted gear shares m_shared.m_name with its mundane counterpart, so matching by type
                // would happily file a legendary into a chest of ordinary swords. Note this asks
                // IsMagicItem, not IsProtectedItem: the latter is additionally gated on Epic Loot's
                // provider having registered, and this switch has to hold either way.
                if (!ValConfig.SortMagicItems.Value && EpicLootIntegration.IsMagicItem(item)) { continue; }

                int remaining = item.m_stack;
                foreach (Container target in hub.LinkedContainers) {
                    if (remaining <= 0) { break; }
                    if (target == null) { continue; }
                    // Public chests only. The linked pool admits a Private chest when the local player
                    // placed it, which is right for crafting out of your own storage, but a locked chest is
                    // a deliberate choice about what goes in it - it is never a sort destination. Group
                    // chests never reach the pool, but the rule is phrased as "Public or nothing" so it
                    // cannot quietly start admitting them.
                    if (target.m_privacy != Container.PrivacySetting.Public) { continue; }
                    // A hopper's store is a working buffer, not storage: it already holds whatever its
                    // smelters just produced, so it matches everything of that type and would soak up
                    // the whole haul - and a full hopper stops collecting output. Crafting still counts
                    // it (it is in the linked pool), it just never receives sorted goods.
                    if (target.GetComponentInParent<HopperHub>() != null) { continue; }
                    Inventory dst = target.GetInventory();
                    if (dst == null || CraftFromStoragePatches.IsBusy(target)) { continue; }
                    if (!HasMatching(dst, item)) { continue; }

                    // Checked before claiming, so a chest with no room does not get its ZDO ownership
                    // yanked across the network for nothing.
                    if (FreeSpaceFor(dst, item) <= 0) { continue; }

                    // Container.OnContainerChanged -> Save() is a no-op for non-owners, so the write would
                    // be silently reverted within the second.
                    CraftFromStoragePatches.ClaimOwnership(target);

                    int landed = MoveMeasured(src, item, dst, remaining);
                    if (landed <= 0) { continue; }
                    remaining -= landed;
                    stored += landed;
                    usedChests.Add(target);
                }
            }

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
            if (report) { Report(player, stored, usedChests.Count, leftover); }

            result.Stored = stored;
            result.Chests = usedChests.Count;
            result.Leftover = leftover;
            return result;
        }

        /// <summary>
        /// Moves up to <paramref name="amount"/> units of <paramref name="item"/> out of
        /// <paramref name="from"/> and into <paramref name="to"/>, returning how many actually landed.
        /// Partial moves are the normal case - that is what lets one stack of 50 wood spread across three
        /// chests.
        ///
        /// The amount is measured across the add rather than taken from <c>AddItem</c>'s return value: for
        /// stackables it merges into existing stacks as it goes and can still report failure, so trusting
        /// it would either strand units or remove more from the source than ever arrived.
        /// </summary>
        internal static int MoveMeasured(Inventory from, ItemDrop.ItemData item, Inventory to, int amount) {
            amount = Mathf.Min(amount, Mathf.Min(item.m_stack, FreeSpaceFor(to, item)));
            if (amount <= 0) { return 0; }

            // A clone, because AddItem takes ownership of whatever instance it is handed. Clone carries
            // durability, quality, crafter and Epic Loot's m_customData across.
            string name = item.m_shared.m_name;
            ItemDrop.ItemData slice = item.Clone();
            slice.m_stack = amount;

            int before = to.CountItems(name, -1, matchWorldLevel: false);
            to.AddItem(slice);
            int landed = to.CountItems(name, -1, matchWorldLevel: false) - before;
            if (landed <= 0) { return 0; }

            // No yield or RPC between the add and this removal, so the units never exist twice.
            from.RemoveItem(item, landed);
            return landed;
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

        /// <summary>
        /// How much of <paramref name="item"/> the chest can take: room on top of matching stacks plus
        /// whole empty slots. Hand-rolled rather than <c>Inventory.FindFreeStackSpace</c>, which ignores
        /// quality and so over-reports for gear.
        /// </summary>
        private static int FreeSpaceFor(Inventory inv, ItemDrop.ItemData item) {
            int max = Mathf.Max(1, item.m_shared.m_maxStackSize);
            // Clamped: GetEmptySlots is a plain width*height - count, and Container.UpdateRows grows the
            // grid to fit an over-full ZDO, so it can read negative.
            int space = Mathf.Max(0, inv.GetEmptySlots()) * max;
            foreach (ItemDrop.ItemData have in inv.GetAllItems()) {
                if (have != null && have.m_stack < max && have.IsSameType(item)) {
                    space += max - have.m_stack;
                }
            }
            return space;
        }

        // One centre message: a second would just overwrite the first.
        private static void Report(Player player, int stored, int chests, int leftover) {
            if (player == null) { return; }
            if (stored > 0 && leftover > 0) {
                player.Message(MessageHud.MessageType.Center, Localization.instance.Localize(
                    "$DA_Sorted_partial", stored.ToString(), chests.ToString(), leftover.ToString()));
            } else if (stored > 0) {
                player.Message(MessageHud.MessageType.Center, Localization.instance.Localize(
                    "$DA_Sorted_items", stored.ToString(), chests.ToString()));
            } else if (leftover > 0) {
                player.Message(MessageHud.MessageType.Center, "$DA_Sort_nothing");
            }
        }
    }
}
