#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <string>

#include <Windows.h>

#include "RogueModHostApi.hpp"

namespace RogueMod
{
    class UnrealReflectionApi
    {
      public:
        bool resolve(HMODULE ue4ss_module);
        void set_ready(bool ready);

        [[nodiscard]] bool is_available() const;
        [[nodiscard]] std::uint64_t find_first_of(const wchar_t* class_name) const;
        [[nodiscard]] bool is_valid(std::uint64_t handle) const;
        [[nodiscard]] std::uint64_t get_class(std::uint64_t handle) const;
        [[nodiscard]] std::int32_t get_path_name(
            std::uint64_t handle,
            wchar_t* buffer,
            std::uint32_t capacity,
            std::uint32_t* required) const;
        [[nodiscard]] std::uint32_t capabilities() const;
        [[nodiscard]] std::int32_t invoke_zero_parameter(
            std::uint64_t handle,
            const wchar_t* function_name) const;
        [[nodiscard]] std::int32_t invoke(
            std::uint64_t handle,
            const wchar_t* function_name,
            std::uint32_t parameter_count,
            UnrealParameter* parameters) const;
        [[nodiscard]] std::int32_t read_property(
            std::uint64_t handle,
            const wchar_t* property_name,
            std::uint32_t encoded_property_kind,
            UnrealValue* value) const;
        [[nodiscard]] std::int32_t write_property(
            std::uint64_t handle,
            const wchar_t* property_name,
            std::uint32_t encoded_property_kind,
            const UnrealValue* value) const;

      private:
        using find_first_of_fn = void*(__cdecl*)(const wchar_t*);
        using get_internal_index_fn = std::int32_t(__cdecl*)(const void*);
        using index_to_object_fn = void*(__cdecl*)(std::int32_t);
        using get_serial_number_fn = const std::int32_t*(__cdecl*)(const void*);
        using is_item_valid_fn = bool(__cdecl*)(const void*, bool);
        using get_item_object_fn = const void* const*(__cdecl*)(const void*);
        using get_class_private_fn = const void* const*(__cdecl*)(const void*);
        using get_path_name_fn = void(__cdecl*)(const void*, void*, std::wstring&);
        using get_function_by_name_in_chain_fn = void*(__cdecl*)(void*, const wchar_t*);
        using get_parms_size_fn = const std::uint16_t*(__cdecl*)(const void*);
        using get_num_parms_fn = const std::uint8_t*(__cdecl*)(const void*);
        using get_return_value_offset_fn = const std::uint16_t*(__cdecl*)(const void*);
        using process_event_fn = void(__cdecl*)(void*, void*, void*);
        using get_property_by_name_in_chain_fn = void*(__cdecl*)(void*, const wchar_t*);
        using get_offset_fn = const std::int32_t*(__cdecl*)(const void*);
        using get_element_size_fn = const std::int32_t*(__cdecl*)(const void*);
        using get_bool_in_container_fn = bool(__cdecl*)(void*, const void*, std::int32_t);
        using set_bool_in_container_fn = void(__cdecl*)(void*, void*, bool, std::int32_t);
        using get_object_property_value_fn = void*(__cdecl*)(const void*, const void*);
        using set_object_property_value_fn = void(__cdecl*)(const void*, void*, void*);
        using fstring_default_constructor_fn = void*(__cdecl*)(void*);
        using fstring_constructor_fn = void*(__cdecl*)(void*, const wchar_t*);
        using fstring_destructor_fn = void(__cdecl*)(void*);
        using fstring_copy_assignment_fn = void*(__cdecl*)(void*, const void*);
        using fname_default_constructor_fn = void*(__cdecl*)(void*);
        using fname_constructor_fn = void*(__cdecl*)(void*, const wchar_t*, std::int32_t, void*);
        // MSVC keeps `this` in RCX and passes the hidden std::wstring return storage in RDX
        // for this member function. Declaring it as a free function returning std::wstring
        // reverses those registers under the Windows x64 ABI.
        using fname_to_string_fn = void(__cdecl*)(const void*, std::wstring*);
        using fname_copy_assignment_fn = void*(__cdecl*)(void*, const void*);
        using ftext_default_constructor_fn = void*(__cdecl*)(void*);
        using ftext_constructor_fn = void*(__cdecl*)(void*, const wchar_t*);
        using ftext_destructor_fn = void(__cdecl*)(void*);
        using ftext_to_string_fn = void(__cdecl*)(const void*, std::wstring*);
        using ftext_copy_assignment_fn = void*(__cdecl*)(void*, const void*);
        using get_first_property_fn = void*(__cdecl*)(void*);
        using get_next_field_as_property_fn = void*(__cdecl*)(void*);
        using get_array_inner_fn = void**(__cdecl*)(void*);
        using get_min_alignment_fn = std::int32_t(__cdecl*)(const void*);
        using initialize_value_fn = void(__cdecl*)(const void*, void*);
        using destroy_value_fn = void(__cdecl*)(const void*, void*);
        using copy_complete_value_fn = void(__cdecl*)(const void*, void*, const void*);
        using fmemory_malloc_fn = void*(__cdecl*)(std::size_t, std::uint32_t);

        [[nodiscard]] std::uint64_t make_handle(const void* object) const;
        [[nodiscard]] const void* resolve_handle(std::uint64_t handle) const;
        [[nodiscard]] bool marshal_typed_value(
            void* property,
            const void* address,
            std::uint32_t encoded_kind,
            UnrealValue& value) const;
        [[nodiscard]] bool assign_typed_value(
            void* property,
            void* address,
            std::uint32_t encoded_kind,
            const UnrealValue& value) const;

        std::atomic_bool m_ready{};
        bool m_resolved{};
        find_first_of_fn m_find_first_of{};
        get_internal_index_fn m_get_internal_index{};
        index_to_object_fn m_index_to_object{};
        get_serial_number_fn m_get_serial_number{};
        is_item_valid_fn m_is_item_valid{};
        get_item_object_fn m_get_item_object{};
        get_class_private_fn m_get_class_private{};
        get_path_name_fn m_get_path_name{};
        get_function_by_name_in_chain_fn m_get_function_by_name_in_chain{};
        get_parms_size_fn m_get_parms_size{};
        get_num_parms_fn m_get_num_parms{};
        get_return_value_offset_fn m_get_return_value_offset{};
        process_event_fn m_process_event{};
        get_property_by_name_in_chain_fn m_get_property_by_name_in_chain{};
        get_offset_fn m_get_offset{};
        get_element_size_fn m_get_element_size{};
        get_bool_in_container_fn m_get_bool_in_container{};
        set_bool_in_container_fn m_set_bool_in_container{};
        get_object_property_value_fn m_get_object_property_value{};
        set_object_property_value_fn m_set_object_property_value{};
        fstring_default_constructor_fn m_fstring_default_constructor{};
        fstring_constructor_fn m_fstring_constructor{};
        fstring_destructor_fn m_fstring_destructor{};
        fstring_copy_assignment_fn m_fstring_copy_assignment{};
        fname_default_constructor_fn m_fname_default_constructor{};
        fname_constructor_fn m_fname_constructor{};
        fname_to_string_fn m_fname_to_string{};
        fname_copy_assignment_fn m_fname_copy_assignment{};
        ftext_default_constructor_fn m_ftext_default_constructor{};
        ftext_constructor_fn m_ftext_constructor{};
        ftext_destructor_fn m_ftext_destructor{};
        ftext_to_string_fn m_ftext_to_string{};
        ftext_copy_assignment_fn m_ftext_copy_assignment{};
        get_first_property_fn m_get_first_property{};
        get_next_field_as_property_fn m_get_next_field_as_property{};
        get_array_inner_fn m_get_array_inner{};
        get_min_alignment_fn m_get_min_alignment{};
        initialize_value_fn m_initialize_value{};
        destroy_value_fn m_destroy_value{};
        copy_complete_value_fn m_copy_complete_value{};
        fmemory_malloc_fn m_fmemory_malloc{};
    };
}
