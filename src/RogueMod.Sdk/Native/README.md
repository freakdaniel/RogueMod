# RogueMod native SDK

`include/UE4SS/CppUserModBase.hpp` pins the lifecycle ABI of RE-UE4SS build `a1e7f571`. `include/RogueMod/NativeMod.hpp` provides `ROGUEMOD_DEFINE_NATIVE_MOD`, which exports the entry points expected by UE4SS. `include/RogueMod/NativeLog.hpp` provides `RogueMod::NativeLog::line`, an append-only UTF-8 logger that writes beside the mod deployment.

These minimal headers are sufficient for mods that use lifecycle callbacks, logging, and Windows APIs. They do not declare Unreal types or functions. A mod that needs `UObject`, hooks, reflection, or Lua APIs must use the complete UE4SS SDK from the same commit.

The minimal sample is located at `src/RogueMod.Sample.Native`.
