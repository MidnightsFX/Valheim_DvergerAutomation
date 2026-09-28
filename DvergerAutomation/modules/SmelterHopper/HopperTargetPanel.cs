using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DvergerAutomation {
    /// <summary>
    /// The [C] button beside the Hopper's Deposit Materials, and the station picker it opens: one icon
    /// per type of station on the Smelter component within the hopper's reach - smelters, kilns, blast
    /// furnaces, windmills, spinning wheels, eitr refineries and modded ones. A lit icon is serviced; a
    /// dark one is left alone.
    ///
    /// Unlike the AutoSorter's deposit filter this is not the player's own setting. It belongs to the
    /// hopper - stored on its ZDO through <see cref="HopperHub.RequestTarget"/> - because the hopper is
    /// serviced by whichever client owns it, and every one of them has to service the same stations.
    ///
    /// The popup is rebuilt each time it opens, since what is in range differs between hoppers and
    /// changes as stations are built. A targeted type with nothing in range is not shown, and stays
    /// targeted.
    /// </summary>
    internal static class HopperTargetPanel {
        private const string ButtonName = "DA_HopperTargetButton";
        private const string PopupName = "DA_HopperTargetPopup";
        private const float NoteHeight = 40f;

        /// <summary>One type of station in range, and how many of it there are.</summary>
        private sealed class Station {
            internal string Type;
            internal string NameKey;
            internal Sprite Icon;
            internal int Count;
        }

        private sealed class Cell : FilterCell {
            internal Station Station;
        }

        private static GameObject button;
        private static RectTransform popup;
        private static readonly List<Cell> Cells = new List<Cell>();

        /// <summary>The hopper whose store is open, so [C] is on screen. Null the rest of the time.</summary>
        private static HopperHub hub;
        /// <summary>The target list the cells were last painted from, so a change from elsewhere repaints them.</summary>
        private static string painted;

        // ---- the [C] button ----------------------------------------------------

        /// <summary>Builds [C] next to the deposit button. Called from <see cref="DepositAll.Attach"/>, once per InventoryGui.</summary>
        internal static void Attach(InventoryGui gui, RectTransform deposit) {
            if (gui.m_container.Find(ButtonName) != null) { return; }

            button = FilterPopupUI.CreateButton(gui, ButtonName, deposit, OnButton);
            popup = null;
            Cells.Clear();
            hub = null;
        }

        /// <summary>
        /// Shows [C] while a hopper's store is open, and hides it and the popup the rest of the time.
        /// Driven every frame from <see cref="DepositAll.Refresh"/>: a reference compare, plus a string
        /// compare while the popup is open to catch a change that came from another player.
        /// </summary>
        internal static void Show(HopperHub current) {
            if (button == null) { return; }

            // By reference: a hopper destroyed since it was last open compares equal to null under Unity's
            // ==, and would leave [C] up over the next ordinary chest.
            if (!ReferenceEquals(current, hub)) {
                // Another hopper, or none. An open popup would be listing the wrong one's stations.
                hub = current;
                Close();
                bool show = current != null;
                if (button.activeSelf != show) { button.SetActive(show); }
                if (show) { Repaint(); }
                return;
            }

            if (popup != null && hub != null && hub.TargetList != painted) { Repaint(); }
        }

        /// <summary>Closes the popup, so it starts closed the next time the inventory opens.</summary>
        internal static void Close() {
            if (popup != null) { UnityEngine.Object.Destroy(popup.gameObject); }
            popup = null;
            Cells.Clear();
        }

        private static void OnButton() {
            if (popup != null) {
                Close();
            } else if (hub != null) {
                Build(InventoryGui.instance);
            }
            Repaint();
        }

        private static void Toggle(string type) {
            if (hub == null) { return; }
            if (!PrivateArea.CheckAccess(hub.transform.position)) { return; }
            hub.RequestTarget(type, !hub.IsTarget(type));
            // Lands at once when this client owns the hopper, which it does while the store is open;
            // otherwise Show repaints once the owner's write reaches this client.
            Repaint();
        }

        // ---- the popup ---------------------------------------------------------

        /// <summary>
        /// One plate of station icons, three to a row, or a single note in its place when there is
        /// nothing in range at all.
        /// </summary>
        private static void Build(InventoryGui gui) {
            Close();
            popup = FilterPopupUI.CreatePopup(gui, button, PopupName);
            if (popup == null) { return; }

            List<Station> stations = Survey(hub);
            float y = 0f;
            if (stations.Count == 0) {
                BuildNote(gui, ref y);
            } else {
                RectTransform plate = FilterPopupUI.IconPlate(gui, popup, ref y, stations.Count);
                for (int i = 0; i < stations.Count; ++i) {
                    Cell cell = FilterPopupUI.IconCell<Cell>(gui, plate, i, stations[i].Icon);
                    cell.Station = stations[i];
                    string type = stations[i].Type;
                    FilterPopupUI.OnClick(cell, () => Toggle(type));
                    Cells.Add(cell);
                }
            }
            FilterPopupUI.Finish(popup, y);
            popup.gameObject.SetActive(true);
        }

        /// <summary>"Nothing in range", with a tooltip saying how far the hopper reaches. Dark, and not clickable.</summary>
        private static void BuildNote(InventoryGui gui, ref float y) {
            Localization loc = Localization.instance;
            RectTransform plate = FilterPopupUI.Plate(gui, popup, ref y, FilterPopupUI.PadY * 2f + NoteHeight);
            Cell note = FilterPopupUI.TextCell<Cell>(gui, plate, FilterPopupUI.PadY, NoteHeight,
                loc.Localize("$DA_hopper_targets_none"));
            if (note.Label != null) {
                note.Label.enableAutoSizing = true;
                note.Label.fontSizeMin = 10f;
                note.Label.fontSizeMax = note.Label.fontSize;
            }
            if (note.Tooltip != null) {
                note.Tooltip.Set(loc.Localize("$DA_hopper_targets"),
                    loc.Localize("$DA_hopper_targets_none_desc", Mathf.RoundToInt(hub.EffectiveRadius).ToString()));
            }
            // A disabled Button gets no clicks, so no hover tint and no click sound; the tooltip is its
            // own pointer handler and still shows.
            Button click = note.Root.GetComponent<Button>();
            if (click != null) { click.enabled = false; }
            FilterPopupUI.Paint(note, CategoryState.Off);
        }

        /// <summary>
        /// The station types in the hopper's reach: the default three first, in the order they chain
        /// (wood, coal, bars), then everything else by name.
        /// </summary>
        private static List<Station> Survey(HopperHub target) {
            Dictionary<string, Station> byType = new Dictionary<string, Station>();
            foreach (Smelter smelter in target.StationsInRange()) {
                string type = HopperHub.StationType(smelter);
                if (byType.TryGetValue(type, out Station seen)) {
                    seen.Count++;
                    continue;
                }
                byType[type] = new Station { Type = type, NameKey = NameOf(smelter), Icon = IconOf(smelter), Count = 1 };
            }

            List<Station> list = new List<Station>(byType.Values);
            Localization loc = Localization.instance;
            list.Sort((a, b) => {
                int rank = DefaultRank(a).CompareTo(DefaultRank(b));
                if (rank != 0) { return rank; }
                return string.Compare(loc.Localize(a.NameKey), loc.Localize(b.NameKey), StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        private static int DefaultRank(Station station) {
            int index = Array.IndexOf(HopperHub.DefaultTargets, station.Type);
            return index < 0 ? int.MaxValue : index;
        }

        /// <summary>The station's display name token, falling back to its piece's and then its prefab name.</summary>
        private static string NameOf(Smelter smelter) {
            if (!string.IsNullOrEmpty(smelter.m_name)) { return smelter.m_name; }
            Piece piece = smelter.GetComponentInParent<Piece>();
            if (piece != null && !string.IsNullOrEmpty(piece.m_name)) { return piece.m_name; }
            return HopperHub.StationType(smelter);
        }

        /// <summary>
        /// The station's build-menu icon. A modded station that is not a buildable piece has none, so it
        /// falls back to the icon of the first thing it makes.
        /// </summary>
        private static Sprite IconOf(Smelter smelter) {
            Piece piece = smelter.GetComponentInParent<Piece>();
            if (piece != null && piece.m_icon != null) { return piece.m_icon; }
            if (smelter.m_conversion != null) {
                foreach (Smelter.ItemConversion conversion in smelter.m_conversion) {
                    ItemDrop.ItemData product = conversion?.m_to != null ? conversion.m_to.m_itemData : null;
                    if (product?.m_shared?.m_icons != null && product.m_shared.m_icons.Length > 0) {
                        return product.GetIcon();
                    }
                }
            }
            return null;
        }

        // ---- state -------------------------------------------------------------

        /// <summary>Repaints [C]'s tooltip and every icon from the open hopper's stored targets.</summary>
        private static void Repaint() {
            if (button == null || hub == null || Localization.instance == null) { return; }
            Localization loc = Localization.instance;

            FilterPopupUI.Dress(button, loc.Localize("$DA_hopper_targets_short"),
                loc.Localize("$DA_hopper_targets"), loc.Localize("$DA_hopper_targets_desc"));

            painted = hub.TargetList;
            foreach (Cell cell in Cells) {
                if (cell.Root == null || cell.Station == null) { continue; }

                bool on = hub.IsTarget(cell.Station.Type);
                FilterPopupUI.Paint(cell, on ? CategoryState.On : CategoryState.Off);

                if (cell.Tooltip == null) { continue; }
                string text = loc.Localize(on ? "$DA_hopper_target_on" : "$DA_hopper_target_off")
                    + "\n" + loc.Localize("$DA_hopper_target_count", cell.Station.Count.ToString())
                    + "\n" + loc.Localize("$DA_filter_click");
                cell.Tooltip.Set(loc.Localize(cell.Station.NameKey), text);
            }
        }
    }
}
