![Card Rarity Randomizer](art/banner.png)

# Card Rarity Randomizer

A Slay the Spire 2 mod. At the start of every run the rarity of every card is shuffled, and a summary screen shows what changed. A Strike might be a Rare, and Whirlwind a Common.

## What it does

- **Shuffles inside each card pool.** Common, Uncommon and Rare cards swap rarities with the other cards of the same pool, so each character keeps exactly as many Commons, Uncommons and Rares as before. Only the identities change.
- **Leaves special cards alone.** Basic, Ancient, Curse, Status, Quest and Event cards keep their rarity.
- **Is fixed for the whole run, and repeatable.** The table comes from the run's seed, so the same seed gives the same rarities, and loading a saved run restores them. Rewards, shops and other rarity rolls then use the new rarities.
- **Shows you the result.** When the run starts, an overlay lists every card that changed, per character, with the biggest jumps (Common to Rare, Rare to Common) first. Press *Start the run* to close it.

## Install

1. Build it (below) or download it from the Steam Workshop.
2. Put `CardRarityRandomizer.dll` and `CardRarityRandomizer.json` in `<Slay the Spire 2>/mods/CardRarityRandomizer/`.
3. Restart the game and enable the mod in Settings > Mod Settings.

It changes gameplay, so everyone in a multiplayer lobby needs the mod. It has no dependencies and no `.pck`. Built for game version 0.111.0.

## Build

```
dotnet build mods/CardRarityRandomizer -c Release
```

The build copies the `.dll`, `.pdb` and `.json` into the game's `mods/CardRarityRandomizer/` folder (this fails while the game is running). The game path is found by `build/Sts2PathDiscovery.props`; override it with `/p:Sts2Path=...`.

## How it works

Code is in `src/`:

| File | Role |
| --- | --- |
| `MainFile.cs` | `[ModInitializer]` entry point |
| `RarityPatches.cs` | Harmony postfixes on `RunManager.InitializeNewRun` (builds the table) and the `CardModel.Rarity` getter (answers from the table) |
| `Randomizer.cs` | The seeded shuffle, one pool at a time (SHA-256 of seed and pool, so it is stable across processes) |
| `RarityState.cs` | The current run's table |
| `SummaryScreen.cs` | The overlay, built from stock Godot nodes on its own `CanvasLayer` |
| `GameReflection.cs` | Looks game members up by name; the one place to fix after a game update |

The game has no official modding API, so the patches find their targets by name. If a game update moves one, the mod logs an error to `%APPDATA%/SlayTheSpire2/logs/godot.log` and leaves the game unchanged.

## Status

This first version was written without access to the game's assemblies, so it has **not been compiled or run in game**. Check the following on first build:

- The member names it looks up by reflection: `RunManager.InitializeNewRun`, `ModelDb.AllCards`, `CardModel.Id`, `.Rarity`, `.Pool`, `.Title`, and where the run seed is found (`GameReflection.FindRunSeed`). Read them with `ilspycmd`; see the repository `CLAUDE.md`.
- That Harmony accepts `ref object __result` on the enum-typed `Rarity` getter. If not, patch with a typed `ref CardRarity __result` and drop the `Enum.Parse`.
- Whether the card library screen should also show the new rarities (it follows the getter).
- Seed handling for multiplayer and for saved-run reload.

Look for `Randomized N of M card rarities` in `godot.log` to confirm it ran.

Unofficial fan-made mod, not affiliated with Mega Crit.
