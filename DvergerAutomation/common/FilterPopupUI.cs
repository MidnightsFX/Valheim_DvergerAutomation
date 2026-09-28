using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace DvergerAutomation {
    /// <summary>
    /// One clickable cell in a [C] popup: an icon, or a text switch. Each popup derives its own cell type
    /// to carry whatever the click acts on.
    /// </summary>
    internal class FilterCell {
        internal GameObject Root;
        internal GameObject Lit;
        internal Image Icon;
        internal TMP_Text Label;
        internal UITooltip Tooltip;
    }

    /// <summary>
    /// The parts both [C] popups are built from: the AutoSorter's deposit filter
    /// (<see cref="DepositFilterPanel"/>) and the Hopper's station picker (<see cref="HopperTargetPanel"/>).
    /// A square [C] button beside the deposit button, and a column of small plates of clickable cells
    /// hanging off it, laid out like MyLittleUI's crafting filter.
    ///
    /// Everything is cloned from vanilla objects, so there is no art in the asset bundle for it: the
    /// button is Place stacks, the plates are the repair button's wood plate (as MyLittleUI does), and the
    /// cells are the inventory grid's own slot.
    /// </summary>
    internal static class FilterPopupUI {
        private const float ButtonSize = 40f;
        /// <summary>
        /// Gap between the deposit button and [C]. The container's wood frame reaches 10px past the panel's
        /// rect, and Place stacks stops about 1px short of it, so this puts [C] clear of the frame.
        /// </summary>
        private const float ButtonGap = 16f;
        private const float PopupGap = 6f;

        // Plate and cell metrics, MyLittleUI's: 32px slots on a 34px pitch, three to a row.
        internal const float PlateWidth = 120f;
        internal const float PlateGap = 4f;
        internal const float PadX = 8f;
        internal const float PadY = 6f;
        private const float CellSize = 32f;
        private const float CellPitch = 34f;
        private const int PerRow = 3;
        internal const float SwitchHeight = 28f;

        // Icon and label tint while a cell is off. Matches the craft-from-storage switch's off state.
        private static readonly Color OffColor = new Color(0.42f, 0.42f, 0.42f, 1f);

        // ---- the [C] button ----------------------------------------------------

        /// <summary>
        /// Builds a hidden [C] next to the deposit button. Called once per InventoryGui, from
        /// <see cref="DepositAll.Attach"/>.
        /// </summary>
        internal static GameObject CreateButton(InventoryGui gui, string name, RectTransform deposit, UnityAction onClick) {
            GameObject go = DepositAll.CloneStackAllButton(gui, name, onClick);
            RectTransform rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(ButtonSize, ButtonSize);
            PlaceBeside(rect, deposit);

            // The template's label is sized for a two-word caption; one letter reads better larger.
            TMP_Text label = Label(go);
            if (label != null) {
                label.enableAutoSizing = false;
                label.fontSize = 20f;
            }
            return go;
        }

        /// <summary>
        /// Puts [C] just outside the container panel, level with the deposit button, by reading that
        /// button's own rect - so a mod that moved Place stacks (and with it our clone) moves [C] too.
        /// Only followed for a point-anchored button; a stretched one is in a coordinate space where this
        /// arithmetic means nothing, and then [C] is left where the template put it.
        /// </summary>
        private static void PlaceBeside(RectTransform rect, RectTransform deposit) {
            if (deposit.anchorMin != deposit.anchorMax) { return; }
            rect.anchorMin = deposit.anchorMin;
            rect.anchorMax = deposit.anchorMax;
            rect.pivot = new Vector2(0f, 0.5f);

            Vector2 size = deposit.sizeDelta;
            Vector2 rightMiddle = deposit.anchoredPosition
                + new Vector2((1f - deposit.pivot.x) * size.x, (0.5f - deposit.pivot.y) * size.y);
            rect.anchoredPosition = rightMiddle + new Vector2(ButtonGap, 0f);
        }

        /// <summary>Sets [C]'s caption and tooltip, already localized.</summary>
        internal static void Dress(GameObject button, string caption, string title, string text) {
            TMP_Text label = Label(button);
            if (label != null) { label.text = caption; }
            UITooltip tooltip = button.GetComponent<UITooltip>();
            if (tooltip != null) { tooltip.Set(title, text); }
        }

        private static TMP_Text Label(GameObject go) {
            Transform root = go.transform.Find("Text");
            return root != null ? root.GetComponent<TMP_Text>() : null;
        }

        // ---- the popup ---------------------------------------------------------

        /// <summary>
        /// An empty, hidden popup to the right of [C], top edges level, growing downwards. There is room
        /// for it there at every GUI scale: nothing of vanilla's sits between the container panel and the
        /// crafting panel's repair column. Null when the GUI has no inventory slot to clone cells from.
        /// </summary>
        internal static RectTransform CreatePopup(InventoryGui gui, GameObject button, string name) {
            if (gui == null || button == null) { return null; }
            if (gui.m_playerGrid == null || gui.m_playerGrid.m_elementPrefab == null) {
                Logger.LogWarning($"No inventory slot to clone; {name} cannot be shown.");
                return null;
            }

            RectTransform anchor = (RectTransform)button.transform;
            GameObject root = new GameObject(name, typeof(RectTransform));
            // A new GameObject starts on Default; everything cloned into it is on the GUI's layer.
            root.layer = gui.m_container.gameObject.layer;
            root.SetActive(false);
            RectTransform popup = (RectTransform)root.transform;
            popup.SetParent(gui.m_container, false);
            popup.anchorMin = anchor.anchorMin;
            popup.anchorMax = anchor.anchorMax;
            popup.pivot = new Vector2(0f, 1f);
            popup.anchoredPosition = anchor.anchoredPosition
                + new Vector2((1f - anchor.pivot.x) * ButtonSize + PopupGap, (1f - anchor.pivot.y) * ButtonSize);
            popup.SetAsLastSibling();
            return popup;
        }

        /// <summary>Sizes the popup to the plates stacked into it; <paramref name="y"/> is where the next one would have gone.</summary>
        internal static void Finish(RectTransform popup, float y) {
            popup.sizeDelta = new Vector2(PlateWidth, y - PlateGap);
        }

        /// <summary>
        /// A copy of the repair button's wood plate, stacked under the previous one. It keeps its raycast
        /// target, so a click that misses a cell lands on the plate rather than on the drop-item catcher
        /// behind the whole inventory.
        /// </summary>
        internal static RectTransform Plate(InventoryGui gui, RectTransform popup, ref float y, float height) {
            GameObject go;
            if (gui.m_repairPanel != null) {
                go = Object.Instantiate(gui.m_repairPanel.gameObject, popup, false);
            } else {
                go = new GameObject("plate", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(popup, false);
                go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
            }
            go.name = "DA_FilterPlate";
            // Vanilla hides the template whenever the open station cannot repair, and a clone of a hidden
            // object starts hidden.
            go.SetActive(true);

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(PlateWidth, height);
            rect.localScale = Vector3.one;

            y += height + PlateGap;
            return rect;
        }

        /// <summary>A plate sized for <paramref name="count"/> icons, three to a row.</summary>
        internal static RectTransform IconPlate(InventoryGui gui, RectTransform popup, ref float y, int count) {
            int rows = (count + PerRow - 1) / PerRow;
            return Plate(gui, popup, ref y, PadY * 2f + rows * CellPitch - (CellPitch - CellSize));
        }

        /// <summary>The <paramref name="index"/>th icon on an <see cref="IconPlate"/>, in reading order.</summary>
        internal static T IconCell<T>(InventoryGui gui, RectTransform plate, int index, Sprite icon) where T : FilterCell, new() {
            T cell = NewCell<T>(gui, plate,
                new Vector2(PadX + (index % PerRow) * CellPitch, -PadY - (index / PerRow) * CellPitch),
                new Vector2(CellSize, CellSize));
            if (cell.Icon != null) {
                cell.Icon.sprite = icon;
                cell.Icon.gameObject.SetActive(icon != null);
            }
            return cell;
        }

        /// <summary>
        /// A full-width cell that is a label rather than a picture: the slot's stack-count text, stretched
        /// over the cell.
        /// </summary>
        internal static T TextCell<T>(InventoryGui gui, RectTransform plate, float top, float height, string text) where T : FilterCell, new() {
            T cell = NewCell<T>(gui, plate, new Vector2(PadX, -top), new Vector2(PlateWidth - PadX * 2f, height));
            if (cell.Icon != null) { cell.Icon.gameObject.SetActive(false); }
            if (cell.Label != null) {
                RectTransform label = cell.Label.rectTransform;
                label.anchorMin = Vector2.zero;
                label.anchorMax = Vector2.one;
                label.pivot = new Vector2(0.5f, 0.5f);
                label.anchoredPosition = Vector2.zero;
                label.sizeDelta = Vector2.zero;
                cell.Label.alignment = TextAlignmentOptions.Center;
                cell.Label.text = text;
                cell.Label.gameObject.SetActive(true);
            }
            return cell;
        }

        /// <summary>
        /// A copy of the inventory grid's slot, stripped down the way MyLittleUI strips it for its filter:
        /// the icon stays, the orange queued-item frame becomes the lit state, the stack-count text is kept
        /// hidden for text cells to use as a label, and everything else goes. The slot already carries a
        /// Button (with its hover tint) and a UITooltip, so both are reused.
        /// </summary>
        private static T NewCell<T>(InventoryGui gui, RectTransform plate, Vector2 position, Vector2 size) where T : FilterCell, new() {
            GameObject go = Object.Instantiate(gui.m_playerGrid.m_elementPrefab, plate, false);
            go.name = "DA_FilterCell";
            go.SetActive(true);

            T cell = new T { Root = go };
            for (int i = go.transform.childCount - 1; i >= 0; --i) {
                Transform child = go.transform.GetChild(i);
                switch (child.name) {
                    case "icon":
                        cell.Icon = child.GetComponent<Image>();
                        child.gameObject.SetActive(true);
                        break;
                    case "queued":
                        cell.Lit = child.gameObject;
                        break;
                    case "amount":
                        cell.Label = child.GetComponent<TMP_Text>();
                        child.gameObject.SetActive(false);
                        break;
                    default:
                        Object.Destroy(child.gameObject);
                        break;
                }
            }

            RectTransform rect = (RectTransform)go.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localScale = Vector3.one;

            Button click = go.GetComponent<Button>();
            if (click == null) { click = go.AddComponent<Button>(); }
            click.onClick.RemoveAllListeners();

            // The slot's own tooltip is the big item card; the repair button's is the plain title-and-text
            // one this wants.
            cell.Tooltip = go.GetComponent<UITooltip>();
            if (cell.Tooltip == null) { cell.Tooltip = go.AddComponent<UITooltip>(); }
            UITooltip plain = gui.m_repairButton != null ? gui.m_repairButton.GetComponent<UITooltip>() : null;
            if (plain != null) { cell.Tooltip.m_tooltipPrefab = plain.m_tooltipPrefab; }

            // Inventory slots click silently; these are buttons, so they borrow Place stacks' click sound.
            ButtonSfx template = gui.m_stackAllButton != null ? gui.m_stackAllButton.GetComponent<ButtonSfx>() : null;
            if (template != null && go.GetComponent<ButtonSfx>() == null) {
                ButtonSfx sfx = go.AddComponent<ButtonSfx>();
                sfx.m_sfxPrefab = template.m_sfxPrefab;
                sfx.m_sfxPrefabVibrationOnly = template.m_sfxPrefabVibrationOnly;
            }

            return cell;
        }

        /// <summary>Wires a cell's click. Cells arrive with their slot's listeners already cleared.</summary>
        internal static void OnClick(FilterCell cell, UnityAction action) {
            cell.Root.GetComponent<Button>().onClick.AddListener(action);
        }

        // ---- state -------------------------------------------------------------

        /// <summary>
        /// Lights or darkens a cell. Partial keeps the frame lit and greys the icon: some of it is in,
        /// some of it is not.
        /// </summary>
        internal static void Paint(FilterCell cell, CategoryState state) {
            bool lit = state != CategoryState.Off;
            if (cell.Lit != null && cell.Lit.activeSelf != lit) { cell.Lit.SetActive(lit); }
            if (cell.Icon != null) { cell.Icon.color = state == CategoryState.On ? Color.white : OffColor; }
            if (cell.Label != null) { cell.Label.color = lit ? Color.white : OffColor; }
        }
    }
}
