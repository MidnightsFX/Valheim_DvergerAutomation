# DvergerAutomation

Build a **Dverger Autosorter** and the chests around it become one shared material pool. Craft at any
workbench it links, build with the Hammer anywhere in its range, and the ingredients come straight out
of your storage instead of your backpack.

No more hauling stacks of wood and iron out of chests before every build session.

## Features

### Craft and build from nearby storage

The Autosorter periodically scans its surroundings and links two things:

- **Crafting stations** in range. Standing at a linked station, recipes count the linked chests as if
  their contents were in your inventory - both the ingredient counts shown in the panel and whether
  the recipe is craftable at all.
- **Storage chests** in range. Building with the Hammer anywhere inside the Autosorter's radius pulls
  materials from those same chests.

Your own inventory is always spent first; only the shortfall is drawn from storage.

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

### Epic Loot support (optional)

If [Epic Loot](https://thunderstore.io/c/valheim/p/RandyKnapp/EpicLoot/) is installed, DvergerAutomation
registers itself with it properly rather than patching around it:

- **The enchanting table draws from linked chests.** Runestones, shards and dust stay in storage
  instead of your inventory.
- **Enchanted gear is protected.** Vanilla ingredient consumption matches items by name, and an
  enchanted item shares its name with the ordinary version - so without this, a recipe could quietly
  consume a legendary sitting in one of your chests. DvergerAutomation refuses to spend magic items
  as plain crafting material, and does not count them as such either.

Epic Loot is a soft dependency: not having it installed changes nothing.

## Installation (manual)

1. Install [BepInEx](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) and
   [Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/).
2. Drop `DvergerAutomation.dll` into `<Valheim>/BepInEx/plugins/`.

**Multiplayer:** every player on the server needs the mod, and versions must match on major and minor.
The Autosorter's settings are server-authoritative and only admins can change them.

## Building the Autosorter

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
| `Scan Interval` | `30` | 5-300 | Seconds between scans. Lower reacts to new chests sooner and costs more. |
| `EnableDebugMode` | `false` | | Verbose logging. Client-side, and hidden behind Advanced. |

Inserting or removing a core relinks immediately rather than waiting for the next scan.

