#pragma once

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <shared_mutex>
#include <string>
#include <vector>

#include <Windows.h>

#include "RogueModHostApi.hpp"
#include "UnrealMutationBackend.hpp"

namespace RogueMod
{
    namespace ScriptContainers
    {
        struct SparseArrayLayout
        {
            std::int32_t alignment;
            std::int32_t size;
        };

        static_assert(sizeof(SparseArrayLayout) == 8);

        struct SetLayout
        {
            std::int32_t hash_next_id_offset;
            std::int32_t hash_index_offset;
            std::int32_t size;
            SparseArrayLayout sparse_array_layout;
        };

        static_assert(sizeof(SetLayout) == 20);

        struct MapLayout
        {
            std::int32_t value_offset;
            SetLayout set_layout;
        };

        static_assert(sizeof(MapLayout) == 24);

        struct ScriptArray
        {
            void* data;
            std::int32_t num;
            std::int32_t max;
        };

        static_assert(sizeof(ScriptArray) == 16);

        struct ScriptBitArray
        {
            std::uint32_t inline_data[4];
            void* secondary_data;
            std::int32_t num_bits;
            std::int32_t max_bits;

            [[nodiscard]] const std::uint32_t* data() const
            {
                return secondary_data != nullptr
                    ? static_cast<const std::uint32_t*>(secondary_data)
                    : inline_data;
            }
        };

        static_assert(offsetof(ScriptBitArray, secondary_data) == 16);
        static_assert(offsetof(ScriptBitArray, num_bits) == 24);
        static_assert(sizeof(ScriptBitArray) == 32);

        struct ScriptSparseArray
        {
            ScriptArray data;
            ScriptBitArray allocation_flags;
            std::int32_t first_free_index;
            std::int32_t num_free_indices;
        };

        static_assert(offsetof(ScriptSparseArray, first_free_index) == 48);
        static_assert(sizeof(ScriptSparseArray) == 56);

        constexpr std::size_t script_set_inline_bucket_offset = 56;
        constexpr std::size_t script_set_hash_offset = 64;
        constexpr std::size_t script_set_hash_size_offset = 72;

        struct ScriptSetContainer
        {
            ScriptSparseArray elements;
            std::int32_t inline_bucket;
            std::int32_t* hash;
            std::int32_t hash_size;
        };

        static_assert(offsetof(ScriptSetContainer, elements) == 0);
        static_assert(offsetof(ScriptSetContainer, inline_bucket) == script_set_inline_bucket_offset);
        static_assert(offsetof(ScriptSetContainer, hash) == script_set_hash_offset);
        static_assert(offsetof(ScriptSetContainer, hash_size) == script_set_hash_size_offset);
    }

    class UnrealReflectionApi
    {
      public:
        bool resolve(
            HMODULE ue4ss_module,
            void(__cdecl* log)(std::int32_t level, const wchar_t* message) = nullptr);
        void set_ready(bool ready);

        [[nodiscard]] bool is_available() const;
        [[nodiscard]] std::uint64_t find_first_of(const wchar_t* class_name) const;
        [[nodiscard]] std::int32_t find_all_of(
            const wchar_t* class_name,
            std::uint64_t* handles,
            std::uint32_t capacity,
            std::uint32_t* required) const;
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
        [[nodiscard]] std::int32_t register_hook(
            const wchar_t* function_path,
            std::int32_t phase,
            std::int32_t priority,
            std::uint64_t instance_filter,
            std::uint32_t parameter_count,
            const UnrealParameter* parameters,
            UnrealHookCallback callback,
            std::uint64_t context,
            std::uint64_t* token);
        [[nodiscard]] std::int32_t unregister_hook(std::uint64_t token);
        [[nodiscard]] std::uint64_t create_object(
            std::uint64_t class_handle,
            std::uint64_t outer_handle,
            const wchar_t* object_name) const;
        [[nodiscard]] std::uint64_t spawn_actor(
            std::uint64_t context_object_handle,
            std::uint64_t class_handle,
            const float* location,
            const float* rotation) const;

      private:
        using find_first_of_fn = void*(__cdecl*)(const wchar_t*);
        using static_find_object_fn = void*(__cdecl*)(void*, void*, const wchar_t*, bool);
        using find_all_of_fn = void(__cdecl*)(const wchar_t*, std::vector<void*>&);
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
        using fstring_default_constructor_fn = void*(__cdecl*)(void*);
        using fstring_constructor_fn = void*(__cdecl*)(void*, const wchar_t*);
        using fstring_destructor_fn = void(__cdecl*)(void*);
        using fstring_copy_assignment_fn = void*(__cdecl*)(void*, const void*);
        using fname_default_constructor_fn = void*(__cdecl*)(void*);
        using fname_constructor_fn = void*(__cdecl*)(void*, const wchar_t*, std::int32_t, void*);
        using fname_to_string_fn = void(__cdecl*)(const void*, std::wstring*);
        using fname_copy_assignment_fn = void*(__cdecl*)(void*, const void*);
        using ftext_default_constructor_fn = void*(__cdecl*)(void*);
        using ftext_constructor_fn = void*(__cdecl*)(void*, const wchar_t*);
        using ftext_destructor_fn = void(__cdecl*)(void*);
        using ftext_to_string_fn = void(__cdecl*)(const void*, std::wstring*);
        using ftext_copy_assignment_fn = void*(__cdecl*)(void*, const void*);
        using get_first_property_fn = void*(__cdecl*)(void*);
        using get_next_field_as_property_fn = void*(__cdecl*)(void*);
        using get_super_struct_fn = void**(__cdecl*)(void*);
        using get_array_inner_fn = void**(__cdecl*)(void*);
        using get_optional_value_property_fn = void**(__cdecl*)(const void*);
        using get_key_prop_fn = void* const*(__cdecl*)(const void*);
        using get_value_prop_fn = void* const*(__cdecl*)(const void*);
        using get_element_prop_fn = void* const*(__cdecl*)(const void*);
        using get_map_layout_fn = const ScriptContainers::MapLayout*(__cdecl*)(const void*);
        using get_set_layout_fn = const ScriptContainers::SetLayout*(__cdecl*)(const void*);
        using optional_is_set_fn = bool(__cdecl*)(const void*, const void*);
        using optional_get_value_pointer_for_read_if_set_fn = const void*(__cdecl*)(const void*, const void*);
        using optional_mark_set_and_get_initialized_value_pointer_fn = void*(__cdecl*)(const void*, void*);
        using optional_mark_unset_fn = void(__cdecl*)(const void*, void*);
        using fweak_object_default_constructor_fn = void*(__cdecl*)(void*);
        using fweak_object_get_fn = void*(__cdecl*)(const void*);
        using fweak_object_assign_fn = void(__cdecl*)(void*, const void*);
        using fweak_object_reset_fn = void(__cdecl*)(void*);
        using lazy_object_set_value_fn = void(__cdecl*)(void*, const void*);
        using soft_object_destroy_value_fn = void(__cdecl*)(void*);
        using fmemory_malloc_fn = void*(__cdecl*)(std::size_t, std::uint32_t);
        using fmemory_free_fn = void(__cdecl*)(void*);
        using initialize_property_value_fn = void(__cdecl*)(const void*, void*);
        using destroy_property_value_fn = void(__cdecl*)(const void*, void*);
        using construct_object_parameters_ctor_fn = void(__cdecl*)(void* self, const void* uclass, void* outer);
        using static_construct_object_fn = void*(__cdecl*)(const void* parameters);
        using object_get_world_fn = void*(__cdecl*)(const void* self);
        using world_spawn_actor_fn = void*(__cdecl*)(
            void* self,
            const void* uclass,
            const void* location,
            const void* rotation);
        using get_value_type_hash_fn = std::uint32_t(__cdecl*)(const void* property, const void* address);
        using struct_get_struct_fn = void*(__cdecl*)(void* self);
        using field_get_class_fn = void**(__cdecl*)(void* self);
        using field_class_get_fname_fn = void*(__cdecl*)(void* self);

        [[nodiscard]] std::uint64_t make_handle(const void* object) const;
        [[nodiscard]] const void* resolve_handle(std::uint64_t handle) const;
        [[nodiscard]] bool marshal_typed_value(
            void* property,
            const void* address,
            std::uint32_t encoded_kind,
            UnrealValue& value) const;
        [[nodiscard]] bool marshal_map_value(
            void* property,
            const void* address,
            std::uint32_t encoded_kind,
            UnrealValue& value) const;
        [[nodiscard]] bool marshal_set_value(
            void* property,
            const void* address,
            std::uint32_t encoded_kind,
            UnrealValue& value) const;
        [[nodiscard]] bool read_script_set(
            const void* container,
            const ScriptContainers::SetLayout& layout,
            std::int32_t& max_index,
            std::int32_t& num) const;
        [[nodiscard]] bool script_set_element(
            const void* container,
            const ScriptContainers::SetLayout& layout,
            std::int32_t index,
            const void*& element) const;
        [[nodiscard]] bool validate_script_set_layout(
            const ScriptContainers::SetLayout& layout) const;
        [[nodiscard]] bool get_value_type_hash(
            void* property,
            const void* address,
            std::uint32_t& hash) const;
        [[nodiscard]] std::int32_t assign_set_value(
            void* property,
            void* address,
            std::uint32_t encoded_kind,
            const UnrealValue& value) const;
        [[nodiscard]] std::int32_t assign_map_value(
            void* property,
            void* address,
            std::uint32_t encoded_kind,
            const UnrealValue& value) const;
        [[nodiscard]] bool resolve_property_kind(
            void* property,
            std::uint32_t& encoded_kind) const;
        [[nodiscard]] bool collect_struct_fields(
            void* script_struct,
            std::vector<void*>& fields) const;
        [[nodiscard]] bool marshal_struct_fields(
            void* property,
            const void* address,
            std::uint32_t encoded_kind,
            UnrealValue& value) const;
        [[nodiscard]] std::int32_t assign_struct_fields(
            void* property,
            void* address,
            std::uint32_t encoded_kind,
            const UnrealValue& value) const;
        [[nodiscard]] std::int32_t assign_script_container(
            void* property,
            void* address,
            std::uint32_t count,
            void* hash_property,
            const ScriptContainers::SetLayout& set_layout,
            const std::function<bool(std::size_t index, void* block)>& construct_element) const;
        [[nodiscard]] std::int32_t assign_typed_value(
            void* property,
            void* address,
            std::uint32_t encoded_kind,
            const UnrealValue& value) const;
        [[nodiscard]] std::int32_t assign_soft_object_property(
            void* object,
            const wchar_t* property_name,
            const UnrealValue& value) const;
        [[nodiscard]] std::int32_t assign_interface_object_property(
            void* object,
            const wchar_t* property_name,
            const UnrealValue& value) const;
        [[nodiscard]] std::int32_t construct_soft_object_value(
            const wchar_t* path,
            void* destination) const;
        void destroy_array_value(void* property, void* address, std::uint32_t encoded_kind) const;
        void destroy_optional_value(void* property, void* address) const;
        void dispatch_hook(
            UnrealHookPhase phase,
            void* object,
            void* function,
            void* parameters) const noexcept;

        struct HookRegistration
        {
            std::uint64_t token{};
            void* function{};
            UnrealHookPhase phase{};
            std::int32_t priority{};
            std::uint64_t instance_filter{};
            std::vector<UnrealParameter> parameters;
            std::vector<void*> properties;
            UnrealHookCallback callback{};
            std::uint64_t context{};
        };

        std::atomic_bool m_ready{};
        bool m_resolved{};
        bool m_process_event_hooks_resolved{};
        std::uint64_t m_next_hook_token{};
        mutable std::shared_mutex m_hook_mutex;
        std::vector<HookRegistration> m_hooks;
        find_first_of_fn m_find_first_of{};
        static_find_object_fn m_static_find_object{};
        find_all_of_fn m_find_all_of{};
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
        get_super_struct_fn m_get_super_struct{};
        get_array_inner_fn m_get_array_inner{};
        get_key_prop_fn m_get_key_prop{};
        get_value_prop_fn m_get_value_prop{};
        get_element_prop_fn m_get_element_prop{};
        get_map_layout_fn m_get_map_layout{};
        get_set_layout_fn m_get_set_layout{};
        get_optional_value_property_fn m_get_optional_value_property{};
        optional_is_set_fn m_optional_is_set{};
        optional_get_value_pointer_for_read_if_set_fn m_optional_get_value_pointer_for_read_if_set{};
        optional_mark_set_and_get_initialized_value_pointer_fn m_optional_mark_set_and_get_initialized_value_pointer{};
        optional_mark_unset_fn m_optional_mark_unset{};
        fweak_object_default_constructor_fn m_fweak_object_default_constructor{};
        fweak_object_get_fn m_fweak_object_get{};
        fweak_object_assign_fn m_fweak_object_assign{};
        fweak_object_reset_fn m_fweak_object_reset{};
        lazy_object_set_value_fn m_lazy_object_set_value{};
        soft_object_destroy_value_fn m_soft_object_destroy_value{};
        fmemory_malloc_fn m_fmemory_malloc{};
        fmemory_free_fn m_fmemory_free{};
        initialize_property_value_fn m_initialize_property_value{};
        destroy_property_value_fn m_destroy_property_value{};
        construct_object_parameters_ctor_fn m_construct_object_parameters_ctor{};
        static_construct_object_fn m_static_construct_object{};
        object_get_world_fn m_object_get_world{};
        world_spawn_actor_fn m_world_spawn_actor{};
        struct_get_struct_fn m_struct_get_struct{};
        field_get_class_fn m_field_get_class{};
        field_class_get_fname_fn m_field_class_get_fname{};
        void(__cdecl* m_log)(std::int32_t level, const wchar_t* message){};
        UnrealMutationBackend m_mutation_backend;
    };
}
