using System.Collections.Generic;
using GUIFramework;
using Jotunn.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
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
    /// </summary>
    internal static class SeekingTablePanel {
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
            display = null;
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
            // A new grid size rebuilds every cell, so the inventory is only replaced when the size moves.
            if (display == null || display.GetHeight() != rows) {
                display = new Inventory("$DA_seek_name", null, Width, rows);
            }
            List<ItemDrop.ItemData> cells = display.m_inventory;
            cells.Clear();
            for (int i = 0; i < Shown.Count; ++i) {
                ItemDrop.ItemData copy = Shown[i].Sample.Clone();
                copy.m_stack = Mathf.Max(1, Shown[i].Total);
                copy.m_equipped = false;
                copy.m_gridPos = new Vector2i(i % Width, i / Width);
                cells.Add(copy);
            }
            PaintTabs();
        }

        private static SeekEntry EntryAt(Vector2i pos) {
            int index = pos.y * Width + pos.x;
            return index >= 0 && index < Shown.Count ? Shown[index] : null;
        }

        // The game writes "37/50" on a stack; here a cell is every unit in range, so it shows that.
        private static void Amounts() {
            List<InventoryElement> elements = grid.m_elements;
            for (int i = 0; i < Shown.Count && i < elements.Count; ++i) {
                InventoryElement element = elements[i];
                if (element == null || element.m_amount == null) { continue; }
                SeekEntry entry = Shown[i];
                bool show = entry.Total > 1 || entry.Sample.m_shared.m_maxStackSize > 1;
                element.m_amount.enabled = show;
                if (show) { element.m_amount.text = Count(entry.Total); }
            }
        }

        private static string Count(int total) {
            return total >= 10000 ? (total / 1000f).ToString("0.#") + "k" : total.ToString();
        }

        private static void Texts() {
            if (status != null) {
                status.text = Localization.instance.Localize("$DA_seek_footer",
                    SeekingIndex.Chests.Count.ToString(), Mathf.RoundToInt(SeekingIndex.Radius).ToString());
            }
            if (detail == null) { return; }

            SeekEntry pointed = Pointed();
            if (pointed != null) {
                detail.text = Localization.instance.Localize(pointed.Chests == 1 ? "$DA_seek_in_chest" : "$DA_seek_in_chests", pointed.Chests.ToString());
            } else if (capped) {
                detail.text = Localization.instance.Localize("$DA_seek_capped");
            } else {
                detail.text = Shown.Count == 0 && SeekingIndex.Entries.Count > 0
                    ? Localization.instance.Localize("$DA_seek_no_match")
                    : "";
            }
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
            foreach (TabCell cell in Tabs) {
                bool lit = cell.Kind == tab && (cell.Kind != TabKind.Category || cell.Category == tabCategory);
                bool plain = lit || (tab == TabKind.All && cell.Kind == TabKind.Category);
                if (cell.Lit != null && cell.Lit.activeSelf != lit) { cell.Lit.SetActive(lit); }
                Color color = plain ? Color.white : DimColor;
                if (cell.Icon != null) { cell.Icon.color = color; }
                if (cell.Label != null) { cell.Label.color = color; }
                if (cell.Tooltip != null) { cell.Tooltip.Set(TabTitle(cell), TabText(cell)); }
            }
        }

        private static string TabTitle(TabCell cell) {
            switch (cell.Kind) {
                case TabKind.Category: return cell.Category.NameKey;
                case TabKind.Enchanted: return "$DA_seek_tab_enchanted";
                default: return "$DA_seek_tab_all";
            }
        }

        private static string TabText(TabCell cell) {
            int count = 0;
            foreach (SeekEntry entry in SeekingIndex.Entries) {
                if (entry.Total <= 0) { continue; }
                if (cell.Kind == TabKind.All
                    || (cell.Kind == TabKind.Enchanted && entry.Magic)
                    || (cell.Kind == TabKind.Category && entry.Category == cell.Category)) {
                    ++count;
                }
            }
            return Localization.instance.Localize("$DA_seek_tab_count", count.ToString());
        }

        // ---- building ----------------------------------------------------------------------

        private static bool Build(InventoryGui gui) {
            if (panel != null) { return true; }
            if (gui.m_container == null || gui.m_containerGrid == null) { return false; }

            Transform parent = gui.m_container.parent;
            Transform stale = parent.Find(PanelName);
            if (stale != null) { Object.Destroy(stale.gameObject); }

            // Copied under a switched-off holder, then stripped, then moved into place. The chest panel is
            // still live if a chest was the last thing open, and a live copy would wake every component in
            // it - other mods' among them - only for them to be thrown away. Woken and then destroyed, a
            // component that registers itself somewhere could take its original's registration with it.
            GameObject holder = new GameObject("DA_SeekBuild");
            holder.SetActive(false);
            GameObject go = Object.Instantiate(gui.m_container.gameObject, holder.transform, false);
            go.name = PanelName;
            go.SetActive(false);
            for (int i = go.transform.childCount - 1; i >= 0; --i) {
                Transform child = go.transform.GetChild(i);
                if (!VanillaChildren.Contains(child.name)) { Object.DestroyImmediate(child.gameObject); }
            }
            go.transform.SetParent(parent, false);
            Object.Destroy(holder);

            Transform gridTwin = Twin(gui.m_container, go.transform, gui.m_containerGrid.transform);
            InventoryGrid copy = gridTwin != null ? gridTwin.GetComponent<InventoryGrid>() : null;
            if (copy == null) {
                Logger.LogWarning("[Seeking Table] the chest panel has no grid to copy; the table cannot be shown.");
                Object.Destroy(go);
                return false;
            }
            SetUpGrid(gui, copy);

            // The copied panel's controller focus starts out on, which would make it a second live group
            // beside the player's grid. It is switched on only while the game's chest slot has focus.
            group = go.GetComponent<UIGroupHandler>();
            if (group != null) { group.SetActive(false); }

            // Nothing to take all of or stack into here, and the weight would be of nothing real.
            HideTwin(gui, go, gui.m_takeAllButton != null ? gui.m_takeAllButton.transform : null);
            HideTwin(gui, go, gui.m_stackAllButton != null ? gui.m_stackAllButton.transform : null);
            // The weight's number sits in a holder with its icon; the holder goes.
            Transform weight = Twin(gui.m_container, go.transform, gui.m_containerWeight != null ? gui.m_containerWeight.transform : null);
            if (weight != null) { (weight.parent != go.transform ? weight.parent : weight).gameObject.SetActive(false); }

            TMP_Text title = Twin(gui.m_container, go.transform, gui.m_containerName != null ? gui.m_containerName.transform : null)?.GetComponent<TMP_Text>();
            if (title != null) {
                status = CloneLabel(title, "DA_SeekStatus", new Vector2(0f, 1f), new Vector2(18f, title.rectTransform.anchoredPosition.y), TextAlignmentOptions.Left);
                detail = CloneLabel(title, "DA_SeekDetail", new Vector2(1f, 1f), new Vector2(-18f, title.rectTransform.anchoredPosition.y), TextAlignmentOptions.Right);
                title.gameObject.SetActive(false);
            }

            search = CloneSearch(go, title);
            BuildTabs(gui, go);

            panel = go;
            return true;
        }

        private static void SetUpGrid(InventoryGui gui, InventoryGrid copy) {
            // The chest's cells come along with the copy, but the list that tracks them does not: the grid
            // makes its own, and only does so when it thinks the size changed.
            if (copy.m_gridRoot != null) {
                foreach (Transform cell in copy.m_gridRoot) { Object.Destroy(cell.gameObject); }
            }
            copy.m_elements.Clear();
            copy.m_width = 0;
            copy.m_height = 0;

            // The game calls every one of these without checking anyone is listening.
            copy.m_onSelected = OnSelected;
            copy.m_onRightClick = OnRightClick;
            copy.m_onReleased = (g, i, p) => { };
            copy.m_onEnter = (g, p) => { };
            copy.OnSetTouchSelection = g => { };
            copy.CanDropDragOntoItem = i => false;
            copy.OnMoveToUpperInventoryGrid = pos => LeaveUpwards(gui, pos);
            copy.OnMoveToLowerInventoryGrid = null;
            grid = copy;
        }

        /// <summary>The copy's counterpart of something in the game's chest panel, found by its path.</summary>
        private static Transform Twin(Transform original, Transform copy, Transform part) {
            if (part == null || part == original || !part.IsChildOf(original)) { return null; }
            List<string> path = new List<string>();
            for (Transform t = part; t != null && t != original; t = t.parent) { path.Insert(0, t.name); }
            return copy.Find(string.Join("/", path.ToArray()));
        }

        private static void HideTwin(InventoryGui gui, GameObject copy, Transform part) {
            Transform twin = Twin(gui.m_container, copy.transform, part);
            if (twin != null) { twin.gameObject.SetActive(false); }
        }

        private static TMP_Text CloneLabel(TMP_Text template, string name, Vector2 anchor, Vector2 position, TextAlignmentOptions alignment) {
            GameObject go = Object.Instantiate(template.gameObject, template.transform.parent, false);
            go.name = name;
            go.SetActive(true);
            TMP_Text text = go.GetComponent<TMP_Text>();
            DetachFromLocalization(text);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(anchor.x, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(150f, rect.sizeDelta.y);
            text.alignment = alignment;
            text.enableAutoSizing = true;
            text.fontSizeMin = 10f;
            text.fontSizeMax = 16f;
            text.text = "";
            return text;
        }

        // The game localizes labels that still read "$..." and caches them, and re-applies its cache over
        // whatever was written since (see DepositAll.DetachFromLocalization).
        private static void DetachFromLocalization(TMP_Text text) {
            if (text == null) { return; }
            if (text.text != null && text.text.Contains("$")) { text.text = ""; }
            Localization.instance?.RemoveTextFromCache(text);
        }

        // Kept as the game's own field type, not TMP's: GuiInputField hides ActivateInputField with a version
        // that opens the Steam virtual keyboard on a Deck or in Big Picture, and only a call through this
        // type reaches it.
        private static GuiInputField CloneSearch(GameObject copy, TMP_Text title) {
            GuiInputField template = Hud.instance != null && Hud.instance.m_buildUi != null ? Hud.instance.m_buildUi.m_searchField : null;
            if (template == null) {
                Logger.LogWarning("[Seeking Table] the build menu has no search box to copy; the table's list cannot be searched.");
                return null;
            }

            GameObject go = Object.Instantiate(template.gameObject, copy.transform, false);
            go.name = "DA_SeekSearch";
            go.SetActive(true);
            GuiInputField field = go.GetComponent<GuiInputField>();

            // Listeners set up in the editor are copied; the build menu's own, added in code, are not. Only
            // ours are wanted. The input block is not driven from here (see BlockInput).
            Silence(field.onValueChanged);
            Silence(field.onEndEdit);
            Silence(field.onSubmit);
            Silence(field.onSelect);
            Silence(field.onDeselect);
            field.onValueChanged.AddListener(_ => shownDirty = true);
            field.SetTextWithoutNotify("");

            if (field.placeholder is TMP_Text placeholder) {
                DetachFromLocalization(placeholder);
                placeholder.text = Localization.instance.Localize(SeekingSearch.EnchantSearchAvailable ? "$DA_seek_search_enchant" : "$DA_seek_search");
            }
            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(includeInactive: true)) {
                if (text != field.placeholder && text != field.textComponent) { DetachFromLocalization(text); }
            }

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(0f, title != null ? title.rectTransform.anchoredPosition.y : -19f);
            rect.sizeDelta = new Vector2(230f, Mathf.Max(26f, rect.sizeDelta.y));
            rect.localScale = Vector3.one;
            go.transform.SetAsLastSibling();
            return field;
        }

        private static void Silence<T>(UnityEvent<T> unityEvent) {
            if (unityEvent == null) { return; }
            for (int i = 0; i < unityEvent.GetPersistentEventCount(); ++i) {
                unityEvent.SetPersistentListenerState(i, UnityEventCallState.Off);
            }
        }

        private static void BuildTabs(InventoryGui gui, GameObject copy) {
            Tabs.Clear();
            if (gui.m_playerGrid == null || gui.m_playerGrid.m_elementPrefab == null) { return; }

            GameObject root = new GameObject("DA_SeekTabs", typeof(RectTransform));
            root.layer = copy.layer;
            RectTransform column = (RectTransform)root.transform;
            column.SetParent(copy.transform, false);
            column.anchorMin = new Vector2(1f, 1f);
            column.anchorMax = new Vector2(1f, 1f);
            column.pivot = new Vector2(0f, 1f);
            column.anchoredPosition = new Vector2(TabGap, 0f);

            float y = 0f;
            float switchPlate = FilterPopupUI.PadY * 2f + FilterPopupUI.SwitchHeight;

            RectTransform allPlate = FilterPopupUI.Plate(gui, column, ref y, switchPlate);
            AddTab(FilterPopupUI.TextCell<TabCell>(gui, allPlate, FilterPopupUI.PadY, FilterPopupUI.SwitchHeight,
                Localization.instance.Localize("$DA_seek_tab_all")), TabKind.All, null);

            DepositCategory[] categories = DepositCategories.All;
            RectTransform iconPlate = FilterPopupUI.IconPlate(gui, column, ref y, categories.Length);
            for (int i = 0; i < categories.Length; ++i) {
                AddTab(FilterPopupUI.IconCell<TabCell>(gui, iconPlate, i, DepositCategories.IconOf(categories[i])), TabKind.Category, categories[i]);
            }

            if (EpicLootIntegration.Active) {
                RectTransform magicPlate = FilterPopupUI.Plate(gui, column, ref y, switchPlate);
                AddTab(FilterPopupUI.TextCell<TabCell>(gui, magicPlate, FilterPopupUI.PadY, FilterPopupUI.SwitchHeight,
                    Localization.instance.Localize("$DA_seek_tab_enchanted")), TabKind.Enchanted, null);
            }
            FilterPopupUI.Finish(column, y);
        }

        private static void AddTab(TabCell cell, TabKind kind, DepositCategory category) {
            cell.Kind = kind;
            cell.Category = category;
            FilterPopupUI.OnClick(cell, () => SelectTab(cell));
            Tabs.Add(cell);
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
