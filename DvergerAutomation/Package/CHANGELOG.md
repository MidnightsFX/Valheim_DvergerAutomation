**0.9.0**

- Carts and boats (Karve, Longship, Drakkar) within an Autosorter's range now count as storage for
  crafting and Hammer building. They are relinked every couple of seconds, so a cart counts as soon
  as it is parked and stops counting when it leaves.
	- A boat with someone else aboard, or a cart someone else is pulling or riding, is left alone
	- Auto-store never stores items into a boat or cart
	- New server settings `Craft From Boats` and `Craft From Carts`, both on by default
- Fixed: with ZenUI's crafting panel enabled, the craft-from-storage switch sat too low, off its
  plate and overlapping the repair button. It now stacks above the repair button wherever a UI mod
  puts it.

**0.8.0**

- New piece: the **Dverger Hopper**
	- Dverger Hopper is an iron age improvement that allows automating Furnances & Kilns
	- It automatically fills machines in its radius (expandable with surtling cores) and provides a small speedup
	- Its inventory will pull back in smelted ores, making them immediately available for crafting from any nearby autosorter
	- Fully configurable, range, speed bonus, pice requirements, default scan rate
- Craft-from-storage switch: A GUI button to enable/disable craft from containers
- Deposit All button, which takes over the slot vanilla's *Place stacks* button sits in whenever one of the mod's own containers is open
	- On the Autosorter it deposits your inventory and files it away, leaving equipped gear, the hotbar and quickslots alone
	- Which item types it holds back is now a config list, defaulting to gear and ammo (this replaces the old `Deposit All Keeps Ammo` switch)
	- On the Dverger Hopper it becomes **Deposit Materials** and moves in wood and ore, from a config list of prefabs
	- Ordinary chests keep *Place stacks* exactly as before
- Fixed: an Autosorter placed on a wooden floor lost its support and broke. It is now an iron piece,
  so wood holds it up.

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
