using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;

namespace DvergerAutomation {
    /// <summary>
    /// Tells apart the cells of the player's inventory that belong to an equipment/quick-slot mod from
    /// the ones that are actually the player's pack, so Deposit Selected only empties a slot the player
    /// has said it may.
    ///
    /// Vanilla Valheim has no quick slots, so this only ever has anything to say when one of the three
    /// slot mods is installed: EquipmentAndQuickSlots, AzuExtendedPlayerInventory or ExtraSlots. They
    /// all declare each other incompatible, so at most one is ever active. None of them keeps its slots
    /// in a separate inventory - each grows <c>Player.m_inventory.m_height</c> and lays its equipment,
    /// quick and custom slots out in the rows past the pack, in the same grid. Without this test those
    /// items read as ordinary backpack contents.
    ///
    /// Every cell is sorted into one of four kinds:
    ///  - the hotbar (row 0), which none of the three mods moves;
    ///  - a quick slot, which here means every slot that is not equipment: EAQS and AzuEPI quick slots,
    ///    and ExtraSlots' quick, ammo, food and misc slots;
    ///  - reserved: anything else in the slot rows - equipment, custom slots another mod registered
    ///    (Jewelcrafting's rings, for one) and empty padding. Never deposited;
    ///  - the pack.
    ///
    /// EAQS is bound through the vendored shim in common/EquipmentAndQuickSlotsAPI. The other two are
    /// bound here by reflection on the mod's own assembly, so with none of them installed every call is
    /// a cheap "pack".
    /// </summary>
    internal static class InventorySlotsIntegration {
        internal const string AzuEPIGUID = "Azumatt.AzuExtendedPlayerInventory";
        internal const string ExtraSlotsGUID = "shudnal.ExtraSlots";

        /// <summary>The player's hotbar is row 0 of their inventory - vanilla reads it as <c>GetItemAt(x, 0)</c>.</summary>
        private const int HotbarRow = 0;

        internal enum SlotKind { Pack, Hotbar, QuickSlot, Reserved }

        private enum Provider { None, EAQS, AzuEPI, ExtraSlots }

        // Resolved once: assemblies do not come and go, and the answer is asked per button press.
        private static Provider? provider;

        // AzuEPI
        private static MethodInfo azuSlotGridPos;
        private static MethodInfo azuAddedRows;
        private static MethodInfo azuQuickItems;

        // ExtraSlots
        private static MethodInfo extraHeightPlayer;
        private static readonly List<MethodInfo> ExtraSlotItemLists = new List<MethodInfo>();

        /// <summary>True when a slot mod is present and bound, which is when the quick-slot toggle means anything.</summary>
        internal static bool Active => Bound() != Provider.None;

        // ---- per-press snapshot ------------------------------------------------

        /// <summary>
        /// Where the slot rows start and which cells hold a non-equipment slot item, read once for one
        /// Deposit Selected press. The slot mods are asked here rather than per item, and nothing on the
        /// grid moves between taking this and the deposit loop reading it.
        /// </summary>
        internal sealed class Snapshot {
            private readonly int firstSlotRow;
            private readonly HashSet<Vector2i> quickCells;
            private readonly bool eaqs;

            internal Snapshot(int firstSlotRow, HashSet<Vector2i> quickCells, bool eaqs) {
                this.firstSlotRow = firstSlotRow;
                this.quickCells = quickCells;
                this.eaqs = eaqs;
            }

            /// <summary>
            /// Which kind of cell this item sits in. Quick cells are matched by position rather than by
            /// item instance, which is how AzuAutoStore does it too: the APIs hand back the items the
            /// slots hold, and their grid positions are what identify the slots.
            /// </summary>
            internal SlotKind Classify(ItemDrop.ItemData item) {
                Vector2i pos = item.m_gridPos;
                if (pos.y == HotbarRow) { return SlotKind.Hotbar; }
                if (quickCells.Contains(pos)) { return SlotKind.QuickSlot; }
                if (pos.y >= firstSlotRow) { return SlotKind.Reserved; }
                // EAQS's layout never puts a slot inside the pack rows, but its API allows one; the cell
                // test catches that case the row test cannot.
                if (eaqs && EquipmentAndQuickSlotsAPI.EAQS.IsSlotCell(pos.x, pos.y, out _)) { return SlotKind.Reserved; }
                return SlotKind.Pack;
            }
        }

        /// <summary>
        /// Reads the slot layout of <paramref name="inv"/> (the local player's inventory).
        ///
        /// Every endpoint fails soft towards depositing less: an unanswered first-row query leaves the
        /// row test at the vanilla four rows (or the full height, where that is smaller), and an
        /// unanswered item query leaves the quick-cell set empty, so those cells read as reserved.
        /// </summary>
        internal static Snapshot Take(Inventory inv) {
            HashSet<Vector2i> quick = new HashSet<Vector2i>();
            int height = inv != null ? inv.GetHeight() : int.MaxValue;
            int firstRow = height;

            try {
                switch (Bound()) {
                    case Provider.EAQS:
                        firstRow = EquipmentAndQuickSlotsAPI.EAQS.GetVisibleRows();
                        AddCells(quick, EquipmentAndQuickSlotsAPI.EAQS.GetQuickSlotItems());
                        break;
                    case Provider.AzuEPI:
                        firstRow = AzuFirstSlotRow(inv, height);
                        AddCells(quick, Invoke<List<ItemDrop.ItemData>>(azuQuickItems));
                        break;
                    case Provider.ExtraSlots:
                        object rows = extraHeightPlayer != null ? extraHeightPlayer.Invoke(null, null) : null;
                        firstRow = rows is int playerRows ? playerRows : Math.Min(height, 4);
                        foreach (MethodInfo list in ExtraSlotItemLists) {
                            AddCells(quick, Invoke<List<ItemDrop.ItemData>>(list));
                        }
                        break;
                }
            } catch (Exception ex) {
                // An API that changed shape under us. Treat every slot row as reserved and carry on.
                Logger.LogWarning($"[AutoStore] Reading the inventory slot layout failed, quick slots are kept this time: {ex.Message}");
                quick.Clear();
                firstRow = Math.Min(height, 4);
            }

            // Row 0 is the hotbar however many rows a mod claims; a first slot row at or above it would
            // make the whole pack read as reserved.
            if (firstRow < 1) { firstRow = height; }
            return new Snapshot(firstRow, quick, Bound() == Provider.EAQS);
        }

        private static void AddCells(HashSet<Vector2i> cells, List<ItemDrop.ItemData> items) {
            if (items == null) { return; }
            foreach (ItemDrop.ItemData item in items) {
                if (item != null) { cells.Add(item.m_gridPos); }
            }
        }

        /// <summary>
        /// AzuEPI's slot rows start at the grid position of slot 0. <c>GetAddedRows</c> is only the
        /// fallback for builds that predate <c>GetSlotGridPos</c>: with AzuEPI's equipment row switched off
        /// it still reports rows while none are added, which would cut real pack rows off as reserved.
        /// </summary>
        private static int AzuFirstSlotRow(Inventory inv, int height) {
            if (inv == null) { return height; }
            if (azuSlotGridPos != null) {
                object pos = azuSlotGridPos.Invoke(null, new object[] { inv, 0 });
                if (pos is Vector2i grid) { return grid.y < 0 ? height : grid.y; }
            }
            if (azuAddedRows != null && azuAddedRows.Invoke(null, new object[] { inv.GetWidth() }) is int added) {
                return height - added;
            }
            return Math.Min(height, 4);
        }

        private static T Invoke<T>(MethodInfo method) {
            if (method == null) { return default; }
            object result = method.Invoke(null, null);
            return result is T typed ? typed : default;
        }

        // ---- binding -----------------------------------------------------------

        private static Provider Bound() {
            if (provider.HasValue) { return provider.Value; }

            provider = Provider.None;
            if (EquipmentAndQuickSlotsAPI.EAQS.IsLoaded()) {
                provider = Provider.EAQS;
            } else if (TryBindAzuEPI()) {
                provider = Provider.AzuEPI;
            } else if (TryBindExtraSlots()) {
                provider = Provider.ExtraSlots;
            }

            Logger.LogDebug(provider.Value == Provider.None
                ? "[AutoStore] No inventory slot mod present; the whole inventory grid is the player's pack."
                : $"[AutoStore] {provider.Value} present; Deposit Selected reads its slots.");
            return provider.Value;
        }

        /// <summary>
        /// Resolves a mod's type from that mod's own assembly. Looking it up by name across the AppDomain
        /// is not safe for AzuEPI: its API ships as a stub assembly other mods merge into themselves
        /// (MyLittleUI, AzuAutoStore, Jewelcrafting), so the same <c>AzuEPI.API</c> name exists in several
        /// places and only one of them answers.
        /// </summary>
        private static Type PluginType(string guid, string typeName) {
            if (!Chainloader.PluginInfos.TryGetValue(guid, out BepInEx.PluginInfo info) || info.Instance == null) { return null; }
            return info.Instance.GetType().Assembly.GetType(typeName);
        }

        private static MethodInfo Static(Type type, string name, params Type[] args) {
            return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);
        }

        private static bool TryBindAzuEPI() {
            Type api = PluginType(AzuEPIGUID, "AzuEPI.API");
            if (api == null) { return false; }
            azuSlotGridPos = Static(api, "GetSlotGridPos", typeof(Inventory), typeof(int));
            azuAddedRows = Static(api, "GetAddedRows", typeof(int));
            azuQuickItems = Static(api, "GetQuickSlotsItems");
            if (azuQuickItems == null) {
                Logger.LogWarning("[AutoStore] AzuExtendedPlayerInventory found without GetQuickSlotsItems; its quick slots are always kept.");
            }
            return true;
        }

        private static bool TryBindExtraSlots() {
            Type api = PluginType(ExtraSlotsGUID, "ExtraSlots.API");
            if (api == null) { return false; }
            extraHeightPlayer = Static(api, "GetInventoryHeightPlayer");
            // Every slot category that is not equipment. Custom slots other mods register are left out on
            // purpose: they are equipment-like more often than not.
            foreach (string name in new[] { "GetQuickSlotsItems", "GetAmmoSlotsItems", "GetFoodSlotsItems", "GetMiscSlotsItems" }) {
                MethodInfo method = Static(api, name);
                if (method != null) {
                    ExtraSlotItemLists.Add(method);
                } else {
                    Logger.LogWarning($"[AutoStore] ExtraSlots found without {name}; those slots are always kept.");
                }
            }
            return true;
        }
    }
}
