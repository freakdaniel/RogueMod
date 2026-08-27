#include "UnrealMutationBackend.hpp"
#include "UnrealReflectionApi.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <iostream>
#include <memory>
#include <new>
#include <vector>

namespace
{
    constexpr std::uint32_t name_kind = 14;
    constexpr std::size_t object_setter_offset = sizeof(void*);
    constexpr std::size_t object_getter_offset = sizeof(void*) * 2;

    struct FakeProperty
    {
        bool is_array;
    };

    enum class ObjectSetterMode
    {
        Normal,
        CorruptTarget,
        CorruptAlways
    };

    struct FakeObjectProperty
    {
        void** vtable{};
        ObjectSetterMode mode{};
    };

    struct FakeScriptArray
    {
        void* data;
        std::int32_t num;
        std::int32_t max;
    };

    static_assert(sizeof(FakeScriptArray) == 16);

    void __cdecl initialize_value(const void* property, void* address)
    {
        const auto& fake_property = *static_cast<const FakeProperty*>(property);
        if (fake_property.is_array)
        {
            *static_cast<FakeScriptArray*>(address) = {};
        }
        else
        {
            *static_cast<std::uint64_t*>(address) = 0;
        }
    }

    void __cdecl destroy_value(const void* property, void* address)
    {
        const auto& fake_property = *static_cast<const FakeProperty*>(property);
        if (!fake_property.is_array)
        {
            return;
        }

        auto& array = *static_cast<FakeScriptArray*>(address);
        std::free(array.data);
        array = {};
    }

    void* __cdecl memory_malloc(std::size_t size, std::uint32_t)
    {
        return std::malloc(size);
    }

    void* __cdecl get_object_value(const void*, const void* address)
    {
        return *static_cast<void* const*>(address);
    }

    void __cdecl set_object_value(
        const void* property,
        void* address,
        void* const* value_reference)
    {
        const auto& fake_property = *static_cast<const FakeObjectProperty*>(property);
        auto* value = *value_reference;
        if (fake_property.mode == ObjectSetterMode::CorruptAlways
            || (fake_property.mode == ObjectSetterMode::CorruptTarget
                && value == reinterpret_cast<void*>(2)))
        {
            value = reinterpret_cast<void*>(3);
        }
        *static_cast<void**>(address) = value;
    }

    bool __cdecl validate_object_accessors(const void*, const void*)
    {
        return true;
    }

    bool __cdecl reject_object_accessors(const void*, const void*)
    {
        return false;
    }

    FakeScriptArray make_array(std::initializer_list<std::uint64_t> values)
    {
        FakeScriptArray array{};
        if (values.size() == 0)
        {
            return array;
        }

        const auto bytes = values.size() * sizeof(std::uint64_t);
        array.data = std::malloc(bytes);
        if (array.data == nullptr)
        {
            throw std::bad_alloc{};
        }
        std::memcpy(array.data, values.begin(), bytes);
        array.num = static_cast<std::int32_t>(values.size());
        array.max = array.num;
        return array;
    }

    bool equals(const FakeScriptArray& array, std::initializer_list<std::uint64_t> expected)
    {
        if (array.num != static_cast<std::int32_t>(expected.size()))
        {
            return false;
        }
        const auto* data = static_cast<const std::uint64_t*>(array.data);
        return std::equal(expected.begin(), expected.end(), data);
    }

    std::unique_ptr<RogueMod::UnrealMutationBackend> create_backend(bool valid_accessors = true)
    {
        auto backend = std::make_unique<RogueMod::UnrealMutationBackend>();
        backend->configure({
            initialize_value,
            destroy_value,
            memory_malloc,
            object_setter_offset,
            object_getter_offset,
            valid_accessors ? validate_object_accessors : reject_object_accessors});
        return backend;
    }

    FakeObjectProperty make_object_property(ObjectSetterMode mode = ObjectSetterMode::Normal)
    {
        static void* vtable[3]{
            nullptr,
            reinterpret_cast<void*>(&set_object_value),
            reinterpret_cast<void*>(&get_object_value)};
        return {vtable, mode};
    }

    RogueMod::UnrealValue make_wire_array(std::vector<RogueMod::UnrealValue>& elements)
    {
        return {
            0,
            static_cast<std::uint32_t>(elements.size()),
            reinterpret_cast<std::uint64_t>(elements.data())};
    }

    bool assign_name(
        void*,
        void* address,
        std::uint32_t encoded_kind,
        const RogueMod::UnrealValue& value)
    {
        if (encoded_kind != name_kind || value.data == 999)
        {
            return false;
        }
        *static_cast<std::uint64_t*>(address) = value.data;
        return true;
    }

    bool test_resize_commits_complete_replacement()
    {
        FakeProperty array_property{true};
        FakeProperty inner_property{false};
        auto destination = make_array({10});
        std::vector<RogueMod::UnrealValue> elements{{name_kind, 0, 20}, {name_kind, 0, 30}};
        const auto wire = make_wire_array(elements);
        const auto result = create_backend()->try_replace_name_array(
            &array_property,
            &inner_property,
            &destination,
            name_kind,
            sizeof(std::uint64_t),
            wire,
            assign_name);
        const auto passed = result == RogueMod::MutationAttempt::Succeeded
            && equals(destination, {20, 30});
        destroy_value(&array_property, &destination);
        return passed;
    }

    bool test_failed_replacement_preserves_destination()
    {
        FakeProperty array_property{true};
        FakeProperty inner_property{false};
        auto destination = make_array({10, 11});
        std::vector<RogueMod::UnrealValue> elements{{name_kind, 0, 20}, {name_kind, 0, 999}};
        const auto wire = make_wire_array(elements);
        const auto result = create_backend()->try_replace_name_array(
            &array_property,
            &inner_property,
            &destination,
            name_kind,
            sizeof(std::uint64_t),
            wire,
            assign_name);
        const auto passed = result == RogueMod::MutationAttempt::Failed
            && equals(destination, {10, 11});
        destroy_value(&array_property, &destination);
        return passed;
    }

    bool test_clear_releases_existing_array()
    {
        FakeProperty array_property{true};
        FakeProperty inner_property{false};
        auto destination = make_array({10, 11});
        std::vector<RogueMod::UnrealValue> elements;
        const auto wire = make_wire_array(elements);
        const auto result = create_backend()->try_replace_name_array(
            &array_property,
            &inner_property,
            &destination,
            name_kind,
            sizeof(std::uint64_t),
            wire,
            assign_name);
        const auto passed = result == RogueMod::MutationAttempt::Succeeded
            && destination.data == nullptr
            && destination.num == 0
            && destination.max == 0;
        destroy_value(&array_property, &destination);
        return passed;
    }

    bool test_object_read_uses_validated_getter()
    {
        auto object_property = make_object_property();
        auto* destination = reinterpret_cast<void*>(1);
        void* value{};
        return create_backend()->try_read_object(&object_property, &destination, value)
            && value == destination;
    }

    bool test_object_assignment_uses_tobjectptr_setter()
    {
        auto object_property = make_object_property();
        auto* destination = reinterpret_cast<void*>(1);
        const auto result = create_backend()->try_assign_object(
            &object_property,
            &destination,
            reinterpret_cast<void*>(2));
        return result == RogueMod::MutationAttempt::Succeeded
            && destination == reinterpret_cast<void*>(2);
    }

    bool test_object_assignment_mismatch_restores_original()
    {
        auto object_property = make_object_property(ObjectSetterMode::CorruptTarget);
        auto* destination = reinterpret_cast<void*>(1);
        const auto result = create_backend()->try_assign_object(
            &object_property,
            &destination,
            reinterpret_cast<void*>(2));
        return result == RogueMod::MutationAttempt::Failed
            && destination == reinterpret_cast<void*>(1);
    }

    bool test_object_assignment_rejects_unvalidated_accessor()
    {
        auto object_property = make_object_property();
        auto* destination = reinterpret_cast<void*>(1);
        const auto result = create_backend(false)->try_assign_object(
            &object_property,
            &destination,
            reinterpret_cast<void*>(2));
        return result == RogueMod::MutationAttempt::Unsupported
            && destination == reinterpret_cast<void*>(1);
    }

    bool test_script_bit_array_uses_inline_allocation()
    {
        RogueMod::ScriptContainers::ScriptBitArray flags{};
        flags.inline_data[0] = 1U;
        flags.num_bits = 1;
        flags.max_bits = 128;
        return flags.data() == flags.inline_data
            && (flags.data()[0] & 1U) != 0;
    }

    bool test_script_bit_array_uses_secondary_allocation()
    {
        std::uint32_t external_words[]{2U};
        RogueMod::ScriptContainers::ScriptBitArray flags{};
        flags.secondary_data = external_words;
        flags.num_bits = 129;
        flags.max_bits = 160;
        return flags.data() == external_words
            && (flags.data()[0] & 2U) != 0;
    }
}

int main()
{
    if (!test_resize_commits_complete_replacement())
    {
        std::cerr << "resize replacement test failed\n";
        return 1;
    }
    if (!test_failed_replacement_preserves_destination())
    {
        std::cerr << "failed replacement preservation test failed\n";
        return 1;
    }
    if (!test_clear_releases_existing_array())
    {
        std::cerr << "clear replacement test failed\n";
        return 1;
    }
    if (!test_object_read_uses_validated_getter())
    {
        std::cerr << "validated object getter test failed\n";
        return 1;
    }
    if (!test_object_assignment_uses_tobjectptr_setter())
    {
        std::cerr << "TObjectPtr setter test failed\n";
        return 1;
    }
    if (!test_object_assignment_mismatch_restores_original())
    {
        std::cerr << "object setter restoration test failed\n";
        return 1;
    }
    if (!test_object_assignment_rejects_unvalidated_accessor())
    {
        std::cerr << "unvalidated object accessor rejection test failed\n";
        return 1;
    }
    if (!test_script_bit_array_uses_inline_allocation())
    {
        std::cerr << "script bit array inline allocation test failed\n";
        return 1;
    }
    if (!test_script_bit_array_uses_secondary_allocation())
    {
        std::cerr << "script bit array secondary allocation test failed\n";
        return 1;
    }
    return 0;
}
