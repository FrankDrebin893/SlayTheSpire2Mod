# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Mods for Slay the Spire 2 (Godot 4 + C#/.NET 9, Early Access). `RasmusSlayTheSpire2Mod/` is one general-purpose mod that collects card tweaks and a seed search; it is code-only (Harmony patches, plus a native Rust library for the seed search), with no BaseLib dependency and no `.pck`. Built against game version 0.111.0. There is no official modding API, so game updates can break patches.

## Commands

```
dotnet build RasmusSlayTheSpire2Mod -c Release
```

The build has side effects: on Windows it first runs `cargo build --release` in `seed-engine/` (needs a Rust toolchain; skip with `/p:SkipRustBuild=true`), then copies the `.dll`, `.pdb`, manifest `.json` and `sts2_seed_engine.dll` into `<Slay the Spire 2>/mods/RasmusSlayTheSpire2Mod/`. The copy fails while the game is running, because the game holds the dlls open. The game path is auto-detected by `Sts2PathDiscovery.props` (override with `/p:Sts2Path=...`), and the build fails if the game is not installed because the project compiles against the game's own `sts2.dll`, `0Harmony.dll` and `GodotSharp.dll` (referenced with `Private=false`, never shipped).

There are no tests or linters. Verification is manual and in game: restart the game (mods load only at startup), check Settings > Mod Settings and the Card Library, and read `%APPDATA%/SlayTheSpire2/logs/godot.log`. The dev console (`~`, then `help card`) spawns cards in a run.

Read game code with `ilspycmd` (dotnet global tool), dumping into the git-ignored `decompiled/` folder:

```
ilspycmd "<Slay the Spire 2>/data_sts2_windows_x86_64/sts2.dll" -t MegaCrit.Sts2.Core.Models.Cards.MasterPlanner
```

Publish to the Steam Workshop (Steam must be running; this publishes under the user's Steam account, so only run it when asked):

```
tools/ModUploader/ModUploader.exe upload -w workshop/RasmusSlayTheSpire2Mod
```

`tools/` is git-ignored; get ModUploader from https://github.com/megacrit/sts2-mod-uploader/releases.

## How the pieces fit

- **Manifest** (`RasmusSlayTheSpire2Mod.json`): the file name, `id`, project name and output assembly name must all match, because the game locates `<id>.dll` from it. The `id` must not change once published. `affects_gameplay` is `true`, so all multiplayer lobby members need the mod. Set `has_pck` to `true` only if a `.pck` is actually shipped.
- **Entry point** (`MainFile.cs`): `[ModInitializer(nameof(Initialize))]` makes the game call `Initialize` at mod load, which runs `Harmony.PatchAll`. Any `[HarmonyPatch]` class in the assembly is therefore picked up automatically; new tweaks need no registration.
- **Patches**: one file per changed card. Game cards live in `MegaCrit.Sts2.Core.Models.Cards` and derive from `CardModel`, whose constructor is `(canonicalEnergyCost, type, rarity, targetType, shouldShowInCardLibrary)`. The canonical cost is a get-only property set in that constructor, and `EnergyCost` is built lazily from it, so base stats are changed by transpiling the card's own constructor (see `MasterPlannerPatch.cs`) rather than by patching getters. Patches log an error when the expected IL is no longer there, so a game update fails loudly in `godot.log`.
- **Seed search** (`SeedSearch/` plus the Rust crate `seed-engine/`): a Search Seed button added next to the seed box of the Custom Run screen (singleplayer only) by a postfix on `NCustomRunScreen._Ready`. It opens `SeedSearchScreen`, a full-screen overlay on its own `CanvasLayer`. The UI uses stock Godot nodes only; do not subclass nodes in the mod assembly (so it cannot be an `NSubmenu`).
  - **Rust engine** (`seed-engine/`, built to `sts2_seed_engine.dll`, Windows x64 only): reimplements the game's RNG (`rng.rs`), start-of-run generation, Neow offers and first fight reward (`gen.rs`), and the act map generator (`map.rs`), and searches random seeds on all cores (`search.rs`). It holds algorithms only. `GameData.cs` exports everything else from `ModelDb` and the player's unlocks as a JSON snapshot of integer ids (`data.rs`), so content, unlock and localisation changes need no Rust change. What is restated by hand because it is code in the game: each act's map rolls (`GameData.WriteMapRolls`), the Neow curse/positive exclusions (`GameData.WriteNeow`), and all of `gen.rs` and `map.rs`. Bump `ABI_VERSION` (Rust) and `ExpectedAbi` (`NativeEngine.cs`) together when the snapshot, filter (`Cond.cs` / `sim.rs`) or result (`SimResult.cs` / `sim.rs`) format changes.
  - **Reference and fallback** (`SeedSimulator.cs`): builds a detached `RunState` and replays the start-of-run generation from `NGame.StartNewSingleplayerRun`, `RunManager.InitializeNewRun` and `RunManager.GenerateRooms` with the game's own code, so those copies must be re-checked against the decompiled game after an update. While it runs, `SimulationPatches.cs` redirects `RunManager.HasAscension` to the previewed ascension and blocks `SaveManager.Mark*AsSeen`. `SeedEngine` compares the Rust engine against it on 20 random seeds whenever the exported data changes; on a mismatch, or when the native library is missing (other platforms), it logs an error and the screen uses the simulator instead, on the main thread in timer slices because the game code shares static state.
  - **Checking the engine after a game update** (`DevSelfCheck.cs`): launch the game with `RASMUS_SEED_SELFCHECK=<seeds per case>` set and it compares both implementations for every character at ascension 0 and 10 on reaching the main menu, logging `Seed engine self-check: ...` lines and a seeds/s benchmark to `godot.log`. `RASMUS_SEED_FIXTURES=<folder>` also dumps the cases as golden fixtures (commit them to `seed-engine/tests/fixtures/`), and `RASMUS_SEED_QUIT=1` quits afterwards. Environment variables only reach the game when its exe is started directly, which needs a `steam_appid.txt` containing `2868840` in the game folder (remove it afterwards). `cargo test --release --manifest-path seed-engine/Cargo.toml` replays the fixtures offline; run it after any change to the Rust generation code, since `map.rs` in particular depends on reproducing .NET `HashSet` enumeration order.
- **Workshop workspace** (`workshop/RasmusSlayTheSpire2Mod/`): `workshop.json` (title, description, `visibility`, `changeNote`), `image.png` (under 1 MB) and `mod_id.txt` (the Workshop item id, written by the first upload; later uploads update that item). `content/` is git-ignored build output and must be refreshed by hand before each upload with the built `.dll` and manifest `.json` (not the `.pdb`).

## Releasing an update

1. Bump `version` in the manifest and build.
2. Copy the `.dll`, the `.json` and `sts2_seed_engine.dll` into `workshop/RasmusSlayTheSpire2Mod/content/`. Ship no other `.json` files: the mod loader reads every `.json` in a mod folder as a manifest.
3. Update `changeNote` in `workshop.json`, then run the upload command.

If a local copy in `<game>/mods/` and the subscribed Workshop copy have the same version, the game loads the local one and disables the Workshop one.

## Adding new cards (not set up yet)

New cards need localization and images packed into a `.pck`, which requires MegaDot (Mega Crit's Godot 4.5.1 build; a newer Godot produces packs the game will not load) and is normally done with BaseLib via the `Alchyr.Sts2.Templates` dotnet templates (`alchyrsts2contentmod`). See https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup and https://alchyr.github.io/BaseLib-Wiki/.
