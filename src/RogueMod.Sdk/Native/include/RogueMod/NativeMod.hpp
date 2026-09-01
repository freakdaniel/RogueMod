/**
 * @file NativeMod.hpp
 * @brief UE4SS entry point exports for RogueMod native mods.
 */

#pragma once

#include <UE4SS/CppUserModBase.hpp>

/**
 * @brief Defines the native mod entry points expected by UE4SS.
 *
 * Expands to the exported `start_mod` and `uninstall_mod` functions that UE4SS calls to
 * instantiate and destroy the mod. Use exactly once per mod DLL, passing the class that
 * derives from `RC::CppUserModBase`.
 *
 * @param mod_type The mod class deriving from `RC::CppUserModBase`.
 */
#define ROGUEMOD_DEFINE_NATIVE_MOD(mod_type)                                      \
    extern "C" __declspec(dllexport) RC::CppUserModBase* start_mod()              \
    {                                                                              \
        return new mod_type();                                                     \
    }                                                                              \
    extern "C" __declspec(dllexport) void uninstall_mod(RC::CppUserModBase* mod)  \
    {                                                                              \
        delete mod;                                                                \
    }
