#pragma once

#include <cstdint>

namespace RogueMod
{
    inline constexpr std::uint32_t HostAbiVersion = 13;

    enum class LogLevel : std::int32_t
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical,
    };

    enum class UnrealPropertyKind : std::uint32_t
    {
        Boolean = 1,
        Int8 = 2,
        UInt8 = 3,
        Int16 = 4,
        UInt16 = 5,
        Int32 = 6,
        UInt32 = 7,
        Int64 = 8,
        UInt64 = 9,
        Float = 10,
        Double = 11,
        Object = 12,
        String = 13,
        Name = 14,
        Struct = 15,
        Text = 16,
        Array = 17,
        Optional = 18,
        WeakObject = 19,
        LazyObject = 20,
    };

    struct UnrealValue
    {
        std::uint32_t kind;
        std::uint32_t reserved;
        std::uint64_t data;
    };

    static_assert(sizeof(UnrealValue) == 16, "RogueMod Unreal value ABI changed unexpectedly.");

    enum class UnrealParameterFlags : std::uint32_t
    {
        None = 0,
        Input = 1U << 0,
        Output = 1U << 1,
        Return = 1U << 2,
        Modified = 1U << 3,
    };

    struct UnrealParameter
    {
        std::uint32_t kind;
        std::uint32_t flags;
        std::int32_t offset;
        std::int32_t size;
        std::uint32_t array_dimension;
        std::uint32_t bool_layout;
        UnrealValue value;
    };

    static_assert(sizeof(UnrealParameter) == 40, "RogueMod Unreal parameter ABI changed unexpectedly.");

    enum class UnrealHookPhase : std::int32_t
    {
        Pre = 1,
        Post = 2,
    };

    using UnrealHookCallback = std::int32_t(__cdecl*)(
        std::uint64_t context,
        std::uint64_t object_handle,
        std::int32_t phase,
        std::uint32_t parameter_count,
        UnrealParameter* parameters);

    struct HostApi
    {
        std::uint32_t size;
        std::uint32_t abi_version;
        void(__cdecl* log)(std::int32_t level, const wchar_t* message);
        const wchar_t* mod_root;
        const wchar_t* game_profile_id;
        std::int32_t(__cdecl* unreal_is_available)();
        std::uint64_t(__cdecl* unreal_find_first_of)(const wchar_t* class_name);
        std::int32_t(__cdecl* unreal_is_valid)(std::uint64_t handle);
        std::uint64_t(__cdecl* unreal_get_class)(std::uint64_t handle);
        std::int32_t(__cdecl* unreal_get_path_name)(
            std::uint64_t handle,
            wchar_t* buffer,
            std::uint32_t capacity,
            std::uint32_t* required);
        std::uint32_t(__cdecl* unreal_get_capabilities)();
        std::int32_t(__cdecl* unreal_invoke_zero_parameter)(
            std::uint64_t handle,
            const wchar_t* function_name);
        std::int32_t(__cdecl* unreal_read_property)(
            std::uint64_t handle,
            const wchar_t* property_name,
            std::uint32_t property_kind,
            UnrealValue* value);
        std::int32_t(__cdecl* unreal_write_property)(
            std::uint64_t handle,
            const wchar_t* property_name,
            std::uint32_t property_kind,
            const UnrealValue* value);
        std::int32_t(__cdecl* unreal_invoke)(
            std::uint64_t handle,
            const wchar_t* function_name,
            std::uint32_t parameter_count,
            UnrealParameter* parameters);
        const wchar_t* game_mods_root;
        std::int32_t(__cdecl* unreal_find_all_of)(
            const wchar_t* class_name,
            std::uint64_t* handles,
            std::uint32_t capacity,
            std::uint32_t* required);
        std::int32_t(__cdecl* unreal_register_hook)(
            const wchar_t* function_path,
            std::int32_t phase,
            std::int32_t priority,
            std::uint64_t instance_filter,
            std::uint32_t parameter_count,
            const UnrealParameter* parameters,
            UnrealHookCallback callback,
            std::uint64_t context,
            std::uint64_t* token);
        std::int32_t(__cdecl* unreal_unregister_hook)(std::uint64_t token);
    };

    static_assert(sizeof(HostApi) == 144, "RogueMod host ABI changed unexpectedly.");
}
