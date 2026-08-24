#pragma once

#include <UE4SS/CppUserModBase.hpp>

#define ROGUEMOD_DEFINE_NATIVE_MOD(mod_type)                                      \
    extern "C" __declspec(dllexport) RC::CppUserModBase* start_mod()              \
    {                                                                              \
        return new mod_type();                                                     \
    }                                                                              \
    extern "C" __declspec(dllexport) void uninstall_mod(RC::CppUserModBase* mod)  \
    {                                                                              \
        delete mod;                                                                \
    }
