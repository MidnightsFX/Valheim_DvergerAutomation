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

### Respects locks and wards

A chest is only linked if you could open it yourself.

A chest that another player has open stays theirs - it is never pulled out from under them. Crafting
and building still count what is in it and can spend from it: the player who has it open takes the
materials out on your behalf and simply sees them leave. The same goes for a boat with someone aboard
and a cart someone is pulling. Materials that were fetched but not used go back to the chest they
came from.

Auto-store files into an open chest the same way: the player who has it open puts the items in, and
anything that no longer fits comes back to the deposit box.

The Furnace Hopper and the Epic Loot enchanting table are more careful: they leave an open chest
alone until it is closed.

Auto-store is stricter still: it only ever files items into public chests, so your own Private
chests can feed crafting but never receive sorted goods.

### Shared chests

Several players can have the same chest, boat hold or cart open at once and move items in and out of
it together. The first player to open it works exactly as in vanilla. Everyone who joins after has
their moves carried out by that player's game, so there is only ever one copy of the chest being
written. Your own moves show straight away and settle a moment later.

- The Autosorter's deposit box and the Furnace Hopper's storage stay one player at a time, because
  sorting and feeding are done by whoever has them open.
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
deposit box and the Furnace Hopper's storage still stay one player at a time.

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

Epic Loot is a soft dependency: not having it installed changes nothing.

## Installation (manual)

1. Install [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) and
   [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/).
2. Drop `DvergerAutomation.dll` into `<Valheim>/BepInEx/plugins/`.

**Multiplayer:** every player on the server needs the mod, and versions must match on major and minor.
The Autosorter's settings are server-authoritative and only admins can change them.
