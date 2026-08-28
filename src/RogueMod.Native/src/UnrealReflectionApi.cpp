#include "UnrealReflectionApi.hpp"

#include <algorithm>
#include <combaseapi.h>
#include <cwchar>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <tuple>
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
        // FScriptMap/FScriptSet footprint confirmed by the live JMap dump for Deadzone: Rogue
        // 1.4.2.0 (every TMap/TSet property reports element size 80). The Valhalla fork
        // deviates from vanilla UE 5.6.1 (72 bytes); a mismatch disables the family.
        constexpr std::size_t deadzone_script_map_size = 80;
        constexpr std::uint32_t map_key_kind_shift = 8U;
        constexpr std::uint32_t map_value_kind_shift = 16U;
        constexpr std::size_t deadzone_object_ptr_setter_vtable_offset = 0x1e8;
        constexpr std::size_t deadzone_object_getter_vtable_offset = 0x200;
        // FProperty::GetValueTypeHashInternal virtual slot for the pinned Deadzone: Rogue
        // 1.4.2.0 build. The slot is recorded in config/Compatibility/DeadzoneRogue/VTableLayout.ini
        // as the 21st FProperty virtual (index 20). A wrong slot produces incorrect hashes, which
        // degrade map/set lookup but never corrupt iteration or lifetime handling, so writes
        // degrade to a correctness failure rather than a crash.
        constexpr std::size_t deadzone_get_value_type_hash_vtable_offset = 20U * sizeof(void*);
        constexpr std::int32_t index_none = -1;
        constexpr std::uint32_t property_kind_mask = 0xffU;
        constexpr std::uint32_t array_element_kind_shift = 8U;
        constexpr std::size_t lazy_object_storage_size = 24;
        // The live property is a 40-byte FSoftObjectPtr. Its leading 32-byte
        // FSoftObjectPath contains an FTopLevelAssetPath (two FNames) and an FString;
        // the trailing eight bytes are private cache/tag state. Writes never infer or
        // mutate this layout: the game builds and assigns the value through Kismet UFunctions.
        constexpr std::size_t soft_object_storage_size = 40;

        struct LazyObjectWire
        {
            std::byte storage[lazy_object_storage_size];
            std::uint64_t cached_handle;
            std::uint32_t guid_a;
            std::uint32_t guid_b;
            std::uint32_t guid_c;
            std::uint32_t guid_d;
        };

        static_assert(sizeof(LazyObjectWire) == 48);

        struct SoftObjectWire
        {
            std::byte storage[soft_object_storage_size];
            std::uint64_t cached_handle;
            wchar_t* path;
        };

        static_assert(sizeof(SoftObjectWire) == 56);

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

        std::uint32_t decode_array_element_encoded_kind(std::uint32_t encoded_kind)
        {
            return encoded_kind >> array_element_kind_shift;
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
            case UnrealPropertyKind::Name:
            case UnrealPropertyKind::WeakObject: return 8;
            case UnrealPropertyKind::LazyObject: return lazy_object_storage_size;
            case UnrealPropertyKind::SoftObject: return soft_object_storage_size;
            case UnrealPropertyKind::Interface: return 16;
            case UnrealPropertyKind::String:
            case UnrealPropertyKind::Text:
            case UnrealPropertyKind::Array: return 16;
            default: return 0;
            }
        }

        std::size_t expected_parameter_size(UnrealPropertyKind kind, std::int32_t declared_size)
        {
            if (kind == UnrealPropertyKind::Struct || kind == UnrealPropertyKind::Optional)
            {
                return declared_size > 0 ? static_cast<std::size_t>(declared_size) : 0U;
            }
            if (kind == UnrealPropertyKind::Map || kind == UnrealPropertyKind::Set)
            {
                return declared_size == static_cast<std::int32_t>(deadzone_script_map_size)
                    ? deadzone_script_map_size
                    : 0U;
            }
            return expected_value_size(kind);
        }

        void free_marshaled_value(UnrealValue& value)
        {
            const auto kind = decode_kind(value.kind);
            if ((kind == UnrealPropertyKind::Array
                 || kind == UnrealPropertyKind::Set
                 || kind == UnrealPropertyKind::Struct)
                && value.data != 0)
            {
                if (value.reserved <= maximum_marshaled_array_length)
                {
                    if (value.reserved > 0)
                    {
                        auto* elements = reinterpret_cast<UnrealValue*>(value.data);
                        for (std::uint32_t index = 0; index < value.reserved; ++index)
                        {
                            free_marshaled_value(elements[index]);
                        }
                    }
                }
                CoTaskMemFree(reinterpret_cast<void*>(value.data));
            }
            else if (kind == UnrealPropertyKind::Map && value.data != 0)
            {
                if (value.reserved <= maximum_marshaled_array_length)
                {
                    auto* entries = reinterpret_cast<UnrealValue*>(value.data);
                    for (std::uint32_t index = 0; index < value.reserved; ++index)
                    {
                        free_marshaled_value(entries[index * 2]);
                        free_marshaled_value(entries[index * 2 + 1]);
                    }
                }
                CoTaskMemFree(reinterpret_cast<void*>(value.data));
            }
            else if (kind == UnrealPropertyKind::Optional && value.data != 0)
            {
                auto* nested = reinterpret_cast<UnrealValue*>(value.data);
                free_marshaled_value(*nested);
                CoTaskMemFree(nested);
            }
            else if (kind == UnrealPropertyKind::SoftObject && value.data != 0)
            {
                auto* wire = reinterpret_cast<SoftObjectWire*>(value.data);
                if (wire->path != nullptr)
                {
                    CoTaskMemFree(wire->path);
                }
                CoTaskMemFree(wire);
            }
            else if ((kind == UnrealPropertyKind::String
                      || kind == UnrealPropertyKind::Name
                      || kind == UnrealPropertyKind::Text
                      || kind == UnrealPropertyKind::LazyObject)
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

        template <typename Function>
        class ScopeExit
        {
          public:
            explicit ScopeExit(Function function) : m_function(std::move(function)) {}
            ~ScopeExit() { m_function(); }

            ScopeExit(const ScopeExit&) = delete;
            ScopeExit& operator=(const ScopeExit&) = delete;

          private:
            Function m_function;
        };

        template <typename Function>
        Function load_export(HMODULE module, const char* name)
        {
            return reinterpret_cast<Function>(GetProcAddress(module, name));
        }

        bool executable_span(const void* address, std::size_t size)
        {
            if (address == nullptr || size == 0)
            {
                return false;
            }
            MEMORY_BASIC_INFORMATION information{};
            if (VirtualQuery(address, &information, sizeof(information)) == 0
                || information.State != MEM_COMMIT)
            {
                return false;
            }
            const auto protection = information.Protect & 0xffU;
            const auto executable = protection == PAGE_EXECUTE
                || protection == PAGE_EXECUTE_READ
                || protection == PAGE_EXECUTE_READWRITE
                || protection == PAGE_EXECUTE_WRITECOPY;
            const auto start = reinterpret_cast<std::uintptr_t>(address);
            const auto region_start = reinterpret_cast<std::uintptr_t>(information.BaseAddress);
            if (!executable || start < region_start)
            {
                return false;
            }
            const auto offset = start - region_start;
            return offset <= information.RegionSize
                && size <= information.RegionSize - offset;
        }

        bool __cdecl validate_deadzone_object_accessors(const void* setter, const void* getter)
        {
            // Deadzone: Rogue 1.4.2.0 / UE 5.6.1. These are structural fragments from the
            // disassembled game functions, not UE4SS wrappers. The setter dereferences the
            // hidden TObjectPtr temporary in R8, runs the incremental-GC write barrier, and
            // commits the pointer through RDX. The getter returns [RDX]. A game update that
            // changes either implementation disables writes instead of calling an unknown slot.
            if (!executable_span(setter, 0x50) || !executable_span(getter, 4))
            {
                return false;
            }
            const auto* code = static_cast<const std::uint8_t*>(setter);
            const std::uint8_t prologue[]{0x48, 0x89, 0x5c, 0x24, 0x08, 0x57, 0x48, 0x83, 0xec, 0x20};
            const std::uint8_t arguments[]{0x49, 0x8b, 0xd8, 0x48, 0x8b, 0xfa};
            const std::uint8_t load_target[]{0x49, 0x8b, 0x08};
            const std::uint8_t barrier_commit[]{0x48, 0x8b, 0x03, 0x48, 0x89, 0x07};
            const std::uint8_t null_commit[]{0x48, 0x89, 0x0a};
            const std::uint8_t direct_load[]{0x49, 0x8b, 0x00};
            const std::uint8_t direct_commit[]{0x48, 0x89, 0x02};
            const std::uint8_t getter_body[]{0x48, 0x8b, 0x02, 0xc3};
            return std::memcmp(code, prologue, sizeof(prologue)) == 0
                && std::memcmp(code + 0x11, arguments, sizeof(arguments)) == 0
                && std::memcmp(code + 0x19, load_target, sizeof(load_target)) == 0
                && std::memcmp(code + 0x26, barrier_commit, sizeof(barrier_commit)) == 0
                && std::memcmp(code + 0x37, null_commit, sizeof(null_commit)) == 0
                && std::memcmp(code + 0x45, direct_load, sizeof(direct_load)) == 0
                && std::memcmp(code + 0x4d, direct_commit, sizeof(direct_commit)) == 0
                && std::memcmp(getter, getter_body, sizeof(getter_body)) == 0;
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

    bool UnrealReflectionApi::resolve(
        HMODULE ue4ss_module,
        void(__cdecl* log)(std::int32_t level, const wchar_t* message))
    {
        if (ue4ss_module == nullptr)
        {
            return false;
        }

        m_log = log;

        m_find_first_of = load_export<find_first_of_fn>(
            ue4ss_module,
            "?FindFirstOf@UObjectGlobals@Unreal@RC@@YAPEAVUObject@23@PEB_W@Z");
        m_static_find_object = load_export<static_find_object_fn>(
            ue4ss_module,
            "?StaticFindObject_InternalSlow@UObjectGlobals@Unreal@RC@@YAPEAVUObject@23@PEAVUClass@23@PEAV423@PEB_W_N@Z");
        m_find_all_of = load_export<find_all_of_fn>(
            ue4ss_module,
            "?FindAllOf@UObjectGlobals@Unreal@RC@@YAXPEB_WAEAV?$vector@PEAVUObject@Unreal@RC@@V?$allocator@PEAVUObject@Unreal@RC@@@std@@@std@@@Z");
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
        m_get_key_prop = load_export<get_key_prop_fn>(
            ue4ss_module,
            "?GetKeyProp@FMapProperty@Unreal@RC@@QEBAAEAPEBVFProperty@23@XZ");
        m_get_value_prop = load_export<get_value_prop_fn>(
            ue4ss_module,
            "?GetValueProp@FMapProperty@Unreal@RC@@QEBAAEAPEBVFProperty@23@XZ");
        m_get_element_prop = load_export<get_element_prop_fn>(
            ue4ss_module,
            "?GetElementProp@FSetProperty@Unreal@RC@@QEBAAEAPEBVFProperty@23@XZ");
        m_get_map_layout = load_export<get_map_layout_fn>(
            ue4ss_module,
            "?GetMapLayout@FMapProperty@Unreal@RC@@QEBAAEBUFScriptMapLayout@23@XZ");
        m_get_set_layout = load_export<get_set_layout_fn>(
            ue4ss_module,
            "?GetSetLayout@FSetProperty@Unreal@RC@@QEBAAEBUFScriptSetLayout@23@XZ");
        m_get_optional_value_property = load_export<get_optional_value_property_fn>(
            ue4ss_module,
            "?GetValueProperty@FOptionalProperty@Unreal@RC@@QEBAAEAPEAVFProperty@23@XZ");
        m_optional_is_set = load_export<optional_is_set_fn>(
            ue4ss_module,
            "?IsSet@FOptionalProperty@Unreal@RC@@QEBA_NPEBX@Z");
        m_optional_get_value_pointer_for_read_if_set = load_export<optional_get_value_pointer_for_read_if_set_fn>(
            ue4ss_module,
            "?GetValuePointerForReadIfSet@FOptionalProperty@Unreal@RC@@QEBAPEBXPEBX@Z");
        m_optional_mark_set_and_get_initialized_value_pointer = load_export<optional_mark_set_and_get_initialized_value_pointer_fn>(
            ue4ss_module,
            "?MarkSetAndGetInitializedValuePointerToReplace@FOptionalProperty@Unreal@RC@@QEBAPEAXPEAX@Z");
        m_optional_mark_unset = load_export<optional_mark_unset_fn>(
            ue4ss_module,
            "?MarkUnset@FOptionalProperty@Unreal@RC@@QEBAXPEAX@Z");
        m_fweak_object_default_constructor = load_export<fweak_object_default_constructor_fn>(
            ue4ss_module,
            "??0FWeakObjectPtr@Unreal@RC@@QEAA@XZ");
        m_fweak_object_get = load_export<fweak_object_get_fn>(
            ue4ss_module,
            "?Get@FWeakObjectPtr@Unreal@RC@@QEBAPEAVUObject@23@XZ");
        m_fweak_object_assign = load_export<fweak_object_assign_fn>(
            ue4ss_module,
            "??4FWeakObjectPtr@Unreal@RC@@QEAAXPEBVUObject@12@@Z");
        m_fweak_object_reset = load_export<fweak_object_reset_fn>(
            ue4ss_module,
            "?Reset@FWeakObjectPtr@Unreal@RC@@QEAAXXZ");
        m_lazy_object_set_value = load_export<lazy_object_set_value_fn>(
            ue4ss_module,
            "?SetPropertyValue@?$TPropertyTypeFundamentals@UFLazyObjectPtr@Unreal@RC@@@Unreal@RC@@SAXPEAXAEBUFLazyObjectPtr@23@@Z");
        m_soft_object_destroy_value = load_export<soft_object_destroy_value_fn>(
            ue4ss_module,
            "?DestroyPropertyValue@?$TPropertyTypeFundamentals@UFSoftObjectPtr@Unreal@RC@@@Unreal@RC@@SAXPEAX@Z");
        m_fmemory_malloc = load_export<fmemory_malloc_fn>(
            ue4ss_module,
            "?Malloc@FMemory@Unreal@RC@@SAPEAX_KI@Z");
        m_fmemory_free = load_export<fmemory_free_fn>(
            ue4ss_module,
            "?Free@FMemory@Unreal@RC@@SAXPEAX@Z");
        m_initialize_property_value = load_export<initialize_property_value_fn>(
            ue4ss_module,
            "?InitializeValue@FProperty@Unreal@RC@@QEBAXPEAX@Z");
        m_destroy_property_value = load_export<destroy_property_value_fn>(
            ue4ss_module,
            "?DestroyValue@FProperty@Unreal@RC@@QEBAXPEAX@Z");
        m_construct_object_parameters_ctor = load_export<construct_object_parameters_ctor_fn>(
            ue4ss_module,
            "??0FStaticConstructObjectParameters@Unreal@RC@@QEAA@PEBVUClass@12@PEAVUObject@12@@Z");
        m_static_construct_object = load_export<static_construct_object_fn>(
            ue4ss_module,
            "?StaticConstructObject@UObjectGlobals@Unreal@RC@@YAPEAVUObject@23@AEBUFStaticConstructObjectParameters@23@@Z");
        m_object_get_world = load_export<object_get_world_fn>(
            ue4ss_module,
            "?GetWorld@UObject@Unreal@RC@@QEBAPEAVUWorld@23@XZ");
        m_world_spawn_actor = load_export<world_spawn_actor_fn>(
            ue4ss_module,
            "?SpawnActor@UWorld@Unreal@RC@@QEAAPEAVAActor@23@PEAVUClass@23@PEBUFVector@23@PEBUFRotator@23@@Z");
        m_struct_get_struct = load_export<struct_get_struct_fn>(
            ue4ss_module,
            "?GetStruct@FStructProperty@Unreal@RC@@QEAAAEAV?$TObjectPtr@VUScriptStruct@Unreal@RC@@@23@XZ");
        m_field_get_class = load_export<field_get_class_fn>(
            ue4ss_module,
            "?GetClassPrivate@FField@Unreal@RC@@AEAAAEAPEAVFFieldClass@23@XZ");
        m_field_class_get_fname = load_export<field_class_get_fname_fn>(
            ue4ss_module,
            "?GetFName@FFieldClass@Unreal@RC@@QEAAAEAVFName@23@XZ");

        m_mutation_backend.configure({
            m_initialize_property_value,
            m_destroy_property_value,
            m_fmemory_malloc,
            deadzone_object_ptr_setter_vtable_offset,
            deadzone_object_getter_vtable_offset,
            validate_deadzone_object_accessors,
            m_log});

        using process_event_callback = std::function<void(void*, void*, void*)>;
        using register_process_event_callback_fn = void(__cdecl*)(process_event_callback);
        const auto register_process_event_pre = load_export<register_process_event_callback_fn>(
            ue4ss_module,
            "?RegisterProcessEventPreCallback@Hook@Unreal@RC@@YAXV?$function@$$A6AXPEAVUObject@Unreal@RC@@PEAVUFunction@23@PEAX@Z@std@@@Z");
        const auto register_process_event_post = load_export<register_process_event_callback_fn>(
            ue4ss_module,
            "?RegisterProcessEventPostCallback@Hook@Unreal@RC@@YAXV?$function@$$A6AXPEAVUObject@Unreal@RC@@PEAVUFunction@23@PEAX@Z@std@@@Z");
        if (register_process_event_pre != nullptr && register_process_event_post != nullptr)
        {
            try
            {
                register_process_event_pre(process_event_callback{
                    [this](void* object, void* function, void* parameters)
                    {
                        dispatch_hook(UnrealHookPhase::Pre, object, function, parameters);
                    }});
                register_process_event_post(process_event_callback{
                    [this](void* object, void* function, void* parameters)
                    {
                        dispatch_hook(UnrealHookPhase::Post, object, function, parameters);
                    }});
                m_process_event_hooks_resolved = true;
            }
            catch (...)
            {
                m_process_event_hooks_resolved = false;
            }
        }

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
            && m_fmemory_malloc != nullptr
            && m_fmemory_free != nullptr
            && m_mutation_backend.is_available()
            && m_mutation_backend.can_access_objects();
        return m_resolved;
    }

    std::uint32_t UnrealReflectionApi::capabilities() const
    {
        // Mirrors RogueMod.Abstractions.UnrealReflectionCapabilities.
        return is_available()
            ? (1U << 0) | (1U << 1) | (1U << 2) | (1U << 3)
                | (m_find_all_of != nullptr ? (1U << 4) : 0U)
                | (1U << 5)
                | (m_get_optional_value_property != nullptr
                       && m_optional_is_set != nullptr
                       && m_optional_get_value_pointer_for_read_if_set != nullptr
                       && m_optional_mark_set_and_get_initialized_value_pointer != nullptr
                       && m_optional_mark_unset != nullptr
                    ? (1U << 6)
                    : 0U)
                | (m_fweak_object_default_constructor != nullptr
                       && m_fweak_object_get != nullptr
                       && m_fweak_object_assign != nullptr
                       && m_fweak_object_reset != nullptr
                    ? (1U << 7)
                    : 0U)
                | (m_lazy_object_set_value != nullptr
                       && m_fweak_object_get != nullptr
                    ? (1U << 8)
                    : 0U)
                | (m_process_event_hooks_resolved && m_static_find_object != nullptr
                    ? (1U << 9)
                    : 0U)
                | (m_static_construct_object != nullptr
                       && m_construct_object_parameters_ctor != nullptr
                    ? (1U << 10)
                    : 0U)
                | (m_object_get_world != nullptr
                       && m_world_spawn_actor != nullptr
                    ? (1U << 11)
                    : 0U)
                | (m_static_find_object != nullptr
                       && m_get_function_by_name_in_chain != nullptr
                       && m_get_parms_size != nullptr
                       && m_get_num_parms != nullptr
                       && m_get_first_property != nullptr
                       && m_get_next_field_as_property != nullptr
                       && m_get_offset != nullptr
                       && m_get_element_size != nullptr
                       && m_process_event != nullptr
                       && m_fstring_constructor != nullptr
                       && m_fstring_destructor != nullptr
                       && m_fname_constructor != nullptr
                       && m_initialize_property_value != nullptr
                       && m_destroy_property_value != nullptr
                       && m_soft_object_destroy_value != nullptr
                    ? (1U << 12)
                    : 0U)
                | (m_get_key_prop != nullptr
                       && m_get_value_prop != nullptr
                       && m_get_element_prop != nullptr
                       && m_get_map_layout != nullptr
                       && m_get_set_layout != nullptr
                    ? (1U << 14)
                    : 0U)
                | (m_get_key_prop != nullptr
                       && m_get_value_prop != nullptr
                       && m_get_element_prop != nullptr
                       && m_get_map_layout != nullptr
                       && m_get_set_layout != nullptr
                       && m_fmemory_malloc != nullptr
                       && m_initialize_property_value != nullptr
                       && m_destroy_property_value != nullptr
                    ? (1U << 15)
                    : 0U)
                | (1U << 13)
            : 0U;
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
        if ((kind != UnrealPropertyKind::Struct
             && kind != UnrealPropertyKind::Optional
             && kind != UnrealPropertyKind::Map
             && kind != UnrealPropertyKind::Set
             && fixed_size != element_size)
            || ((kind == UnrealPropertyKind::Struct || kind == UnrealPropertyKind::Optional)
                && element_size > maximum_marshaled_struct_size))
        {
            return false;
        }
        if ((kind == UnrealPropertyKind::Map || kind == UnrealPropertyKind::Set)
            && element_size != deadzone_script_map_size)
        {
            return false;
        }

        value.kind = encoded_kind;
        if (kind == UnrealPropertyKind::Map)
        {
            return marshal_map_value(property, address, encoded_kind, value);
        }
        if (kind == UnrealPropertyKind::Set)
        {
            return marshal_set_value(property, address, encoded_kind, value);
        }
        if (kind == UnrealPropertyKind::Boolean)
        {
            value.data = m_get_bool_in_container(property, address, 0) ? 1U : 0U;
            return true;
        }
        if (kind == UnrealPropertyKind::Object)
        {
            void* target{};
            if (!m_mutation_backend.try_read_object(property, address, target))
            {
                return false;
            }
            value.data = make_handle(target);
            return true;
        }
        if (kind == UnrealPropertyKind::Interface)
        {
            // FScriptInterface is a 16-byte pair: a raw UObject* object pointer at +0 and an
            // IInterface* interface pointer at +8. Managed transport carries the object only;
            // the engine lazily re-resolves the interface pointer from the object when needed.
            const auto* object_pointer = *static_cast<void* const*>(address);
            value.data = make_handle(object_pointer);
            return true;
        }
        if (kind == UnrealPropertyKind::SoftObject)
        {
            if (m_static_find_object == nullptr || m_get_function_by_name_in_chain == nullptr
                || m_get_parms_size == nullptr || m_get_num_parms == nullptr
                || m_get_first_property == nullptr || m_get_next_field_as_property == nullptr
                || m_get_offset == nullptr || m_get_element_size == nullptr
                || m_process_event == nullptr || m_fstring_destructor == nullptr)
            {
                return false;
            }
            auto* library = m_static_find_object(
                nullptr,
                nullptr,
                L"/Script/Engine.Default__KismetSystemLibrary",
                false);
            auto* function = library == nullptr
                ? nullptr
                : m_get_function_by_name_in_chain(library, L"Conv_SoftObjectReferenceToString");
            const auto* parms_size = function == nullptr ? nullptr : m_get_parms_size(function);
            const auto* num_parms = function == nullptr ? nullptr : m_get_num_parms(function);
            auto* input_property = function == nullptr ? nullptr : m_get_first_property(function);
            auto* return_property = input_property == nullptr
                ? nullptr
                : m_get_next_field_as_property(input_property);
            const auto* input_offset = input_property == nullptr ? nullptr : m_get_offset(input_property);
            const auto* input_size = input_property == nullptr ? nullptr : m_get_element_size(input_property);
            const auto* return_offset = return_property == nullptr ? nullptr : m_get_offset(return_property);
            const auto* return_size = return_property == nullptr ? nullptr : m_get_element_size(return_property);
            if (function == nullptr || parms_size == nullptr || *parms_size != 56
                || num_parms == nullptr || *num_parms != 2
                || input_offset == nullptr || *input_offset != 0
                || input_size == nullptr || *input_size != 40
                || return_offset == nullptr || *return_offset != 40
                || return_size == nullptr || *return_size != 16)
            {
                return false;
            }

            std::vector<std::byte> buffer(56);
            // The Kismet input is const-ref. A shallow opaque copy is valid for the duration
            // of ProcessEvent and is deliberately not destroyed because it does not own the
            // live property's FString allocation.
            std::memcpy(buffer.data(), address, 40);
            auto* string_result = buffer.data() + 40;
            m_process_event(library, function, buffer.data());
            ScopeExit string_cleanup([&]() { m_fstring_destructor(string_result); });

            UnrealValue path_value{};
            if (!marshal_fstring(string_result, path_value))
            {
                return false;
            }
            auto* wire = static_cast<SoftObjectWire*>(CoTaskMemAlloc(sizeof(SoftObjectWire)));
            if (wire == nullptr)
            {
                free_marshaled_value(path_value);
                return false;
            }
            *wire = {};
            wire->path = reinterpret_cast<wchar_t*>(path_value.data);

            value.reserved = sizeof(SoftObjectWire);
            value.data = reinterpret_cast<std::uint64_t>(wire);
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
            return marshal_struct_fields(property, address, encoded_kind, value);
        }
        if (kind == UnrealPropertyKind::Array)
        {
            auto** inner_pointer = m_get_array_inner(property);
            auto* inner = inner_pointer == nullptr ? nullptr : *inner_pointer;
            const auto inner_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
            const auto inner_kind = decode_kind(inner_encoded_kind);
            if (inner == nullptr || inner_kind == static_cast<UnrealPropertyKind>(0))
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
                        inner_encoded_kind,
                        elements[index]))
                {
                    free_marshaled_value(value);
                    return false;
                }
            }
            return true;
        }
        if (kind == UnrealPropertyKind::WeakObject)
        {
            if (m_fweak_object_get == nullptr)
            {
                return false;
            }
            value.data = make_handle(m_fweak_object_get(address));
            return true;
        }
        if (kind == UnrealPropertyKind::LazyObject)
        {
            if (m_fweak_object_get == nullptr || element_size != lazy_object_storage_size)
            {
                return false;
            }
            auto* wire = static_cast<LazyObjectWire*>(CoTaskMemAlloc(sizeof(LazyObjectWire)));
            if (wire == nullptr)
            {
                return false;
            }
            std::memcpy(wire->storage, address, lazy_object_storage_size);
            wire->cached_handle = make_handle(m_fweak_object_get(address));

            const auto* guid = static_cast<const std::byte*>(address) + sizeof(std::uint64_t);
            std::memcpy(&wire->guid_a, guid, sizeof(std::uint32_t) * 4);
            value.reserved = sizeof(LazyObjectWire);
            value.data = reinterpret_cast<std::uint64_t>(wire);
            return true;
        }
        if (kind == UnrealPropertyKind::Optional)
        {
            if (m_get_optional_value_property == nullptr
                || m_optional_is_set == nullptr
                || m_optional_get_value_pointer_for_read_if_set == nullptr)
            {
                return false;
            }
            auto** value_property_pointer = m_get_optional_value_property(property);
            auto* value_property = value_property_pointer == nullptr ? nullptr : *value_property_pointer;
            const auto value_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
            if (value_property == nullptr
                || decode_kind(value_encoded_kind) == static_cast<UnrealPropertyKind>(0))
            {
                return false;
            }
            if (!m_optional_is_set(property, address))
            {
                value.reserved = 0;
                value.data = 0;
                return true;
            }
            const auto* value_address = m_optional_get_value_pointer_for_read_if_set(property, address);
            if (value_address == nullptr)
            {
                return false;
            }
            auto* nested = static_cast<UnrealValue*>(CoTaskMemAlloc(sizeof(UnrealValue)));
            if (nested == nullptr)
            {
                return false;
            }
            *nested = {};
            value.reserved = 1;
            value.data = reinterpret_cast<std::uint64_t>(nested);
            if (!marshal_typed_value(value_property, value_address, value_encoded_kind, *nested))
            {
                free_marshaled_value(value);
                return false;
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

    bool UnrealReflectionApi::validate_script_set_layout(
        const ScriptContainers::SetLayout& layout) const
    {
        // These are the fields the bridge reads during sparse iteration; a game update that
        // moves any of them disables the map/set family instead of dereferencing bad offsets.
        return layout.hash_next_id_offset >= 0
            && layout.hash_index_offset >= 0
            && layout.size > 0
            && layout.sparse_array_layout.alignment > 0
            && layout.sparse_array_layout.size > 0
            && layout.sparse_array_layout.size >= layout.size
            && static_cast<std::size_t>(layout.sparse_array_layout.size) <= maximum_marshaled_struct_size;
    }

    bool UnrealReflectionApi::read_script_set(
        const void* container,
        const ScriptContainers::SetLayout& layout,
        std::int32_t& max_index,
        std::int32_t& num) const
    {
        // FScriptSet stores its elements in a TScriptSparseArray (vanilla UE 5.6.1 layout):
        // Data{ptr,num,max} begins at 0. AllocationFlags begins at 16 and uses
        // FDefaultBitArrayAllocator (four inline uint32 words, then a secondary allocation
        // pointer, NumBits and MaxBits). The free-list fields begin at 48. Iteration only
        // needs Data and AllocationFlags, so the live count is derived from allocation bits.
        const auto* sparse = static_cast<const ScriptContainers::ScriptSparseArray*>(container);
        max_index = sparse->data.num;
        if (max_index < 0 || static_cast<std::size_t>(max_index) > maximum_marshaled_array_length)
        {
            return false;
        }
        const auto& flags = sparse->allocation_flags;
        if (max_index == 0)
        {
            if (sparse->data.data != nullptr || flags.num_bits != 0)
            {
                return false;
            }
            num = 0;
            return true;
        }
        const auto* allocation_words = flags.data();
        if (flags.num_bits < max_index)
        {
            return false;
        }
        std::int32_t count = 0;
        for (std::int32_t index = 0; index < max_index; ++index)
        {
            const auto word = static_cast<std::size_t>(index) / 32U;
            const auto bit = static_cast<std::uint32_t>(index) % 32U;
            if ((allocation_words[word] & (1U << bit)) != 0)
            {
                ++count;
            }
        }
        num = count;
        return true;
    }

    bool UnrealReflectionApi::script_set_element(
        const void* container,
        const ScriptContainers::SetLayout& layout,
        std::int32_t index,
        const void*& element) const
    {
        const auto* sparse = static_cast<const ScriptContainers::ScriptSparseArray*>(container);
        if (index < 0 || index >= sparse->data.num || sparse->data.data == nullptr)
        {
            return false;
        }
        const auto& flags = sparse->allocation_flags;
        if (index >= flags.num_bits)
        {
            return false;
        }
        const auto* allocation_words = flags.data();
        const auto word = static_cast<std::size_t>(index) / 32U;
        const auto bit = static_cast<std::uint32_t>(index) % 32U;
        if ((allocation_words[word] & (1U << bit)) == 0)
        {
            return false;
        }
        const auto stride = static_cast<std::size_t>(layout.sparse_array_layout.size);
        element = static_cast<const std::byte*>(sparse->data.data) + stride * static_cast<std::size_t>(index);
        return true;
    }

    bool UnrealReflectionApi::marshal_set_value(
        void* property,
        const void* address,
        std::uint32_t encoded_kind,
        UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr)
        {
            return false;
        }
        const auto* element_pointer = m_get_element_prop(property);
        auto* element_prop = element_pointer == nullptr ? nullptr : *element_pointer;
        const auto* set_layout_pointer = m_get_set_layout(property);
        if (element_prop == nullptr || set_layout_pointer == nullptr
            || !validate_script_set_layout(*set_layout_pointer))
        {
            return false;
        }
        const auto element_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
        const auto element_kind = decode_kind(element_encoded_kind);
        if (element_kind == static_cast<UnrealPropertyKind>(0))
        {
            return false;
        }
        const auto* element_size_pointer = m_get_element_size(element_prop);
        if (element_size_pointer == nullptr || *element_size_pointer <= 0)
        {
            return false;
        }
        const auto element_size = static_cast<std::size_t>(*element_size_pointer);
        const auto element_fixed_size = expected_value_size(element_kind);
        if ((element_kind != UnrealPropertyKind::Struct
             && element_kind != UnrealPropertyKind::Array
             && element_fixed_size != element_size)
            || (element_kind == UnrealPropertyKind::Array && element_size != 16)
            || (element_kind == UnrealPropertyKind::Struct && element_size > maximum_marshaled_struct_size))
        {
            return false;
        }

        std::int32_t max_index = 0;
        std::int32_t num = 0;
        if (!read_script_set(address, *set_layout_pointer, max_index, num))
        {
            return false;
        }
        if (static_cast<std::size_t>(num) > maximum_marshaled_array_length
            || static_cast<std::size_t>(num) > maximum_marshaled_array_bytes / sizeof(UnrealValue))
        {
            return false;
        }
        if (num == 0)
        {
            return true;
        }
        const auto wire_size = static_cast<std::size_t>(num) * sizeof(UnrealValue);
        auto* elements = static_cast<UnrealValue*>(CoTaskMemAlloc(wire_size));
        if (elements == nullptr)
        {
            value = {};
            return false;
        }
        std::memset(elements, 0, wire_size);
        value.reserved = static_cast<std::uint32_t>(num);
        value.data = reinterpret_cast<std::uint64_t>(elements);

        std::int32_t written = 0;
        for (std::int32_t index = 0; index < max_index; ++index)
        {
            const void* element = nullptr;
            if (!script_set_element(address, *set_layout_pointer, index, element))
            {
                continue;
            }
            if (written >= num)
            {
                free_marshaled_value(value);
                value = {};
                return false;
            }
            if (!marshal_typed_value(element_prop, element, element_encoded_kind, elements[written]))
            {
                free_marshaled_value(value);
                value = {};
                return false;
            }
            ++written;
        }
        if (written != num)
        {
            free_marshaled_value(value);
            value = {};
            return false;
        }
        return true;
    }

    bool UnrealReflectionApi::marshal_map_value(
        void* property,
        const void* address,
        std::uint32_t encoded_kind,
        UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr)
        {
            return false;
        }
        const auto* key_pointer = m_get_key_prop(property);
        auto* key_prop = key_pointer == nullptr ? nullptr : *key_pointer;
        const auto* value_pointer = m_get_value_prop(property);
        auto* value_prop = value_pointer == nullptr ? nullptr : *value_pointer;
        const auto* map_layout_pointer = m_get_map_layout(property);
        if (key_prop == nullptr || value_prop == nullptr || map_layout_pointer == nullptr
            || map_layout_pointer->value_offset < 0
            || !validate_script_set_layout(map_layout_pointer->set_layout))
        {
            return false;
        }
        const auto key_encoded_kind = (encoded_kind >> map_key_kind_shift) & 0xffU;
        const auto value_encoded_kind = encoded_kind >> map_value_kind_shift;
        const auto key_kind = decode_kind(key_encoded_kind);
        const auto value_kind = decode_kind(value_encoded_kind);
        if (key_kind == static_cast<UnrealPropertyKind>(0)
            || value_kind == static_cast<UnrealPropertyKind>(0))
        {
            return false;
        }
        const auto* key_size_pointer = m_get_element_size(key_prop);
        const auto* value_size_pointer = m_get_element_size(value_prop);
        if (key_size_pointer == nullptr || *key_size_pointer <= 0
            || value_size_pointer == nullptr || *value_size_pointer <= 0)
        {
            return false;
        }
        const auto key_size = static_cast<std::size_t>(*key_size_pointer);
        const auto value_size = static_cast<std::size_t>(*value_size_pointer);
        const auto key_fixed_size = expected_value_size(key_kind);
        const auto value_fixed_size = expected_value_size(value_kind);
        if ((key_kind != UnrealPropertyKind::Struct && key_fixed_size != key_size)
            || (key_kind == UnrealPropertyKind::Struct && key_size > maximum_marshaled_struct_size)
            || (value_kind != UnrealPropertyKind::Struct
                && value_kind != UnrealPropertyKind::Array
                && value_fixed_size != value_size)
            || (value_kind == UnrealPropertyKind::Array && value_size != 16)
            || (value_kind == UnrealPropertyKind::Struct && value_size > maximum_marshaled_struct_size))
        {
            return false;
        }

        std::int32_t max_index = 0;
        std::int32_t num = 0;
        if (!read_script_set(address, map_layout_pointer->set_layout, max_index, num))
        {
            return false;
        }
        if (static_cast<std::size_t>(num) > maximum_marshaled_array_length
            || static_cast<std::size_t>(num) > maximum_marshaled_array_bytes / (2U * sizeof(UnrealValue)))
        {
            return false;
        }
        if (num == 0)
        {
            return true;
        }
        const auto wire_size = static_cast<std::size_t>(num) * 2U * sizeof(UnrealValue);
        auto* entries = static_cast<UnrealValue*>(CoTaskMemAlloc(wire_size));
        if (entries == nullptr)
        {
            value = {};
            return false;
        }
        std::memset(entries, 0, wire_size);
        value.reserved = static_cast<std::uint32_t>(num);
        value.data = reinterpret_cast<std::uint64_t>(entries);

        const auto value_offset = static_cast<std::size_t>(map_layout_pointer->value_offset);
        std::int32_t written = 0;
        for (std::int32_t index = 0; index < max_index; ++index)
        {
            const void* pair = nullptr;
            if (!script_set_element(address, map_layout_pointer->set_layout, index, pair))
            {
                continue;
            }
            if (written >= num)
            {
                free_marshaled_value(value);
                value = {};
                return false;
            }
            const auto* pair_bytes = static_cast<const std::byte*>(pair);
            const auto slot = static_cast<std::size_t>(written) * 2U;
            if (!marshal_typed_value(key_prop, pair_bytes, key_encoded_kind, entries[slot])
                || !marshal_typed_value(value_prop, pair_bytes + value_offset, value_encoded_kind, entries[slot + 1]))
            {
                free_marshaled_value(value);
                value = {};
                return false;
            }
            ++written;
        }
        if (written != num)
        {
            free_marshaled_value(value);
            value = {};
            return false;
        }
        return true;
    }

    bool UnrealReflectionApi::get_value_type_hash(
        void* property,
        const void* address,
        std::uint32_t& hash) const
    {
        if (property == nullptr || address == nullptr)
        {
            return false;
        }
        const auto* vtable = *static_cast<void***>(property);
        if (vtable == nullptr)
        {
            return false;
        }
        const auto function = reinterpret_cast<get_value_type_hash_fn>(
            vtable[deadzone_get_value_type_hash_vtable_offset / sizeof(void*)]);
        if (function == nullptr || !executable_span(reinterpret_cast<const void*>(function), 4))
        {
            return false;
        }
        hash = function(property, address);
        return true;
    }

    std::int32_t UnrealReflectionApi::assign_script_container(
        void* property,
        void* address,
        std::uint32_t count,
        void* hash_property,
        const ScriptContainers::SetLayout& set_layout,
        const std::function<bool(std::size_t index, void* block)>& construct_element) const
    {
        if (property == nullptr || address == nullptr || !construct_element
            || count > maximum_marshaled_array_length
            || m_fmemory_malloc == nullptr
            || m_initialize_property_value == nullptr
            || m_destroy_property_value == nullptr)
        {
            return -4;
        }

        const auto element_block_size = static_cast<std::size_t>(set_layout.sparse_array_layout.size);
        if (element_block_size == 0
            || static_cast<std::size_t>(count) > maximum_marshaled_array_bytes / element_block_size)
        {
            return -4;
        }

        // Size the hash table up front (a power of two large enough that the build never
        // rehashes). Empty containers keep the engine-initialized zero state.
        std::uint32_t hash_size = 0;
        if (count != 0)
        {
            hash_size = 1;
            while (hash_size < count * 2U)
            {
                hash_size <<= 1U;
            }
        }

        alignas(16) std::byte scratch[deadzone_script_map_size];
        m_initialize_property_value(property, scratch);
        ScopeExit scratch_cleanup([&]() { m_destroy_property_value(property, scratch); });

        // Validate that the engine's empty-container layout matches the offsets this write
        // path assumes. A mismatch disables writes before any memory is committed.
        const auto& empty = *reinterpret_cast<const ScriptContainers::ScriptSetContainer*>(scratch);
        if (empty.elements.data.data != nullptr
            || empty.elements.data.num != 0
            || empty.hash != nullptr
            || empty.hash_size != 0)
        {
            return -4;
        }

        if (count == 0)
        {
            std::swap_ranges(
                scratch,
                scratch + deadzone_script_map_size,
                static_cast<std::byte*>(address));
            return 0;
        }

        const auto element_bytes = static_cast<std::size_t>(count) * element_block_size;
        auto* element_data = static_cast<std::byte*>(m_fmemory_malloc(element_bytes, 0));
        if (element_data == nullptr)
        {
            return -4;
        }

        const auto bucket_bytes = static_cast<std::size_t>(hash_size) * sizeof(std::int32_t);
        auto* buckets = static_cast<std::int32_t*>(m_fmemory_malloc(bucket_bytes, 0));
        if (buckets == nullptr)
        {
            m_fmemory_free(element_data);
            return -4;
        }
        for (std::uint32_t index = 0; index < hash_size; ++index)
        {
            buckets[index] = index_none;
        }

        auto& container = *reinterpret_cast<ScriptContainers::ScriptSetContainer*>(scratch);
        container.elements.data.data = element_data;
        container.elements.data.num = static_cast<std::int32_t>(count);
        container.elements.data.max = static_cast<std::int32_t>(count);
        container.elements.first_free_index = index_none;
        container.elements.num_free_indices = 0;
        container.inline_bucket = index_none;
        container.hash = buckets;
        container.hash_size = static_cast<std::int32_t>(hash_size);

        std::uint32_t* allocation_words;
        if (count <= 128)
        {
            container.elements.allocation_flags.secondary_data = nullptr;
            container.elements.allocation_flags.num_bits = static_cast<std::int32_t>(count);
            container.elements.allocation_flags.max_bits = 128;
            allocation_words = container.elements.allocation_flags.inline_data;
            for (std::uint32_t index = 0; index < 4; ++index)
            {
                container.elements.allocation_flags.inline_data[index] = 0;
            }
        }
        else
        {
            const auto word_count = (count + 31U) / 32U;
            const auto flag_bytes = static_cast<std::size_t>(word_count) * sizeof(std::uint32_t);
            auto* secondary_flags = static_cast<std::uint32_t*>(m_fmemory_malloc(flag_bytes, 0));
            if (secondary_flags == nullptr)
            {
                m_fmemory_free(element_data);
                m_fmemory_free(buckets);
                return -4;
            }
            std::memset(secondary_flags, 0, flag_bytes);
            container.elements.allocation_flags.secondary_data = secondary_flags;
            container.elements.allocation_flags.num_bits = static_cast<std::int32_t>(count);
            container.elements.allocation_flags.max_bits = static_cast<std::int32_t>(word_count * 32U);
            allocation_words = secondary_flags;
        }

        for (std::uint32_t index = 0; index < count; ++index)
        {
            auto* block = element_data + static_cast<std::size_t>(index) * element_block_size;
            if (!construct_element(index, block))
            {
                return -4;
            }

            std::uint32_t hash = 0;
            if (!get_value_type_hash(hash_property, block, hash))
            {
                return -4;
            }
            const auto bucket = hash & (hash_size - 1U);
            *reinterpret_cast<std::int32_t*>(
                block + static_cast<std::size_t>(set_layout.hash_next_id_offset)) = buckets[bucket];
            *reinterpret_cast<std::int32_t*>(
                block + static_cast<std::size_t>(set_layout.hash_index_offset)) = static_cast<std::int32_t>(bucket);
            buckets[bucket] = static_cast<std::int32_t>(index);
            allocation_words[index / 32U] |= (1U << (index % 32U));
        }

        std::swap_ranges(
            scratch,
            scratch + deadzone_script_map_size,
            static_cast<std::byte*>(address));
        return 0;
    }

    std::int32_t UnrealReflectionApi::assign_set_value(
        void* property,
        void* address,
        std::uint32_t encoded_kind,
        const UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr || value.kind != encoded_kind)
        {
            return -4;
        }
        const auto* element_pointer = m_get_element_prop(property);
        auto* element_prop = element_pointer == nullptr ? nullptr : *element_pointer;
        const auto* set_layout_pointer = m_get_set_layout(property);
        if (element_prop == nullptr || set_layout_pointer == nullptr
            || !validate_script_set_layout(*set_layout_pointer))
        {
            return -4;
        }
        const auto element_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
        const auto element_kind = decode_kind(element_encoded_kind);
        if (element_kind == static_cast<UnrealPropertyKind>(0))
        {
            return -4;
        }
        const auto* element_size_pointer = m_get_element_size(element_prop);
        if (element_size_pointer == nullptr || *element_size_pointer <= 0)
        {
            return -4;
        }
        const auto element_size = static_cast<std::size_t>(*element_size_pointer);
        const auto element_fixed_size = expected_value_size(element_kind);
        if ((element_kind != UnrealPropertyKind::Struct
             && element_kind != UnrealPropertyKind::Array
             && element_fixed_size != element_size)
            || (element_kind == UnrealPropertyKind::Array && element_size != 16)
            || (element_kind == UnrealPropertyKind::Struct && element_size > maximum_marshaled_struct_size))
        {
            return -4;
        }
        if (value.reserved > maximum_marshaled_array_length || (value.reserved != 0 && value.data == 0))
        {
            return -4;
        }
        const auto* elements = reinterpret_cast<const UnrealValue*>(value.data);
        for (std::uint32_t index = 0; index < value.reserved; ++index)
        {
            if (elements[index].kind != element_encoded_kind)
            {
                return -4;
            }
        }

        return assign_script_container(
            property,
            address,
            value.reserved,
            element_prop,
            *set_layout_pointer,
            [this, element_prop, element_encoded_kind, elements](std::size_t index, void* block)
            {
                m_initialize_property_value(element_prop, block);
                return assign_typed_value(
                           element_prop,
                           block,
                           element_encoded_kind,
                           elements[index]) == 0;
            });
    }

    std::int32_t UnrealReflectionApi::assign_map_value(
        void* property,
        void* address,
        std::uint32_t encoded_kind,
        const UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr || value.kind != encoded_kind)
        {
            return -4;
        }
        const auto* key_pointer = m_get_key_prop(property);
        auto* key_prop = key_pointer == nullptr ? nullptr : *key_pointer;
        const auto* value_pointer = m_get_value_prop(property);
        auto* value_prop = value_pointer == nullptr ? nullptr : *value_pointer;
        const auto* map_layout_pointer = m_get_map_layout(property);
        if (key_prop == nullptr || value_prop == nullptr || map_layout_pointer == nullptr
            || map_layout_pointer->value_offset < 0
            || !validate_script_set_layout(map_layout_pointer->set_layout))
        {
            return -4;
        }
        const auto key_encoded_kind = (encoded_kind >> map_key_kind_shift) & 0xffU;
        const auto value_encoded_kind = encoded_kind >> map_value_kind_shift;
        const auto key_kind = decode_kind(key_encoded_kind);
        const auto value_kind = decode_kind(value_encoded_kind);
        if (key_kind == static_cast<UnrealPropertyKind>(0)
            || value_kind == static_cast<UnrealPropertyKind>(0))
        {
            return -4;
        }
        const auto* key_size_pointer = m_get_element_size(key_prop);
        const auto* value_size_pointer = m_get_element_size(value_prop);
        if (key_size_pointer == nullptr || *key_size_pointer <= 0
            || value_size_pointer == nullptr || *value_size_pointer <= 0)
        {
            return -4;
        }
        const auto key_size = static_cast<std::size_t>(*key_size_pointer);
        const auto value_size = static_cast<std::size_t>(*value_size_pointer);
        const auto key_fixed_size = expected_value_size(key_kind);
        const auto value_fixed_size = expected_value_size(value_kind);
        if ((key_kind != UnrealPropertyKind::Struct && key_fixed_size != key_size)
            || (key_kind == UnrealPropertyKind::Struct && key_size > maximum_marshaled_struct_size)
            || (value_kind != UnrealPropertyKind::Struct
                && value_kind != UnrealPropertyKind::Array
                && value_fixed_size != value_size)
            || (value_kind == UnrealPropertyKind::Array && value_size != 16)
            || (value_kind == UnrealPropertyKind::Struct && value_size > maximum_marshaled_struct_size))
        {
            return -4;
        }
        if (value.reserved > maximum_marshaled_array_length || (value.reserved != 0 && value.data == 0))
        {
            return -4;
        }
        const auto* entries = reinterpret_cast<const UnrealValue*>(value.data);
        for (std::uint32_t index = 0; index < value.reserved; ++index)
        {
            if (entries[index * 2].kind != key_encoded_kind
                || entries[index * 2 + 1].kind != value_encoded_kind)
            {
                return -4;
            }
        }

        const auto value_offset = static_cast<std::size_t>(map_layout_pointer->value_offset);
        return assign_script_container(
            property,
            address,
            value.reserved,
            key_prop,
            map_layout_pointer->set_layout,
            [this, key_prop, key_encoded_kind, value_prop, value_encoded_kind, value_offset, entries](
                std::size_t index,
                void* block)
            {
                auto* key_address = static_cast<std::byte*>(block);
                auto* value_address = key_address + value_offset;
                m_initialize_property_value(key_prop, key_address);
                bool key_live = true;
                ScopeExit key_cleanup([&]()
                {
                    if (key_live)
                    {
                        m_destroy_property_value(key_prop, key_address);
                    }
                });
                m_initialize_property_value(value_prop, value_address);
                bool value_live = true;
                ScopeExit value_cleanup([&]()
                {
                    if (value_live)
                    {
                        m_destroy_property_value(value_prop, value_address);
                    }
                });
                if (assign_typed_value(key_prop, key_address, key_encoded_kind, entries[index * 2]) != 0)
                {
                    return false;
                }
                if (assign_typed_value(
                        value_prop,
                        value_address,
                        value_encoded_kind,
                        entries[index * 2 + 1]) != 0)
                {
                    return false;
                }
                key_live = false;
                value_live = false;
                return true;
            });
    }

    bool UnrealReflectionApi::resolve_property_kind(
        void* property,
        std::uint32_t& encoded_kind) const
    {
        if (property == nullptr || m_field_get_class == nullptr || m_field_class_get_fname == nullptr)
        {
            return false;
        }
        auto* class_reference = m_field_get_class(property);
        if (class_reference == nullptr || *class_reference == nullptr)
        {
            return false;
        }
        void* field_name = m_field_class_get_fname(*class_reference);
        if (field_name == nullptr)
        {
            return false;
        }

        alignas(std::wstring) std::byte storage[sizeof(std::wstring)];
        auto* name = reinterpret_cast<std::wstring*>(storage);
        m_fname_to_string(field_name, name);
        const std::wstring type = *name;
        name->~basic_string();

        const auto* size_pointer = m_get_element_size(property);
        const auto size = size_pointer == nullptr ? 0 : *size_pointer;

        if (type == L"BoolProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Boolean);
            return true;
        }
        if (type == L"Int8Property")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Int8);
            return true;
        }
        if (type == L"ByteProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt8);
            return true;
        }
        if (type == L"Int16Property")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Int16);
            return true;
        }
        if (type == L"UInt16Property")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt16);
            return true;
        }
        if (type == L"IntProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Int32);
            return true;
        }
        if (type == L"UInt32Property")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt32);
            return true;
        }
        if (type == L"Int64Property")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Int64);
            return true;
        }
        if (type == L"UInt64Property")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt64);
            return true;
        }
        if (type == L"FloatProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Float);
            return true;
        }
        if (type == L"DoubleProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Double);
            return true;
        }
        if (type == L"ObjectProperty" || type == L"ClassProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Object);
            return true;
        }
        if (type == L"StrProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::String);
            return true;
        }
        if (type == L"NameProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Name);
            return true;
        }
        if (type == L"TextProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Text);
            return true;
        }
        if (type == L"StructProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Struct);
            return true;
        }
        if (type == L"WeakObjectProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::WeakObject);
            return true;
        }
        if (type == L"LazyObjectProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::LazyObject);
            return true;
        }
        if (type == L"SoftObjectProperty" || type == L"SoftClassProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::SoftObject);
            return true;
        }
        if (type == L"InterfaceProperty")
        {
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Interface);
            return true;
        }
        if (type == L"EnumProperty")
        {
            switch (size)
            {
            case 1: encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt8); return true;
            case 2: encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt16); return true;
            case 4: encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt32); return true;
            case 8: encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::UInt64); return true;
            default: return false;
            }
        }
        if (type == L"ArrayProperty")
        {
            auto** inner_pointer = m_get_array_inner(property);
            void* inner = inner_pointer == nullptr ? nullptr : *inner_pointer;
            std::uint32_t inner_kind = 0;
            if (inner == nullptr || !resolve_property_kind(inner, inner_kind)
                || inner_kind > 0x00ff'ffffU)
            {
                return false;
            }
            encoded_kind = static_cast<std::uint32_t>(UnrealPropertyKind::Array) | (inner_kind << 8U);
            return true;
        }

        return false;
    }

    bool UnrealReflectionApi::marshal_struct_fields(
        void* property,
        const void* address,
        std::uint32_t encoded_kind,
        UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr || m_struct_get_struct == nullptr)
        {
            return false;
        }
        void* script_struct = nullptr;
        if (void* struct_reference = m_struct_get_struct(property))
        {
            script_struct = *static_cast<void**>(struct_reference);
        }
        if (script_struct == nullptr)
        {
            return false;
        }

        std::vector<void*> fields;
        for (void* field = m_get_first_property(script_struct);
             field != nullptr;
             field = m_get_next_field_as_property(field))
        {
            fields.push_back(field);
        }
        if (fields.size() > maximum_marshaled_array_length)
        {
            return false;
        }

        value.kind = encoded_kind;
        if (fields.empty())
        {
            value.reserved = 0;
            const auto* elem_sz = m_get_element_size(property);
            std::size_t struct_size = (elem_sz && *elem_sz > 0) ? static_cast<std::size_t>(*elem_sz) : 0U;
            if (struct_size == 0 || struct_size > maximum_marshaled_struct_size)
            {
                value.data = 0;
                return true;
            }
            auto* copy = CoTaskMemAlloc(struct_size);
            if (copy == nullptr)
            {
                value.data = 0;
                return false;
            }
            std::memcpy(copy, address, struct_size);
            value.data = reinterpret_cast<std::uint64_t>(copy);
            return true;
        }
        value.reserved = static_cast<std::uint32_t>(fields.size());

        const auto wire_size = fields.size() * sizeof(UnrealValue);
        auto* wire = static_cast<UnrealValue*>(CoTaskMemAlloc(wire_size));
        if (wire == nullptr)
        {
            value = {};
            return false;
        }
        std::memset(wire, 0, wire_size);
        value.data = reinterpret_cast<std::uint64_t>(wire);

        for (std::size_t index = 0; index < fields.size(); ++index)
        {
            std::uint32_t field_kind = 0;
            if (!resolve_property_kind(fields[index], field_kind))
            {
                free_marshaled_value(value);
                return false;
            }
            const auto* offset_pointer = m_get_offset(fields[index]);
            if (offset_pointer == nullptr || *offset_pointer < 0)
            {
                free_marshaled_value(value);
                return false;
            }
            const auto* field_address = static_cast<const std::byte*>(address)
                + static_cast<std::size_t>(*offset_pointer);
            if (!marshal_typed_value(fields[index], field_address, field_kind, wire[index]))
            {
                free_marshaled_value(value);
                return false;
            }
        }
        return true;
    }

    std::int32_t UnrealReflectionApi::assign_struct_fields(
        void* property,
        void* address,
        std::uint32_t encoded_kind,
        const UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr || value.kind != encoded_kind
            || m_struct_get_struct == nullptr
            || m_initialize_property_value == nullptr
            || m_destroy_property_value == nullptr)
        {
            return -4;
        }
        void* script_struct = nullptr;
        if (void* struct_reference = m_struct_get_struct(property))
        {
            script_struct = *static_cast<void**>(struct_reference);
        }
        if (script_struct == nullptr)
        {
            return -4;
        }

        std::vector<void*> fields;
        for (void* field = m_get_first_property(script_struct);
             field != nullptr;
             field = m_get_next_field_as_property(field))
        {
            fields.push_back(field);
        }
        if (fields.size() != value.reserved || fields.size() > maximum_marshaled_array_length)
        {
            if (!(fields.size() == 0 && value.reserved == 0))
            {
                return -4;
            }
        }
        const auto* wire = reinterpret_cast<const UnrealValue*>(value.data);
        if (value.reserved != 0 && wire == nullptr)
        {
            return -4;
        }

        bool committed = false;

        if (fields.empty())
        {
            if (value.reserved == 0 && value.data != 0)
            {
                const auto* szp = m_get_element_size(property);
                auto sz = szp ? static_cast<std::size_t>(*szp) : 0U;
                if (sz > 0 && sz <= maximum_marshaled_struct_size)
                {
                    std::memcpy(address, reinterpret_cast<const void*>(value.data), sz);
                }
                committed = true;
                return 0;
            }
        }

        m_destroy_property_value(property, address);
        m_initialize_property_value(property, address);
        ScopeExit struct_cleanup([&]()
        {
            if (!committed)
            {
                m_destroy_property_value(property, address);
            }
        });

        for (std::size_t index = 0; index < fields.size(); ++index)
        {
            const auto* offset_pointer = m_get_offset(fields[index]);
            if (offset_pointer == nullptr || *offset_pointer < 0)
            {
                return -4;
            }
            auto* field_address = static_cast<std::byte*>(address)
                + static_cast<std::size_t>(*offset_pointer);
            if (assign_typed_value(fields[index], field_address, wire[index].kind, wire[index]) != 0)
            {
                return -4;
            }
        }

        committed = true;
        return 0;
    }

    std::int32_t UnrealReflectionApi::assign_typed_value(
        void* property,
        void* address,
        std::uint32_t encoded_kind,
        const UnrealValue& value) const
    {
        if (property == nullptr || address == nullptr || value.kind != encoded_kind)
        {
            return -4;
        }
        const auto kind = decode_kind(encoded_kind);
        if (kind == UnrealPropertyKind::Map)
        {
            return assign_map_value(property, address, encoded_kind, value);
        }
        if (kind == UnrealPropertyKind::Set)
        {
            return assign_set_value(property, address, encoded_kind, value);
        }
        const auto* element_size_pointer = m_get_element_size(property);
        if (element_size_pointer == nullptr || *element_size_pointer <= 0)
        {
            return -4;
        }
        const auto element_size = static_cast<std::size_t>(*element_size_pointer);
        const auto fixed_size = expected_value_size(kind);
        if ((kind != UnrealPropertyKind::Struct
             && kind != UnrealPropertyKind::Optional
             && fixed_size != element_size)
            || ((kind == UnrealPropertyKind::Struct || kind == UnrealPropertyKind::Optional)
                && element_size > maximum_marshaled_struct_size))
        {
            return -4;
        }

        if (kind == UnrealPropertyKind::Boolean)
        {
            m_set_bool_in_container(property, address, value.data != 0, 0);
            return 0;
        }
        if (kind == UnrealPropertyKind::Object)
        {
            void* target{};
            if (value.data != 0)
            {
                target = const_cast<void*>(resolve_handle(value.data));
                if (target == nullptr)
                {
                    return -4;
                }
            }
            switch (m_mutation_backend.try_assign_object(property, address, target))
            {
                case MutationAttempt::Succeeded:
                    return 0;
                case MutationAttempt::Failed:
                    return -7;
                case MutationAttempt::RestorationFailed:
                    return -8;
                default:
                    return -4;
            }
        }
        if (kind == UnrealPropertyKind::Interface)
        {
            // Temporary-slot write (ProcessEvent parameter buffers, hook replacement, and
            // array/optional scratch storage). FScriptInterface stores a raw UObject* at +0;
            // the engine lazily re-resolves the interface pointer from the object, so +8 is
            // zeroed rather than inferred. Persistent property writes are gated in
            // write_property until the game's incremental-GC write path is live-confirmed.
            void* target{};
            if (value.data != 0)
            {
                target = const_cast<void*>(resolve_handle(value.data));
                if (target == nullptr)
                {
                    return -4;
                }
            }
            std::memcpy(address, &target, sizeof(target));
            std::memset(static_cast<std::byte*>(address) + sizeof(target), 0, 8);
            return 0;
        }
        if (kind == UnrealPropertyKind::SoftObject)
        {
            if (value.data == 0 || value.reserved != sizeof(SoftObjectWire)
                || m_soft_object_destroy_value == nullptr)
            {
                return -4;
            }
            const auto* wire = reinterpret_cast<const SoftObjectWire*>(value.data);
            const auto* path = wire->path == nullptr ? L"" : wire->path;
            std::byte temporary[soft_object_storage_size]{};
            const auto construction_result = construct_soft_object_value(path, temporary);
            if (construction_result != 0)
            {
                return construction_result;
            }
            bool temporary_live = true;
            ScopeExit temporary_cleanup([&]()
            {
                if (temporary_live)
                {
                    m_soft_object_destroy_value(temporary);
                }
            });

            // This path is used for initialized UFunction parameter storage (including
            // hook replacement), never for direct reflected property assignment. Replace
            // it with a game-built value while preserving the owned FString lifetime.
            m_soft_object_destroy_value(address);
            std::memcpy(address, temporary, soft_object_storage_size);
            std::memset(temporary, 0, soft_object_storage_size);
            temporary_live = false;
            return 0;
        }
        if (kind == UnrealPropertyKind::String || kind == UnrealPropertyKind::Name || kind == UnrealPropertyKind::Text)
        {
            if (!valid_marshaled_input(value))
            {
                return -4;
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
            return 0;
        }
        if (kind == UnrealPropertyKind::Struct)
        {
            return assign_struct_fields(property, address, encoded_kind, value);
        }
        if (kind == UnrealPropertyKind::Array)
        {
            if (value.reserved > maximum_marshaled_array_length || (value.reserved != 0 && value.data == 0))
            {
                return -4;
            }
            auto** inner_pointer = m_get_array_inner(property);
            auto* inner = inner_pointer == nullptr ? nullptr : *inner_pointer;
            const auto inner_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
            const auto inner_kind = decode_kind(inner_encoded_kind);
            if (inner == nullptr || inner_kind == static_cast<UnrealPropertyKind>(0))
            {
                return -4;
            }
            const auto* inner_size_pointer = m_get_element_size(inner);
            if (inner_size_pointer == nullptr || *inner_size_pointer <= 0)
            {
                return -4;
            }
            const auto inner_size = static_cast<std::size_t>(*inner_size_pointer);
            const auto inner_fixed_size = expected_value_size(inner_kind);
            if ((inner_kind != UnrealPropertyKind::Struct && inner_fixed_size != inner_size)
                || (inner_kind == UnrealPropertyKind::Struct && inner_size > maximum_marshaled_struct_size)
                || value.reserved > maximum_marshaled_array_bytes / inner_size)
            {
                return -4;
            }
            const auto* elements = reinterpret_cast<const UnrealValue*>(value.data);
            for (std::uint32_t index = 0; index < value.reserved; ++index)
            {
                if (elements[index].kind != inner_encoded_kind)
                {
                    return -4;
                }
            }

            const auto mutation = m_mutation_backend.try_replace_name_array(
                property,
                inner,
                address,
                inner_encoded_kind,
                inner_size,
                value,
                [this](
                    void* element_property,
                    void* element_address,
                    std::uint32_t element_encoded_kind,
                    const UnrealValue& element_value)
                {
                    return assign_typed_value(
                               element_property,
                               element_address,
                               element_encoded_kind,
                               element_value) == 0;
                });
            if (mutation != MutationAttempt::Unsupported)
            {
                return mutation == MutationAttempt::Succeeded ? 0 : -4;
            }

            auto& destination = *static_cast<FScriptArrayLayout*>(address);
            if (destination.num < 0 || destination.max < destination.num
                || (destination.max != 0 && destination.data == nullptr))
            {
                return -4;
            }

            if (value.reserved == static_cast<std::uint32_t>(destination.num))
            {
                auto* destination_data = static_cast<std::byte*>(destination.data);
                for (std::uint32_t index = 0; index < value.reserved; ++index)
                {
                    auto* element_address = destination_data + static_cast<std::size_t>(index) * inner_size;
                    if (inner_kind == UnrealPropertyKind::Object)
                    {
                        if (assign_typed_value(
                                inner,
                                element_address,
                                inner_encoded_kind,
                                elements[index]) != 0)
                        {
                            return -7;
                        }
                        continue;
                    }
                    if (assign_typed_value(
                            inner,
                            element_address,
                            inner_encoded_kind,
                            elements[index]) != 0)
                    {
                        return -4;
                    }
                }
                return 0;
            }

            if (destination.data != nullptr || destination.max != 0)
            {
                return -4;
            }

            alignas(8) FScriptArrayLayout temporary{};
            if (value.reserved != 0)
            {
                const auto bytes = static_cast<std::size_t>(value.reserved) * inner_size;
                temporary.data = m_fmemory_malloc(bytes, 0);
                if (temporary.data == nullptr)
                {
                    return -4;
                }
                temporary.max = static_cast<std::int32_t>(value.reserved);
                auto* data = static_cast<std::byte*>(temporary.data);
                for (std::uint32_t index = 0; index < value.reserved; ++index)
                {
                    auto* element_address = data + static_cast<std::size_t>(index) * inner_size;
                    std::memset(element_address, 0, inner_size);
                    if (inner_kind == UnrealPropertyKind::String)
                    {
                        m_fstring_default_constructor(element_address);
                    }
                    else if (inner_kind == UnrealPropertyKind::Name)
                    {
                        m_fname_default_constructor(element_address);
                    }
                    else if (inner_kind == UnrealPropertyKind::Text)
                    {
                        m_ftext_default_constructor(element_address);
                    }
                    ++temporary.num;
                    if (assign_typed_value(
                            inner,
                            element_address,
                            inner_encoded_kind,
                            elements[index]) != 0)
                    {
                        destroy_array_value(property, &temporary, encoded_kind);
                        return -4;
                    }
                }
            }

            std::swap(destination, temporary);
            if (temporary.data != nullptr || temporary.num != 0 || temporary.max != 0)
            {
                destroy_array_value(property, &temporary, encoded_kind);
            }
            return 0;
        }
        if (kind == UnrealPropertyKind::WeakObject)
        {
            if (m_fweak_object_assign == nullptr || m_fweak_object_reset == nullptr)
            {
                return false;
            }
            if (value.data == 0)
            {
                m_fweak_object_reset(address);
                return 0;
            }
            const auto* target = resolve_handle(value.data);
            if (target == nullptr)
            {
                return -4;
            }
            m_fweak_object_assign(address, target);
            return 0;
        }
        if (kind == UnrealPropertyKind::LazyObject)
        {
            if (m_lazy_object_set_value == nullptr
                || element_size != lazy_object_storage_size
                || value.reserved != sizeof(LazyObjectWire)
                || value.data == 0)
            {
                return -4;
            }
            const auto* wire = reinterpret_cast<const LazyObjectWire*>(value.data);
            m_lazy_object_set_value(address, wire->storage);
            return 0;
        }
        if (kind == UnrealPropertyKind::Optional)
        {
            if (m_get_optional_value_property == nullptr
                || m_optional_mark_set_and_get_initialized_value_pointer == nullptr
                || m_optional_mark_unset == nullptr
                || value.reserved > 1
                || (value.reserved == 0 && value.data != 0)
                || (value.reserved == 1 && value.data == 0))
            {
                return -4;
            }
            if (value.reserved == 0)
            {
                m_optional_mark_unset(property, address);
                return 0;
            }
            auto** value_property_pointer = m_get_optional_value_property(property);
            auto* value_property = value_property_pointer == nullptr ? nullptr : *value_property_pointer;
            const auto value_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
            const auto* nested = reinterpret_cast<const UnrealValue*>(value.data);
            if (value_property == nullptr || nested->kind != value_encoded_kind)
            {
                return -4;
            }
            auto* value_address = m_optional_mark_set_and_get_initialized_value_pointer(property, address);
            if (value_address == nullptr
                || assign_typed_value(value_property, value_address, value_encoded_kind, *nested) != 0)
            {
                m_optional_mark_unset(property, address);
                return -4;
            }
            return 0;
        }

        if (fixed_size == 0 || fixed_size > sizeof(value.data))
        {
            return -4;
        }
        std::memcpy(address, &value.data, fixed_size);
        return 0;
    }

    void UnrealReflectionApi::destroy_array_value(
        void* property,
        void* address,
        std::uint32_t encoded_kind) const
    {
        if (property == nullptr || address == nullptr)
        {
            return;
        }

        auto& array = *static_cast<FScriptArrayLayout*>(address);
        if (array.data == nullptr)
        {
            array = {};
            return;
        }

        auto** inner_pointer = m_get_array_inner(property);
        auto* inner = inner_pointer == nullptr ? nullptr : *inner_pointer;
        const auto* inner_size_pointer = inner == nullptr ? nullptr : m_get_element_size(inner);
        const auto inner_encoded_kind = decode_array_element_encoded_kind(encoded_kind);
        const auto inner_kind = decode_kind(inner_encoded_kind);
        if (inner_size_pointer != nullptr && *inner_size_pointer > 0 && array.num > 0 && array.num <= array.max)
        {
            const auto inner_size = static_cast<std::size_t>(*inner_size_pointer);
            auto* data = static_cast<std::byte*>(array.data);
            for (std::int32_t index = array.num; index > 0; --index)
            {
                auto* element = data + static_cast<std::size_t>(index - 1) * inner_size;
                if (inner_kind == UnrealPropertyKind::String)
                {
                    m_fstring_destructor(element);
                }
                else if (inner_kind == UnrealPropertyKind::Text)
                {
                    m_ftext_destructor(element);
                }
                else if (inner_kind == UnrealPropertyKind::Array)
                {
                    destroy_array_value(inner, element, inner_encoded_kind);
                }
            }
        }

        m_fmemory_free(array.data);
        array = {};
    }

    void UnrealReflectionApi::destroy_optional_value(void* property, void* address) const
    {
        if (property != nullptr && address != nullptr && m_optional_mark_unset != nullptr)
        {
            m_optional_mark_unset(property, address);
        }
    }

    std::int32_t UnrealReflectionApi::construct_soft_object_value(
        const wchar_t* path,
        void* destination) const
    {
        if (path == nullptr || destination == nullptr
            || m_static_find_object == nullptr || m_get_function_by_name_in_chain == nullptr
            || m_get_parms_size == nullptr || m_get_num_parms == nullptr
            || m_get_first_property == nullptr || m_get_next_field_as_property == nullptr
            || m_get_offset == nullptr || m_get_element_size == nullptr
            || m_process_event == nullptr || m_fstring_constructor == nullptr
            || m_fstring_destructor == nullptr
            || m_initialize_property_value == nullptr || m_destroy_property_value == nullptr
            || m_soft_object_destroy_value == nullptr)
        {
            return -4;
        }

        auto* library = m_static_find_object(
            nullptr,
            nullptr,
            L"/Script/Engine.Default__KismetSystemLibrary",
            false);
        if (library == nullptr)
        {
            return -7;
        }

        auto get_parameters = [&](const wchar_t* function_name,
                                  std::uint16_t expected_size,
                                  std::uint8_t expected_count)
        {
            std::vector<void*> properties;
            auto* function = m_get_function_by_name_in_chain(library, function_name);
            const auto* size = function == nullptr ? nullptr : m_get_parms_size(function);
            const auto* count = function == nullptr ? nullptr : m_get_num_parms(function);
            if (function == nullptr || size == nullptr || count == nullptr
                || *size != expected_size || *count != expected_count)
            {
                return std::pair<void*, std::vector<void*>>{};
            }
            properties.reserve(expected_count);
            auto* property = m_get_first_property(function);
            for (std::uint8_t index = 0; index < expected_count; ++index)
            {
                if (property == nullptr)
                {
                    return std::pair<void*, std::vector<void*>>{};
                }
                properties.push_back(property);
                property = m_get_next_field_as_property(property);
            }
            return std::pair{function, std::move(properties)};
        };
        auto has_layout = [&](void* property, std::int32_t offset, std::int32_t size)
        {
            const auto* live_offset = m_get_offset(property);
            const auto* live_size = m_get_element_size(property);
            return live_offset != nullptr && live_size != nullptr
                && *live_offset == offset && *live_size == size;
        };

        auto [make_path, make_properties] = get_parameters(L"MakeSoftObjectPath", 48, 2);
        auto [to_reference, conversion_properties] = get_parameters(L"Conv_SoftObjPathToSoftObjRef", 72, 2);
        if (make_path == nullptr || to_reference == nullptr
            || !has_layout(make_properties[0], 0, 16)
            || !has_layout(make_properties[1], 16, 32)
            || !has_layout(conversion_properties[0], 0, 32)
            || !has_layout(conversion_properties[1], 32, 40))
        {
            return -7;
        }

        std::vector<std::byte> make_buffer(48);
        m_fstring_constructor(make_buffer.data(), path);
        ScopeExit make_string_cleanup([&]() { m_fstring_destructor(make_buffer.data()); });
        auto* make_return = make_buffer.data() + 16;
        m_initialize_property_value(make_properties[1], make_return);
        bool make_return_live = true;
        ScopeExit make_return_cleanup([&]()
        {
            if (make_return_live)
            {
                m_destroy_property_value(make_properties[1], make_return);
            }
        });
        m_process_event(library, make_path, make_buffer.data());

        std::vector<std::byte> conversion_buffer(72);
        auto* conversion_input = conversion_buffer.data();
        std::memcpy(conversion_input, make_return, 32);
        std::memset(make_return, 0, 32);
        make_return_live = false;
        bool conversion_input_live = true;
        ScopeExit conversion_input_cleanup([&]()
        {
            if (conversion_input_live)
            {
                m_destroy_property_value(conversion_properties[0], conversion_input);
            }
        });
        auto* conversion_return = conversion_buffer.data() + 32;
        bool conversion_return_live = false;
        m_process_event(library, to_reference, conversion_buffer.data());
        conversion_return_live = true;
        ScopeExit conversion_return_cleanup([&]()
        {
            if (conversion_return_live)
            {
                m_soft_object_destroy_value(conversion_return);
            }
        });

        std::memcpy(destination, conversion_return, soft_object_storage_size);
        std::memset(conversion_return, 0, 40);
        conversion_return_live = false;
        conversion_input_live = false;
        m_destroy_property_value(conversion_properties[0], conversion_input);
        return 0;
    }

    std::int32_t UnrealReflectionApi::assign_soft_object_property(
        void* object,
        const wchar_t* property_name,
        const UnrealValue& value) const
    {
        if (object == nullptr || property_name == nullptr || *property_name == L'\0'
            || value.data == 0 || value.reserved != sizeof(SoftObjectWire)
            || m_static_find_object == nullptr || m_get_function_by_name_in_chain == nullptr
            || m_get_parms_size == nullptr || m_get_num_parms == nullptr
            || m_get_first_property == nullptr || m_get_next_field_as_property == nullptr
            || m_get_offset == nullptr || m_get_element_size == nullptr
            || m_process_event == nullptr || m_fname_constructor == nullptr
            || m_soft_object_destroy_value == nullptr)
        {
            return -4;
        }

        const auto* wire = reinterpret_cast<const SoftObjectWire*>(value.data);
        const auto* path = wire->path == nullptr ? L"" : wire->path;
        std::byte soft_value[soft_object_storage_size]{};
        const auto construction_result = construct_soft_object_value(path, soft_value);
        if (construction_result != 0)
        {
            return construction_result;
        }
        bool soft_value_live = true;
        ScopeExit soft_value_cleanup([&]()
        {
            if (soft_value_live)
            {
                m_soft_object_destroy_value(soft_value);
            }
        });

        auto* library = m_static_find_object(
            nullptr,
            nullptr,
            L"/Script/Engine.Default__KismetSystemLibrary",
            false);
        auto* set_property = library == nullptr
            ? nullptr
            : m_get_function_by_name_in_chain(library, L"SetSoftObjectPropertyByName");
        const auto* parms_size = set_property == nullptr ? nullptr : m_get_parms_size(set_property);
        const auto* num_parms = set_property == nullptr ? nullptr : m_get_num_parms(set_property);
        auto* object_property = set_property == nullptr ? nullptr : m_get_first_property(set_property);
        auto* name_property = object_property == nullptr
            ? nullptr
            : m_get_next_field_as_property(object_property);
        auto* value_property = name_property == nullptr
            ? nullptr
            : m_get_next_field_as_property(name_property);
        auto has_layout = [&](void* property, std::int32_t offset, std::int32_t size)
        {
            const auto* live_offset = property == nullptr ? nullptr : m_get_offset(property);
            const auto* live_size = property == nullptr ? nullptr : m_get_element_size(property);
            return live_offset != nullptr && live_size != nullptr
                && *live_offset == offset && *live_size == size;
        };
        if (set_property == nullptr || parms_size == nullptr || *parms_size != 56
            || num_parms == nullptr || *num_parms != 3
            || !has_layout(object_property, 0, 8)
            || !has_layout(name_property, 8, 8)
            || !has_layout(value_property, 16, 40))
        {
            return -7;
        }

        std::vector<std::byte> setter_buffer(56);
        std::memcpy(setter_buffer.data(), &object, sizeof(object));
        m_fname_constructor(setter_buffer.data() + 8, property_name, 1, nullptr);
        std::memcpy(setter_buffer.data() + 16, soft_value, soft_object_storage_size);
        std::memset(soft_value, 0, soft_object_storage_size);
        soft_value_live = false;
        ScopeExit setter_value_cleanup([&]()
        {
            m_soft_object_destroy_value(setter_buffer.data() + 16);
        });
        m_process_event(library, set_property, setter_buffer.data());
        return 0;
    }

    std::int32_t UnrealReflectionApi::assign_interface_object_property(
        void* object,
        const wchar_t* property_name,
        const UnrealValue& value) const
    {
        if (object == nullptr || property_name == nullptr || *property_name == L'\0'
            || value.kind != static_cast<std::uint32_t>(UnrealPropertyKind::Interface)
            || m_get_property_by_name_in_chain == nullptr || m_get_offset == nullptr)
        {
            return -4;
        }

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
        auto* address = static_cast<std::byte*>(object) + *offset;

        if (value.data == 0)
        {
            // Clearing removes a reference; reference removal is safe under incremental GC
            // without a write barrier. FScriptInterface is a POD (CPF_ZeroConstructor |
            // CPF_NoDestructor), so zeroing the complete 16-byte slot is the default state.
            std::memset(address, 0, 16);
            return 0;
        }

        void* target = const_cast<void*>(resolve_handle(value.data));
        if (target == nullptr)
        {
            return -4;
        }

        if (m_static_find_object == nullptr || m_get_function_by_name_in_chain == nullptr
            || m_get_parms_size == nullptr || m_get_num_parms == nullptr
            || m_get_first_property == nullptr || m_get_next_field_as_property == nullptr
            || m_process_event == nullptr || m_fname_constructor == nullptr)
        {
            return -4;
        }

        // KismetSystemLibrary.SetInterfacePropertyByName is the engine's canonical interface
        // setter: it finds the property by name and validates that the target object's class
        // implements the interface (UClass::ImplementsInterface) before assigning, applying
        // the engine's own property write. The FScriptInterface value carries the raw object
        // pointer at +0 and a null interface pointer at +8: that is a valid engine state
        // (blueprint-only interfaces and deserialized values store only ObjectPointer), and
        // the engine lazily re-resolves the interface pointer from the object.
        auto* library = m_static_find_object(
            nullptr,
            nullptr,
            L"/Script/Engine.Default__KismetSystemLibrary",
            false);
        auto* set_property = library == nullptr
            ? nullptr
            : m_get_function_by_name_in_chain(library, L"SetInterfacePropertyByName");
        const auto* parms_size = set_property == nullptr ? nullptr : m_get_parms_size(set_property);
        const auto* num_parms = set_property == nullptr ? nullptr : m_get_num_parms(set_property);
        auto* object_property = set_property == nullptr ? nullptr : m_get_first_property(set_property);
        auto* name_property = object_property == nullptr
            ? nullptr
            : m_get_next_field_as_property(object_property);
        auto* value_property = name_property == nullptr
            ? nullptr
            : m_get_next_field_as_property(name_property);
        auto has_layout = [&](void* property, std::int32_t offset, std::int32_t size)
        {
            const auto* live_offset = property == nullptr ? nullptr : m_get_offset(property);
            const auto* live_size = property == nullptr ? nullptr : m_get_element_size(property);
            return live_offset != nullptr && live_size != nullptr
                && *live_offset == offset && *live_size == size;
        };
        if (set_property == nullptr || parms_size == nullptr || *parms_size != 32
            || num_parms == nullptr || *num_parms != 3
            || !has_layout(object_property, 0, 8)
            || !has_layout(name_property, 8, 8)
            || !has_layout(value_property, 16, 16))
        {
            return -7;
        }

        std::vector<std::byte> setter_buffer(32);
        std::memcpy(setter_buffer.data(), &object, sizeof(object));
        m_fname_constructor(setter_buffer.data() + 8, property_name, 1, nullptr);
        std::memcpy(setter_buffer.data() + 16, &target, sizeof(target));
        std::memset(setter_buffer.data() + 24, 0, 8);
        m_process_event(library, set_property, setter_buffer.data());

        // SetInterfacePropertyByName silently skips a value whose object does not implement
        // the target interface. Verify the object pointer actually landed and surface a clear
        // failure status instead of a silent no-op.
        void* written{};
        std::memcpy(&written, address, sizeof(written));
        return written == target ? 0 : -7;
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
            const auto property_kind = decode_kind(encoded_property_kind);
            if (property_kind == UnrealPropertyKind::SoftObject)
            {
                return assign_soft_object_property(object, property_name, *value);
            }
            if (property_kind == UnrealPropertyKind::Interface)
            {
                // Persistent FScriptInterface writes route through the engine's canonical
                // KismetSystemLibrary.SetInterfacePropertyByName (which validates the target
                // implements the interface) plus a direct clear for null values; assign
                // verifies the write landed and reports a rejection otherwise.
                return assign_interface_object_property(object, property_name, *value);
            }
            auto* address = static_cast<std::byte*>(object) + *offset;
            return assign_typed_value(property, address, encoded_property_kind, *value);
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
                const auto size = expected_parameter_size(kind, parameter.size);
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
                        && (parameter.value.reserved > maximum_marshaled_array_length
                            || (parameter.value.reserved != 0 && parameter.value.data == 0)))
                    || (kind == UnrealPropertyKind::Array
                        && (parameter.value.reserved > maximum_marshaled_array_length
                            || (parameter.value.reserved != 0 && parameter.value.data == 0)))
                    || ((kind == UnrealPropertyKind::Map || kind == UnrealPropertyKind::Set)
                        && (parameter.value.reserved > maximum_marshaled_array_length
                            || (parameter.value.reserved != 0 && parameter.value.data == 0)))
                    || (kind == UnrealPropertyKind::Optional
                        && (parameter.value.reserved > 1
                            || (parameter.value.reserved == 0 && parameter.value.data != 0)
                            || (parameter.value.reserved == 1 && parameter.value.data == 0)))
                    || (kind == UnrealPropertyKind::LazyObject
                        && (parameter.value.reserved != sizeof(LazyObjectWire)
                            || parameter.value.data == 0))
                    || (kind == UnrealPropertyKind::SoftObject
                        && (parameter.value.reserved != sizeof(SoftObjectWire)
                            || parameter.value.data == 0)))
                {
                    return -5;
                }
            }

            FStringCleanup string_cleanup(m_fstring_destructor);
            FStringCleanup text_cleanup(m_ftext_destructor);
            std::vector<std::tuple<void*, void*, std::uint32_t>> array_values;
            ScopeExit array_cleanup([&]()
            {
                for (auto iterator = array_values.rbegin(); iterator != array_values.rend(); ++iterator)
                {
                    destroy_array_value(
                        std::get<0>(*iterator),
                        std::get<1>(*iterator),
                        std::get<2>(*iterator));
                }
            });
            std::vector<std::pair<void*, void*>> optional_values;
            ScopeExit optional_cleanup([&]()
            {
                for (auto iterator = optional_values.rbegin(); iterator != optional_values.rend(); ++iterator)
                {
                    destroy_optional_value(iterator->first, iterator->second);
                }
            });
            std::vector<void*> soft_object_values;
            ScopeExit soft_object_cleanup([&]()
            {
                for (auto iterator = soft_object_values.rbegin(); iterator != soft_object_values.rend(); ++iterator)
                {
                    m_soft_object_destroy_value(*iterator);
                }
            });
            std::vector<std::pair<void*, void*>> struct_values;
            ScopeExit struct_cleanup([&]()
            {
                for (auto iterator = struct_values.rbegin(); iterator != struct_values.rend(); ++iterator)
                {
                    m_destroy_property_value(iterator->first, iterator->second);
                }
            });
            for (std::uint32_t index = 0; index < parameter_count; ++index)
            {
                auto& parameter = parameters[index];
                const auto kind = decode_kind(parameter.kind);
                const auto size = expected_parameter_size(kind, parameter.size);
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
                    std::memset(address, 0, sizeof(FScriptArrayLayout));
                    array_values.emplace_back(property, address, parameter.kind);
                    if (is_input && assign_typed_value(property, address, parameter.kind, parameter.value) != 0)
                    {
                        return -5;
                    }
                }
                else if (kind == UnrealPropertyKind::Optional)
                {
                    auto* property = parameter_properties[index];
                    std::memset(address, 0, size);
                    optional_values.emplace_back(property, address);
                    destroy_optional_value(property, address);
                    if (is_input && assign_typed_value(property, address, parameter.kind, parameter.value) != 0)
                    {
                        return -5;
                    }
                }
                else if (kind == UnrealPropertyKind::Map || kind == UnrealPropertyKind::Set)
                {
                    auto* property = parameter_properties[index];
                    m_initialize_property_value(property, address);
                    if (is_input && assign_typed_value(property, address, parameter.kind, parameter.value) != 0)
                    {
                        m_destroy_property_value(property, address);
                        return -5;
                    }
                    struct_values.emplace_back(property, address);
                }
                else if (kind == UnrealPropertyKind::WeakObject)
                {
                    if (m_fweak_object_default_constructor == nullptr)
                    {
                        return -5;
                    }
                    m_fweak_object_default_constructor(address);
                    if (is_input
                        && assign_typed_value(
                            parameter_properties[index],
                            address,
                            parameter.kind,
                            parameter.value) != 0)
                    {
                        return -5;
                    }
                }
                else if (kind == UnrealPropertyKind::LazyObject)
                {
                    std::memset(address, 0, size);
                    if (is_input
                        && assign_typed_value(
                            parameter_properties[index],
                            address,
                            parameter.kind,
                            parameter.value) != 0)
                    {
                        return -5;
                    }
                }
                else if (kind == UnrealPropertyKind::SoftObject)
                {
                    if (m_soft_object_destroy_value == nullptr)
                    {
                        return -5;
                    }
                    std::memset(address, 0, size);
                    soft_object_values.push_back(address);
                    if (is_input)
                    {
                        const auto* wire = reinterpret_cast<const SoftObjectWire*>(parameter.value.data);
                        const auto* path = wire->path == nullptr ? L"" : wire->path;
                        if (construct_soft_object_value(path, address) != 0)
                        {
                            return -5;
                        }
                    }
                }
                else if (!is_input && kind != UnrealPropertyKind::Struct)
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
                else if (kind == UnrealPropertyKind::Interface)
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
                    std::memset(address + sizeof(target), 0, 8);
                }
                else if (kind == UnrealPropertyKind::Struct)
                {
                    // The parameter buffer is zeroed, not a constructed struct. Construct it
                    // first, then assign the input fields through the transactional struct
                    // backend. The constructed value is destroyed by struct_cleanup after
                    // ProcessEvent.
                    m_initialize_property_value(parameter_properties[index], address);
                    if (is_input
                        && assign_typed_value(
                            parameter_properties[index],
                            address,
                            parameter.kind,
                            parameter.value) != 0)
                    {
                        return -5;
                    }
                    struct_values.emplace_back(parameter_properties[index], address);
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
                const auto size = expected_parameter_size(kind, parameter.size);
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
                        || kind == UnrealPropertyKind::Array
                        || kind == UnrealPropertyKind::Optional
                        || kind == UnrealPropertyKind::Map
                        || kind == UnrealPropertyKind::Set
                        || kind == UnrealPropertyKind::LazyObject
                        || kind == UnrealPropertyKind::SoftObject)
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

    std::int32_t UnrealReflectionApi::register_hook(
        const wchar_t* function_path,
        std::int32_t phase_value,
        std::int32_t priority,
        std::uint64_t instance_filter,
        std::uint32_t parameter_count,
        const UnrealParameter* parameters,
        UnrealHookCallback callback,
        std::uint64_t context,
        std::uint64_t* token)
    {
        if (token == nullptr)
        {
            return -2;
        }
        *token = 0;
        if (!is_available() || !m_process_event_hooks_resolved || m_static_find_object == nullptr)
        {
            return -1;
        }
        if (function_path == nullptr || *function_path == L'\0' || callback == nullptr
            || (parameter_count != 0 && parameters == nullptr)
            || parameter_count > UINT8_MAX)
        {
            return -2;
        }
        const auto phase = static_cast<UnrealHookPhase>(phase_value);
        if (phase != UnrealHookPhase::Pre && phase != UnrealHookPhase::Post)
        {
            return -2;
        }
        if (instance_filter != 0 && resolve_handle(instance_filter) == nullptr)
        {
            return -7;
        }

        try
        {
            const std::wstring path{function_path};
            const auto separator = path.rfind(L':');
            if (separator == std::wstring::npos || separator == 0 || separator + 1 >= path.size())
            {
                return -3;
            }
            const auto owner_path = path.substr(0, separator);
            const auto function_name = path.substr(separator + 1);
            auto* owner = m_static_find_object(nullptr, nullptr, owner_path.c_str(), false);
            auto* function = owner == nullptr
                ? nullptr
                : m_get_function_by_name_in_chain(owner, function_name.c_str());
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

            HookRegistration registration;
            registration.function = function;
            registration.phase = phase;
            registration.priority = priority;
            registration.instance_filter = instance_filter;
            registration.callback = callback;
            registration.context = context;
            if (parameter_count != 0)
            {
                registration.parameters.assign(parameters, parameters + parameter_count);
            }
            registration.properties.reserve(parameter_count);

            auto* live_property = parameter_count == 0 ? nullptr : m_get_first_property(function);
            bool has_return{};
            for (std::uint32_t index = 0; index < parameter_count; ++index)
            {
                if (live_property == nullptr)
                {
                    return -4;
                }
                registration.properties.push_back(live_property);
                auto& parameter = registration.parameters[index];
                const auto kind = decode_kind(parameter.kind);
                const auto size = expected_parameter_size(kind, parameter.size);
                const auto* live_offset = m_get_offset(live_property);
                const auto* live_size = m_get_element_size(live_property);
                if (size == 0 || parameter.array_dimension != 1 || parameter.offset < 0
                    || size > maximum_marshaled_struct_size
                    || parameter.size != static_cast<std::int32_t>(size)
                    || static_cast<std::size_t>(parameter.offset) + size > parms_size
                    || live_offset == nullptr || *live_offset != parameter.offset
                    || live_size == nullptr || *live_size != parameter.size)
                {
                    return -5;
                }
                if ((parameter.flags & static_cast<std::uint32_t>(UnrealParameterFlags::Return)) != 0)
                {
                    if (has_return || parameter.offset != *return_offset_pointer)
                    {
                        return -5;
                    }
                    has_return = true;
                }
                parameter.value = {parameter.kind, 0, 0};
                live_property = m_get_next_field_as_property(live_property);
            }

            std::unique_lock lock{m_hook_mutex};
            registration.token = ++m_next_hook_token;
            if (registration.token == 0)
            {
                registration.token = ++m_next_hook_token;
            }
            *token = registration.token;
            m_hooks.push_back(std::move(registration));
            return 0;
        }
        catch (...)
        {
            return -6;
        }
    }

    std::int32_t UnrealReflectionApi::unregister_hook(std::uint64_t token)
    {
        if (token == 0)
        {
            return -2;
        }
        try
        {
            std::unique_lock lock{m_hook_mutex};
            const auto iterator = std::find_if(
                m_hooks.begin(), m_hooks.end(),
                [token](const HookRegistration& registration) { return registration.token == token; });
            if (iterator == m_hooks.end())
            {
                return -3;
            }
            m_hooks.erase(iterator);
            return 0;
        }
        catch (...)
        {
            return -4;
        }
    }

    void UnrealReflectionApi::dispatch_hook(
        UnrealHookPhase phase,
        void* object,
        void* function,
        void* parameter_buffer) const noexcept
    {
        if (!is_available() || object == nullptr || function == nullptr)
        {
            return;
        }
        try
        {
            const auto object_handle = make_handle(object);
            if (object_handle == 0)
            {
                return;
            }
            std::vector<HookRegistration> matching;
            {
                std::shared_lock lock{m_hook_mutex};
                for (const auto& registration : m_hooks)
                {
                    if (registration.phase == phase
                        && registration.function == function
                        && (registration.instance_filter == 0
                            || registration.instance_filter == object_handle))
                    {
                        matching.push_back(registration);
                    }
                }
            }
            if (matching.empty())
            {
                return;
            }

            std::sort(
                matching.begin(), matching.end(),
                [](const HookRegistration& left, const HookRegistration& right)
                {
                    return left.priority != right.priority
                        ? left.priority > right.priority
                        : left.token < right.token;
                });
            for (const auto& registration : matching)
            {
                auto transported = registration.parameters;
                bool valid = parameter_buffer != nullptr || transported.empty();
                for (std::size_t index = 0; valid && index < transported.size(); ++index)
                {
                    auto& parameter = transported[index];
                    const auto is_input = (parameter.flags & static_cast<std::uint32_t>(UnrealParameterFlags::Input)) != 0;
                    const auto is_output = (parameter.flags & static_cast<std::uint32_t>(UnrealParameterFlags::Output)) != 0;
                    const auto is_return = (parameter.flags & static_cast<std::uint32_t>(UnrealParameterFlags::Return)) != 0;
                    const auto should_marshal = phase == UnrealHookPhase::Pre
                        ? is_input
                        : is_input || is_output || is_return;
                    parameter.value = {parameter.kind, 0, 0};
                    if (!should_marshal)
                    {
                        continue;
                    }

                    const auto* address = static_cast<const std::byte*>(parameter_buffer) + parameter.offset;
                    const auto kind = decode_kind(parameter.kind);
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
                    else if (!marshal_typed_value(
                                 registration.properties[index], address, parameter.kind, parameter.value))
                    {
                        valid = false;
                    }
                }

                if (valid)
                {
                    const auto callback_result = registration.callback(
                        registration.context,
                        object_handle,
                        static_cast<std::int32_t>(phase),
                        static_cast<std::uint32_t>(transported.size()),
                        transported.empty() ? nullptr : transported.data());
                    if (callback_result == 0)
                    {
                        for (std::size_t index = 0; index < transported.size(); ++index)
                        {
                            const auto& descriptor = registration.parameters[index];
                            const auto& parameter = transported[index];
                            const auto modified = (parameter.flags
                                                   & static_cast<std::uint32_t>(UnrealParameterFlags::Modified)) != 0;
                            const auto is_input = (descriptor.flags
                                                   & static_cast<std::uint32_t>(UnrealParameterFlags::Input)) != 0;
                            const auto is_output = (descriptor.flags
                                                    & static_cast<std::uint32_t>(UnrealParameterFlags::Output)) != 0;
                            const auto is_return = (descriptor.flags
                                                    & static_cast<std::uint32_t>(UnrealParameterFlags::Return)) != 0;
                            const auto may_replace = phase == UnrealHookPhase::Pre
                                ? is_input
                                : is_output || is_return;
                            if (!modified || !may_replace || parameter.value.kind != descriptor.kind)
                            {
                                continue;
                            }

                            auto* address = static_cast<std::byte*>(parameter_buffer) + descriptor.offset;
                            if (decode_kind(descriptor.kind) == UnrealPropertyKind::Boolean)
                            {
                                const auto byte_offset = static_cast<std::uint8_t>(descriptor.bool_layout);
                                auto byte_mask = static_cast<std::uint8_t>(descriptor.bool_layout >> 8);
                                if (byte_mask == 0)
                                {
                                    byte_mask = 1;
                                }
                                auto* boolean_address = reinterpret_cast<std::uint8_t*>(address) + byte_offset;
                                if (parameter.value.data != 0)
                                {
                                    *boolean_address |= byte_mask;
                                }
                                else
                                {
                                    *boolean_address &= static_cast<std::uint8_t>(~byte_mask);
                                }
                            }
                            else
                            {
                                (void)assign_typed_value(
                                    registration.properties[index],
                                    address,
                                    descriptor.kind,
                                    parameter.value);
                            }
                        }
                    }
                }
                for (auto& parameter : transported)
                {
                    free_marshaled_value(parameter.value);
                }
            }
            return;
        }
        catch (...)
        {
            return;
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
        const auto* object = class_name[0] == L'/' && m_static_find_object != nullptr
            ? m_static_find_object(nullptr, nullptr, class_name, false)
            : m_find_first_of(class_name);
        return make_handle(object);
    }

    std::uint64_t UnrealReflectionApi::create_object(
        std::uint64_t class_handle,
        std::uint64_t outer_handle,
        const wchar_t* object_name) const
    {
        if (m_static_construct_object == nullptr || m_construct_object_parameters_ctor == nullptr)
        {
            return 0;
        }
        auto* klass = const_cast<void*>(resolve_handle(class_handle));
        if (klass == nullptr)
        {
            return 0;
        }
        auto* outer = outer_handle == 0
            ? nullptr
            : const_cast<void*>(resolve_handle(outer_handle));
        if (outer_handle != 0 && outer == nullptr)
        {
            return 0;
        }

        alignas(16) std::byte parameters[0x40]{};
        m_construct_object_parameters_ctor(parameters, klass, outer);

        if (object_name != nullptr && *object_name != L'\0')
        {
            alignas(8) std::byte name_storage[8]{};
            m_fname_constructor(name_storage, object_name, 1, nullptr);
            std::memcpy(parameters + 0x10, name_storage, sizeof(name_storage));
        }

        auto* created = m_static_construct_object(parameters);
        if (created == nullptr)
        {
            return 0;
        }
        return make_handle(created);
    }

    std::uint64_t UnrealReflectionApi::spawn_actor(
        std::uint64_t context_object_handle,
        std::uint64_t class_handle,
        const float* location,
        const float* rotation) const
    {
        if (m_object_get_world == nullptr || m_world_spawn_actor == nullptr)
        {
            return 0;
        }
        auto* context = const_cast<void*>(resolve_handle(context_object_handle));
        if (context == nullptr)
        {
            return 0;
        }
        auto* klass = const_cast<void*>(resolve_handle(class_handle));
        if (klass == nullptr)
        {
            return 0;
        }
        auto* world = m_object_get_world(context);
        if (world == nullptr)
        {
            return 0;
        }

        const float default_location[3]{0.0F, 0.0F, 0.0F};
        const float default_rotation[3]{0.0F, 0.0F, 0.0F};
        auto* spawned = m_world_spawn_actor(
            world,
            klass,
            location != nullptr ? location : default_location,
            rotation != nullptr ? rotation : default_rotation);
        if (spawned == nullptr)
        {
            return 0;
        }
        return make_handle(spawned);
    }

    std::int32_t UnrealReflectionApi::find_all_of(
        const wchar_t* class_name,
        std::uint64_t* handles,
        std::uint32_t capacity,
        std::uint32_t* required) const
    {
        if (required == nullptr)
        {
            return -2;
        }
        *required = 0;
        if (!is_available() || m_find_all_of == nullptr || class_name == nullptr || *class_name == L'\0')
        {
            return -1;
        }

        try
        {
            std::vector<void*> objects;
            m_find_all_of(class_name, objects);
            std::vector<std::uint64_t> valid_handles;
            valid_handles.reserve(objects.size());
            for (const auto* object : objects)
            {
                const auto handle = make_handle(object);
                if (handle != 0)
                {
                    valid_handles.push_back(handle);
                }
            }
            if (valid_handles.size() > UINT32_MAX)
            {
                return -3;
            }
            *required = static_cast<std::uint32_t>(valid_handles.size());
            if (valid_handles.empty())
            {
                return 0;
            }
            if (handles == nullptr || capacity < valid_handles.size())
            {
                return 1;
            }
            std::copy(valid_handles.begin(), valid_handles.end(), handles);
            return 0;
        }
        catch (...)
        {
            *required = 0;
            return -4;
        }
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
