using System.Collections.Generic;
using GUIFramework;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace DvergerAutomation {
    // Building the Seeking Table's window, once per inventory screen: a stripped copy of the game's chest
    // panel, with a search box cloned from the build menu's and the item types down its right-hand side.
    internal static partial class SeekingTablePanel {
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
            FilterPopupUI.DetachFromLocalization(text);
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
                FilterPopupUI.DetachFromLocalization(placeholder);
                placeholder.text = Localization.instance.Localize(SeekingSearch.EnchantSearchAvailable ? "$DA_seek_search_enchant" : "$DA_seek_search");
            }
            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(includeInactive: true)) {
                if (text != field.placeholder && text != field.textComponent) { FilterPopupUI.DetachFromLocalization(text); }
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

            // Set a little apart from the types: it changes what is searched, not which part is shown.
            y += GroundSwitchGap;
            RectTransform plate = FilterPopupUI.Plate(gui, column, ref y, switchPlate);
            groundPlate = plate.gameObject;
            groundSwitch = FilterPopupUI.TextCell<FilterCell>(gui, plate, FilterPopupUI.PadY, FilterPopupUI.SwitchHeight,
                Localization.instance.Localize("$DA_seek_ground"));
            FilterPopupUI.OnClick(groundSwitch, ToggleGround);
            FilterPopupUI.Finish(column, y);
        }

        private static void AddTab(TabCell cell, TabKind kind, DepositCategory category) {
            cell.Kind = kind;
            cell.Category = category;
            FilterPopupUI.OnClick(cell, () => SelectTab(cell));
            Tabs.Add(cell);
        }
    }
}
