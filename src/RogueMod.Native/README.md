# RogueMod.Bridge ABI

RogueMod.Bridge is the only required native component for managed mods. It is intentionally small and is built as a UE4SS C++ mod for Windows x64. The same DLL is used on a Windows host and inside Proton on Linux.

The bridge deliberately uses a minimal header-only declaration of `CppUserModBase` pinned to UE4SS commit `a1e7f571`. It does not compile or link against private UEPseudo headers. Its small reflection surface dynamically resolves exact decorated exports from the installed UE4SS DLL. Regular C++ gameplay mods still use the complete official UE4SS SDK.

## Responsibilities

- Receive UE4SS lifecycle events on the Unreal game thread.
- Load hostfxr and `RogueMod.Runtime.dll`.
- Pass a versioned table of native callbacks to managed code.
- Forward UE4SS lifecycle events to managed mods on the game thread.
- Never resolve packages, dependencies or UI state.

## Versioned boundary

The bridge ABI exposes a structure with size and version fields followed by function pointers. ABI 11 exposes the game-root package directory, single and multi-object discovery, Unreal object handles, capabilities, primitive/object/`FString`/`FName`/`FText`/POD-struct/`TArray` property reads and writes, UFunction input/return/out marshalling, and ownership-safe read-only pre/post UFunction hooks:

    struct RogueModHostApi {
        uint32_t size;
        uint32_t abi_version;
        void (*log)(int level, const wchar_t* message);
        const wchar_t* mod_root;
        const wchar_t* game_profile_id;
        int32_t (*unreal_is_available)();
        uint64_t (*unreal_find_first_of)(const wchar_t* class_name);
        int32_t (*unreal_is_valid)(uint64_t handle);
        uint64_t (*unreal_get_class)(uint64_t handle);
        int32_t (*unreal_get_path_name)(uint64_t handle, wchar_t* buffer, uint32_t capacity, uint32_t* required);
        uint32_t (*unreal_get_capabilities)();
        int32_t (*unreal_invoke_zero_parameter)(uint64_t handle, const wchar_t* function_name);
        int32_t (*unreal_read_property)(uint64_t handle, const wchar_t* property_name, uint32_t kind, UnrealValue* value);
        int32_t (*unreal_write_property)(uint64_t handle, const wchar_t* property_name, uint32_t kind, const UnrealValue* value);
        int32_t (*unreal_invoke)(uint64_t handle, const wchar_t* function_name, uint32_t parameter_count, UnrealParameter* parameters);
        const wchar_t* game_mods_root;
        int32_t (*unreal_find_all_of)(const wchar_t* class_name, uint64_t* handles, uint32_t capacity, uint32_t* required);
    };

The host table carries logging and startup metadata. Reverse calls use the managed `DispatchGameEvent` entry point and the stable numeric `ModGameEventKind` values. Every call checks ABI inputs before use. Startup and input strings are UTF-16 and owned by the caller for the duration of a call. Reflection output strings, POD byte buffers, and recursive array values are allocated with `CoTaskMemAlloc`; managed code copies them and releases them with `Marshal.FreeCoTaskMem`. Unreal-owned `FString`, `FName`, and `FText` values use their exported constructors and destructors. `TArray` storage uses exported `FMemory` allocation plus type-specific element construction and destruction; the bridge deliberately avoids generic `FProperty` copy/destruction virtual paths that are incompatible with the supported game build.

The installed UE4SS build does not expose a safe setter for Deadzone: Rogue's non-empty `TArray<TObjectPtr<...>>` elements. Reads and function outputs are supported, while replacement of those elements is rejected instead of writing a raw pointer into build-specific `TObjectPtr` storage.

## Native C++ mods

Regular C++ mods remain standard UE4SS mods and do not pass through CoreCLR. RogueMod.Core installs and orders them using the same package manifest used for managed, Lua and Pak mods.

## Constraints

The bridge must not bypass anti-cheat or activate mods in a mode forbidden by the selected game profile. A crash inside a native callback cannot be isolated by AssemblyLoadContext.
