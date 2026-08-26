#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>

#include "RogueModHostApi.hpp"

namespace RogueMod
{
    enum class MutationAttempt
    {
        Unsupported,
        Succeeded,
        Failed,
        RestorationFailed
    };

    class UnrealMutationBackend
    {
      public:
        struct Exports
        {
            void(__cdecl* initialize_value)(const void* property, void* address){};
            void(__cdecl* destroy_value)(const void* property, void* address){};
            void*(__cdecl* memory_malloc)(std::size_t size, std::uint32_t alignment){};
            std::size_t object_ptr_setter_vtable_offset{};
            std::size_t object_getter_vtable_offset{};
            bool(__cdecl* validate_object_accessors)(const void* setter, const void* getter){};
            void(__cdecl* log)(std::int32_t level, const wchar_t* message){};
        };

        using AssignValue = std::function<bool(
            void* property,
            void* address,
            std::uint32_t encoded_kind,
            const UnrealValue& value)>;

        void configure(Exports exports);

        [[nodiscard]] bool is_available() const;
        [[nodiscard]] bool can_access_objects() const;

        [[nodiscard]] bool try_read_object(
            void* property,
            const void* address,
            void*& target) const;

        [[nodiscard]] MutationAttempt try_assign_object(
            void* property,
            void* address,
            void* target) const;

        [[nodiscard]] MutationAttempt try_replace_name_array(
            void* array_property,
            void* inner_property,
            void* destination,
            std::uint32_t inner_encoded_kind,
            std::size_t inner_size,
            const UnrealValue& value,
            const AssignValue& assign_value) const;

      private:
        Exports m_exports{};
    };
}
