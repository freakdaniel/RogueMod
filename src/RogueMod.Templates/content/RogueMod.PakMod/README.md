# RogueMod.PakMod

Sample pak mod is an Unreal pak mod for Deadzone: Rogue. The project directory itself is the installable package: `roguemod install --package .` works once the payload is packed.

## Layout

- `mod.json` — package manifest (`kind: "pak"`). `entryPoint` points at the packed payload in `paks/`.
- `content/` — create this directory and put the files exactly as they should appear inside the pak. For Deadzone: Rogue that is normally a `Valhalla/Content/...` tree.
- `paks/` — build output, ignored by git.

## Pack the payload

With repak (cross-platform):

```powershell
repak pak content paks/sample.pak-mod.pak
```

Alternatively use UnrealPak from the matching Unreal Engine install and its `-create` response file syntax; the result must be a plain `.pak` in `paks/`.

If the payload uses IoStore, place the `.utoc`, `.ucas`, and `.sig` files beside the `.pak` with the same base name. RogueMod deploys them together and removes them again on disable or uninstall.

## Install

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package . --replace
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
```

The canonical package is stored under `<GameRoot>/Mods/sample.pak-mod`; the payload is deployed to the profile pak directory `Valhalla/Content/Paks/~mods` with a stable package-id hash in the file name. `roguemod disable` and `roguemod enable` remove and recreate those deployed files without touching the canonical package.
