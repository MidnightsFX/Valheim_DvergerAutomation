# DvergerAutomation

Build a **Dverger Autosorter** and the chests around it become one shared material pool. Craft at any
workbench it links, build with the Hammer anywhere in its range, and the ingredients come straight out
of your storage instead of your backpack.

It works in both directions: dump a haul into the sorter's deposit box and it files each stack
into the chest that already holds that item.

No more hauling stacks of wood and iron out of chests before every build session, and no more
hand-sorting them back in afterwards.

## Features

### Craft and build from nearby storage

The Autosorter periodically scans its surroundings and links two things:

- **Crafting stations** in range. Standing at a linked station, recipes treat the linked chests as
  part of your inventory: a recipe is craftable when your pack and the chests together cover it.
- **Storage chests** in range. Building with the Hammer anywhere inside the Autosorter's radius pulls
  materials from those same chests.

Each ingredient row shows what storage can contribute: a green **`+N`** after the required amount is
how many of that material the linked chests hold, and hovering the row spells it out. The amount
only flashes red when your inventory and the chests together still fall short. The figure counts
only what the Autosorter would actually spend - enchanted gear and anything sitting in the deposit
box are left out. Turn it off with `Show Storage Counts` if you would rather keep the vanilla panel.

Your own inventory is always spent first; only the shortfall is drawn from storage.

### Auto-store: dump a haul, walk away

The Autosorter has a **deposit box** on its front face. Open it, drop in whatever you came home
with, and close it - each stack is filed into the chests in range that **already hold that item**.

- One stack **splits across chests**: 40 wood will top up a nearly-full wood chest and put the
  rest in the next one.
- A chest only qualifies if it already contains that exact item, so nothing ever lands somewhere
  you did not already decide it belongs. Empty chests are never filled.
- Anything with no home **stays in the deposit box**. Take it out yourself, or leave it there: it is
  tried again every time the box is closed, so it files itself away once a chest for it exists.
- Only **public** chests receive items. A Private chest is never filled, not even one you placed
  yourself - locking a chest is a decision about what goes in it.
- Chests another player currently has open are skipped.
- The box is the size of your own inventory by default, so a full backpack fits in one trip.
  Admins can resize it; items already inside are never lost to a smaller size - the box keeps
  room for them until they are sorted out.

Items sitting in the deposit box are deliberately **not** counted as crafting stock - the box is an
inbox, not storage, and anything in it is either about to be filed away or waiting for a home.

### Surtling Core slots

The Autosorter has **four Surtling Core slots**. Interact with a slot to insert a core, or to take one
back out. Cores are stored on the piece itself, replicate to other players, survive reloads, and drop
on the ground if the Autosorter is destroyed or deconstructed - they are never lost.

By default the Autosorter stays dormant until at least one core is inserted, and **each core extends
the link radius** (+25m by default, on top of the 20m base). Four cores reach 120m. Both the
requirement and the per-core bonus are configurable.

### Respects locks and wards

A chest is only linked if you could open it yourself. A chest set to **Private** is linked only for
the player who placed it, a chest set to **Group** is skipped entirely, and any chest inside a ward
you are not permitted in is skipped. Your own ward is fine.

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
- **Enchanted gear is protected.** Vanilla ingredient consumption matches items by name, and an
  enchanted item shares its name with the ordinary version - so without this, a recipe could quietly
  consume a legendary sitting in one of your chests. DvergerAutomation refuses to spend magic items
  as plain crafting material, and does not count them as such either.
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

## Building the Autosorter

![Dverger Autosorter](https://github.com/MidnightsFX/Valheim_DvergerAutomation/blob/master/DvergerAutomationUnity/Assets/PrefabIcons/DA_Autosorter.png?raw=true)

Built with the Hammer, in the **Crafting** category, near a **Forge**:

| Material | Amount |
|---|---|
| Stone | 20 |
| Bronze | 8 |
| Greydwarf eye | 20 |
| Ectoplasm | 4 |

All costs are refunded on deconstruction.

## Configuration

Settings live under `BepInEx/config/MidngightsFX.DvergerAutomation.cfg`.

| Setting | Default | Range | What it does |
|---|---|---|---|
| `Enabled` | `true` | | Master switch for the Autosorter. |
| `Scan Radius` | `20` | 1-64 | Base radius (meters) in which stations and chests are linked. |
| `Range Per Core` | `25` | 0-100 | Extra radius added per inserted Surtling Core. |
| `Require Cores` | `true` | | When on, the Autosorter links nothing until a core is inserted. |
| `Auto Store` | `true` | | Closing the deposit box files its contents into chests that already hold the same item. |
| `Sort Magic Items` | `false` | | When on, enchanted (Epic Loot) items are filed away too instead of staying in the deposit box. |
| `Deposit Box Width` | `8` | 2-8 | Columns in the deposit box. 8 is the most the container panel can show. |
| `Deposit Box Height` | `4` | 2-8 | Rows in the deposit box. Extra rows scroll. |
| `Scan Interval` | `30` | 5-300 | Seconds between scans. Lower reacts to new chests sooner and costs more. |
| `Show Storage Counts` | `true` | | Shows the green `+N` storage figure next to each ingredient. Client-side. |
| `EnableDebugMode` | `false` | | Verbose logging. Client-side, and hidden behind Advanced. |

Inserting or removing a core relinks immediately rather than waiting for the next scan.

## Known issues

- New chests placed inside the radius are not picked up by the crafting pool until the next scan
  (up to `Scan Interval` seconds). Reinserting a core forces an immediate rescan. Auto-store is not
  affected: closing the deposit box always relinks first.
- Auto-store runs when you close the deposit box. If you log out or the area unloads with items
  still inside, they stay there until you open and close it again. They are safe either way -
  destroying the Autosorter drops them on the ground along with the cores.

## Changelog

See `CHANGELOG.md`.
