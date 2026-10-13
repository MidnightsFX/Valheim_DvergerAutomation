**0.12.3**
- Fixes the Seeking Table saying the inventory is full when taking items with ExtraSlots

**0.12.2**
- Improves ward ownership check compatibility with other mods


**0.12.1**
- Fixes build placement issues with the Seeking table
- Fixes Cores not showing up or allowing placement for the autosorter in multiplayer

**0.12.0**
- Adds the Dverger Seeking Table, a Mistlands machine that lists everything in the chests around it and lets you search for and take any of it, respects chest access.
	- Black Cores set into its pillars let it reach chests further away.
	- With Epic Loot installed, its search can find gear by enchantment.
	- Also lists the items lying on the ground in its range, so you can pick them up from the table.
- Adds the Dverger Grinder, an Ashlands machine that breaks crafted items down into part of what they cost.
	- Molten Cores set into the Grinder make it work faster and return more.
	- With Epic Loot installed, the Grinder breaks enchanted items down too, and can be set to return part of their enchanting materials.
- Adds the Dverger Lighter, a Mountains machine that keeps the fires, torches, ovens and hot tubs around it fuelled from its tank.
- The Dverger Hopper now collects bars and coal the moment they are made, instead of on its next pass.
- In multiplayer the Hopper now keeps every station in its range running, not just the ones that happened to run on the same player's game.
- Crafting from a Lighter's tank or a Grinder's results chest no longer moves that machine to the crafting player's game.
- Fixes a crash at crafting stations for recipes that take any one of several ingredients.
- Crafting stops drawing from chests as soon as the server switches the Autosorter off.
- Placing an Autosorter or Hopper no longer makes its preview act like a working machine.
- A mistyped building cost no longer stops the mod loading, and a piece switched off and on again can be built again.
- Crafting panels, the build menu, boats and carts, the Seeking Table and every machine do much less repeated work in large bases.

**0.11.1**
- The deposit button on the Autosorter and Hopper can now be pressed with a controller, using the same button as Place stacks.

**0.11.0**
- Several players can now have the same chest open at once and move items in and out of it together.
- Crafting and building can now use materials from a chest another player has open, and from a boat or cart someone else is using.
- Auto-store can now file items into a chest another player has open.
- Crafting and building wait a moment for materials another player is holding instead of asking you to try again.
- Works alongside MultiUserChest, which takes over shared chests when it is installed.
- Hardened chests against showing out-of-date contents right after they change hands.

**0.10.1**
- Fixed Epic Loot's enchanting table losing changes to gear stored in linked chests: enchanting, augmenting, etching or disenchanting an item there now saves it to its chest, instead of the chest undoing it a moment later after the materials were spent
	- Needs an Epic Loot with inventory provider save handlers; with an older one the log warns once, and gear is best taken out of the chest before working on it
- The Autosorter now picks up every chest in your base within moments of you arriving by portal or logging in, instead of some of them only after its next scan.

**0.10.0**
- Fixes buildable prefabs missing sounds/visual effects for damage, destruction and placement
- The Autosorter's Deposit All is now **Deposit Selected**, with a **[C]** button beside it that opens a deposit filter
	- Click item categories to choose what gets deposited: armor, utility & trinkets, shields, weapons, ammo, tools & torches, food, meads & potions, materials, trophies, fish and miscellaneous
	- Choice is user specific, and not synced to other players
- The Dverger Hopper now services only smelters, charcoal kilns and blast furnaces by default. A new **[C]** lets you configure that.
	- It shows one icon for each type of station in the hopper's range: smelters, kilns and blast furnaces, plus windmills, spinning wheels, eitr refineries and modded stations. Click an icon to turn it on or off
	- The choice is saved on each hopper, so every player sees the same setting
- Fixed the Dverger Hopper losing items on laggy multiplayer servers
- Fixed crafting, building, auto-store and Epic Loot enchanting losing or duplicating items in linked chests on laggy multiplayer servers

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
