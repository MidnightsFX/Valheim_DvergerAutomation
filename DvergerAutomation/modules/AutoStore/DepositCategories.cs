using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ItemType = ItemDrop.ItemData.ItemType;

namespace DvergerAutomation {
    /// <summary>The three plates the filter popup stacks its category icons on.</summary>
    internal enum DepositPanel { Gear, Consumables, Loot }

    /// <summary>What one category icon shows, from the player's current config.</summary>
    internal enum CategoryState { On, Off, Partial }

    /// <summary>
    /// One icon in the Deposit Selected filter: a friendly group of vanilla item types.
    ///
    /// Food is the one category that is not a type list - anything that fills a food slot, whatever its
    /// type - so it has no members and is backed by <see cref="ValConfig.DepositKeepFood"/> instead.
    /// </summary>
    internal sealed class DepositCategory {
        internal readonly string NameKey;
        internal readonly DepositPanel Panel;
        internal readonly ItemType[] Types;
        /// <summary>The item whose icon stands for the category, looked up in ObjectDB by prefab name.</summary>
        internal readonly string IconPrefab;

        internal DepositCategory(string nameKey, DepositPanel panel, string iconPrefab, params ItemType[] types) {
            NameKey = nameKey;
            Panel = panel;
            IconPrefab = iconPrefab;
            Types = types;
        }

        internal bool IsFood => Types.Length == 0;
    }

    /// <summary>
    /// The category table behind the filter popup, and the translation between it and the two config
    /// entries that actually store the choice.
    ///
    /// The store stays the plain ItemType list in <see cref="ValConfig.DepositIgnoredTypes"/> - so an
    /// existing config keeps working, and anyone who wants finer control than a category still has it
    /// in the file. Every category owns a disjoint set of types, and together they cover every type
    /// there is (modded ones are folded into Misc), so each type answers to exactly one icon.
    /// </summary>
    internal static class DepositCategories {
        internal static readonly DepositCategory Food =
            new DepositCategory("$DA_cat_food", DepositPanel.Consumables, "CookedMeat");

        internal static readonly DepositCategory[] All = {
            new DepositCategory("$DA_cat_armor", DepositPanel.Gear, "ArmorIronChest",
                ItemType.Helmet, ItemType.Chest, ItemType.Legs, ItemType.Hands, ItemType.Shoulder),
            new DepositCategory("$DA_cat_utility", DepositPanel.Gear, "BeltStrength",
                ItemType.Utility, ItemType.Trinket),
            new DepositCategory("$DA_cat_shields", DepositPanel.Gear, "ShieldWood",
                ItemType.Shield),
            new DepositCategory("$DA_cat_weapons", DepositPanel.Gear, "SwordIron",
                ItemType.OneHandedWeapon, ItemType.TwoHandedWeapon, ItemType.TwoHandedWeaponLeft, ItemType.Bow),
            new DepositCategory("$DA_cat_ammo", DepositPanel.Gear, "ArrowWood",
                ItemType.Ammo, ItemType.AmmoNonEquipable),
            new DepositCategory("$DA_cat_tools", DepositPanel.Gear, "Hammer",
                ItemType.Tool, ItemType.Torch),
            Food,
            new DepositCategory("$DA_cat_meads", DepositPanel.Consumables, "MeadHealthMinor",
                ItemType.Consumable),
            new DepositCategory("$DA_cat_materials", DepositPanel.Loot, "Wood",
                ItemType.Material),
            new DepositCategory("$DA_cat_trophies", DepositPanel.Loot, "TrophyDeer",
                ItemType.Trophy),
            new DepositCategory("$DA_cat_fish", DepositPanel.Loot, "Fish1",
                ItemType.Fish),
            // Attach_Atgeir, None and Customization carry no real items in vanilla; they live here so the
            // table covers every type and no hand-written list can leave a type without an icon.
            new DepositCategory("$DA_cat_misc", DepositPanel.Loot, "CryptKey",
                ItemType.Misc, ItemType.None, ItemType.Customization, ItemType.Attach_Atgeir),
        };

        private static readonly Dictionary<DepositCategory, Sprite> Icons = new Dictionary<DepositCategory, Sprite>();

        /// <summary>
        /// The type a filter decision is made on. A type number vanilla does not define belongs to some
        /// other mod, and there is no name to list it under in the config, so it is filed as Misc.
        /// </summary>
        internal static ItemType Normalize(ItemType type) {
            return Enum.IsDefined(typeof(ItemType), type) ? type : ItemType.Misc;
        }

        // ---- state -------------------------------------------------------------

        /// <summary>
        /// On when Deposit Selected takes every type in the category, Off when it takes none. Partial
        /// only arises from a hand edit that lists some of a category's types but not all of them.
        /// </summary>
        internal static CategoryState StateOf(DepositCategory category) {
            if (category.IsFood) {
                return ValConfig.DepositKeepFood.Value ? CategoryState.Off : CategoryState.On;
            }

            HashSet<ItemType> ignored = DepositAll.IgnoredTypes();
            int kept = category.Types.Count(ignored.Contains);
            if (kept == 0) { return CategoryState.On; }
            return kept == category.Types.Length ? CategoryState.Off : CategoryState.Partial;
        }

        /// <summary>
        /// Flips a category. On goes to Off; Off and Partial both go to On, so a click on a half-edited
        /// category settles it on the side the icon's highlight already shows.
        ///
        /// Only writes the config. The entry's SettingChanged drops the cached filter and repaints the
        /// popup and the button's tooltip, and SaveOnConfigSet has it on disk before the next frame.
        /// </summary>
        internal static void Toggle(DepositCategory category) {
            if (category.IsFood) {
                ValConfig.DepositKeepFood.Value = !ValConfig.DepositKeepFood.Value;
                return;
            }

            bool keep = StateOf(category) == CategoryState.On;
            HashSet<ItemType> ignored = new HashSet<ItemType>(DepositAll.IgnoredTypes());
            foreach (ItemType type in category.Types) {
                if (keep) { ignored.Add(type); } else { ignored.Remove(type); }
            }

            // Written back in enum order, as names, so the file stays readable and diffs cleanly between
            // clicks. Any token the parser rejected is dropped here; it was already doing nothing.
            ValConfig.DepositIgnoredTypes.Value = string.Join(", ",
                ignored.OrderBy(type => (int)type).Select(type => type.ToString()));
        }

        // ---- icons -------------------------------------------------------------

        /// <summary>
        /// The category's icon: its named item's, else the first item ObjectDB has that the category
        /// covers, so a renamed prefab degrades to a less typical icon rather than a blank. Null only
        /// before ObjectDB is up, and not cached in that case.
        /// </summary>
        internal static Sprite IconOf(DepositCategory category) {
            if (Icons.TryGetValue(category, out Sprite cached)) { return cached; }

            ObjectDB db = ObjectDB.instance;
            if (db == null) { return null; }

            Sprite icon = IconOf(db.GetItemPrefab(category.IconPrefab));
            if (icon == null) {
                foreach (GameObject prefab in db.m_items) {
                    ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                    if (drop == null || drop.m_itemData == null || !Covers(category, drop.m_itemData)) { continue; }
                    icon = IconOf(prefab);
                    if (icon != null) { break; }
                }
            }

            Icons[category] = icon;
            return icon;
        }

        private static Sprite IconOf(GameObject prefab) {
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            ItemDrop.ItemData.SharedData shared = drop != null && drop.m_itemData != null ? drop.m_itemData.m_shared : null;
            if (shared == null || shared.m_icons == null || shared.m_icons.Length == 0) { return null; }
            return drop.m_itemData.GetIcon();
        }

        /// <summary>Whether an item falls under this category, by the same rule the deposit applies.</summary>
        private static bool Covers(DepositCategory category, ItemDrop.ItemData item) {
            bool food = item.m_shared.m_food > 0f;
            if (category.IsFood) { return food; }
            return !food && Array.IndexOf(category.Types, Normalize(item.m_shared.m_itemType)) >= 0;
        }
    }
}
