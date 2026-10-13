using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergerAutomation {
    /// <summary>
    /// What the Grinder takes, and what it gives back for it.
    ///
    /// An item returns a share of what vanilla actually charged to make it, level by level. Up to the
    /// item's <c>m_maxQuality</c> that is the recipe's ordinary requirements. Levels above that can only
    /// be reached at an upgrader station, where vanilla charges the upgrader requirements alone
    /// (<c>Player.ConsumeResources</c>, <c>InventoryGui.DoCrafting</c>). Requirements vanilla would not
    /// hand back when an upgrade breaks (<c>m_recover</c> false) are left out here too. The first level's
    /// cost is spread over the units one craft makes, so 20 arrows return one craft's worth.
    ///
    /// Items made by more than one recipe return, per material, the least any of those recipes charges, so
    /// feeding an item never pays out more than the cheapest way to make it.
    ///
    /// One exception, on purpose: an upgrader station can also raise an item below its maximum level, for
    /// the upgrader requirement alone (one non-craftable upgrade token, where the forge takes metal). The
    /// game keeps no record of which way an item went up, and nearly every weapon and armour recipe has that
    /// route, so taking the cheaper of the two would return nothing at all for ordinary upgrades. Levels up
    /// to the maximum are paid back as the forge charges them; at worst a token turns into a share of the
    /// metal it stood in for.
    ///
    /// Enchanted (Epic Loot) items can return either half of what they cost, or both, as the server sets
    /// it: the mundane half is the crafting cost above, the magic half what enchanting the item to its
    /// rarity costs at Epic Loot's enchanting table. That is the closest thing to what went into it a found
    /// item has, since loot comes enchanted.
    /// </summary>
    internal static class GrinderRecipes {
        // Far above anything vanilla or the usual mods reach; only bounds the loop.
        private const int MaxQuality = 20;

        // Every recipe by the shared name of what it makes. Enabled is checked at use, since mods toggle it
        // from config without touching the list.
        private static readonly Dictionary<string, List<Recipe>> ByName = new Dictionary<string, List<Recipe>>();
        private static ObjectDB cachedDb;
        private static int cachedCount = -1;

        // Shared names of everything a trader sells. Buying one and grinding it would turn coins into
        // materials, so those are refused outright.
        private static readonly HashSet<string> SoldNames = new HashSet<string>();
        private static ZNetScene soldFor;

        private static readonly NameSet Blacklist = new NameSet(StringComparer.OrdinalIgnoreCase);

        private static readonly List<Recipe> Scratch = new List<Recipe>();

        /// <summary>
        /// Whether the Grinder breaks this item down. Anything it refuses - not craftable, worn, enchanted
        /// with both enchanted options off, on the server's blacklist, or sold by a trader - comes out in the
        /// results chest as it went in.
        /// </summary>
        internal static bool CanGrind(ItemDrop.ItemData item) {
            if (item == null || item.m_shared == null || item.m_equipped) { return false; }
            bool magic = EpicLootIntegration.IsMagicItem(item);
            if (magic && !ValConfig.GrinderMagicMundane.Value && !ValConfig.GrinderMagicEnchant.Value) { return false; }
            // An item with no prefab is something the game itself cannot rebuild.
            if (item.m_dropPrefab == null) { return false; }
            if (Blacklist.Contains(ValConfig.GrinderBlacklist.Value, item.m_dropPrefab.name)) { return false; }
            if (IsSold(item.m_shared.m_name)) { return false; }
            if (ReturnsMundane(magic) && HasRecipes(item)) { return true; }
            return ReturnsMagic(magic) && EpicLootIntegration.EnchantCost(item).Count > 0;
        }

        // Which halves of its cost an item returns: everything its crafting cost, and an enchanted item
        // either or both of that and its enchanting cost, as the server sets it.
        private static bool ReturnsMundane(bool magic) => !magic || ValConfig.GrinderMagicMundane.Value;

        private static bool ReturnsMagic(bool magic) => magic && ValConfig.GrinderMagicEnchant.Value;

        /// <summary>
        /// The units ground in one go: a stack goes through one craft's worth at a time, so one bronze
        /// bar, or 20 arrows. Gear is one item.
        /// </summary>
        internal static int CraftUnit(ItemDrop.ItemData item) {
            if (item == null || item.m_shared == null || item.m_shared.m_maxStackSize <= 1) { return 1; }
            int unit = 0;
            foreach (Recipe recipe in Enabled(item)) {
                unit = Mathf.Max(unit, recipe.m_amount);
            }
            return Mathf.Clamp(unit, 1, item.m_shared.m_maxStackSize);
        }

        /// <summary>
        /// Adds what <paramref name="units"/> of <paramref name="item"/> return at <paramref name="yield"/>
        /// (0-1) to <paramref name="milli"/>, in thousandths of a unit keyed by material prefab name. False
        /// when there is no longer anything to work it out from, and nothing was added.
        /// </summary>
        internal static bool AddReturns(ItemDrop.ItemData item, int units, float yield, Dictionary<string, long> milli) {
            if (item == null || item.m_shared == null || units <= 0) { return false; }
            bool magic = EpicLootIntegration.IsMagicItem(item);
            bool added = false;
            if (ReturnsMundane(magic)) { added |= AddCraftingReturns(item, units, yield, milli); }
            if (ReturnsMagic(magic)) { added |= AddEnchantingReturns(item, units, yield, milli); }
            return added;
        }

        // The mundane half: a share of the crafting cost.
        private static bool AddCraftingReturns(ItemDrop.ItemData item, int units, float yield, Dictionary<string, long> milli) {
            List<Recipe> recipes = Enabled(item);
            if (recipes.Count == 0) { return false; }

            bool stackable = item.m_shared.m_maxStackSize > 1;
            int quality = Mathf.Clamp(item.m_quality, 1, MaxQuality);
            Dictionary<string, float> cheapest = null;
            foreach (Recipe recipe in recipes) {
                Dictionary<string, float> cost = CostPerItem(recipe, quality, stackable);
                if (cheapest == null) {
                    cheapest = cost;
                    continue;
                }
                // A material one recipe does not use at all costs nothing by that route.
                foreach (string key in new List<string>(cheapest.Keys)) {
                    cheapest[key] = cost.TryGetValue(key, out float other) ? Mathf.Min(cheapest[key], other) : 0f;
                }
            }

            float share = Mathf.Clamp01(yield) * units;
            foreach (KeyValuePair<string, float> entry in cheapest) {
                Owe(milli, entry.Key, entry.Value * share);
            }
            return true;
        }

        // The magic half: a share of what enchanting it to its rarity costs. A material Epic Loot names
        // that this game does not have is left out rather than owed for ever.
        private static bool AddEnchantingReturns(ItemDrop.ItemData item, int units, float yield, Dictionary<string, long> milli) {
            List<EpicLootAPI.ItemAmount> cost = EpicLootIntegration.EnchantCost(item);
            if (cost.Count == 0) { return false; }
            float share = Mathf.Clamp01(yield) * units;
            foreach (EpicLootAPI.ItemAmount entry in cost) {
                if (entry == null || string.IsNullOrEmpty(entry.Item) || entry.Amount <= 0) { continue; }
                if (ObjectDB.instance == null || ObjectDB.instance.GetItemPrefab(entry.Item) == null) { continue; }
                Owe(milli, entry.Item, entry.Amount * share);
            }
            return true;
        }

        private static void Owe(Dictionary<string, long> milli, string prefab, float amount) {
            // Rounded down, and only by a hair's breadth of float error: never paid out above cost.
            long add = (long)Math.Floor(amount * 1000.0 + 1e-4);
            if (add <= 0) { return; }
            milli.TryGetValue(prefab, out long have);
            milli[prefab] = have + add;
        }

        /// <summary>
        /// What one unit of the item cost by this recipe, by material prefab name: the first level spread
        /// over the craft's output, plus every upgrade level in full.
        ///
        /// A stackable crafted at a station can come out with bonus units: with the player's skill at its
        /// highest, each craft of a 5x multi-craft rolls for one, and the bonus grows with each roll that
        /// lands (<c>InventoryGui.DoCrafting</c>). Grinding those at a high yield would pay out more than they
        /// cost, so the first level is spread over the expected output instead of the listed one. At the
        /// default yields this changes nothing.
        /// </summary>
        private static Dictionary<string, float> CostPerItem(Recipe recipe, int quality, bool stackable) {
            Dictionary<string, float> cost = new Dictionary<string, float>();
            int maxQuality = Mathf.Max(1, recipe.m_item.m_itemData.m_shared.m_maxQuality);
            float perCraft = Mathf.Max(1, recipe.m_amount);
            if (stackable && recipe.m_craftingStation != null && recipe.m_craftingStation.m_craftingSkill != Skills.SkillType.None) {
                perCraft += ExpectedBonusPerCraft();
            }

            for (int level = 1; level <= quality; ++level) {
                bool upgrader = level > maxQuality;
                foreach (Piece.Requirement req in recipe.m_resources) {
                    if (req == null || req.m_resItem == null || !req.m_recover) { continue; }
                    if (req.m_upgraderResource != upgrader) { continue; }
                    int amount = req.GetAmount(level);
                    if (amount <= 0) { continue; }
                    string key = req.m_resItem.gameObject.name;
                    cost.TryGetValue(key, out float have);
                    // An upgrade of a stackable (only ever modded) charges once and returns the craft's
                    // whole output at the new level, so its cost is spread the same way as the first.
                    cost[key] = have + (level == 1 || stackable ? amount / perCraft : amount);
                }
            }
            return cost;
        }

        /// <summary>
        /// Bonus units one craft makes on average with the skill at its highest, from vanilla's own
        /// numbers: in a run of N crafts each rolls with chance p, and the k-th success adds k units, so
        /// the run makes E[k(k+1)/2] extra with k ~ Binomial(N, p).
        /// </summary>
        private static float ExpectedBonusPerCraft() {
            InventoryGui gui = InventoryGui.instance;
            float p = Mathf.Clamp01(gui != null ? gui.m_craftBonusChance : 0.25f);
            int n = Mathf.Max(1, gui != null ? gui.m_multiCraftAmount : 5);
            int amount = gui != null ? gui.m_craftBonusAmount : 1;
            float mean = n * p;
            float meanSquare = n * p * (1f - p) + mean * mean;
            return amount * (meanSquare + mean) / 2f / n;
        }

        private static bool HasRecipes(ItemDrop.ItemData item) {
            foreach (Recipe recipe in Enabled(item)) {
                foreach (Piece.Requirement req in recipe.m_resources) {
                    if (req != null && req.m_resItem != null && req.m_recover) { return true; }
                }
            }
            return false;
        }

        /// <summary>
        /// The enabled recipes that make this item, or none when any of them takes just one of its
        /// ingredients: there is no telling which one this particular item was made from. The list is
        /// shared scratch, so callers must be done with it before asking again.
        /// </summary>
        private static List<Recipe> Enabled(ItemDrop.ItemData item) {
            Scratch.Clear();
            Refresh();
            if (item == null || item.m_shared == null) { return Scratch; }
            if (!ByName.TryGetValue(item.m_shared.m_name, out List<Recipe> all)) { return Scratch; }
            foreach (Recipe recipe in all) {
                if (recipe == null || !recipe.m_enabled) { continue; }
                if (recipe.m_requireOnlyOneIngredient) {
                    Scratch.Clear();
                    return Scratch;
                }
                Scratch.Add(recipe);
            }
            return Scratch;
        }

        // Mods add recipes after ObjectDB.Awake, so a longer list is a new list.
        private static void Refresh() {
            ObjectDB db = ObjectDB.instance;
            if (db == null) {
                ByName.Clear();
                cachedDb = null;
                return;
            }
            if (db == cachedDb && db.m_recipes.Count == cachedCount) { return; }

            ByName.Clear();
            foreach (Recipe recipe in db.m_recipes) {
                if (recipe == null || recipe.m_item == null || recipe.m_item.m_itemData == null) { continue; }
                string name = recipe.m_item.m_itemData.m_shared.m_name;
                if (!ByName.TryGetValue(name, out List<Recipe> list)) {
                    list = new List<Recipe>();
                    ByName[name] = list;
                }
                list.Add(recipe);
            }
            cachedDb = db;
            cachedCount = db.m_recipes.Count;
        }

        private static bool IsSold(string sharedName) {
            ZNetScene scene = ZNetScene.instance;
            if (scene != null && scene != soldFor) {
                SoldNames.Clear();
                foreach (GameObject prefab in scene.m_prefabs) {
                    Trader trader = prefab != null ? prefab.GetComponent<Trader>() : null;
                    if (trader == null) { continue; }
                    foreach (Trader.TradeItem trade in trader.m_items) {
                        if (trade != null && trade.m_prefab != null) { SoldNames.Add(trade.m_prefab.m_itemData.m_shared.m_name); }
                    }
                }
                soldFor = scene;
            }
            return SoldNames.Contains(sharedName);
        }
    }
}
