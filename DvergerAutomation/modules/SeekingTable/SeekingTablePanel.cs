using System.Collections.Generic;
using GUIFramework;
using Jotunn.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DvergerAutomation {
    /// <summary>
    /// The Seeking Table's window: a copy of the game's chest panel, shown where a chest's would be while
    /// the inventory is open from a table. Its grid holds one display copy per entry of the
    /// <see cref="SeekingIndex"/>, each carrying the entry's total, so the game draws the icons, the
    /// quality and durability, the tooltips - and Epic Loot its rarity backgrounds - as it does for any
    /// chest. The copies are never real: a click is turned straight into a take
    /// (<see cref="SeekingRetrieval"/>), and nothing from this grid ever goes on the cursor.
    ///
    /// Above the grid, where a chest shows its name, is a search box cloned from the build menu's; down
    /// the right-hand side, the item types, laid out like the AutoSorter's deposit filter.
    ///
    /// Clicks:
    ///  - click, right-click, controller A or X: one stack;
    ///  - shift-click, controller LT+A: the amount dialog;
    ///  - ctrl-click, controller LT+X: everything that fits.
    ///
    /// The window itself is built in SeekingTablePanel.Build.cs.
    /// </summary>
    internal static partial class SeekingTablePanel {
        private const string PanelName = "DA_SeekingPanel";
        private const int Width = 8;
        private const int RowStep = 4;
        // 512 cells. Every cell is redrawn every frame, by the game and by Epic Loot, so the grid is kept to
        // a size that costs nothing noticeable; past it the list says to narrow the search.
        private const int MaxRows = 64;
        private const float CloseDistance = 5f;
        private const float TabGap = 16f;

        // The chest panel's own children, from the game's GUI prefab. Anything else under it was put there
        // by a mod - this one's deposit button and filters among them - and is not wanted in the copy.
        private static readonly HashSet<string> VanillaChildren = new HashSet<string> {
            "Darken", "selected_frame", "Weight", "Bkg", "container_name", "sunken",
            "ContainerGrid", "ContainerScroll", "TakeAll", "TooltipAnchor", "StackAll",
        };

        private enum TabKind { All, Category, Enchanted }

        private sealed class TabCell : FilterCell {
            internal TabKind Kind;
            internal DepositCategory Category;
        }

        private static GameObject panel;
        private static InventoryGrid grid;
        private static UIGroupHandler group;
        private static GuiInputField search;
        private static TMP_Text status;
        private static TMP_Text detail;
        private static readonly List<TabCell> Tabs = new List<TabCell>();
        private static FilterCell groundSwitch;
        private static GameObject groundPlate;
        private const float GroundSwitchGap = 8f;

        private static Inventory display;
        // The split dialog's "inventory" while it is asking how many of an entry to take. Never holds
        // anything; it only marks the dialog as this panel's (see OnSplitOk).
        private static readonly Inventory SplitMarker = new Inventory("DA_seek_split", null, 1, 1);
        private static SeekEntry splitEntry;

        private static readonly List<SeekEntry> Shown = new List<SeekEntry>();
        // Kept apart from the table reference: a table taken down while its window is up compares equal to
        // null, and the window still has to be put away.
        private static bool isOpen;
        private static SeekingTable open;
        private static TabKind tab = TabKind.All;
        private static DepositCategory tabCategory;
        private static int shownVersion = -1;
        private static string shownQuery;
        private static bool shownDirty;
        private static bool capped;
        private static bool resetView;
        private static bool blocking;

        // The game writes "37/50" into each cell's amount label every frame; here a cell is every unit in
        // range, so it shows that instead. Two writers a frame would rebuild every label's text every
        // frame, so the game is pointed at a hidden stand-in, and the real labels are written here, and
        // only when a cell's figure changes.
        private static TMP_Text amountStandIn;
        private static readonly List<TMP_Text> AmountLabels = new List<TMP_Text>();
        private static readonly List<int> AmountShown = new List<int>();
        private const int AmountHidden = -1;
        private const int AmountUnknown = int.MinValue;

        // Display copies of the entries' samples, kept while the index has not regrouped, so a keystroke in
        // the search box does not copy every shown item again.
        private static Dictionary<ItemDrop.ItemData, ItemDrop.ItemData> cellCopies = new Dictionary<ItemDrop.ItemData, ItemDrop.ItemData>();
        private static Dictionary<ItemDrop.ItemData, ItemDrop.ItemData> spareCopies = new Dictionary<ItemDrop.ItemData, ItemDrop.ItemData>();
        private static int copiesVersion = -1;

        // How many entries each tab holds, counted once per regroup.
        private static readonly List<int> TabCounts = new List<int>();
        private static int tabCountsVersion = -1;

        // What the footer and the detail line were last written from, so they are worded only on a change.
        private static int statusChests = -1;
        private static int statusGround = -1;
        private static int statusRadius = -1;
        private static bool statusGroundOn;
        private static SeekEntry detailEntry;
        private static int detailChests = -1;
        private static int detailGround = -1;
        private static int detailMode = -1;

        internal static bool IsOpen => isOpen;

        internal static bool IsSplitMarker(Inventory inventory) => inventory != null && inventory == SplitMarker;

        // ---- opening and closing ------------------------------------------------------

        internal static void Open(SeekingTable table) {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || !Build(gui)) { return; }

            isOpen = true;
            open = table;
            tab = TabKind.All;
            tabCategory = null;
            if (search != null) { search.SetTextWithoutNotify(""); }
            shownVersion = -1;
            shownDirty = true;
            resetView = true;
            // The grid only grows while the window is up (see Refilter); each opening starts it afresh.
            display = null;
            tabCountsVersion = -1;
            ForgetTexts();
            SeekingIndex.Open(table);

            gui.Show(null, 1);
            panel.SetActive(true);
            if (group != null) { group.SetActive(gui.m_activeGroup == 0); }
            PaintTabs();
        }

        /// <summary>Puts the window away. Safe to call whether or not it is up.</summary>
        internal static void Close() {
            ReleaseInput();
            if (!isOpen) { return; }
            isOpen = false;
            open = null;
            splitEntry = null;
            InventoryGui gui = InventoryGui.instance;
            if (gui != null && IsSplitMarker(gui.m_splitInventory)) { gui.OnSplitCancel(); }
            Unfocus();
            if (group != null) { group.SetActive(false); }
            if (panel != null) { panel.SetActive(false); }
            Shown.Clear();
            if (display != null) { display.m_inventory.Clear(); }
            cellCopies.Clear();
            copiesVersion = -1;
            SeekingIndex.Close();
        }

        /// <summary>The inventory screen itself is going away; everything built on it goes with it.</summary>
        internal static void Forget() {
            Close();
            panel = null;
            grid = null;
            group = null;
            search = null;
            status = null;
            detail = null;
            Tabs.Clear();
            groundSwitch = null;
            groundPlate = null;
            display = null;
            amountStandIn = null;
            AmountLabels.Clear();
            AmountShown.Clear();
        }

        // ---- every frame ---------------------------------------------------------------

        /// <summary>Driven from <c>InventoryGui.Update</c>, after the game has drawn its own panels.</summary>
        internal static void Update(InventoryGui gui) {
            if (!isOpen) { return; }
            Player player = Player.m_localPlayer;
            bool keep = gui.m_animator.GetBool("visible")
                && player != null
                && open != null && open.IsLive && open.IsAwake
                // A chest opened from the inventory screen takes the slot back.
                && gui.m_currentContainer == null
                && Vector3.Distance(open.transform.position, player.transform.position) <= CloseDistance;
            if (!keep) {
                Close();
                return;
            }

            SeekingIndex.Tick();
            if (shownDirty || shownVersion != SeekingIndex.Version || shownQuery != (search != null ? search.text : "")) {
                Refilter();
            }

            grid.UpdateInventory(display, null, null);
            CaptureAmountLabels();
            if (resetView) {
                grid.ResetView();
                resetView = false;
            }
            Amounts();
            Texts();
            GamepadSearch();
            // Decided after the game's own update has run, so on the frame Enter or Escape ends the typing,
            // the block from the frame before still keeps that key from closing the inventory.
            if (search != null && search.isFocused) { BlockInput(); } else { ReleaseInput(); }
        }

        // ---- the list ------------------------------------------------------------------

        private static void Refilter() {
            shownVersion = SeekingIndex.Version;
            string text = search != null ? search.text : "";
            if (shownQuery != text) { resetView = true; }
            shownQuery = text;
            shownDirty = false;

            SeekingSearch.Query query = SeekingSearch.Parse(text);
            Shown.Clear();
            capped = false;
            foreach (SeekEntry entry in SeekingIndex.Entries) {
                if (entry.Total <= 0) { continue; }
                if (tab == TabKind.Enchanted && !entry.Magic) { continue; }
                if (tab == TabKind.Category && entry.Category != tabCategory) { continue; }
                if (!SeekingSearch.Matches(query, entry)) { continue; }
                if (Shown.Count >= MaxRows * Width) {
                    capped = true;
                    break;
                }
                Shown.Add(entry);
            }

            int rows = Mathf.Max(RowStep, (Shown.Count + Width - 1) / Width);
            rows = (rows + RowStep - 1) / RowStep * RowStep;
            // A new grid size rebuilds every cell, so while the window is up the grid only ever grows:
            // typing a search narrows the list without rebuilding it, and empty cells at the end cost little.
            if (display != null) { rows = Mathf.Max(rows, display.GetHeight()); }
            if (display == null || display.GetHeight() != rows) {
                display = new Inventory("$DA_seek_name", null, Width, rows);
            }

            // A regroup can hand back new samples; the copies are only kept for the same ones.
            if (copiesVersion != SeekingIndex.Version) {
                cellCopies.Clear();
                copiesVersion = SeekingIndex.Version;
            }
            List<ItemDrop.ItemData> cells = display.m_inventory;
            cells.Clear();
            spareCopies.Clear();
            for (int i = 0; i < Shown.Count; ++i) {
                ItemDrop.ItemData sample = Shown[i].Sample;
                if (!cellCopies.TryGetValue(sample, out ItemDrop.ItemData copy)) {
                    copy = sample.Clone();
                    copy.m_equipped = false;
                }
                copy.m_stack = Mathf.Max(1, Shown[i].Total);
                copy.m_gridPos = new Vector2i(i % Width, i / Width);
                spareCopies[sample] = copy;
                cells.Add(copy);
            }
            // What is shown now is what is kept for the next keystroke.
            Dictionary<ItemDrop.ItemData, ItemDrop.ItemData> kept = spareCopies;
            spareCopies = cellCopies;
            cellCopies = kept;
            spareCopies.Clear();

            if (tabCountsVersion != SeekingIndex.Version) { PaintTabs(); }
        }

        private static SeekEntry EntryAt(Vector2i pos) {
            int index = pos.y * Width + pos.x;
            return index >= 0 && index < Shown.Count ? Shown[index] : null;
        }

        // Points every cell the grid has built since the last look at the stand-in, keeping its real
        // label to write here (see amountStandIn).
        private static void CaptureAmountLabels() {
            List<InventoryElement> elements = grid.m_elements;
            if (AmountLabels.Count == elements.Count && (elements.Count == 0 || elements[0].m_amount == amountStandIn)) { return; }
            if (amountStandIn == null) {
                GameObject go = new GameObject("DA_SeekAmountStandIn", typeof(RectTransform));
                go.SetActive(false);
                go.transform.SetParent(panel.transform, false);
                amountStandIn = go.AddComponent<TextMeshProUGUI>();
            }
            AmountLabels.Clear();
            AmountShown.Clear();
            foreach (InventoryElement element in elements) {
                if (element.m_amount != amountStandIn) {
                    AmountLabels.Add(element.m_amount);
                    element.m_amount = amountStandIn;
                } else {
                    AmountLabels.Add(null);
                }
                AmountShown.Add(AmountUnknown);
            }
        }

        // A cell shows every unit in range: its entry's total, written only when it changes.
        private static void Amounts() {
            for (int i = 0; i < AmountLabels.Count; ++i) {
                TMP_Text label = AmountLabels[i];
                if (label == null) { continue; }
                int shown = AmountHidden;
                if (i < Shown.Count) {
                    SeekEntry entry = Shown[i];
                    if (entry.Total > 1 || entry.Sample.m_shared.m_maxStackSize > 1) { shown = entry.Total; }
                }
                if (AmountShown[i] == shown) { continue; }
                AmountShown[i] = shown;
                label.enabled = shown != AmountHidden;
                if (shown != AmountHidden) { label.text = Count(shown); }
            }
        }

        private static string Count(int total) {
            return total >= 10000 ? (total / 1000f).ToString("0.#") + "k" : total.ToString();
        }

        // Written every frame they could change, but worded only when what they say does.
        private static void Texts() {
            if (status != null) {
                int chests = SeekingIndex.Chests.Count;
                int ground = SeekingIndex.Ground.Count;
                int radius = Mathf.RoundToInt(SeekingIndex.Radius);
                bool groundOn = SeekingIndex.GroundEnabled;
                if (chests != statusChests || ground != statusGround || radius != statusRadius || groundOn != statusGroundOn) {
                    statusChests = chests;
                    statusGround = ground;
                    statusRadius = radius;
                    statusGroundOn = groundOn;
                    status.text = groundOn
                        ? Localization.instance.Localize("$DA_seek_footer_ground", chests.ToString(), ground.ToString(), radius.ToString())
                        : Localization.instance.Localize("$DA_seek_footer", chests.ToString(), radius.ToString());
                }
            }
            if (detail == null) { return; }

            SeekEntry pointed = Pointed();
            int mode = pointed != null ? 0 : capped ? 1 : Shown.Count == 0 && SeekingIndex.Entries.Count > 0 ? 2 : 3;
            int pointedChests = pointed != null ? pointed.Chests : -1;
            int pointedGround = pointed != null ? pointed.GroundDrops : -1;
            if (mode == detailMode && pointed == detailEntry && pointedChests == detailChests && pointedGround == detailGround) { return; }
            detailMode = mode;
            detailEntry = pointed;
            detailChests = pointedChests;
            detailGround = pointedGround;
            switch (mode) {
                case 0: detail.text = Where(pointed); break;
                case 1: detail.text = Localization.instance.Localize("$DA_seek_capped"); break;
                case 2: detail.text = Localization.instance.Localize("$DA_seek_no_match"); break;
                default: detail.text = ""; break;
            }
        }

        // The texts are worded afresh the next frame.
        private static void ForgetTexts() {
            statusChests = -1;
            detailMode = -1;
            detailEntry = null;
        }

        private static string Where(SeekEntry entry) {
            Localization loc = Localization.instance;
            string chests = entry.Chests.ToString();
            string ground = entry.GroundDrops.ToString();
            if (entry.GroundDrops == 0) { return loc.Localize(entry.Chests == 1 ? "$DA_seek_in_chest" : "$DA_seek_in_chests", chests); }
            if (entry.Chests == 0) { return loc.Localize("$DA_seek_on_ground", ground); }
            return entry.Chests == 1
                ? loc.Localize("$DA_seek_in_chest_ground", ground)
                : loc.Localize("$DA_seek_in_chests_ground", chests, ground);
        }

        // The cell under the mouse, or the controller's selected cell while the window has its focus.
        private static SeekEntry Pointed() {
            InventoryElement element = group != null && group.IsActive && ZInput.IsGamepadActive()
                ? grid.GetElement(grid.m_selected.x, grid.m_selected.y, grid.m_width)
                : grid.GetHoveredElement();
            return element != null ? EntryAt(element.Position) : null;
        }

        // ---- clicks --------------------------------------------------------------------

        private static void OnSelected(InventoryGrid from, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod) {
            InventoryGui gui = InventoryGui.instance;
            if (open == null || gui == null) { return; }
            // An item of the player's own on the cursor: there is nothing here to put it into.
            if (gui.m_dragItem != null) { return; }
            SeekEntry entry = EntryAt(pos);
            if (entry == null) { return; }

            switch (mod) {
                case InventoryGrid.Modifier.Split:
                    AskAmount(gui, entry);
                    break;
                case InventoryGrid.Modifier.Move:
                    SeekingRetrieval.Take(open, entry, SeekingRetrieval.MaxTake(entry));
                    break;
                default:
                    SeekingRetrieval.Take(open, entry, OneStack(entry));
                    break;
            }
        }

        private static void OnRightClick(InventoryGrid from, ItemDrop.ItemData item, Vector2i pos) {
            InventoryGui gui = InventoryGui.instance;
            if (open == null || gui == null || gui.m_dragItem != null) { return; }
            SeekEntry entry = EntryAt(pos);
            if (entry != null) { SeekingRetrieval.Take(open, entry, OneStack(entry)); }
        }

        private static int OneStack(SeekEntry entry) => Mathf.Max(1, entry.Sample.m_shared.m_maxStackSize);

        // The game's own amount dialog, on a copy holding as many as could be taken. Its answer comes back
        // through OnSplitOk, which the patch hands to TakeFromDialog.
        private static void AskAmount(InventoryGui gui, SeekEntry entry) {
            int max = SeekingRetrieval.MaxTake(entry);
            if (max <= 0) {
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "$inventory_full");
                return;
            }
            if (max == 1) {
                SeekingRetrieval.Take(open, entry, 1);
                return;
            }
            ItemDrop.ItemData copy = entry.Sample.Clone();
            copy.m_stack = max;
            splitEntry = entry;
            gui.ShowSplitDialog(copy, SplitMarker);
        }

        /// <summary>The amount dialog was answered for this panel.</summary>
        internal static void TakeFromDialog(int amount) {
            SeekEntry entry = Resolve(splitEntry);
            splitEntry = null;
            if (open != null && entry != null && amount > 0) { SeekingRetrieval.Take(open, entry, amount); }
        }

        // The list may have been regrouped while the dialog was up, which makes new entries: find the one
        // standing for the same thing.
        private static SeekEntry Resolve(SeekEntry asked) {
            if (asked == null) { return null; }
            foreach (SeekEntry entry in SeekingIndex.Entries) {
                if (entry == asked || entry.Sample == asked.Sample) { return entry; }
            }
            foreach (SeekEntry entry in SeekingIndex.Entries) {
                if (entry.Magic != asked.Magic) { continue; }
                bool same = asked.Magic
                    ? ItemIdentity.SameExact(entry.Sample, asked.Sample)
                    : ItemIdentity.Interchangeable(entry.Sample, asked.Sample);
                if (same) { return entry; }
            }
            return null;
        }

        // ---- the search box --------------------------------------------------------------

        // While it has the keyboard, the game must not read the keys as play: E would close the inventory,
        // Tab too, and the number keys would use the hotbar. Jotunn's input block covers all of that. It is
        // a counter, so it is released exactly once for every time it was taken.
        //
        // It follows whether the box is actually taking keys, checked every frame, rather than the
        // EventSystem's select and deselect: Enter or Escape stops the typing but leaves the box selected,
        // which would have kept the block on, and a box still selected when the window last closed sends
        // no select when it is clicked again, which would have left it off.
        private static void BlockInput() {
            if (blocking) { return; }
            blocking = true;
            GUIManager.BlockInput(true);
        }

        private static void ReleaseInput() {
            if (!blocking) { return; }
            blocking = false;
            GUIManager.BlockInput(false);
        }

        // Stops the typing and lets go of the selection, so the next click selects the box afresh.
        private static void Unfocus() {
            if (search == null) { return; }
            if (search.isFocused) { search.DeactivateInputField(); }
            EventSystem events = EventSystem.current;
            if (events != null && events.currentSelectedGameObject == search.gameObject) { events.SetSelectedGameObject(null); }
        }

        // Clicking the left stick puts the controller in the search box, as it does in the build menu.
        private static void GamepadSearch() {
            if (search == null || group == null || !group.IsActive || search.isFocused) { return; }
            if (ZInput.GetButtonDown("JoyLStick")) { search.ActivateInputField(); }
        }

        // ---- the item types --------------------------------------------------------------

        private static void SelectTab(TabCell cell) {
            if (cell.Kind == TabKind.Category && tab == TabKind.Category && tabCategory == cell.Category) {
                // A second click on the open type goes back to everything.
                tab = TabKind.All;
                tabCategory = null;
            } else {
                tab = cell.Kind;
                tabCategory = cell.Category;
            }
            shownDirty = true;
            resetView = true;
            PaintTabs();
        }

        private static readonly Color DimColor = new Color(0.42f, 0.42f, 0.42f, 1f);

        // The open type is lit. With All open every type shows plainly, since all of them are listed;
        // otherwise the others are dimmed.
        private static void PaintTabs() {
            CountTabs();
            for (int i = 0; i < Tabs.Count; ++i) {
                TabCell cell = Tabs[i];
                bool lit = cell.Kind == tab && (cell.Kind != TabKind.Category || cell.Category == tabCategory);
                bool plain = lit || (tab == TabKind.All && cell.Kind == TabKind.Category);
                if (cell.Lit != null && cell.Lit.activeSelf != lit) { cell.Lit.SetActive(lit); }
                Color color = plain ? Color.white : DimColor;
                if (cell.Icon != null) { cell.Icon.color = color; }
                if (cell.Label != null) { cell.Label.color = color; }
                if (cell.Tooltip != null) {
                    cell.Tooltip.Set(TabTitle(cell), Localization.instance.Localize("$DA_seek_tab_count", TabCounts[i].ToString()));
                }
            }
            PaintGround();
        }

        // Every tab's count in one walk of the entries, once per regroup.
        private static void CountTabs() {
            if (tabCountsVersion == SeekingIndex.Version && TabCounts.Count == Tabs.Count) { return; }
            tabCountsVersion = SeekingIndex.Version;
            TabCounts.Clear();
            for (int i = 0; i < Tabs.Count; ++i) { TabCounts.Add(0); }
            foreach (SeekEntry entry in SeekingIndex.Entries) {
                if (entry.Total <= 0) { continue; }
                for (int i = 0; i < Tabs.Count; ++i) {
                    TabCell cell = Tabs[i];
                    if (cell.Kind == TabKind.All
                        || (cell.Kind == TabKind.Enchanted && entry.Magic)
                        || (cell.Kind == TabKind.Category && entry.Category == cell.Category)) {
                        ++TabCounts[i];
                    }
                }
            }
        }

        private static string TabTitle(TabCell cell) {
            switch (cell.Kind) {
                case TabKind.Category: return cell.Category.NameKey;
                case TabKind.Enchanted: return "$DA_seek_tab_enchanted";
                default: return "$DA_seek_tab_all";
            }
        }

        // ---- the ground switch ------------------------------------------------------------

        // The player's own setting, kept in the client config so it outlasts the session. Its change hook
        // has the index look at the ground now, or let go of it (SeekingIndex.GroundSettingChanged).
        private static void ToggleGround() {
            ValConfig.SeekGroundItems.Value = !ValConfig.SeekGroundItems.Value;
            PaintGround();
        }

        // Lit while on; hidden altogether when the server does not allow it.
        private static void PaintGround() {
            if (groundPlate == null || groundSwitch == null) { return; }
            bool allowed = ValConfig.SeekAllowGround.Value;
            if (groundPlate.activeSelf != allowed) { groundPlate.SetActive(allowed); }
            bool on = ValConfig.SeekGroundItems.Value;
            FilterPopupUI.Paint(groundSwitch, on ? CategoryState.On : CategoryState.Off);
            if (groundSwitch.Tooltip != null) {
                groundSwitch.Tooltip.Set("$DA_seek_ground", Localization.instance.Localize(
                    (on ? "$DA_seek_ground_on" : "$DA_seek_ground_off") + "\n$DA_seek_ground_click"));
            }
        }

        // ---- controller focus ---------------------------------------------------------------

        /// <summary>
        /// The game gives the chest slot focus as group 0, a group this window shares; mirrors that onto the
        /// window's own focus handler while the window is up.
        /// </summary>
        internal static void SyncFocus(InventoryGui gui) {
            if (group == null) { return; }
            group.SetActive(isOpen && gui.m_activeGroup == 0);
        }

        // Up off the top row: onto the player's bottom row, same column. The game's own version of this
        // works the column out from the hidden chest grid's width, which is stale here.
        private static void LeaveUpwards(InventoryGui gui, Vector2i pos) {
            if (gui.m_inventoryGroup == null || !gui.m_inventoryGroup.IsActive || gui.m_playerGrid == null) { return; }
            InventoryGrid player = gui.m_playerGrid;
            player.SetGamepadSelection(new Vector2i(Mathf.Clamp(pos.x, 0, Mathf.Max(0, player.GridWidth - 1)), Mathf.Max(0, player.m_height - 1)));
            gui.SetActiveGroup(1);
        }

        /// <summary>
        /// The controller came down from the player's grid: start on the same column here. Read off the
        /// player's grid, which is as wide as this one, rather than off the hidden chest grid the game just
        /// aimed at, whose width is whatever chest was last open.
        /// </summary>
        internal static void EnterFromAbove(InventoryGui gui) {
            if (!isOpen || grid == null || gui.m_playerGrid == null) { return; }
            Vector2i at = gui.m_playerGrid.SelectionGridPosition;
            grid.SetGamepadSelection(new Vector2i(Mathf.Clamp(at.x, 0, Width - 1), 0));
        }
    }
}
