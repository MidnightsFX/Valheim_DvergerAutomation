# DvergerAutomation

Dverger Automation is about making your experience through the game less full of friction, without giving you a mobile automated vacuum.

The goal of this mod is to add progressive automation for repetative tasks. Some of the automation will be expensive.
But you will feel like you earned each improvement.

## Features

### Autosorter - Craft, Build and store

![](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_Autosorter.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Forge** (Rrequirements are all configurable):

| Material | Amount |
|---|---|
| Stone | 20 |
| Bronze | 8 |
| Greydwarf eye | 20 |
| Ectoplasm | 4 |

The autosorter can be upgraded with Surtling cores to increase its range. It has 4 core slots, and configurably requires at least 1 core to work.
There is a front hopper on the machine that allows depositing items for it to store into chests. Items not sorted will be left in the inventory.
Its **Deposit Selected** button moves your pack straight in. The **[C]** button beside it picks which kinds of item go, and whether your hotbar and
quick slots (EquipmentAndQuickSlots, AzuExtendedPlayerInventory or ExtraSlots) are included. Equipped gear always stays on you.

Carts and boats in range count as storage too, so you can craft straight out of the cart you just pulled up to the workshop. 
They are only ever crafted from, never sorted into, and each can be turned off (`Craft From Carts`, `Craft From Boats`).

### Furnace Hopper - Automate your Furnanaces and Kilns

![](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_ForgeHopper.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Forge** (Rrequirements are all configurable):

| Material | Amount |
|---|---|
| Stone | 30 |
| Iron | 12 |
| Bronze | 8 |
| Coal | 20 |

The furnace hopper allows you to automatically process wood, and metals in nearby charcoal kilns, smelters and blast furnaces. It has a relatively short range.
Other processing stations (windmills, spinning wheels, eitr refineries and modded ones) are left alone unless you turn them on: the **[C]** button beside its deposit button
shows every type of station in range, and a click switches each one on or off for that hopper.
But can be upgraded with up to 6 surtling cores. Configurably 1 core is required for it to turn on. Each core increase processing speed of managed furnaces/kilns.
Bars and coal go into the hopper as soon as they are made. In multiplayer, the game running the hopper also takes over the stations it feeds, so every one of them keeps going.

### Lighter - Keep every fire burning

![](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_Lighter.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Forge** (requirements are all configurable):

| Material | Amount |
|---|---|
| Silver | 10 |
| Wood | 5 |
| Coal | 40 |
| Iron | 5 |

Three dverger fuel tanks around a vented column, with a hatch on a wooden stand. Fill its tank through the hatch (or by using any part
of the machine) with whatever your fires burn, and it keeps everything around it topped up: campfires, hearths, bonfires, braziers,
standing and wall torches, ovens and hot tubs, each with its own fuel - wood, resin, coal, greydwarf eyes or guck.

- **Range:** 64 metres by default.
- **Emptiest first:** when the tank runs short, the pieces closest to going out are filled first.
- **Never overfills:** each piece gets only what you could add by hand, and only on the game running both the Lighter and that piece,
  so two players can never top up the same fire at once. In multiplayer the Lighter takes over the pieces it fills, as the Furnace Hopper
  does with its stations.
- Hover it to see how many pieces it is tending, and which fuels it is missing. Fires the world placed itself and fires that are
  switched off are left alone.
- The Autosorter never files anything into its tank, but crafting from storage can draw from it. Vanilla's Place stacks tops the tank up.

Its settings are under `Dverger Lighter`: range, seconds between passes, tank rows, and a list of pieces it should never fuel
(for example `piece_oven` or `piece_bathtub`).

### Seeking Table - Find and fetch from any chest

![](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_SeekingTable.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Black Forge** (requirements are all configurable):

| Material | Amount |
|---|---|
| Black marble | 20 |
| Yggdrasil wood | 10 |
| Copper | 10 |
| Refined eitr | 10 |

A dverger table holding a blue galdr orb between four pillars. Use it and your inventory opens with every item in the chests
around it laid out like one big chest: everything that stacks is added up, and gear is shown piece by piece.

- **Search** by name or by words in an item's description. With Epic Loot installed, start with `@` to search enchantments,
  exactly as on the enchanting table: `@attack speed, fire` finds gear carrying both.
- **Filter** by item type with the icons down the right-hand side.
- **Take** an item with a click (one stack), shift-click (choose how many) or ctrl-click (everything that fits).
  On a controller: A or X for a stack, LT+A to choose, LT+X for everything, and click the left stick to search.

A brief blue line runs from the orb to each chest an item came out of. Each of the four pillars takes a **Black Core**, and every core
reaches further. Boats, carts and Furnace Hopper storage in range are listed too; each can be turned off.

### Grinder - Break down what you no longer need

![](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_Grinder.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Black Forge** (requirements are all configurable):

| Material | Amount |
|---|---|
| Grausten | 20 |
| Flametal | 10 |
| Ashwood | 10 |
| Charred cogwheel | 2 |

A brutal dverger crusher: a spiked feed hopper over two spiked rollers, driven by a flywheel, on an iron machine body fed by a tank
of molten lava. Anything with a crafting recipe can be fed to it: old gear, spare arrows, surplus food. It works through its queue
one item at a time and returns part of what each one cost to make into the results chest behind its front hatch.

- **Feeding:** use the hopper to choose from what you carry that it will take. The menu stays open while there is room in the queue.
  A hotbar key feeds plain materials straight in, and Alt + Use takes the last item queued back out.
- **Returns:** 20% of the crafting cost by default, upgrades included, so a sword at quality 3 returns part of everything spent making
  and upgrading it. Fractions carry over from one item to the next, so nothing is lost to rounding.
- **Molten Cores:** each of the ten sockets in its power panel adds 2% to the return and 10% to the speed.
- Equipped items, items a trader sells and (by default) enchanted items are refused. The Autosorter never files anything into the
  results chest, but crafting from storage can draw from it.

Its settings are under `Dverger Grinder`: base yield, yield and speed per core, seconds per item, queue size, results chest rows,
enchanted items and a blacklist.

### Respects locks and wards

A chest is only linked if you could open it yourself.

A chest that another player has open stays theirs - it is never pulled out from under them. Crafting
and building still count what is in it and can spend from it: the player who has it open takes the
materials out on your behalf and simply sees them leave. The same goes for a boat with someone aboard
and a cart someone is pulling. Materials that were fetched but not used go back to the chest they
came from.

Auto-store files into an open chest the same way: the player who has it open puts the items in, and
anything that no longer fits comes back to the deposit box.

The Seeking Table takes from an open chest the same way: the player who has it open hands the item over.

The Furnace Hopper and the Epic Loot enchanting table are more careful: they leave an open chest
alone until it is closed.

Crafting takes from a Furnace Hopper's storage the same way, through the player running the hopper,
so the hopper never leaves its smelters to hand you its bars.

Auto-store is stricter still: it only ever files items into public chests, so your own Private
chests can feed crafting but never receive sorted goods.

### Shared chests

Several players can have the same chest, boat hold or cart open at once and move items in and out of
it together. The first player to open it works exactly as in vanilla. Everyone who joins after has
their moves carried out by that player's game, so there is only ever one copy of the chest being
written. Your own moves show straight away and settle a moment later.

- The Autosorter's deposit box, the Furnace Hopper's storage, the Grinder's results chest and the
  Lighter's fuel tank stay one player at a time, because sorting, feeding, grinding and refuelling are
  done by whoever has them open.
- Buttons other mods add to the chest screen (quick stack, sort) act for the player who opened the
  chest first. For anyone who joined after they do nothing, rather than risk losing or duplicating items.
- The server setting `Enabled` under `Shared Chests` turns it off.

Credit to [MultiUserChest](https://github.com/MSchmoecker/No-Chest-Block) by MSchmoecker, which
showed that a chest could be shared this way and was the reference for how one should behave. The
version here is written separately and works differently underneath: it runs on the same owner
requests as crafting from storage, notices when the player holding a chest has left and puts back
whatever was on its way, and refuses a move it cannot carry out safely instead of letting it through.

### MultiUserChest support (optional)

If MultiUserChest is installed it takes over shared chests and this mod's own version steps aside.
Crafting and building still draw from a chest however many players have it open, and the Autosorter's
deposit box, the Furnace Hopper's storage, the Grinder's results chest and the Lighter's fuel tank still
stay one player at a time.

### Epic Loot support (optional)

If [Epic Loot](https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/) is installed, DvergerAutomation
registers itself with it properly rather than patching around it:

- **The enchanting table draws from linked chests.** Runestones, shards and dust stay in storage
  instead of your inventory.
- **Gear in linked chests can be worked on in place.** Enchanting, augmenting, etching or disenchanting
  an item stored in a linked chest is saved back to that chest, so it does not revert.
- **Auto-store leaves magic items alone** by default, for the same reason: a legendary would
  otherwise be filed into whatever chest holds the ordinary version. It stays in the deposit box instead.
  Turn on `Sort Magic Items` if you would rather have them stored.
- **The Seeking Table lists magic items one by one**, with their rarity backgrounds, and its search
  finds them by enchantment when the query starts with `@`.

Epic Loot is a soft dependency: not having it installed changes nothing.

## Installation (manual)

1. Install [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) and
   [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/).
2. Drop `DvergerAutomation.dll` into `<Valheim>/BepInEx/plugins/`.

**Multiplayer:** every player on the server needs the mod, and versions must match on major and minor.
The Autosorter's settings are server-authoritative and only admins can change them.
