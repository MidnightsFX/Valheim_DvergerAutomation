namespace DvergerAutomation {
    /// <summary>
    /// Tells apart the cells of the player's inventory that belong to an equipment/quick-slot mod from
    /// the ones that are actually the player's pack, so "deposit everything" never empties a slot the
    /// player is carrying something in on purpose.
    ///
    /// Vanilla Valheim has no quick slots, so this only ever has anything to say when
    /// EquipmentAndQuickSlots is installed. That mod does not keep its slots in a separate inventory -
    /// it grows <c>Player.m_inventory.m_height</c> and lays the equipment, quick and custom slots out in
    /// the rows past the visible ones, in the same grid. Without this test those items read as ordinary
    /// backpack contents.
    ///
    /// Bound through the vendored reflection shim in common/EquipmentAndQuickSlotsAPI, so with the mod
    /// absent every call here is a cheap "no".
    /// </summary>
    internal static class EquipmentSlotsIntegration {
        // Resolved once: assemblies do not come and go, and this is asked per item per button press.
        private static bool? loaded;

        /// <summary>True once EquipmentAndQuickSlots is present and its API answers.</summary>
        internal static bool Active {
            get {
                if (!loaded.HasValue) {
                    loaded = EquipmentAndQuickSlotsAPI.EAQS.IsLoaded();
                    Logger.LogDebug(loaded.Value
                        ? "[AutoStore] EquipmentAndQuickSlots present; its slots are excluded from Deposit All."
                        : "[AutoStore] EquipmentAndQuickSlots not present; the whole inventory grid is the player's pack.");
                }
                return loaded.Value;
            }
        }

        /// <summary>
        /// True when this grid cell is an equipment, quick or custom slot rather than pack space.
        ///
        /// Two tests, because either alone has a gap. The row test is the structural one: the mod lays
        /// every slot out at <c>y &gt;= VisibleRows</c> (visible rows being the vanilla four plus whatever
        /// a backpack mod added), so it catches the whole slot region including the padding cells that
        /// <c>IsSlotCell</c> deliberately reports as "not a slot". The cell test then covers a slot
        /// placed inside the visible region, which the layout does not do today but the API allows.
        ///
        /// Both endpoints fail soft in the shim - a missing one returns the vanilla default of 4 rows
        /// and false respectively, which errs towards depositing less, never towards emptying a slot.
        /// </summary>
        internal static bool IsReservedSlot(Vector2i gridPos) {
            if (!Active) { return false; }
            if (gridPos.y >= EquipmentAndQuickSlotsAPI.EAQS.GetVisibleRows()) { return true; }
            return EquipmentAndQuickSlotsAPI.EAQS.IsSlotCell(gridPos.x, gridPos.y, out _);
        }
    }
}
