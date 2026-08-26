#include "UnrealMutationBackend.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <utility>

namespace RogueMod
{
    namespace
    {
        constexpr std::uint32_t property_kind_mask = 0xffU;

        struct FScriptArrayLayout
        {
            void* data;
            std::int32_t num;
            std::int32_t max;
        };

        static_assert(sizeof(FScriptArrayLayout) == 16);

        class InitializedPropertyValue
        {
          public:
            InitializedPropertyValue(
                const UnrealMutationBackend::Exports& exports,
                void* property,
                void* address)
                : m_exports(exports), m_property(property), m_address(address)
            {
                m_exports.initialize_value(m_property, m_address);
            }

            ~InitializedPropertyValue()
            {
                m_exports.destroy_value(m_property, m_address);
            }

            InitializedPropertyValue(const InitializedPropertyValue&) = delete;
            InitializedPropertyValue& operator=(const InitializedPropertyValue&) = delete;

          private:
            const UnrealMutationBackend::Exports& m_exports;
            void* m_property;
            void* m_address;
        };

        UnrealPropertyKind decode_kind(std::uint32_t encoded_kind)
        {
            return static_cast<UnrealPropertyKind>(encoded_kind & property_kind_mask);
        }

        using ObjectPtrSetter = void(__cdecl*)(const void*, void*, void* const*);
        using ObjectGetter = void*(__cdecl*)(const void*, const void*);

        bool resolve_object_accessors(
            const UnrealMutationBackend::Exports& exports,
            void* property,
            ObjectPtrSetter& setter,
            ObjectGetter& getter)
        {
            if (property == nullptr
                || exports.object_ptr_setter_vtable_offset == 0
                || exports.object_getter_vtable_offset == 0
                || exports.validate_object_accessors == nullptr)
            {
                return false;
            }

            const auto* vtable = *static_cast<void***>(property);
            if (vtable == nullptr)
            {
                return false;
            }
            setter = reinterpret_cast<ObjectPtrSetter>(
                vtable[exports.object_ptr_setter_vtable_offset / sizeof(void*)]);
            getter = reinterpret_cast<ObjectGetter>(
                vtable[exports.object_getter_vtable_offset / sizeof(void*)]);
            return setter != nullptr && getter != nullptr
                && exports.validate_object_accessors(
                    reinterpret_cast<const void*>(setter),
                    reinterpret_cast<const void*>(getter));
        }
    }

    void UnrealMutationBackend::configure(Exports exports)
    {
        m_exports = exports;
    }

    bool UnrealMutationBackend::is_available() const
    {
        return m_exports.initialize_value != nullptr
            && m_exports.destroy_value != nullptr
            && m_exports.memory_malloc != nullptr;
    }

    bool UnrealMutationBackend::can_access_objects() const
    {
        return m_exports.object_ptr_setter_vtable_offset != 0
            && m_exports.object_getter_vtable_offset != 0
            && m_exports.validate_object_accessors != nullptr;
    }

    bool UnrealMutationBackend::try_read_object(
        void* property,
        const void* address,
        void*& target) const
    {
        target = nullptr;
        if (address == nullptr)
        {
            return false;
        }
        ObjectPtrSetter setter{};
        ObjectGetter getter{};
        if (!resolve_object_accessors(m_exports, property, setter, getter))
        {
            return false;
        }
        target = getter(property, address);
        return true;
    }

    MutationAttempt UnrealMutationBackend::try_assign_object(
        void* property,
        void* address,
        void* target) const
    {
        if (address == nullptr)
        {
            return MutationAttempt::Failed;
        }
        ObjectPtrSetter setter{};
        ObjectGetter getter{};
        if (!resolve_object_accessors(m_exports, property, setter, getter))
        {
            return MutationAttempt::Unsupported;
        }

        const auto original = getter(property, address);
        try
        {
            auto* temporary = target;
            setter(property, address, &temporary);
            const auto written = getter(property, address);
            if (written == target)
            {
                return MutationAttempt::Succeeded;
            }
            if (written == original)
            {
                return MutationAttempt::Failed;
            }

            temporary = original;
            setter(property, address, &temporary);
            return getter(property, address) == original
                ? MutationAttempt::Failed
                : MutationAttempt::RestorationFailed;
        }
        catch (...)
        {
            const auto current = getter(property, address);
            if (current == original)
            {
                return MutationAttempt::Failed;
            }
            try
            {
                auto* temporary = original;
                setter(property, address, &temporary);
                return getter(property, address) == original
                    ? MutationAttempt::Failed
                    : MutationAttempt::RestorationFailed;
            }
            catch (...)
            {
                return MutationAttempt::RestorationFailed;
            }
        }
    }

    MutationAttempt UnrealMutationBackend::try_replace_name_array(
        void* array_property,
        void* inner_property,
        void* destination,
        std::uint32_t inner_encoded_kind,
        std::size_t inner_size,
        const UnrealValue& value,
        const AssignValue& assign_value) const
    {
        if (!is_available()
            || decode_kind(inner_encoded_kind) != UnrealPropertyKind::Name
            || inner_size != sizeof(std::uint64_t))
        {
            return MutationAttempt::Unsupported;
        }
        if (array_property == nullptr
            || inner_property == nullptr
            || destination == nullptr
            || !assign_value
            || value.reserved > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())
            || (value.reserved != 0 && value.data == 0))
        {
            return MutationAttempt::Failed;
        }

        const auto* elements = reinterpret_cast<const UnrealValue*>(value.data);
        for (std::uint32_t index = 0; index < value.reserved; ++index)
        {
            if (elements[index].kind != inner_encoded_kind)
            {
                return MutationAttempt::Failed;
            }
        }

        alignas(16) FScriptArrayLayout replacement{};
        InitializedPropertyValue replacement_lifetime(m_exports, array_property, &replacement);

        if (value.reserved != 0)
        {
            const auto bytes = static_cast<std::size_t>(value.reserved) * inner_size;
            replacement.data = m_exports.memory_malloc(bytes, 0);
            if (replacement.data == nullptr)
            {
                return MutationAttempt::Failed;
            }
            replacement.max = static_cast<std::int32_t>(value.reserved);

            auto* replacement_data = static_cast<std::byte*>(replacement.data);
            for (std::uint32_t index = 0; index < value.reserved; ++index)
            {
                auto* element = replacement_data + static_cast<std::size_t>(index) * inner_size;
                m_exports.initialize_value(inner_property, element);
                ++replacement.num;
                if (!assign_value(inner_property, element, inner_encoded_kind, elements[index]))
                {
                    return MutationAttempt::Failed;
                }
            }
        }

        auto& destination_array = *static_cast<FScriptArrayLayout*>(destination);
        if (destination_array.num < 0
            || destination_array.max < destination_array.num
            || (destination_array.max != 0 && destination_array.data == nullptr))
        {
            return MutationAttempt::Failed;
        }

        std::swap(destination_array, replacement);

        return MutationAttempt::Succeeded;
    }
}
