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



### Furnace Hopper - Automate your Furnanaces and Kilns

![](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_ForgeHopper.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Forge** (Rrequirements are all configurable):

| Material | Amount |
|---|---|
| Stone | 30 |
| Iron | 12 |
| Bronze | 8 |
| Coal | 20 |

The furnace hopper allows you to automatically process wood, and metals in nearby kilns and furnances. It has a relatively short range.
But can be upgraded with up to 6 surtling cores. Configurably 1 core is required for it to turn on. Each core increase processing speed of managed furnaces/kilns.

### Respects locks and wards

A chest is only linked if you could open it yourself.

A chest that someone - you or another player - currently has open is left alone until it is closed:
its contents are not counted, shown, or spent while it is open, so nobody's chest panel is pulled out
from under them.

Auto-store is stricter still: it only ever files items into public chests, so your own Private
chests can feed crafting but never receive sorted goods.

### Epic Loot support (optional)

If [Epic Loot](https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/) is installed, DvergerAutomation
registers itself with it properly rather than patching around it:

- **The enchanting table draws from linked chests.** Runestones, shards and dust stay in storage
  instead of your inventory.
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
