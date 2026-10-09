# Slay the Spire 2 mods

Built against game version 0.111.0.

## Mods

- `RasmusSlayTheSpire2Mod/` – general mod for card tweaks. Code-only (Harmony), no BaseLib, no `.pck`.
  - Master Planner costs 1 energy instead of 2 (0 upgraded).
  - Seed search: a Search Seed button on the Custom Run screen (singleplayer) opens a screen that finds seeds by Neow offers, acts, bosses, ancients, event and fight order, relic order, map contents and the first fight reward, and previews any seed. Seeds can be bookmarked with a note, and searches are remembered (the last 12, plus any you keep) in `%APPDATA%/SlayTheSpire2/RasmusSlayTheSpire2Mod/seed_search.json`. The search runs in `seed-engine/`, a Rust reimplementation of the game's start-of-run generation (Windows x64; other platforms fall back to a slow search using the game's own code). The mod checks the engine against the game each time and falls back if they disagree.

## Build and test

```
dotnet build RasmusSlayTheSpire2Mod -c Release
```

On Windows the build also runs `cargo build --release` in `seed-engine/` (needs Rust; skip with `/p:SkipRustBuild=true`). It copies the `.dll`, `.pdb`, `.json` and `sts2_seed_engine.dll` to `<Slay the Spire 2>/mods/RasmusSlayTheSpire2Mod/`. The game path is found by `Sts2PathDiscovery.props`; override with `/p:Sts2Path=...` if needed.

Restart the game, check Settings > Mod Settings, then look up the card in the Card Library. Logs are in `%APPDATA%/SlayTheSpire2/logs/godot.log`; the mod logs one line when the patch applies. The dev console (`~`, then `help card`) can spawn cards in a run.

## Publish to the Steam Workshop

Uses Mega Crit's [ModUploader](https://github.com/megacrit/sts2-mod-uploader/releases), unzipped to `tools/ModUploader/` (git-ignored). Steam must be running.

1. Bump `version` in the mod's `.json` and build.
2. Copy the `.dll`, the `.json` and `sts2_seed_engine.dll` (not the `.pdb`) into `workshop/<Mod>/content/`.
3. Edit `workshop/<Mod>/workshop.json` (`changeNote`, `visibility`: `private`, `friends_only`, `unlisted` or `public`) and replace `image.png` (under 1 MB).
4. Run `tools/ModUploader/ModUploader.exe upload -w workshop/<Mod>`.

The first upload writes `mod_id.txt` into the workspace; later uploads update that same item.

## License

Copyright (c) 2026 Rasmus Hoejte. All rights reserved; see [LICENSE](LICENSE). Unofficial fan-made mod, not affiliated with Mega Crit.

## Reading game code

```
ilspycmd "<Slay the Spire 2>/data_sts2_windows_x86_64/sts2.dll" -t MegaCrit.Sts2.Core.Models.Cards.MasterPlanner
```

Dump into `decompiled/` (git-ignored). Cards live in `MegaCrit.Sts2.Core.Models.Cards`.
