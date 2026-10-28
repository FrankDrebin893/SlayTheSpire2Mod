<p align="center"><img src="art/banner.png" alt="Seed Search"></p>

# Seed Search

Find the seed you want before the run starts. A **Search Seed** button on the Custom Run screen (singleplayer) opens a full-screen search where you pick what you want from the game's own icons.

## Features

- **Filters**: Neow's offers and what they give when taken, the act, its boss and ancient, the order of events, fights and elites, the order relics come in, elites/rests/shops/unknowns on the act maps, and the first card reward and potion. Only filters a seed can actually produce are offered, and contradicting ones are refused.
- **Preview** any seed: Neow's offers, the first fight reward, every act's boss, ancient, events and fights, the relic bags, and the maps.
- **Keep what you find**: bookmark seeds with a note; recent searches (last 12) and kept searches are saved. One click puts a seed into the Custom Run screen.
- **Fast**: a Rust reimplementation of the game's start-of-run generation searches millions of seeds per second on all cores (Windows x64). Elsewhere, or if the engine ever disagrees with the game, the mod falls back to a slower search that uses the game's own code.

Saved data lives in `%APPDATA%/SlayTheSpire2/RasmusSlayTheSpire2Mod/seed_search.json`.

## Layout

```
RasmusSlayTheSpire2Mod.csproj / .json   project and mod manifest (id kept from v1 so the Workshop item stays the same)
MainFile.cs                              entry point, applies the Harmony patches
src/                                     seed search UI, simulator, native engine bindings
seed-engine/                             Rust crate built to sts2_seed_engine.dll
workshop/                                Workshop item metadata (title, description, image, mod_id.txt)
art/                                     icon and banner
```

See the repository `CLAUDE.md` for how the engine and the game code are kept in sync.

## Build

```
dotnet build mods/SeedSearch -c Release
```

On Windows this first runs `cargo build --release` in `seed-engine/` (needs Rust; skip with `/p:SkipRustBuild=true`), then copies the `.dll`, `.pdb`, `.json` and `sts2_seed_engine.dll` to `<Slay the Spire 2>/mods/RasmusSlayTheSpire2Mod/`. Offline engine tests: `cargo test --release --manifest-path mods/SeedSearch/seed-engine/Cargo.toml`.

Built against game version 0.111.0. Unofficial fan-made mod, not affiliated with Mega Crit.

## Upgrading from v1.x

The card tweaks that used to ship in this mod (Master Planner) moved to the separate [Card Balance](../CardBalance/) mod.
