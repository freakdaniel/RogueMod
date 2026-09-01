# RogueMod.LuaMod

Sample Lua mod is a Lua mod for Deadzone: Rogue.

RogueMod owns the package around the script: it validates `mod.json`, stores the package under `<GameRoot>/Mods/sample.lua-mod`, and deploys it to UE4SS under the loader id `SampleLua`. The Unreal object model, hooks, and utility globals come from the UE4SS Lua API — see https://ue4ss.org/docs for the API reference.

## Edit the mod

- `Scripts/main.lua` — the mod entry point, executed by UE4SS when the game loads.
- `mod.json` — package manifest. `loaderId` is the immutable UE4SS directory name; changing it after installation desynchronizes the deployment.

## Install

```powershell
roguemod install --game 'E:\Steam\steamapps\common\Deadzone Rogue' --package . --replace
roguemod list --game 'E:\Steam\steamapps\common\Deadzone Rogue'
```

Lua packages deploy transactionally to `ue4ss/Mods/SampleLua` and are activated through `mods.txt`. `roguemod disable` and `roguemod enable` toggle that activation line without touching the canonical package.
