# Slay the Spire 2 mods

Built against game version 0.111.0.

## Mods

- `RasmusSlayTheSpire2Mod/` – general mod for card tweaks. Code-only (Harmony), no BaseLib, no `.pck`.
  - Master Planner costs 1 energy instead of 2 (0 upgraded).

## Build and test

```
dotnet build RasmusSlayTheSpire2Mod -c Release
```

The build copies the `.dll`, `.pdb` and `.json` to `<Slay the Spire 2>/mods/RasmusSlayTheSpire2Mod/`. The game path is found by `Sts2PathDiscovery.props`; override with `/p:Sts2Path=...` if needed.

Restart the game, check Settings > Mod Settings, then look up the card in the Card Library. Logs are in `%APPDATA%/SlayTheSpire2/logs/godot.log`; the mod logs one line when the patch applies. The dev console (`~`, then `help card`) can spawn cards in a run.

## Publish to the Steam Workshop

Uses Mega Crit's [ModUploader](https://github.com/megacrit/sts2-mod-uploader/releases), unzipped to `tools/ModUploader/` (git-ignored). Steam must be running.

1. Bump `version` in the mod's `.json` and build.
2. Copy the `.dll` and `.json` (not the `.pdb`) into `workshop/<Mod>/content/`.
3. Edit `workshop/<Mod>/workshop.json` (`changeNote`, `visibility`: `private`, `friends_only`, `unlisted` or `public`) and replace `image.png` (under 1 MB).
4. Run `tools/ModUploader/ModUploader.exe upload -w workshop/<Mod>`.

The first upload writes `mod_id.txt` into the workspace; later uploads update that same item.

## Reading game code

```
ilspycmd "<Slay the Spire 2>/data_sts2_windows_x86_64/sts2.dll" -t MegaCrit.Sts2.Core.Models.Cards.MasterPlanner
```

Dump into `decompiled/` (git-ignored). Cards live in `MegaCrit.Sts2.Core.Models.Cards`.
