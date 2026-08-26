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

    MutationAttempt UnrealMutationBackend::try_assign_object(
        void* property,
        void* address,
        void* target) const
    {
        if (property == nullptr || address == nullptr)
        {
            return MutationAttempt::Failed;
        }

        std::uint64_t original_raw{};
        std::memcpy(&original_raw, address, sizeof(original_raw));
        const auto target_raw = static_cast<std::uint64_t>(
            reinterpret_cast<std::uintptr_t>(target));

        std::memcpy(address, &target_raw, sizeof(target_raw));
        std::uint64_t after_raw{};
        std::memcpy(&after_raw, address, sizeof(after_raw));
        if (after_raw == target_raw)
        {
            return MutationAttempt::Succeeded;
        }

        std::memcpy(address, &original_raw, sizeof(original_raw));
        std::uint64_t restored_raw{};
        std::memcpy(&restored_raw, address, sizeof(restored_raw));
        return restored_raw == original_raw
            ? MutationAttempt::Failed
            : MutationAttempt::RestorationFailed;
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
