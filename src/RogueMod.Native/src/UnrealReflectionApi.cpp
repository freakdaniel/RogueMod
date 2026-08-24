#include "UnrealReflectionApi.hpp"

#include <algorithm>
#include <combaseapi.h>
#include <cwchar>
#include <cstring>
#include <string>
#include <utility>
#include <vector>

namespace RogueMod
{
    namespace
    {
        constexpr std::size_t maximum_marshaled_string_length = 1'048'576;
        constexpr std::size_t maximum_marshaled_struct_size = 1'048'576;
        constexpr std::size_t maximum_marshaled_array_length = 1'048'576;
        constexpr std::size_t maximum_marshaled_array_bytes = 64U * 1024U * 1024U;
        constexpr std::uint32_t property_kind_mask = 0xffU;
        constexpr std::uint32_t array_element_kind_shift = 8U;

        struct FStringLayout
        {
            const wchar_t* data;
            std::int32_t num;
            std::int32_t max;
        };

        static_assert(sizeof(FStringLayout) == 16);

        struct FScriptArrayLayout
        {
            void* data;
            std::int32_t num;
            std::int32_t max;
        };

        static_assert(sizeof(FScriptArrayLayout) == 16);

        UnrealPropertyKind decode_kind(std::uint32_t encoded_kind)
        {
            return static_cast<UnrealPropertyKind>(encoded_kind & property_kind_mask);
        }

        UnrealPropertyKind decode_array_element_kind(std::uint32_t encoded_kind)
        {
            return static_cast<UnrealPropertyKind>((encoded_kind >> array_element_kind_shift) & property_kind_mask);
        }

        std::size_t expected_value_size(UnrealPropertyKind kind)
        {
            switch (kind)
            {
            case UnrealPropertyKind::Boolean:
            case UnrealPropertyKind::Int8:
            case UnrealPropertyKind::UInt8: return 1;
            case UnrealPropertyKind::Int16:
            case UnrealPropertyKind::UInt16: return 2;
            case UnrealPropertyKind::Int32:
            case UnrealPropertyKind::UInt32:
            case UnrealPropertyKind::Float: return 4;
            case UnrealPropertyKind::Int64:
            case UnrealPropertyKind::UInt64:
            case UnrealPropertyKind::Double:
            case UnrealPropertyKind::Object:
            case UnrealPropertyKind::Name: return 8;
            case UnrealPropertyKind::String:
            case UnrealPropertyKind::Text:
            case UnrealPropertyKind::Array: return 16;
            default: return 0;
            }
        }

        void free_marshaled_value(UnrealValue& value)
        {
            const auto kind = decode_kind(value.kind);
            if (kind == UnrealPropertyKind::Array && value.data != 0)
            {
                if (value.reserved <= maximum_marshaled_array_length)
                {
                    auto* elements = reinterpret_cast<UnrealValue*>(value.data);
                    for (std::uint32_t index = 0; index < value.reserved; ++index)
                    {
                        free_marshaled_value(elements[index]);
                    }
                }
                CoTaskMemFree(reinterpret_cast<void*>(value.data));
            }
            else if ((kind == UnrealPropertyKind::String
                      || kind == UnrealPropertyKind::Name
                      || kind == UnrealPropertyKind::Text
                      || kind == UnrealPropertyKind::Struct)
                     && value.data != 0)
            {
                CoTaskMemFree(reinterpret_cast<void*>(value.data));
            }
            value = {};
        }

        class FStringCleanup
        {
          public:
            explicit FStringCleanup(void(__cdecl* destructor)(void*)) : m_destructor(destructor) {}

            void add(void* address)
            {
                m_addresses.push_back(address);
            }

            ~FStringCleanup()
            {
                for (auto iterator = m_addresses.rbegin(); iterator != m_addresses.rend(); ++iterator)
                {
                    try
                    {
                        m_destructor(*iterator);
                    }
                    catch (...)
                    {
                    }
                }
            }

          private:
            void(__cdecl* m_destructor)(void*);
            std::vector<void*> m_addresses;
        };

        class OutputAllocationCleanup
        {
          public:
            void add(UnrealValue& value)
            {
                if (value.data != 0)
                {
                    m_values.push_back(&value);
                }
            }

            void commit()
            {
                m_committed = true;
            }

            ~OutputAllocationCleanup()
            {
                if (!m_committed)
                {
                    for (auto* value : m_values)
                    {
                        free_marshaled_value(*value);
                    }
                }
            }

          private:
            bool m_committed{};
            std::vector<UnrealValue*> m_values;
        };

        class PropertyValueCleanup
        {
          public:
            explicit PropertyValueCleanup(void(__cdecl* destroy)(const void*, void*)) : m_destroy(destroy) {}

            void add(const void* property, void* address)
            {
                m_values.emplace_back(property, address);
            }

            ~PropertyValueCleanup()
            {
                for (auto iterator = m_values.rbegin(); iterator != m_values.rend(); ++iterator)
                {
                    try
                    {
                        m_destroy(iterator->first, iterator->second);
                    }
                    catch (...)
                    {
                    }
                }
            }

          private:
            void(__cdecl* m_destroy)(const void*, void*);
            std::vector<std::pair<const void*, void*>> m_values;
        };

        template <typename Function>
        Function load_export(HMODULE module, const char* name)
        {
            return reinterpret_cast<Function>(GetProcAddress(module, name));
        }

        bool marshal_string(const wchar_t* data, std::size_t length, UnrealValue& value)
        {
            if (length > maximum_marshaled_string_length || (data == nullptr && length != 0))
            {
                return false;
            }
            if (length == 0)
            {
                value.data = 0;
                value.reserved = 0;
                return true;
            }
            const auto bytes = (length + 1) * sizeof(wchar_t);
            auto* copy = static_cast<wchar_t*>(CoTaskMemAlloc(bytes));
            if (copy == nullptr)
            {
                return false;
            }
            std::memcpy(copy, data, length * sizeof(wchar_t));
            copy[length] = L'\0';
            value.data = reinterpret_cast<std::uint64_t>(copy);
            value.reserved = static_cast<std::uint32_t>(length);
            return true;
        }

        bool marshal_bytes(const void* data, std::size_t size, UnrealValue& value)
        {
            if (data == nullptr || size == 0 || size > maximum_marshaled_struct_size || size > UINT32_MAX)
            {
                return false;
            }
            auto* copy = CoTaskMemAlloc(size);
            if (copy == nullptr)
            {
                return false;
            }
            std::memcpy(copy, data, size);
            value.data = reinterpret_cast<std::uint64_t>(copy);
            value.reserved = static_cast<std::uint32_t>(size);
            return true;
        }

        bool valid_marshaled_input(const UnrealValue& value)
        {
            if (value.reserved > maximum_marshaled_string_length || (value.data == 0 && value.reserved != 0))
            {
                return false;
            }
            const auto* text = value.data == 0 ? L"" : reinterpret_cast<const wchar_t*>(value.data);
            return text[value.reserved] == L'\0'
                && std::wmemchr(text, L'\0', value.reserved) == nullptr;
        }

        bool marshal_fstring(const void* address, UnrealValue& value)
        {
            const auto& string = *static_cast<const FStringLayout*>(address);
            if (string.num < 0 || string.max < string.num || string.num > static_cast<std::int32_t>(maximum_marshaled_string_length + 1))
            {
                return false;
            }
            const auto length = string.num == 0 ? 0U : static_cast<std::size_t>(string.num - 1);
            return marshal_string(string.data, length, value);
        }

        template <typename Function>
        bool marshal_fname(Function function, const void* address, UnrealValue& value)
        {
            alignas(std::wstring) std::byte storage[sizeof(std::wstring)];
            auto* text = reinterpret_cast<std::wstring*>(storage);
            function(address, text);
            const auto marshalled = marshal_string(text->data(), text->size(), value);
            text->~basic_string();
            return marshalled;
        }
    }

    bool UnrealReflectionApi::resolve(HMODULE ue4ss_module)
    {
        if (ue4ss_module == nullptr)
        {
            return false;
        }

        m_find_first_of = load_export<find_first_of_fn>(
            ue4ss_module,
            "?FindFirstOf@UObjectGlobals@Unreal@RC@@YAPEAVUObject@23@PEB_W@Z");
        m_get_internal_index = load_export<get_internal_index_fn>(
            ue4ss_module,
            "?GetInternalIndex@UObjectBase@Unreal@RC@@QEBA?BHXZ");
        m_index_to_object = load_export<index_to_object_fn>(
            ue4ss_module,
            "?IndexToObject@FUObjectArray@Unreal@RC@@SAPEAUFUObjectItem@23@H@Z");
        m_get_serial_number = load_export<get_serial_number_fn>(
            ue4ss_module,
            "?GetSerialNumber@FUObjectItem@Unreal@RC@@QEBAAEBHXZ");
        m_is_item_valid = load_export<is_item_valid_fn>(
            ue4ss_module,
            "?IsValid@FUObjectItem@Unreal@RC@@QEBA_N_N@Z");
        m_get_item_object = load_export<get_item_object_fn>(
            ue4ss_module,
            "?GetObject@FUObjectItem@Unreal@RC@@AEBAAEAPEBVUObjectBase@23@XZ");
        m_get_class_private = load_export<get_class_private_fn>(
            ue4ss_module,
            "?GetClassPrivate@UObjectBase@Unreal@RC@@QEBAAEAPEBVUClass@23@XZ");
        m_get_path_name = load_export<get_path_name_fn>(
            ue4ss_module,
            "?GetPathName@UObject@Unreal@RC@@QEBAXPEAV123@AEAV?$basic_string@_WU?$char_traits@_W@std@@V?$allocator@_W@2@@std@@@Z");
        m_get_function_by_name_in_chain = load_export<get_function_by_name_in_chain_fn>(
            ue4ss_module,
            "?GetFunctionByNameInChain@UObject@Unreal@RC@@QEAAPEAVUFunction@23@PEB_W@Z");
        m_get_parms_size = load_export<get_parms_size_fn>(
            ue4ss_module,
            "?GetParmsSize@UFunction@Unreal@RC@@QEBAAEBGXZ");
        m_get_num_parms = load_export<get_num_parms_fn>(
            ue4ss_module,
            "?GetNumParms@UFunction@Unreal@RC@@QEBAAEBEXZ");
        m_get_return_value_offset = load_export<get_return_value_offset_fn>(
            ue4ss_module,
            "?GetReturnValueOffset@UFunction@Unreal@RC@@QEBAAEBGXZ");
        m_process_event = load_export<process_event_fn>(
            ue4ss_module,
            "?ProcessEvent@UObject@Unreal@RC@@QEAAXPEAVUFunction@23@PEAX@Z");
        m_get_property_by_name_in_chain = load_export<get_property_by_name_in_chain_fn>(
            ue4ss_module,
            "?GetPropertyByNameInChain@UObject@Unreal@RC@@QEAAPEAVFProperty@23@PEB_W@Z");
        m_get_offset = load_export<get_offset_fn>(
            ue4ss_module,
            "?GetOffset_Internal@FProperty@Unreal@RC@@QEBAAEBHXZ");
        m_get_element_size = load_export<get_element_size_fn>(
            ue4ss_module,
            "?GetElementSize@FProperty@Unreal@RC@@QEBAAEBHXZ");
        m_get_bool_in_container = load_export<get_bool_in_container_fn>(
            ue4ss_module,
            "?GetPropertyValueInContainer@FBoolProperty@Unreal@RC@@QEAA_NPEBXH@Z");
        m_set_bool_in_container = load_export<set_bool_in_container_fn>(
            ue4ss_module,
            "?SetPropertyValueInContainer@FBoolProperty@Unreal@RC@@QEAA@PEAX_NH@Z");
        m_get_object_property_value = load_export<get_object_property_value_fn>(
            ue4ss_module,
            "?GetObjectPropertyValue@FObjectPropertyBase@Unreal@RC@@QEBAPEAVUObject@23@PEBX@Z");
        m_set_object_property_value = load_export<set_object_property_value_fn>(
            ue4ss_module,
            "?SetObjectPropertyValue@FObjectPropertyBase@Unreal@RC@@QEBAXPEAXPEAVUObject@23@@Z");
        m_fstring_default_constructor = load_export<fstring_default_constructor_fn>(
            ue4ss_module,
            "??0FString@Unreal@RC@@QEAA@XZ");
        m_fstring_constructor = load_export<fstring_constructor_fn>(
            ue4ss_module,
            "??0FString@Unreal@RC@@QEAA@PEB_W@Z");
        m_fstring_destructor = load_export<fstring_destructor_fn>(
            ue4ss_module,
            "??1FString@Unreal@RC@@QEAA@XZ");
        m_fstring_copy_assignment = load_export<fstring_copy_assignment_fn>(
            ue4ss_module,
            "??4FString@Unreal@RC@@QEAAAEAV012@AEBV012@@Z");
        m_fname_default_constructor = load_export<fname_default_constructor_fn>(
            ue4ss_module,
            "??0FName@Unreal@RC@@QEAA@XZ");
        m_fname_constructor = load_export<fname_constructor_fn>(
            ue4ss_module,
            "??0FName@Unreal@RC@@QEAA@PEB_WW4EFindName@12@PEAX@Z");
        m_fname_to_string = load_export<fname_to_string_fn>(
            ue4ss_module,
            "?ToString@FName@Unreal@RC@@QEBA?BV?$basic_string@_WU?$char_traits@_W@std@@V?$allocator@_W@2@@std@@XZ");
        m_fname_copy_assignment = load_export<fname_copy_assignment_fn>(
            ue4ss_module,
            "??4FName@Unreal@RC@@QEAAAEAV012@AEBV012@@Z");
        m_ftext_default_constructor = load_export<ftext_default_constructor_fn>(
            ue4ss_module,
            "??0FText@Unreal@RC@@QEAA@XZ");
        m_ftext_constructor = load_export<ftext_constructor_fn>(
            ue4ss_module,
            "??0FText@Unreal@RC@@QEAA@PEB_W@Z");
        m_ftext_destructor = load_export<ftext_destructor_fn>(
            ue4ss_module,
            "??1FText@Unreal@RC@@QEAA@XZ");
        m_ftext_to_string = load_export<ftext_to_string_fn>(
            ue4ss_module,
            "?ToString@FText@Unreal@RC@@QEBA?AV?$basic_string@_WU?$char_traits@_W@std@@V?$allocator@_W@2@@std@@XZ");
        m_ftext_copy_assignment = load_export<ftext_copy_assignment_fn>(
            ue4ss_module,
            "??4FText@Unreal@RC@@QEAAAEAV012@AEBV012@@Z");
        m_get_first_property = load_export<get_first_property_fn>(
            ue4ss_module,
            "?GetFirstProperty@UStruct@Unreal@RC@@QEAAPEAVFProperty@23@XZ");
        m_get_next_field_as_property = load_export<get_next_field_as_property_fn>(
            ue4ss_module,
            "?GetNextFieldAsProperty@FField@Unreal@RC@@QEAAPEAVFProperty@23@XZ");
        m_get_array_inner = load_export<get_array_inner_fn>(
            ue4ss_module,
            "?GetInner@FArrayProperty@Unreal@RC@@QEAAAEAPEAVFProperty@23@XZ");
        m_get_min_alignment = load_export<get_min_alignment_fn>(
            ue4ss_module,
            "?GetMinAlignment@FProperty@Unreal@RC@@QEBAHXZ");
        m_initialize_value = load_export<initialize_value_fn>(
            ue4ss_module,
            "?InitializeValue@FProperty@Unreal@RC@@QEBAXPEAX@Z");
        m_destroy_value = load_export<destroy_value_fn>(
            ue4ss_module,
            "?DestroyValue@FProperty@Unreal@RC@@QEBAXPEAX@Z");
        m_copy_complete_value = load_export<copy_complete_value_fn>(
            ue4ss_module,
            "?CopyCompleteValue@FProperty@Unreal@RC@@QEBAXPEAXPEBX@Z");
        m_fmemory_malloc = load_export<fmemory_malloc_fn>(
            ue4ss_module,
            "?Malloc@FMemory@Unreal@RC@@SAPEAX_KI@Z");

        m_resolved = m_find_first_of != nullptr
            && m_get_internal_index != nullptr
            && m_index_to_object != nullptr
            && m_get_serial_number != nullptr
            && m_is_item_valid != nullptr
            && m_get_item_object != nullptr
            && m_get_class_private != nullptr
            && m_get_path_name != nullptr
            && m_get_function_by_name_in_chain != nullptr
            && m_get_parms_size != nullptr
            && m_get_num_parms != nullptr
            && m_get_return_value_offset != nullptr
            && m_process_event != nullptr
            && m_get_property_by_name_in_chain != nullptr
            && m_get_offset != nullptr
            && m_get_element_size != nullptr
            && m_get_bool_in_container != nullptr
            && m_set_bool_in_container != nullptr
            && m_get_object_property_value != nullptr
            && m_set_object_property_value != nullptr
            && m_fstring_default_constructor != nullptr
            && m_fstring_constructor != nullptr
            && m_fstring_destructor != nullptr
            && m_fstring_copy_assignment != nullptr
            && m_fname_default_constructor != nullptr
            && m_fname_constructor != nullptr
            && m_fname_to_string != nullptr
            && m_fname_copy_assignment != nullptr
            && m_ftext_default_constructor != nullptr
            && m_ftext_constructor != nullptr
            && m_ftext_destructor != nullptr
            && m_ftext_to_string != nullptr
            && m_ftext_copy_assignment != nullptr
            && m_get_first_property != nullptr
            && m_get_next_field_as_property != nullptr
            && m_get_array_inner != nullptr
            && m_get_min_alignment != nullptr
            && m_initialize_value != nullptr
            && m_destroy_value != nullptr
            && m_copy_complete_value != nullptr
            && m_fmemory_malloc != nullptr;
        return m_resolved;
    }

    std::uint32_t UnrealReflectionApi::capabilities() const
    {
        // Mirrors RogueMod.Abstractions.UnrealReflectionCapabilities.
        return is_available() ? (1U << 0) | (1U << 1) | (1U << 2) | (1U << 3) : 0U;
    }

    bool UnrealReflectionApi::marshal_typed_value(
        void* property,
        const void* address,
        std::uint32_t encoded_kind,
        UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr)
        {
            return false;
        }
        value = {};
        const auto kind = decode_kind(encoded_kind);
        const auto* element_size_pointer = m_get_element_size(property);
        if (element_size_pointer == nullptr || *element_size_pointer <= 0)
        {
            return false;
        }
        const auto element_size = static_cast<std::size_t>(*element_size_pointer);
        const auto fixed_size = expected_value_size(kind);
        if ((kind != UnrealPropertyKind::Struct && fixed_size != element_size)
            || (kind == UnrealPropertyKind::Struct && element_size > maximum_marshaled_struct_size))
        {
            return false;
        }

        value.kind = encoded_kind;
        if (kind == UnrealPropertyKind::Boolean)
        {
            value.data = m_get_bool_in_container(property, address, 0) ? 1U : 0U;
            return true;
        }
        if (kind == UnrealPropertyKind::Object)
        {
            value.data = make_handle(m_get_object_property_value(property, address));
            return true;
        }
        if (kind == UnrealPropertyKind::String)
        {
            if (!marshal_fstring(address, value))
            {
                value = {};
                return false;
            }
            value.kind = encoded_kind;
            return true;
        }
        if (kind == UnrealPropertyKind::Name || kind == UnrealPropertyKind::Text)
        {
            const auto marshalled = kind == UnrealPropertyKind::Name
                ? marshal_fname(m_fname_to_string, address, value)
                : marshal_fname(m_ftext_to_string, address, value);
            if (!marshalled)
            {
                value = {};
                return false;
            }
            value.kind = encoded_kind;
            return true;
        }
        if (kind == UnrealPropertyKind::Struct)
        {
            if (!marshal_bytes(address, element_size, value))
            {
                value = {};
                return false;
            }
            value.kind = encoded_kind;
            return true;
        }
        if (kind == UnrealPropertyKind::Array)
        {
            auto** inner_pointer = m_get_array_inner(property);
            auto* inner = inner_pointer == nullptr ? nullptr : *inner_pointer;
            const auto inner_kind = decode_array_element_kind(encoded_kind);
            if (inner == nullptr || inner_kind == UnrealPropertyKind::Array || inner_kind == static_cast<UnrealPropertyKind>(0))
            {
                return false;
            }
            const auto* inner_size_pointer = m_get_element_size(inner);
            if (inner_size_pointer == nullptr || *inner_size_pointer <= 0)
            {
                return false;
            }
            const auto inner_size = static_cast<std::size_t>(*inner_size_pointer);
            const auto inner_fixed_size = expected_value_size(inner_kind);
            if ((inner_kind != UnrealPropertyKind::Struct && inner_fixed_size != inner_size)
                || (inner_kind == UnrealPropertyKind::Struct && inner_size > maximum_marshaled_struct_size))
            {
                return false;
            }
            const auto& array = *static_cast<const FScriptArrayLayout*>(address);
            if (array.num < 0 || array.max < array.num
                || static_cast<std::size_t>(array.num) > maximum_marshaled_array_length
                || (array.num != 0 && array.data == nullptr)
                || static_cast<std::size_t>(array.num) > maximum_marshaled_array_bytes / inner_size)
            {
                return false;
            }
            if (array.num == 0)
            {
                return true;
            }
            const auto wire_size = static_cast<std::size_t>(array.num) * sizeof(UnrealValue);
            auto* elements = static_cast<UnrealValue*>(CoTaskMemAlloc(wire_size));
            if (elements == nullptr)
            {
                value = {};
                return false;
            }
            std::memset(elements, 0, wire_size);
            value.reserved = static_cast<std::uint32_t>(array.num);
            value.data = reinterpret_cast<std::uint64_t>(elements);
            const auto* array_data = static_cast<const std::byte*>(array.data);
            for (std::int32_t index = 0; index < array.num; ++index)
            {
                if (!marshal_typed_value(
                        inner,
                        array_data + static_cast<std::size_t>(index) * inner_size,
                        static_cast<std::uint32_t>(inner_kind),
                        elements[index]))
                {
                    free_marshaled_value(value);
                    return false;
                }
            }
            return true;
        }

        if (fixed_size == 0 || fixed_size > sizeof(value.data))
        {
            return false;
        }
        std::memcpy(&value.data, address, fixed_size);
        return true;
    }

    bool UnrealReflectionApi::assign_typed_value(
        void* property,
        void* address,
        std::uint32_t encoded_kind,
        const UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr || value.kind != encoded_kind)
        {
            return false;
        }
        const auto kind = decode_kind(encoded_kind);
        const auto* element_size_pointer = m_get_element_size(property);
        if (element_size_pointer == nullptr || *element_size_pointer <= 0)
        {
            return false;
        }
        const auto element_size = static_cast<std::size_t>(*element_size_pointer);
        const auto fixed_size = expected_value_size(kind);
        if ((kind != UnrealPropertyKind::Struct && fixed_size != element_size)
            || (kind == UnrealPropertyKind::Struct && element_size > maximum_marshaled_struct_size))
        {
            return false;
        }

        if (kind == UnrealPropertyKind::Boolean)
        {
            m_set_bool_in_container(property, address, value.data != 0, 0);
            return true;
        }
        if (kind == UnrealPropertyKind::Object)
        {
            void* target{};
            if (value.data != 0)
            {
                target = const_cast<void*>(resolve_handle(value.data));
                if (target == nullptr)
                {
                    return false;
                }
            }
            m_set_object_property_value(property, address, target);
            return true;
        }
        if (kind == UnrealPropertyKind::String || kind == UnrealPropertyKind::Name || kind == UnrealPropertyKind::Text)
        {
            if (!valid_marshaled_input(value))
            {
                return false;
            }
            const auto* text = value.data == 0 ? L"" : reinterpret_cast<const wchar_t*>(value.data);
            if (kind == UnrealPropertyKind::String)
            {
                alignas(8) std::byte temporary[16]{};
                FStringCleanup cleanup(m_fstring_destructor);
                m_fstring_constructor(temporary, text);
                cleanup.add(temporary);
                m_fstring_copy_assignment(address, temporary);
            }
            else if (kind == UnrealPropertyKind::Name)
            {
                alignas(8) std::byte temporary[8]{};
                m_fname_constructor(temporary, text, 1, nullptr);
                m_fname_copy_assignment(address, temporary);
            }
            else
            {
                alignas(8) std::byte temporary[16]{};
                FStringCleanup cleanup(m_ftext_destructor);
                m_ftext_constructor(temporary, text);
                cleanup.add(temporary);
                m_ftext_copy_assignment(address, temporary);
            }
            return true;
        }
        if (kind == UnrealPropertyKind::Struct)
        {
            if (value.data == 0 || value.reserved != element_size)
            {
                return false;
            }
            std::memcpy(address, reinterpret_cast<const void*>(value.data), element_size);
            return true;
        }
        if (kind == UnrealPropertyKind::Array)
        {
            if (value.reserved > maximum_marshaled_array_length || (value.reserved != 0 && value.data == 0))
            {
                return false;
            }
            auto** inner_pointer = m_get_array_inner(property);
            auto* inner = inner_pointer == nullptr ? nullptr : *inner_pointer;
            const auto inner_kind = decode_array_element_kind(encoded_kind);
            if (inner == nullptr || inner_kind == UnrealPropertyKind::Array || inner_kind == static_cast<UnrealPropertyKind>(0))
            {
                return false;
            }
            const auto* inner_size_pointer = m_get_element_size(inner);
            const auto inner_alignment = m_get_min_alignment(inner);
            if (inner_size_pointer == nullptr || *inner_size_pointer <= 0 || inner_alignment <= 0)
            {
                return false;
            }
            const auto inner_size = static_cast<std::size_t>(*inner_size_pointer);
            const auto inner_fixed_size = expected_value_size(inner_kind);
            if ((inner_kind != UnrealPropertyKind::Struct && inner_fixed_size != inner_size)
                || (inner_kind == UnrealPropertyKind::Struct && inner_size > maximum_marshaled_struct_size)
                || value.reserved > maximum_marshaled_array_bytes / inner_size)
            {
                return false;
            }
            const auto* elements = reinterpret_cast<const UnrealValue*>(value.data);
            for (std::uint32_t index = 0; index < value.reserved; ++index)
            {
                if (elements[index].kind != static_cast<std::uint32_t>(inner_kind))
                {
                    return false;
                }
            }

            alignas(8) FScriptArrayLayout temporary{};
            m_initialize_value(property, &temporary);
            PropertyValueCleanup cleanup(m_destroy_value);
            cleanup.add(property, &temporary);
            if (value.reserved != 0)
            {
                const auto bytes = static_cast<std::size_t>(value.reserved) * inner_size;
                temporary.data = m_fmemory_malloc(bytes, static_cast<std::uint32_t>(inner_alignment));
                if (temporary.data == nullptr)
                {
                    return false;
                }
                temporary.max = static_cast<std::int32_t>(value.reserved);
                auto* data = static_cast<std::byte*>(temporary.data);
                for (std::uint32_t index = 0; index < value.reserved; ++index)
                {
                    auto* element_address = data + static_cast<std::size_t>(index) * inner_size;
                    m_initialize_value(inner, element_address);
                    ++temporary.num;
                    if (!assign_typed_value(
                            inner,
                            element_address,
                            static_cast<std::uint32_t>(inner_kind),
                            elements[index]))
                    {
                        return false;
                    }
                }
            }
            m_copy_complete_value(property, address, &temporary);
            return true;
        }

        if (fixed_size == 0 || fixed_size > sizeof(value.data))
        {
            return false;
        }
        std::memcpy(address, &value.data, fixed_size);
        return true;
    }

    std::int32_t UnrealReflectionApi::write_property(
        std::uint64_t handle,
        const wchar_t* property_name,
        std::uint32_t encoded_property_kind,
        const UnrealValue* value) const
    {
        auto* object = const_cast<void*>(resolve_handle(handle));
        if (object == nullptr || value == nullptr)
        {
            return -1;
        }
        if (property_name == nullptr || *property_name == L'\0')
        {
            return -2;
        }
        if (value->kind != encoded_property_kind)
        {
            return -5;
        }

        try
        {
            auto* property = m_get_property_by_name_in_chain(object, property_name);
            if (property == nullptr)
            {
                return -3;
            }
            const auto* offset = m_get_offset(property);
            if (offset == nullptr || *offset < 0)
            {
                return -4;
            }
            if (decode_kind(encoded_property_kind) == UnrealPropertyKind::Boolean)
            {
                const auto* element_size = m_get_element_size(property);
                if (element_size == nullptr || *element_size != 1)
                {
                    return -4;
                }
                m_set_bool_in_container(property, object, value->data != 0, 0);
                return 0;
            }
            auto* address = static_cast<std::byte*>(object) + *offset;
            return assign_typed_value(property, address, encoded_property_kind, *value) ? 0 : -4;
        }
        catch (...)
        {
            return -6;
        }
    }

    std::int32_t UnrealReflectionApi::read_property(
        std::uint64_t handle,
        const wchar_t* property_name,
        std::uint32_t encoded_property_kind,
        UnrealValue* value) const
    {
        auto* object = const_cast<void*>(resolve_handle(handle));
        if (object == nullptr || value == nullptr)
        {
            return -1;
        }
        *value = {};
        if (property_name == nullptr || *property_name == L'\0')
        {
            return -2;
        }

        try
        {
            auto* property = m_get_property_by_name_in_chain(object, property_name);
            if (property == nullptr)
            {
                return -3;
            }
            const auto* offset = m_get_offset(property);
            if (offset == nullptr || *offset < 0)
            {
                return -4;
            }
            if (decode_kind(encoded_property_kind) == UnrealPropertyKind::Boolean)
            {
                const auto* element_size = m_get_element_size(property);
                if (element_size == nullptr || *element_size != 1)
                {
                    return -4;
                }
                value->kind = encoded_property_kind;
                value->data = m_get_bool_in_container(property, object, 0) ? 1U : 0U;
                return 0;
            }
            const auto* address = static_cast<const std::byte*>(object) + *offset;
            return marshal_typed_value(property, address, encoded_property_kind, *value) ? 0 : -9;
        }
        catch (...)
        {
            return -6;
        }
    }

    std::int32_t UnrealReflectionApi::invoke_zero_parameter(
        std::uint64_t handle,
        const wchar_t* function_name) const
    {
        auto* object = const_cast<void*>(resolve_handle(handle));
        if (object == nullptr)
        {
            return -1;
        }
        if (function_name == nullptr || *function_name == L'\0')
        {
            return -2;
        }

        try
        {
            auto* function = m_get_function_by_name_in_chain(object, function_name);
            if (function == nullptr)
            {
                return -3;
            }
            const auto* parms_size = m_get_parms_size(function);
            if (parms_size == nullptr || *parms_size != 0)
            {
                return -4;
            }
            m_process_event(object, function, nullptr);
            return 0;
        }
        catch (...)
        {
            return -5;
        }
    }

    std::int32_t UnrealReflectionApi::invoke(
        std::uint64_t handle,
        const wchar_t* function_name,
        std::uint32_t parameter_count,
        UnrealParameter* parameters) const
    {
        auto* object = const_cast<void*>(resolve_handle(handle));
        if (object == nullptr)
        {
            return -1;
        }
        if (function_name == nullptr || *function_name == L'\0'
            || (parameter_count != 0 && parameters == nullptr))
        {
            return -2;
        }
        if (parameter_count > UINT8_MAX)
        {
            return -4;
        }

        try
        {
            auto* function = m_get_function_by_name_in_chain(object, function_name);
            if (function == nullptr)
            {
                return -3;
            }
            const auto* parms_size_pointer = m_get_parms_size(function);
            const auto* num_parms_pointer = m_get_num_parms(function);
            const auto* return_offset_pointer = m_get_return_value_offset(function);
            if (parms_size_pointer == nullptr || num_parms_pointer == nullptr || return_offset_pointer == nullptr
                || *num_parms_pointer != parameter_count)
            {
                return -4;
            }

            const auto parms_size = static_cast<std::size_t>(*parms_size_pointer);
            if ((parameter_count == 0) != (parms_size == 0))
            {
                return -4;
            }
            std::vector<std::byte> buffer(parms_size);
            auto* buffer_data = buffer.empty() ? nullptr : buffer.data();
            bool has_return{};
            auto has_flag = [](std::uint32_t flags, UnrealParameterFlags flag)
            {
                return (flags & static_cast<std::uint32_t>(flag)) != 0;
            };

            std::vector<void*> parameter_properties;
            parameter_properties.reserve(parameter_count);
            auto* live_property = parameter_count == 0 ? nullptr : m_get_first_property(function);
            for (std::uint32_t index = 0; index < parameter_count; ++index)
            {
                if (live_property == nullptr)
                {
                    return -4;
                }
                parameter_properties.push_back(live_property);
                live_property = m_get_next_field_as_property(live_property);
            }

            for (std::uint32_t index = 0; index < parameter_count; ++index)
            {
                auto& parameter = parameters[index];
                const auto kind = decode_kind(parameter.kind);
                const auto size = kind == UnrealPropertyKind::Struct && parameter.size > 0
                    ? static_cast<std::size_t>(parameter.size)
                    : expected_value_size(kind);
                const auto* live_offset = m_get_offset(parameter_properties[index]);
                const auto* live_size = m_get_element_size(parameter_properties[index]);
                if (size == 0 || parameter.array_dimension != 1 || parameter.offset < 0
                    || size > maximum_marshaled_struct_size
                    || parameter.size != static_cast<std::int32_t>(size)
                    || static_cast<std::size_t>(parameter.offset) + size > parms_size
                    || live_offset == nullptr || *live_offset != parameter.offset
                    || live_size == nullptr || *live_size != parameter.size)
                {
                    return -5;
                }
                if (has_flag(parameter.flags, UnrealParameterFlags::Return))
                {
                    if (has_return || parameter.offset != *return_offset_pointer)
                    {
                        return -8;
                    }
                    has_return = true;
                }
                if (!has_flag(parameter.flags, UnrealParameterFlags::Input))
                {
                    continue;
                }
                if (parameter.value.kind != parameter.kind
                    || ((kind == UnrealPropertyKind::String
                         || kind == UnrealPropertyKind::Name
                         || kind == UnrealPropertyKind::Text)
                        && !valid_marshaled_input(parameter.value))
                    || (kind == UnrealPropertyKind::Struct
                        && (parameter.value.data == 0 || parameter.value.reserved != size))
                    || (kind == UnrealPropertyKind::Array
                        && (parameter.value.reserved > maximum_marshaled_array_length
                            || (parameter.value.reserved != 0 && parameter.value.data == 0))))
                {
                    return -5;
                }
            }

            FStringCleanup string_cleanup(m_fstring_destructor);
            FStringCleanup text_cleanup(m_ftext_destructor);
            PropertyValueCleanup property_cleanup(m_destroy_value);
            for (std::uint32_t index = 0; index < parameter_count; ++index)
            {
                auto& parameter = parameters[index];
                const auto kind = decode_kind(parameter.kind);
                const auto size = kind == UnrealPropertyKind::Struct
                    ? static_cast<std::size_t>(parameter.size)
                    : expected_value_size(kind);
                const auto is_input = has_flag(parameter.flags, UnrealParameterFlags::Input);

                auto* address = buffer_data + parameter.offset;
                if (kind == UnrealPropertyKind::String)
                {
                    if (is_input)
                    {
                        const auto* text = parameter.value.data == 0
                            ? L""
                            : reinterpret_cast<const wchar_t*>(parameter.value.data);
                        m_fstring_constructor(address, text);
                    }
                    else
                    {
                        m_fstring_default_constructor(address);
                    }
                    string_cleanup.add(address);
                }
                else if (kind == UnrealPropertyKind::Name)
                {
                    if (is_input)
                    {
                        const auto* text = parameter.value.data == 0
                            ? L""
                            : reinterpret_cast<const wchar_t*>(parameter.value.data);
                        m_fname_constructor(address, text, 1, nullptr);
                    }
                    else
                    {
                        m_fname_default_constructor(address);
                    }
                }
                else if (kind == UnrealPropertyKind::Text)
                {
                    if (is_input)
                    {
                        const auto* text = parameter.value.data == 0
                            ? L""
                            : reinterpret_cast<const wchar_t*>(parameter.value.data);
                        m_ftext_constructor(address, text);
                    }
                    else
                    {
                        m_ftext_default_constructor(address);
                    }
                    text_cleanup.add(address);
                }
                else if (kind == UnrealPropertyKind::Array)
                {
                    auto* property = parameter_properties[index];
                    m_initialize_value(property, address);
                    property_cleanup.add(property, address);
                    if (is_input && !assign_typed_value(property, address, parameter.kind, parameter.value))
                    {
                        return -5;
                    }
                }
                else if (!is_input)
                {
                    continue;
                }
                else if (kind == UnrealPropertyKind::Boolean)
                {
                    const auto byte_offset = static_cast<std::uint8_t>(parameter.bool_layout);
                    auto byte_mask = static_cast<std::uint8_t>(parameter.bool_layout >> 8);
                    if (byte_mask == 0)
                    {
                        byte_mask = 1;
                    }
                    if (static_cast<std::size_t>(parameter.offset) + byte_offset >= parms_size)
                    {
                        return -5;
                    }
                    auto* boolean_address = reinterpret_cast<std::uint8_t*>(address) + byte_offset;
                    *boolean_address = parameter.value.data != 0
                        ? static_cast<std::uint8_t>(*boolean_address | byte_mask)
                        : static_cast<std::uint8_t>(*boolean_address & ~byte_mask);
                }
                else if (kind == UnrealPropertyKind::Object)
                {
                    void* target{};
                    if (parameter.value.data != 0)
                    {
                        target = const_cast<void*>(resolve_handle(parameter.value.data));
                        if (target == nullptr)
                        {
                            return -7;
                        }
                    }
                    std::memcpy(address, &target, sizeof(target));
                }
                else if (kind == UnrealPropertyKind::Struct)
                {
                    std::memcpy(address, reinterpret_cast<const void*>(parameter.value.data), size);
                }
                else
                {
                    std::memcpy(address, &parameter.value.data, size);
                }
            }

            m_process_event(object, function, buffer_data);

            OutputAllocationCleanup allocation_cleanup;
            for (std::uint32_t index = 0; index < parameter_count; ++index)
            {
                auto& parameter = parameters[index];
                if (!has_flag(parameter.flags, UnrealParameterFlags::Output)
                    && !has_flag(parameter.flags, UnrealParameterFlags::Return))
                {
                    continue;
                }
                const auto kind = decode_kind(parameter.kind);
                const auto size = kind == UnrealPropertyKind::Struct
                    ? static_cast<std::size_t>(parameter.size)
                    : expected_value_size(kind);
                const auto* address = buffer_data + parameter.offset;
                parameter.value = {parameter.kind, 0, 0};
                if (kind == UnrealPropertyKind::Boolean)
                {
                    const auto byte_offset = static_cast<std::uint8_t>(parameter.bool_layout);
                    auto byte_mask = static_cast<std::uint8_t>(parameter.bool_layout >> 8);
                    if (byte_mask == 0)
                    {
                        byte_mask = 1;
                    }
                    const auto* boolean_address = reinterpret_cast<const std::uint8_t*>(address) + byte_offset;
                    parameter.value.data = (*boolean_address & byte_mask) != 0 ? 1U : 0U;
                }
                else
                {
                    if (!marshal_typed_value(
                            parameter_properties[index],
                            address,
                            parameter.kind,
                            parameter.value))
                    {
                        parameter.value = {};
                        return -9;
                    }
                    if (kind == UnrealPropertyKind::String
                        || kind == UnrealPropertyKind::Name
                        || kind == UnrealPropertyKind::Text
                        || kind == UnrealPropertyKind::Struct
                        || kind == UnrealPropertyKind::Array)
                    {
                        allocation_cleanup.add(parameter.value);
                    }
                }
            }
            allocation_cleanup.commit();
            return 0;
        }
        catch (...)
        {
            return -6;
        }
    }

    void UnrealReflectionApi::set_ready(bool ready)
    {
        m_ready.store(ready, std::memory_order_release);
    }

    bool UnrealReflectionApi::is_available() const
    {
        return m_resolved && m_ready.load(std::memory_order_acquire);
    }

    std::uint64_t UnrealReflectionApi::find_first_of(const wchar_t* class_name) const
    {
        if (!is_available() || class_name == nullptr || *class_name == L'\0')
        {
            return 0;
        }
        return make_handle(m_find_first_of(class_name));
    }

    bool UnrealReflectionApi::is_valid(std::uint64_t handle) const
    {
        return resolve_handle(handle) != nullptr;
    }

    std::uint64_t UnrealReflectionApi::get_class(std::uint64_t handle) const
    {
        const auto* object = resolve_handle(handle);
        if (object == nullptr)
        {
            return 0;
        }
        const auto* class_reference = m_get_class_private(object);
        return class_reference == nullptr ? 0 : make_handle(*class_reference);
    }

    std::int32_t UnrealReflectionApi::get_path_name(
        std::uint64_t handle,
        wchar_t* buffer,
        std::uint32_t capacity,
        std::uint32_t* required) const
    {
        if (required == nullptr)
        {
            return -2;
        }
        *required = 0;

        const auto* object = resolve_handle(handle);
        if (object == nullptr)
        {
            return -1;
        }

        try
        {
            std::wstring path;
            m_get_path_name(object, nullptr, path);
            const auto required_size = path.size() + 1;
            if (required_size > UINT32_MAX)
            {
                return -3;
            }
            *required = static_cast<std::uint32_t>(required_size);
            if (buffer == nullptr || capacity < required_size)
            {
                return 1;
            }

            std::wmemcpy(buffer, path.c_str(), required_size);
            return 0;
        }
        catch (...)
        {
            return -3;
        }
    }

    std::uint64_t UnrealReflectionApi::make_handle(const void* object) const
    {
        if (!is_available() || object == nullptr)
        {
            return 0;
        }

        const auto index = m_get_internal_index(object);
        if (index < 0)
        {
            return 0;
        }
        const auto* item = m_index_to_object(index);
        if (item == nullptr || !m_is_item_valid(item, false))
        {
            return 0;
        }
        const auto* serial = m_get_serial_number(item);
        if (serial == nullptr || *serial < 0)
        {
            return 0;
        }

        return (static_cast<std::uint64_t>(static_cast<std::uint32_t>(*serial)) << 32)
            | (static_cast<std::uint32_t>(index) + 1ULL);
    }

    const void* UnrealReflectionApi::resolve_handle(std::uint64_t handle) const
    {
        if (!is_available() || handle == 0)
        {
            return nullptr;
        }

        const auto encoded_index = static_cast<std::uint32_t>(handle);
        if (encoded_index == 0)
        {
            return nullptr;
        }
        const auto index = static_cast<std::int32_t>(encoded_index - 1);
        const auto expected_serial = static_cast<std::int32_t>(handle >> 32);
        const auto* item = m_index_to_object(index);
        if (item == nullptr || !m_is_item_valid(item, false))
        {
            return nullptr;
        }
        const auto* serial = m_get_serial_number(item);
        if (serial == nullptr || *serial != expected_serial)
        {
            return nullptr;
        }
        const auto* object_reference = m_get_item_object(item);
        return object_reference == nullptr ? nullptr : *object_reference;
    }
}
