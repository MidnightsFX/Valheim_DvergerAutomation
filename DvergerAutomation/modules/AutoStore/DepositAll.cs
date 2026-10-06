using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DvergerAutomation {
    /// <summary>
    /// The deposit button, shown on both of the mod's containers: one press moves the matching part of
    /// the player's pack into the open container, so a haul goes in without dragging a stack at a time.
    ///
    /// What "matching" means depends on which container is open:
    ///  - the AutoSorter's deposit box shows it as Deposit Selected, and takes what the player picked in
    ///    the [C] filter beside it (<see cref="DepositFilterPanel"/>): item categories, and whether the
    ///    hotbar and an inventory mod's quick slots count. Equipped gear and equipment slots are never
    ///    taken. What lands is then sorted exactly as closing the box would sort it, so nothing here
    ///    needs its own idea of where an item belongs.
    ///  - the Hopper's storage takes only what its stations actually consume - the prefabs listed in
    ///    Hopper Deposit Items, which defaults to the wood a kiln burns and every ore a smelter or blast
    ///    furnace melts. There is no sort step; the Hopper feeds itself from what is inside it. Its [C]
    ///    (<see cref="HopperTargetPanel"/>) picks which stations it services, not what is deposited.
    ///
    /// The button takes over the slot vanilla's Place stacks button occupies, and its controller binding,
    /// and hides it for as long as one of our containers is open. Ordinary chests are untouched and keep
    /// Place stacks.
    /// </summary>
    internal static class DepositAll {
        private const string ObjectName = "DA_DepositAllButton";

        /// <summary>Which of the mod's containers is open, which decides both the filter and the wording.</summary>
        private enum Target { None, Sorter, Hopper }

        private static GameObject button;

        /// <summary>What the button is currently dressed as, so label and tooltip are rewritten only on a change.</summary>
        private static Target shown = Target.None;

        // Parsed forms of the two filter configs. Null means "not built yet"; ValConfig clears them
        // through InvalidateFilters whenever either entry is edited or synced from an admin.
        private static HashSet<ItemDrop.ItemData.ItemType> ignoredTypes;
        private static HashSet<string> hopperItems;

        // ---- the button ---------------------------------------------------------

        /// <summary>
        /// Clones vanilla's Place stacks button into the container panel. That template is the right shape
        /// for this (a wide labelled button that moves the pack into the open chest), and it carries the
        /// vanilla button sprite, label styling and click sound with it.
        /// </summary>
        internal static void Attach(InventoryGui gui) {
            if (gui == null || gui.m_container == null || gui.m_stackAllButton == null) { return; }
            if (gui.m_container.Find(ObjectName) != null) { return; }

            GameObject go = CloneStackAllButton(gui, ObjectName, OnClick, keepGamepad: true);

            // The template's rect is deliberately left exactly as cloned. Place stacks is a direct child
            // of the same Container panel, so the copy lands precisely on top of it - which is the point:
            // Refresh hides the original while ours is up, so the slot is taken over rather than shared.
            button = go;
            // A fresh button has a blank label, so the first container opened must write its wording
            // even when it is the same kind as the last one opened before a logout.
            shown = Target.None;

            DepositFilterPanel.Attach(gui, (RectTransform)go.transform);
            HopperTargetPanel.Attach(gui, (RectTransform)go.transform);
        }

        /// <summary>
        /// A copy of Place stacks, parented into the container panel, hidden, and calling
        /// <paramref name="onClick"/>. Shared with the filter's [C] button so both carry the vanilla
        /// button sprite, label styling, click sound and a tooltip. With <paramref name="keepGamepad"/>
        /// the copy also keeps Place stacks' controller binding and hint glyph; otherwise it is
        /// pointer-only.
        /// </summary>
        internal static GameObject CloneStackAllButton(InventoryGui gui, string name, UnityEngine.Events.UnityAction onClick, bool keepGamepad = false) {
            // worldPositionStays false: the two-argument overload keeps the clone's world transform by
            // rewriting its local scale and position, which is never what a UI element parented into a
            // different panel wants.
            GameObject go = Object.Instantiate(gui.m_stackAllButton.gameObject, gui.m_container, false);
            go.name = name;
            go.SetActive(false);

            // Place stacks binds the right stick (Take All has the left) and owns the hint glyph that
            // advertises it. The deposit button keeps both: it takes over Place stacks' slot and Refresh
            // hides the original while ours is up, and a hidden UIGamePad gets no Update, so the binding
            // is handed across rather than contested. Anything else cloned from here is on screen
            // alongside the deposit button and would be a second claimant to the same stick.
            UIGamePad pad = go.GetComponent<UIGamePad>();
            GameObject hint = null;
            if (keepGamepad) {
                hint = pad != null ? pad.m_hint : null;
            } else {
                if (pad != null) { Object.Destroy(pad); }
                foreach (Transform child in go.transform) {
                    if (child.name.StartsWith("gamepad_hint")) { Object.Destroy(child.gameObject); }
                }
            }

            // Instantiate carries serialized listeners only, and InventoryGui adds OnStackAll at runtime,
            // so the copy's onClick arrives empty. Cleared anyway rather than relying on that.
            Button click = go.GetComponent<Button>();
            click.onClick.RemoveAllListeners();
            click.onClick.AddListener(onClick);

            DetachFromLocalization(go, hint);
            go.transform.SetAsLastSibling();
            AddTooltip(gui, go);
            return go;
        }

        /// <summary>
        /// Keeps vanilla from writing "Place stacks" back over the clone's label. This runs from
        /// InventoryGui.Awake, before the inventory's Localize component has had its Start, so the copied
        /// label still reads "$inventory_stackall". Start's pass would localize it and cache that token as
        /// the label's original text, and every UIInputHint that toggles afterwards (RefreshLocalization)
        /// re-applies cached originals to all visible labels, overwriting whatever we wrote there.
        /// A label with no '$' in it is never cached, and dropping any entry already made covers the
        /// case where Start has run.
        ///
        /// A kept gamepad hint (<paramref name="hint"/>) is left alone: its "$KEY_RStick" is meant to go
        /// through that machinery, which turns it into the controller's glyph and swaps the glyph when
        /// the input layout changes, exactly as it does for the original.
        /// </summary>
        private static void DetachFromLocalization(GameObject go, GameObject hint) {
            Localization loc = Localization.instance;
            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(includeInactive: true)) {
                if (hint != null && text.transform.IsChildOf(hint.transform)) { continue; }
                if (text.text != null && text.text.Contains("$")) { text.text = ""; }
                loc?.RemoveTextFromCache(text);
            }
        }

        /// <summary>
        /// Place stacks has no tooltip of its own, so one is added and pointed at the tooltip prefab the
        /// repair button already carries. Skipped rather than improvised if that is not there.
        /// </summary>
        private static void AddTooltip(InventoryGui gui, GameObject go) {
            UITooltip template = gui.m_repairButton != null ? gui.m_repairButton.GetComponent<UITooltip>() : null;
            if (template == null || template.m_tooltipPrefab == null) { return; }

            UITooltip tooltip = go.AddComponent<UITooltip>();
            tooltip.m_tooltipPrefab = template.m_tooltipPrefab;
        }

        /// <summary>
        /// Shows the button only while one of the mod's containers is open, and hides vanilla's Place
        /// stacks for exactly as long. Driven from <c>InventoryGui.UpdateContainer</c>, which is where
        /// vanilla decides whether the container panel is up at all - so this runs every frame the
        /// inventory is open, and is a couple of component lookups and a bool compare.
        ///
        /// Vanilla never touches the Place stacks GameObject's active state itself (it only wires the
        /// click in Awake), so there is nothing here to fight over it. Opening any ordinary chest runs
        /// this again with target None and hands the button straight back.
        /// </summary>
        internal static void Refresh(InventoryGui gui) {
            if (button == null) { return; }

            Target target = TargetOf(gui != null ? gui.m_currentContainer : null, out HopperHub hopper);
            bool show = target != Target.None;

            if (button.activeSelf != show) { button.SetActive(show); }
            if (gui != null && gui.m_stackAllButton != null) {
                GameObject stackAll = gui.m_stackAllButton.gameObject;
                if (stackAll.activeSelf == show) { stackAll.SetActive(!show); }
            }

            if (show && target != shown) {
                shown = target;
                ApplyWording();
            }

            // Each container has its own [C] in the same spot: the Sorter's picks what Deposit Selected
            // takes, the Hopper's picks which stations it services.
            DepositFilterPanel.Show(target == Target.Sorter);
            HopperTargetPanel.Show(hopper);
        }

        /// <summary>Which of the mod's containers this is, if either, and the Hopper when it is that one.</summary>
        private static Target TargetOf(Container container, out HopperHub hopper) {
            hopper = null;
            if (container == null) { return Target.None; }
            if (AutoStore.IsDepositBox(container)) { return Target.Sorter; }

            // The store is a child object of the piece, so the hub is found upwards; the identity check
            // keeps this to the Hopper's own storage rather than any container that happens to sit under
            // a Hopper in the hierarchy.
            HopperHub hub = container.GetComponentInParent<HopperHub>();
            if (hub != null && hub.Store == container) {
                hopper = hub;
                return Target.Hopper;
            }

            return Target.None;
        }

        // ---- wording ------------------------------------------------------------

        /// <summary>
        /// Rewrites the label and tooltip for whichever container is open. Called when the target changes
        /// and when either filter config does - never on the per-frame path.
        /// </summary>
        internal static void ApplyWording(object sender = null, System.EventArgs e = null) {
            if (button == null || Localization.instance == null) { return; }

            bool hopper = shown == Target.Hopper;
            string title = Localization.instance.Localize(hopper ? "$DA_deposit_hopper" : "$DA_deposit_selected");

            // Addressed by name rather than GetComponentInChildren, which would depend on this button's
            // label still being the first text in its subtree. The resolved string is written rather than
            // the raw "$..." token: vanilla's Localize pass over the panel only rewrites strings that
            // still contain '$', so writing resolved text keeps this label out of that machinery and out
            // of its re-localization cache (CloneStackAllButton evicts the entry the cloned template
            // text would otherwise have left there). The cost is that the label does not follow a
            // language change made mid-session, which the tooltip below does.
            Transform labelRoot = button.transform.Find("Text");
            TMP_Text label = labelRoot != null ? labelRoot.GetComponent<TMP_Text>() : null;
            if (label != null) { label.text = title; }

            UITooltip tooltip = button.GetComponent<UITooltip>();
            if (tooltip == null) { return; }

            string text = Localization.instance.Localize(hopper ? "$DA_deposit_hopper_desc" : "$DA_deposit_selected_desc");
            if (!hopper) {
                // The item categories are on show in the filter itself; the tooltip only spells out the
                // two choices that decide whether the player's working set is touched at all.
                text += "\n" + Localization.instance.Localize(ValConfig.DepositIncludeHotbar.Value
                    ? "$DA_deposit_hotbar_in" : "$DA_deposit_hotbar_kept");
                if (InventorySlotsIntegration.Active) {
                    text += "\n" + Localization.instance.Localize(ValConfig.DepositIncludeQuickSlots.Value
                        ? "$DA_deposit_quickslots_in" : "$DA_deposit_quickslots_kept");
                }
            }

            // Set() rather than writing the fields: it also repaints a tooltip that is on screen right
            // now, which is the case when these are edited from the F1 menu with the container open.
            tooltip.Set(title, text);
        }

        // ---- filters ------------------------------------------------------------

        /// <summary>
        /// Drops both parsed filters and repaints the button's tooltip and the filter popup. Subscribed
        /// by ValConfig to every deposit filter entry, so a click in the popup, an edit in the F1 menu
        /// or a reload of the file takes effect on the next press.
        /// </summary>
        internal static void InvalidateFilters(object sender = null, System.EventArgs e = null) {
            ignoredTypes = null;
            hopperItems = null;
            ApplyWording();
            DepositFilterPanel.Repaint();
        }

        /// <summary>
        /// The configured item types the deposit box will not take, parsed once and kept. An entry that
        /// is not an <c>ItemType</c> is reported rather than silently dropped - a typo here is otherwise
        /// invisible, because the only symptom is that one kind of item keeps getting deposited.
        /// </summary>
        internal static HashSet<ItemDrop.ItemData.ItemType> IgnoredTypes() {
            if (ignoredTypes != null) { return ignoredTypes; }

            HashSet<ItemDrop.ItemData.ItemType> parsed = new HashSet<ItemDrop.ItemData.ItemType>();
            foreach (string token in SplitList(ValConfig.DepositIgnoredTypes.Value)) {
                if (System.Enum.TryParse(token, true, out ItemDrop.ItemData.ItemType type)) {
                    parsed.Add(type);
                } else {
                    Logger.LogWarning($"AutoStore: Deposit All Ignored Types - '{token}' is not an item type.");
                }
            }
            ignoredTypes = parsed;
            return ignoredTypes;
        }

        /// <summary>
        /// The configured Hopper intake, as the shared-name tokens the items actually carry.
        ///
        /// The config names prefabs, because that is the stable name a player can look up, but matching
        /// happens on <c>m_shared.m_name</c>: an item in an inventory is only guaranteed to carry its
        /// shared data, whereas <c>m_dropPrefab</c> is a reference that a save/load round trip need not
        /// restore. Resolving is deferred to the first press because ObjectDB is not up when configs bind.
        /// </summary>
        private static HashSet<string> HopperItems() {
            if (hopperItems != null) { return hopperItems; }

            HashSet<string> parsed = new HashSet<string>();
            ObjectDB db = ObjectDB.instance;
            foreach (string name in SplitList(ValConfig.HopperDepositItems.Value)) {
                GameObject prefab = db != null ? db.GetItemPrefab(name) : null;
                ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop == null || drop.m_itemData == null || drop.m_itemData.m_shared == null) {
                    Logger.LogWarning($"Hopper: Deposit Items - no item prefab named '{name}'.");
                    continue;
                }
                parsed.Add(drop.m_itemData.m_shared.m_name);
            }

            // Only cached once ObjectDB could actually answer, so a press made before it is up does not
            // freeze an empty set in for the session.
            if (db != null) { hopperItems = parsed; }
            return parsed;
        }

        /// <summary>Splits a comma-separated config entry, trimming blanks and ignoring empty slots.</summary>
        private static IEnumerable<string> SplitList(string value) {
            if (string.IsNullOrEmpty(value)) { yield break; }
            foreach (string part in value.Split(',')) {
                string trimmed = part.Trim();
                if (trimmed.Length > 0) { yield return trimmed; }
            }
        }

        // ---- the action ---------------------------------------------------------

        private static void OnClick() {
            InventoryGui gui = InventoryGui.instance;
            Player player = Player.m_localPlayer;
            if (gui == null || player == null) { return; }
            // Vanilla's Take All and Place stacks both refuse mid-teleport, when the inventory is in a
            // state the server has not acknowledged yet.
            if (player.IsTeleporting()) { return; }

            Container box = gui.m_currentContainer;
            Target target = TargetOf(box, out _);
            if (target == Target.None) { return; }

            // The same guard vanilla puts in front of both its bulk moves: a drag in progress holds an
            // item instance this is about to empty out from under it.
            gui.SetupDragItem(null, null, 1);

            try {
                int moved = MoveInventory(player, box, target, out int noRoom);

                AutoStore.SortResult sorted = target == Target.Sorter
                    ? SortAfterDeposit(box, player)
                    : default;

                if (moved > 0 && gui.m_moveItemEffects != null) {
                    gui.m_moveItemEffects.Create(box.transform.position, Quaternion.identity);
                }
                Report(player, moved, noRoom, sorted, target);
            } catch (System.Exception ex) {
                // A throw here would leave the panel mid-move with no feedback at all.
                Logger.LogError($"AutoStore: Deposit All failed: {ex}");
            }

            // Deliberately no ApplySize: the container is open and its grid is on screen, and resizing an
            // inventory the container panel is drawing resets the view under the player.
        }

        /// <summary>
        /// Files the deposit box away exactly as closing it would. Gated exactly as it is on close: with
        /// auto-store off, or a hub too dormant to have linked anything, the box is just a chest and the
        /// press was just a bulk move into it.
        /// </summary>
        private static AutoStore.SortResult SortAfterDeposit(Container box, Player player) {
            AutomationHub hub = box.GetComponentInParent<AutomationHub>();
            if (hub == null || hub.DepositBox != box) { return default; }

            bool dormant = ValConfig.RequireCores.Value && hub.CoreCount == 0;
            if (!ValConfig.AutoStoreEnabled.Value || dormant) { return default; }
            return AutoStore.Sort(hub, player, report: false);
        }

        /// <summary>
        /// Moves every depositable item from the player's pack into the container, returning how many
        /// units landed. <paramref name="noRoom"/> receives the units that were eligible but had nowhere
        /// to go, so the report can say so rather than leaving them silently behind.
        /// </summary>
        private static int MoveInventory(Player player, Container box, Target target, out int noRoom) {
            noRoom = 0;
            Inventory src = player.GetInventory();
            Inventory dst = box.GetInventory();
            if (src == null || dst == null) { return 0; }

            // Opening the container made this client its owner (Container.RPC_RequestOpen sets it). If that
            // no longer holds, a write here would never reach Container.Save and would be reverted off the
            // ZDO within the second, while the items had already left the pack - so move nothing. Vanilla
            // hides the panel of a container it no longer owns anyway.
            if (box.m_nview == null || !box.m_nview.IsValid() || !box.m_nview.IsOwner()) { return 0; }

            // Which of the player's cells are hotbar, quick slots or an inventory mod's reserved rows -
            // read once, before anything moves. The Hopper takes its list wherever it sits, so it skips this.
            InventorySlotsIntegration.Snapshot slots = target == Target.Sorter ? InventorySlotsIntegration.Take(src) : null;

            int moved = 0;
            // GetAllItems hands back the live backing list and emptied stacks drop out of it, so iterate
            // a copy.
            foreach (ItemDrop.ItemData item in new List<ItemDrop.ItemData>(src.GetAllItems())) {
                if (!ShouldDeposit(item, target, slots)) { continue; }

                // Read before the move: MoveMeasured removes from the source, which decrements m_stack.
                int wanted = item.m_stack;
                int landed = AutoStore.MoveMeasured(src, item, dst, wanted);
                moved += landed;
                noRoom += wanted - landed;
            }
            return moved;
        }

        /// <summary>Whether this item is the open container's to take.</summary>
        private static bool ShouldDeposit(ItemDrop.ItemData item, Target target, InventorySlotsIntegration.Snapshot slots) {
            if (item == null || item.m_shared == null) { return false; }

            // Never moved, for either container and whatever the filters say: the equipped flag lives on
            // the item, so a worn item filed into a chest leaves the player equipping thin air.
            if (item.m_equipped) { return false; }

            return target == Target.Hopper ? ShouldDepositHopper(item) : ShouldDepositSorter(item, slots);
        }

        /// <summary>
        /// What the player selected in the filter: the categories they left lit, from the cells they let
        /// it reach. Everything held back here is either something the player is actively using or
        /// something they told the mod to leave alone.
        /// </summary>
        private static bool ShouldDepositSorter(ItemDrop.ItemData item, InventorySlotsIntegration.Snapshot slots) {
            // The working set. The hotbar is row 0; quick and equipment slots are rows an inventory mod
            // carved out of the same grid. The first two are the player's call, equipment slots never are.
            switch (slots.Classify(item)) {
                case InventorySlotsIntegration.SlotKind.Hotbar:
                    if (!ValConfig.DepositIncludeHotbar.Value) { return false; }
                    break;
                case InventorySlotsIntegration.SlotKind.QuickSlot:
                    if (!ValConfig.DepositIncludeQuickSlots.Value) { return false; }
                    break;
                case InventorySlotsIntegration.SlotKind.Reserved:
                    return false;
            }

            // The same rule the sort itself follows: with Sort Magic Items off the AutoSorter does not
            // handle enchanted gear, and depositing a legendary that the sort then refuses to file would
            // just strand it in the box.
            if (!ValConfig.SortMagicItems.Value && EpicLootIntegration.IsMagicItem(item)) { return false; }

            // Food is a property rather than a type - anything that fills a food slot - so it is its own
            // switch, and the only one that decides food. Going by ItemType would either miss food that
            // is typed as something else or sweep up the meads and potions that share Consumable with it.
            if (item.m_shared.m_food > 0f) { return !ValConfig.DepositKeepFood.Value; }

            return !IgnoredTypes().Contains(DepositCategories.Normalize(item.m_shared.m_itemType));
        }

        /// <summary>
        /// Only what the Hopper's stations consume. Deliberately does not spare the hotbar or quick
        /// slots the way the deposit box can: this is a short, explicit list of raw materials, so
        /// "deposit my ore" is expected to mean all of it.
        /// </summary>
        private static bool ShouldDepositHopper(ItemDrop.ItemData item) {
            return HopperItems().Contains(item.m_shared.m_name);
        }

        /// <summary>
        /// One centre message covering the whole press - a second would simply overwrite the first. Built
        /// from clauses rather than one string per outcome, because "deposited", "left in the box" and
        /// "would not fit" are three independent facts.
        /// </summary>
        private static void Report(Player player, int moved, int noRoom, AutoStore.SortResult sorted, Target target) {
            if (player == null) { return; }
            bool hopper = target == Target.Hopper;

            if (moved <= 0) {
                // Nothing moved for one of two quite different reasons: there was nothing the filters
                // would part with, or there was and the container could not take it.
                string empty = noRoom > 0
                    ? (hopper ? "$DA_Deposit_hopper_full" : "$DA_Deposit_box_full")
                    : "$DA_Deposit_none";
                player.Message(MessageHud.MessageType.Center, Localization.instance.Localize(empty));
                return;
            }

            if (hopper) {
                string line = Localization.instance.Localize("$DA_Deposit_hopper_result", moved.ToString());
                if (noRoom > 0) {
                    line += ", " + Localization.instance.Localize("$DA_Deposit_no_room", noRoom.ToString());
                }
                player.Message(MessageHud.MessageType.Center, line);
                return;
            }

            // Stored is zero whenever the sort did not run (auto-store off, or a dormant hub) and also
            // when it ran and nothing matched a chest. Either way the haul is sitting in the box, and
            // saying "0 filed into 0 chests" would be a worse way to say that.
            string message = sorted.Stored > 0
                ? Localization.instance.Localize("$DA_Deposit_result", moved.ToString(), sorted.Stored.ToString(), sorted.Chests.ToString())
                : Localization.instance.Localize("$DA_Deposit_box", moved.ToString());
            if (sorted.Stored > 0 && sorted.Leftover > 0) {
                message += ", " + Localization.instance.Localize("$DA_Deposit_in_box", sorted.Leftover.ToString());
            }
            if (noRoom > 0) {
                message += ", " + Localization.instance.Localize("$DA_Deposit_no_room", noRoom.ToString());
            }
            player.Message(MessageHud.MessageType.Center, message);
        }
    }

    // ---- hosts ---------------------------------------------------------------

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
    internal static class InventoryGui_Awake_DepositAll_Patch {
        // m_container and m_stackAllButton are serialized references, live by the end of Awake, and the
        // panel is built once per session - so the button outlives every open/close cycle.
        private static void Postfix(InventoryGui __instance) {
            DepositAll.Attach(__instance);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.UpdateContainer))]
    internal static class InventoryGui_UpdateContainer_DepositAll_Patch {
        // Where vanilla decides whether the container panel is shown at all, which is exactly when the
        // button's own visibility has to be decided.
        private static void Postfix(InventoryGui __instance) {
            DepositAll.Refresh(__instance);
        }
    }
}
