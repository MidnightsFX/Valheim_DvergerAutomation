using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// The [C] button beside the AutoSorter's Deposit Selected, and the filter popup it opens: a column of
    /// small plates, laid out like MyLittleUI's crafting filter, where the player picks what Deposit
    /// Selected takes.
    ///
    /// The top plate holds two switches for the player's working set - the hotbar, and an inventory mod's
    /// quick slots (only shown when one is installed). Below it, three plates of category icons: Gear,
    /// Consumables and Loot. A lit icon (vanilla's orange queued-item frame) is deposited; a dark one
    /// stays in the pack.
    ///
    /// Purely a front end for client config entries (<see cref="DepositCategories"/> does the
    /// translating): a click writes the entry, and the entry's SettingChanged repaints everything. That
    /// also keeps the popup in step with edits made from the F1 menu while it is open.
    ///
    /// The button, plates and cells come from <see cref="FilterPopupUI"/>, shared with the Hopper's
    /// station picker.
    /// </summary>
    internal static class DepositFilterPanel {
        private const string ButtonName = "DA_DepositFilterButton";
        private const string PopupName = "DA_DepositFilterPopup";

        /// <summary>One clickable cell - a category icon or one of the two switches.</summary>
        private sealed class Cell : FilterCell {
            // Exactly one of these two is set.
            internal DepositCategory Category;
            internal ConfigEntry<bool> Switch;
            internal string SwitchNameKey;
        }

        private static GameObject button;
        private static RectTransform popup;
        private static readonly List<Cell> Cells = new List<Cell>();

        /// <summary>Whether the Sorter's box is the open container, so [C] is on screen.</summary>
        private static bool shown;
        /// <summary>Whether the player has the popup open. Reset whenever the box or the inventory closes.</summary>
        private static bool open;

        // ---- the [C] button ----------------------------------------------------

        /// <summary>
        /// Builds [C] next to the Deposit Selected button. Called from <see cref="DepositAll.Attach"/>,
        /// once per InventoryGui. The popup is built later, on its first opening: its icons come from
        /// ObjectDB, which is not up when the GUI wakes.
        /// </summary>
        internal static void Attach(InventoryGui gui, RectTransform deposit) {
            if (gui.m_container.Find(ButtonName) != null) { return; }

            button = FilterPopupUI.CreateButton(gui, ButtonName, deposit, OnButton);
            popup = null;
            Cells.Clear();
            shown = false;
            open = false;
        }

        /// <summary>
        /// Shows [C] while the Sorter's box is open, and hides it and the popup the rest of the time.
        /// Driven from <see cref="DepositAll.Refresh"/>, so it runs every frame the inventory is open and
        /// is a bool compare unless something changed.
        /// </summary>
        internal static void Show(bool show) {
            if (button == null) { return; }
            if (show == shown && button.activeSelf == show) { return; }

            shown = show;
            if (!show) { open = false; }
            button.SetActive(show);
            if (show) { Repaint(); }
            UpdatePopup();
        }

        /// <summary>Closes the popup, so it starts closed the next time the inventory opens.</summary>
        internal static void Close() {
            open = false;
            UpdatePopup();
        }

        private static void OnButton() {
            open = !open;
            if (open) { Build(InventoryGui.instance); }
            UpdatePopup();
            Repaint();
        }

        private static void UpdatePopup() {
            if (popup == null) { return; }
            bool visible = shown && open;
            if (popup.gameObject.activeSelf != visible) { popup.gameObject.SetActive(visible); }
        }

        // ---- the popup ---------------------------------------------------------

        /// <summary>Builds the popup once: the switches, then the Gear, Consumables and Loot plates.</summary>
        private static void Build(InventoryGui gui) {
            if (popup != null) { return; }
            popup = FilterPopupUI.CreatePopup(gui, button, PopupName);
            if (popup == null) { return; }

            float y = 0f;
            BuildSwitches(gui, ref y);
            foreach (DepositPanel panel in new[] { DepositPanel.Gear, DepositPanel.Consumables, DepositPanel.Loot }) {
                BuildCategories(gui, panel, ref y);
            }
            FilterPopupUI.Finish(popup, y);
        }

        /// <summary>The hotbar switch, and the quick-slot switch when an inventory mod gives it anything to switch.</summary>
        private static void BuildSwitches(InventoryGui gui, ref float y) {
            List<(ConfigEntry<bool> entry, string nameKey)> switches = new List<(ConfigEntry<bool>, string)> {
                (ValConfig.DepositIncludeHotbar, "$DA_filter_hotbar"),
            };
            if (InventorySlotsIntegration.Active) {
                switches.Add((ValConfig.DepositIncludeQuickSlots, "$DA_filter_quickslots"));
            }

            const float height = FilterPopupUI.SwitchHeight;
            const float gap = FilterPopupUI.PlateGap;
            RectTransform plate = FilterPopupUI.Plate(gui, popup, ref y,
                FilterPopupUI.PadY * 2f + switches.Count * height + (switches.Count - 1) * gap);
            for (int i = 0; i < switches.Count; ++i) {
                Cell cell = FilterPopupUI.TextCell<Cell>(gui, plate, FilterPopupUI.PadY + i * (height + gap), height,
                    Localization.instance.Localize(switches[i].nameKey));
                cell.Switch = switches[i].entry;
                cell.SwitchNameKey = switches[i].nameKey;

                ConfigEntry<bool> entry = cell.Switch;
                FilterPopupUI.OnClick(cell, () => entry.Value = !entry.Value);
                Cells.Add(cell);
            }
        }

        /// <summary>One plate of category icons, three to a row in table order.</summary>
        private static void BuildCategories(InventoryGui gui, DepositPanel panel, ref float y) {
            List<DepositCategory> categories = new List<DepositCategory>();
            foreach (DepositCategory category in DepositCategories.All) {
                if (category.Panel == panel) { categories.Add(category); }
            }
            if (categories.Count == 0) { return; }

            RectTransform plate = FilterPopupUI.IconPlate(gui, popup, ref y, categories.Count);
            for (int i = 0; i < categories.Count; ++i) {
                DepositCategory category = categories[i];
                Cell cell = FilterPopupUI.IconCell<Cell>(gui, plate, i, DepositCategories.IconOf(category));
                cell.Category = category;
                FilterPopupUI.OnClick(cell, () => DepositCategories.Toggle(category));
                Cells.Add(cell);
            }
        }

        // ---- state -------------------------------------------------------------

        /// <summary>
        /// Repaints [C]'s tooltip and every cell from the current config. Called on show, on opening, and
        /// from <see cref="DepositAll.InvalidateFilters"/> whenever a filter entry changes - which is how a
        /// click here lands, since clicks only write config.
        /// </summary>
        internal static void Repaint() {
            if (button == null || Localization.instance == null) { return; }
            Localization loc = Localization.instance;

            FilterPopupUI.Dress(button, loc.Localize("$DA_deposit_filter_short"),
                loc.Localize("$DA_deposit_filter"), loc.Localize("$DA_deposit_filter_desc"));

            foreach (Cell cell in Cells) {
                if (cell.Root == null) { continue; }

                CategoryState state = cell.Category != null
                    ? DepositCategories.StateOf(cell.Category)
                    : (cell.Switch.Value ? CategoryState.On : CategoryState.Off);
                FilterPopupUI.Paint(cell, state);

                if (cell.Tooltip == null) { continue; }
                string topic = loc.Localize(cell.Category != null ? cell.Category.NameKey : cell.SwitchNameKey);
                string text;
                switch (state) {
                    case CategoryState.On: text = loc.Localize("$DA_filter_on"); break;
                    case CategoryState.Partial: text = loc.Localize("$DA_filter_partial"); break;
                    default: text = loc.Localize("$DA_filter_off"); break;
                }
                if (cell.Switch == ValConfig.DepositIncludeQuickSlots) {
                    text += "\n" + loc.Localize("$DA_filter_quickslots_note");
                }
                text += "\n" + loc.Localize("$DA_filter_click");
                cell.Tooltip.Set(topic, text);
            }
        }
    }

    // ---- hosts ---------------------------------------------------------------

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class InventoryGui_Hide_DepositFilter_Patch {
        // Closing the box already hides the popup (Refresh sees no container), but closing the whole
        // inventory stops Refresh from running at all, and reopening the same box would find it open.
        private static void Postfix() {
            DepositFilterPanel.Close();
            HopperTargetPanel.Close();
        }
    }
}
