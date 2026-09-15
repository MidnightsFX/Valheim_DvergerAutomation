**0.7.1**

- Fixed: gear whose recipe has a Valheim 1.0 upgrader ingredient could not be crafted or upgraded
  from linked chests - the craft button stayed greyed out even though the chests held everything
  the panel listed.
- Fixed: crafting that gear at a regular station could spend its upgrader ingredient out of a
  linked chest, although the station never charges it.

**0.7.0**

- Ingredient rows in the crafting panel and the build HUD now show how many of each material the
  linked chests hold, as a green `+N` after the required amount. Hovering the row spells it out.
  The figure counts only what the Autosorter would actually spend: enchanted gear and anything in
  the deposit box are left out. Turn it off with the new client-side `Show Storage Counts` setting.
- Fixed: mods that print your own inventory count next to an ingredient (MyLittleUI) were shown
  the inventory-plus-chests total instead. Chest stock now only appears in the `+N` figure.
- Fixed: auto-store could duplicate the leftovers it handed back out of the deposit box. Anything
  with no matching chest now stays in the box instead, and is tried again every time it is closed.
- Fixed: crafting, building and enchanting from storage could take over a chest another player had
  open, blanking their chest panel and leaving their view of that chest out of date. Open chests are
  now skipped until they are closed.

**0.6.0**

- Auto-store: the Autosorter now has a deposit box. Drop goods in, close it, and each stack is
  filed into the chests in range that already hold that item - splitting across chests as needed.
  Anything with no home comes back to you. Only public chests are filled; Private chests are never
  targeted, even your own.
- New settings: `Auto Store` (on), `Sort Magic Items` (off), and `Deposit Box Width` /
  `Deposit Box Height` (default 8x4, the size of your own inventory; minimum 2x2).
- Fixed: taking materials out of linked chests while crafting left the per-frame item counts stale
  for the rest of the frame.

**0.5.0**

- Beta release.
