using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DvergerAutomation {
    /// <summary>
    /// The in-panel switch for "spend out of the AutoSorter's linked chests", shown on every crafting
    /// grid the mod feeds: the vanilla crafting panel (one panel for both the Craft and Upgrade tabs)
    /// and, when Epic Loot is installed, the enchanting table window.
    ///
    /// Purely a front end for <see cref="ValConfig.CraftFromStorageEnabled"/>, which is a client-side
    /// BepInEx entry: the pool is read locally by whoever is crafting, so switching it off changes
    /// nothing for anyone else on the server, and the config file is what carries the choice between
    /// sessions. The switch itself does no gating - <see cref="ContainerNetwork"/> hands back an empty
    /// pool while it is off, which every consumer already reads as "no AutoSorter here".
    /// </summary>
    internal static class CraftFromStorageToggle {
        /// <summary>Name given to every instance, and how <see cref="Attach"/> recognises one it already built.</summary>
        private const string ObjectName = "DA_CraftFromStorageToggle";

        /// <summary>Name given to the cloned wood plate, and how <see cref="AttachBackdrop"/> recognises one.</summary>
        private const string BackdropName = "DA_CraftFromStorageBackdrop";

        /// <summary>
        /// Vertical gap between the repair button and the switch sitting above it. The plate behind each
        /// one is offset by the same amount, so button and plate stay locked together as a column.
        /// </summary>
        private const float ButtonPitch = 72f;

        // Icon tint while the switch is off. Dark enough to read as inactive next to the lit Glow ring
        // that marks the on state, without going so flat that the piece is unrecognisable.
        private static readonly Color OffIconColor = new Color(0.42f, 0.42f, 0.42f, 1f);

        // Live buttons, one per host panel. Unity's fake null is the "host was destroyed" test, so the
        // list is pruned on each refresh rather than tracked through scene-unload callbacks.
        private static readonly List<GameObject> Instances = new List<GameObject>();

        // The crafting panel's wood plate. Only ever one: the enchanting window has no such column to
        // join, so the backdrop is deliberately not created there.
        private static GameObject backdrop;

        private static Sprite icon;
        private static bool iconMissingLogged;

        // ---- placement ---------------------------------------------------------

        /// <summary>
        /// Hangs the switch off the left edge of the vanilla crafting panel, above the repair button -
        /// the one place vanilla itself floats a control outside this panel, so it reads as native.
        /// Same anchor and x as the prefab's RepairButton (0, 0.5 / x -37) and the same 72px pitch, so
        /// the two read as one column. That puts its lower edge flush against the top of RepairSimple,
        /// the decorative plate the repair button sits in, and clear of everything inside the panel -
        /// the station icon and recipe list both start at positive x, and this sits entirely outside.
        /// </summary>
        internal static void AttachToCraftingPanel(InventoryGui gui) {
            if (gui == null || gui.m_crafting == null) { return; }
            AttachBackdrop(gui);
            Attach(gui.m_crafting, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                   new Vector2(-37f, 167f + ButtonPitch));
            RefreshBackdrop();
        }

        /// <summary>
        /// Stacks a second copy of the repair button's wood plate directly above the original, so the
        /// switch sits in one rather than floating against the panel edge.
        ///
        /// Crafting panel only, by design: Epic Loot's enchanting window has no plate and no column to
        /// join, and a lone plank there would read as a stray graphic.
        ///
        /// The template is <see cref="InventoryGui.m_repairPanel"/> - a serialized reference to the
        /// object vanilla names RepairSimple - rather than a Find by name, so a rename on their side
        /// cannot silently drop the plate.
        /// </summary>
        private static void AttachBackdrop(InventoryGui gui) {
            Transform existing = gui.m_crafting.Find(BackdropName);
            if (existing != null) {
                backdrop = existing.gameObject;
                return;
            }

            RectTransform template = gui.m_repairPanel as RectTransform;
            if (template == null) {
                Logger.LogWarning("[Autosorter] No repair plate to clone; craft-from-storage switch gets no backdrop.");
                return;
            }

            GameObject go = UnityEngine.Object.Instantiate(template.gameObject, gui.m_crafting, false);
            go.name = BackdropName;
            // Vanilla hides the template whenever the open station cannot repair, and a clone of a
            // hidden object starts hidden. This one follows the switch instead - see RefreshBackdrop.
            go.SetActive(true);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchoredPosition = template.anchoredPosition + new Vector2(0f, ButtonPitch);

            // Purely decorative, so it must not swallow a click. The original keeps its raycast target
            // because the repair button's own hit area is smaller than the plate it sits in.
            Image plate = go.GetComponent<Image>();
            if (plate != null) { plate.raycastTarget = false; }

            // Slotted in at the template's own depth, so it layers against the rest of the panel exactly
            // as the original does. That pushes the original one along, which means it draws over the
            // 8px where the two plates overlap (they are 80 tall on a 72 pitch) and the seam falls under
            // the lower plank rather than on top of it. Attach then moves the switch to the end of the
            // panel, keeping it in front of both.
            rect.SetSiblingIndex(template.GetSiblingIndex());

            backdrop = go;
        }

        /// <summary>
        /// Hangs the switch off the top-right corner of Epic Loot's enchanting window. That panel is
        /// 1120x700 with the tab rail filling its left edge and the tab content the rest, so there is no
        /// free space inside it; floating just outside is the same trick used on the crafting panel.
        /// </summary>
        /// <param name="panel">Epic Loot's <c>EnchantingTableUI.Root</c>, which is the window's Panel object.</param>
        internal static void AttachToEnchantingPanel(GameObject panel) {
            if (panel == null) { return; }
            Attach(panel.transform, new Vector2(1f, 1f), new Vector2(0f, 1f), new Vector2(6f, -24f));
        }

        // ---- construction ------------------------------------------------------

        private static void Attach(Transform parent, Vector2 anchor, Vector2 pivot, Vector2 position) {
            Transform existing = parent.Find(ObjectName);
            if (existing != null) {
                Refresh(existing.gameObject);
                return;
            }

            GameObject go = Build(parent);
            if (go == null) { return; }

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = position;
            rect.localScale = Vector3.one;
            rect.SetAsLastSibling();

            Instances.Add(go);
            Refresh(go);
        }

        /// <summary>
        /// Clones the crafting panel's repair button. Building the widget from scratch would mean
        /// sourcing the button sprite, the hover/press tinting and the tooltip prefab by hand; the
        /// repair button already carries all three, is the right size (64x64) and is the control this
        /// one sits beside. Returns null if the inventory GUI is not built yet.
        /// </summary>
        private static GameObject Build(Transform parent) {
            InventoryGui gui = InventoryGui.instance;
            if (gui == null || gui.m_repairButton == null) {
                Logger.LogWarning("[Autosorter] No repair button to clone; craft-from-storage switch not shown.");
                return null;
            }

            // worldPositionStays false: the two-argument overload keeps the clone's world transform by
            // rewriting its local scale and position, which is never what a UI element parented into a
            // different panel wants.
            GameObject go = UnityEngine.Object.Instantiate(gui.m_repairButton.gameObject, parent, false);
            go.name = ObjectName;
            // The template is hidden whenever the player is not at a station that can repair, and a
            // clone of a hidden object starts hidden. Refresh decides visibility from here on.
            go.SetActive(true);

            // The repair button claims a gamepad button and owns the hint glyph that advertises it. A
            // second claimant would fight the original for the same binding, so both come off the copy:
            // this is a pointer-only control.
            UIGamePad pad = go.GetComponent<UIGamePad>();
            if (pad != null) { UnityEngine.Object.Destroy(pad); }
            Transform hint = go.transform.Find("gamepad_hint");
            if (hint != null) { UnityEngine.Object.Destroy(hint.gameObject); }

            // Instantiate copies serialized listeners only, and InventoryGui adds the repair handler at
            // runtime, so the copy's onClick arrives empty. Cleared anyway rather than relying on that.
            Button button = go.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(Toggle);
            // Vanilla greys the repair button out when there is nothing to repair. Nothing here is ever
            // unavailable - off is a state, not a disabled control - so the copy stays interactable.
            button.interactable = true;

            // ButtonImageColor is what does that greying, and it reasserts the icon's colour from Update
            // every frame the button is interactable - which, above, is always. Left on, it would paint
            // over the dimmed off-state tint each frame and the switch would look identical either way.
            // Its sibling ButtonTextColor goes for tidiness: the label is blanked just below.
            ButtonImageColor imageColor = go.GetComponent<ButtonImageColor>();
            if (imageColor != null) { UnityEngine.Object.Destroy(imageColor); }
            ButtonTextColor textColor = go.GetComponent<ButtonTextColor>();
            if (textColor != null) { UnityEngine.Object.Destroy(textColor); }

            // The template's (empty) label slot, blanked so it can never print over the icon.
            Transform label = go.transform.Find("Text");
            if (label != null) {
                TMP_Text text = label.GetComponent<TMP_Text>();
                if (text != null) { text.text = string.Empty; }
            }

            Sprite sprite = Icon();
            if (sprite != null) {
                Transform iconRoot = go.transform.Find("Image");
                Image iconImage = iconRoot != null ? iconRoot.GetComponent<Image>() : null;
                if (iconImage != null) { iconImage.sprite = sprite; }
            }

            return go;
        }

        /// <summary>The AutoSorter's own piece icon, so the switch names its source without a label.</summary>
        private static Sprite Icon() {
            if (icon != null) { return icon; }
            if (DvergerAutomation.EmbeddedResourceBundle != null) {
                icon = DvergerAutomation.EmbeddedResourceBundle.LoadAsset<Sprite>("DA_Autosorter.png");
            }
            if (icon == null && !iconMissingLogged) {
                iconMissingLogged = true;
                Logger.LogWarning("[Autosorter] AutoSorter icon missing from the bundle; the craft-from-storage switch keeps the repair glyph.");
            }
            return icon;
        }

        // ---- state -------------------------------------------------------------

        private static void Toggle() {
            // Writing the entry fires SettingChanged, which repaints every instance and drops the
            // memoized pool, so the click handler itself has nothing left to do. ValConfig sets
            // SaveOnConfigSet, so the choice is on disk before the panel redraws.
            ValConfig.CraftFromStorageEnabled.Value = !ValConfig.CraftFromStorageEnabled.Value;
        }

        /// <summary>
        /// Repaints every live switch and makes the panels behind them reflect the new pool.
        ///
        /// The aggregate has to go first: it is memoized per frame and was built from the pre-toggle
        /// pool, so the redraws below would otherwise read the counts the click just invalidated.
        /// Then each host is nudged, because neither rebuilds itself every frame - vanilla redraws the
        /// selected recipe's rows from Update but rebuilds the recipe list only on events, and Epic
        /// Loot's panels only on selection changes.
        /// </summary>
        internal static void OnConfigChanged(object sender, EventArgs e) {
            ContainerNetwork.InvalidateItemCounts();
            RefreshAll();

            // Preserves the selected recipe (it re-selects by index); focusView left off so the list
            // does not jump under the cursor.
            if (InventoryGui.IsVisible() && InventoryGui.instance != null && Player.m_localPlayer != null) {
                InventoryGui.instance.UpdateCraftingPanel();
            }
            EpicLootIntegration.RefreshEnchantingWindow();
        }

        internal static void RefreshAll() {
            RefreshBackdrop();
            for (int i = Instances.Count - 1; i >= 0; --i) {
                GameObject go = Instances[i];
                if (go == null) {
                    Instances.RemoveAt(i);
                    continue;
                }
                Refresh(go);
            }
        }

        /// <summary>
        /// The plate is scenery for the switch, so it shares the switch's one hiding condition: with the
        /// AutoSorter off server-side there is nothing to switch, and an empty plank would be worse than
        /// no plank. Unity's fake null doubles as the "panel was torn down" test.
        /// </summary>
        private static void RefreshBackdrop() {
            if (backdrop == null) { return; }
            bool available = ValConfig.AutomationEnabled.Value;
            if (backdrop.activeSelf != available) { backdrop.SetActive(available); }
        }

        private static void Refresh(GameObject go) {
            // With the AutoSorter turned off server-side no chest is ever linked, so the switch has
            // nothing to switch and would only invite bug reports. Hidden rather than disabled.
            bool available = ValConfig.AutomationEnabled.Value;
            if (go.activeSelf != available) { go.SetActive(available); }
            if (!available) { return; }

            bool on = ValConfig.CraftFromStorageEnabled.Value;

            // The template's glow ring, driven here as the on-lamp. Vanilla pulses the *original*
            // button's glow from InventoryGui, which holds a direct reference to it, so this copy's
            // ring is ours alone - but the copy was taken mid-pulse and inherited whatever alpha the
            // original was at, which can be zero. Pinned opaque so the lamp is steady.
            Transform glow = go.transform.Find("Glow");
            if (glow != null) {
                glow.gameObject.SetActive(on);
                Image glowImage = glow.GetComponent<Image>();
                if (glowImage != null) {
                    Color glowColor = glowImage.color;
                    if (glowColor.a < 1f) {
                        glowColor.a = 1f;
                        glowImage.color = glowColor;
                    }
                }
            }

            Transform iconRoot = go.transform.Find("Image");
            Image iconImage = iconRoot != null ? iconRoot.GetComponent<Image>() : null;
            if (iconImage != null) { iconImage.color = on ? Color.white : OffIconColor; }

            UITooltip tooltip = go.GetComponent<UITooltip>();
            if (tooltip != null && Localization.instance != null) {
                // Set() rather than writing m_topic/m_text: the player is hovering the button at the
                // moment they click it, and only Set repaints a tooltip that is already on screen. It
                // also no-ops when nothing changed, so a repaint that is not a toggle costs nothing.
                tooltip.Set(
                    Localization.instance.Localize("$DA_craft_from_storage"),
                    Localization.instance.Localize(on ? "$DA_craft_from_storage_on" : "$DA_craft_from_storage_off")
                        + "\n" + Localization.instance.Localize("$DA_craft_from_storage_hint"));
            }
        }
    }

    // ---- hosts ---------------------------------------------------------------

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Awake))]
    internal static class InventoryGui_Awake_StorageToggle_Patch {
        // Awake, not Show: m_crafting and m_repairButton are serialized references that are live by
        // then, the panel is only built once per session, and a child added here survives every
        // open/close cycle without the toggle having to watch for one.
        private static void Postfix(InventoryGui __instance) {
            CraftFromStorageToggle.AttachToCraftingPanel(__instance);
        }
    }

    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
    internal static class InventoryGui_Show_StorageToggle_Patch {
        // Belt and braces for the one input SettingChanged does not cover: an admin config sync that
        // lands while the panel is closed. Once per open, so nothing per-frame rides on it.
        private static void Postfix() {
            CraftFromStorageToggle.RefreshAll();
        }
    }
}
