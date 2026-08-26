#include "UnrealMutationBackend.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <initializer_list>
#include <iostream>
#include <memory>
#include <new>
#include <stdexcept>
#include <vector>

namespace
{
    constexpr std::uint32_t name_kind = 14;

    struct FakeProperty
    {
        bool is_array;
    };

    struct FakeObjectProperty
    {
        void** vtable{};
        bool reject_generic_assignment{};
        void* logical_override{};
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

    void* __cdecl get_object_value(const void* property, const void* address)
    {
        const auto& fake_property = *static_cast<const FakeObjectProperty*>(property);
        if (fake_property.logical_override != nullptr)
        {
            return fake_property.logical_override;
        }
        return *static_cast<void* const*>(address);
    }

    void __cdecl set_object_value(const void* property, void* address, void* value)
    {
        const auto& fake_property = *static_cast<const FakeObjectProperty*>(property);
        if (fake_property.reject_generic_assignment)
        {
            throw std::runtime_error("generic object setter rejected");
        }
        *static_cast<void**>(address) = value;
    }

    void __cdecl set_typed_object_value(void* address, const void* value_reference)
    {
        *static_cast<void**>(address) = *static_cast<void* const*>(value_reference);
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

    std::unique_ptr<RogueMod::UnrealMutationBackend> create_backend()
    {
        auto backend = std::make_unique<RogueMod::UnrealMutationBackend>();
        backend->configure({
            initialize_value,
            destroy_value,
            memory_malloc,
            get_object_value,
            set_object_value,
            set_typed_object_value});
        return backend;
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

    bool test_object_assignment_writes_raw_pointer()
    {
        FakeObjectProperty object_property{};
        auto* destination = reinterpret_cast<void*>(1);
        const auto result = create_backend()->try_assign_object(
            &object_property,
            &destination,
            reinterpret_cast<void*>(2));
        return result == RogueMod::MutationAttempt::Succeeded
            && destination == reinterpret_cast<void*>(2);
    }

    bool test_object_assignment_supports_null_target()
    {
        FakeObjectProperty object_property{};
        auto* destination = reinterpret_cast<void*>(1);
        const auto result = create_backend()->try_assign_object(&object_property, &destination, nullptr);
        return result == RogueMod::MutationAttempt::Succeeded && destination == nullptr;
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
    if (!test_object_assignment_writes_raw_pointer())
    {
        std::cerr << "raw object assignment test failed\n";
        return 1;
    }
    if (!test_object_assignment_supports_null_target())
    {
        std::cerr << "raw null object assignment test failed\n";
        return 1;
    }
    return 0;
}
