# Vendored: Epic Loot API

Do not edit these files. They are a verbatim copy of the upstream Epic Loot API shim, kept
here so DvergerAutomation can talk to Epic Loot without a hard assembly reference or a
Harmony patch on its internals. Everything is reflection-bound, so a missing (or too old)
Epic Loot degrades to a logged warning and a no-op rather than a crash.

| | |
|---|---|
| Upstream | `RandyKnapp/ValheimMods` -> `EpicLootAPI/EpicLootAPI/src` |
| Commit    | `9083e916113c6213880b2ec4b199fd8e93d6da15` (2026-09-09) |
| Local checkout | `Valheim_Stuff/Randy_Vapok_ValheimMods` |

`README.md` in this folder is upstream's, and documents the whole API surface. DvergerAutomation
only uses a small part of it - see `modules/CraftFromStorage/EpicLootIntegration.cs`.

## Refreshing

Recopy `src/*.cs` over this folder and rebuild. Two project-level settings exist for this
code's benefit and must stay:

- `<Nullable>annotations</Nullable>` - upstream builds under `Nullable=enable` and uses `?`
  annotations throughout. `annotations` makes that syntax legal without turning null-analysis
  warnings on for the rest of the mod.
- `JetBrains.Annotations` PackageReference - upstream marks its public surface `[PublicAPI]`.
