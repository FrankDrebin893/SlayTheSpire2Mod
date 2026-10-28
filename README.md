# Slay the Spire 2 mods

Built against game version 0.111.0. Each mod lives in its own folder under `mods/` with its own README, art and Workshop metadata.

| Mod | What it does |
| --- | --- |
| [Seed Search](mods/SeedSearch/) | Find seeds by Neow offers, bosses, events, fights, relic order, maps and the first card reward. A Rust engine searches millions of seeds per second. |
| [Card Balance](mods/CardBalance/) | Small card balance changes (Master Planner costs 1, 0 upgraded). |

## Repository layout

```
build/      shared build files (game path discovery, art generator)
mods/       one self-contained folder per mod
```

New mods copy `mods/CardBalance/` as a template. See [CLAUDE.md](CLAUDE.md) for build, release and internals.

## Build

```
dotnet build mods/SeedSearch -c Release
dotnet build mods/CardBalance -c Release
```

Requires the game installed (the game path is found by `build/Sts2PathDiscovery.props`; override with `/p:Sts2Path=...`). Mods are copied into `<Slay the Spire 2>/mods/<id>/`. Restart the game to load them.

## Publish to the Steam Workshop

Uses Mega Crit's [ModUploader](https://github.com/megacrit/sts2-mod-uploader/releases), unzipped to `tools/ModUploader/` (git-ignored). Steam must be running.

1. Bump `version` in the mod's manifest `.json` and build.
2. Copy the built `.dll` and `.json` (plus `sts2_seed_engine.dll` for Seed Search; never the `.pdb`) into `mods/<Name>/workshop/content/`.
3. Edit `mods/<Name>/workshop/workshop.json` (`changeNote`, `visibility`) and `image.png` (under 1 MB).
4. `tools/ModUploader/ModUploader.exe upload -w mods/<Name>/workshop`

The first upload writes `mod_id.txt`; later uploads update that item. Card Balance has not been uploaded yet.

## License

Copyright (c) 2026 Rasmus Hoejte. All rights reserved; see [LICENSE](LICENSE). Unofficial fan-made mod, not affiliated with Mega Crit.
