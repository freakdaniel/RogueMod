#include <cstdint>
#include <cstdio>
#include <cwchar>
#include <filesystem>
#include <mutex>
#include <string>
#include <system_error>
#include <vector>

#include <Windows.h>

#include <UE4SS/CppUserModBase.hpp>

#include "RogueModHostApi.hpp"
#include "UnrealReflectionApi.hpp"

namespace
{
    enum class hostfxr_delegate_type : std::int32_t
    {
        com_activation,
        load_in_memory_assembly,
        winrt_activation,
        com_register,
        com_unregister,
        load_assembly_and_get_function_pointer,
        get_function_pointer,
        load_assembly,
        load_assembly_bytes,
    };

    enum class managed_game_event : std::int32_t
    {
        program_started = 1,
        unreal_initialized = 2,
        ui_initialized = 3,
        update = 4,
        cpp_mods_loaded = 5,
    };

    using hostfxr_handle = void*;
    using hostfxr_initialize_for_runtime_config_fn = std::int32_t(__cdecl*)(const wchar_t*, void*, hostfxr_handle*);
    using hostfxr_get_runtime_delegate_fn = std::int32_t(__cdecl*)(hostfxr_handle, hostfxr_delegate_type, void**);
    using hostfxr_close_fn = std::int32_t(__cdecl*)(hostfxr_handle);
    using load_assembly_and_get_function_pointer_fn = std::int32_t(__cdecl*)(
        const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, void*, void**);
    using initialize_managed_fn = std::int32_t(__cdecl*)(void*);
    using dispatch_game_event_managed_fn = std::int32_t(__cdecl*)(std::int32_t);
    using shutdown_managed_fn = std::int32_t(__cdecl*)();

    const auto* UnmanagedCallersOnly = reinterpret_cast<const wchar_t*>(static_cast<std::intptr_t>(-1));
    std::filesystem::path LogPath;
    std::mutex LogMutex;
    RogueMod::UnrealReflectionApi UnrealReflection;

    const wchar_t* log_level_name(std::int32_t level)
    {
        switch (static_cast<RogueMod::LogLevel>(level))
        {
        case RogueMod::LogLevel::Trace: return L"TRACE";
        case RogueMod::LogLevel::Debug: return L"DEBUG";
        case RogueMod::LogLevel::Information: return L"INFO";
        case RogueMod::LogLevel::Warning:
            return L"WARN";
        case RogueMod::LogLevel::Error:
            return L"ERROR";
        case RogueMod::LogLevel::Critical: return L"CRITICAL";
        default: return L"INFO";
        }
    }

    std::wstring format_timestamp()
    {
        SYSTEMTIME time{};
        GetLocalTime(&time);
        wchar_t buffer[32]{};
        swprintf_s(
            buffer,
            L"%04u-%02u-%02u %02u:%02u:%02u.%03u",
            time.wYear,
            time.wMonth,
            time.wDay,
            time.wHour,
            time.wMinute,
            time.wSecond,
            time.wMilliseconds);
        return buffer;
    }

    std::wstring padded_log_level_name(std::int32_t level)
    {
        std::wstring name = log_level_name(level);
        name.resize(8, L' ');
        return name;
    }

    void write_log(std::int32_t level, const wchar_t* message)
    {
        const auto* safe_message = message == nullptr ? L"<null>" : message;
        std::wstring line = L"[";
        line += format_timestamp();
        line += L"] [";
        line += padded_log_level_name(level);
        line += L"] ";
        line += safe_message;
        line += L"\r\n";
        OutputDebugStringW(line.c_str());

        std::scoped_lock lock{LogMutex};
        if (LogPath.empty())
        {
            return;
        }

        const auto utf8_size = WideCharToMultiByte(
            CP_UTF8, 0, line.data(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        if (utf8_size <= 0)
        {
            return;
        }
        std::vector<char> utf8(static_cast<std::size_t>(utf8_size));
        WideCharToMultiByte(
            CP_UTF8, 0, line.data(), static_cast<int>(line.size()), utf8.data(), utf8_size, nullptr, nullptr);

        const auto file = CreateFileW(
            LogPath.c_str(),
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }
        DWORD bytes_written{};
        WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &bytes_written, nullptr);
        CloseHandle(file);
    }

    void log_bridge(std::int32_t level, const wchar_t* message)
    {
        std::wstring structured = L"[Bridge] ";
        structured += message == nullptr ? L"<null>" : message;
        write_log(level, structured.c_str());
    }

    void log_from_managed(std::int32_t level, const wchar_t* message)
    {
        write_log(level, message);
    }

    std::int32_t __cdecl unreal_is_available()
    {
        return UnrealReflection.is_available() ? 1 : 0;
    }

    std::uint64_t __cdecl unreal_find_first_of(const wchar_t* class_name)
    {
        return UnrealReflection.find_first_of(class_name);
    }

    std::uint64_t __cdecl unreal_create_object(
        std::uint64_t class_handle,
        std::uint64_t outer_handle,
        const wchar_t* object_name)
    {
        return UnrealReflection.create_object(class_handle, outer_handle, object_name);
    }

    std::uint64_t __cdecl unreal_spawn_actor(
        std::uint64_t context_object_handle,
        std::uint64_t class_handle,
        const float* location,
        const float* rotation)
    {
        return UnrealReflection.spawn_actor(context_object_handle, class_handle, location, rotation);
    }

    std::int32_t __cdecl unreal_find_all_of(
        const wchar_t* class_name,
        std::uint64_t* handles,
        std::uint32_t capacity,
        std::uint32_t* required)
    {
        return UnrealReflection.find_all_of(class_name, handles, capacity, required);
    }

    std::int32_t __cdecl unreal_is_valid(std::uint64_t handle)
    {
        return UnrealReflection.is_valid(handle) ? 1 : 0;
    }

    std::uint64_t __cdecl unreal_get_class(std::uint64_t handle)
    {
        return UnrealReflection.get_class(handle);
    }

    std::int32_t __cdecl unreal_get_path_name(
        std::uint64_t handle,
        wchar_t* buffer,
        std::uint32_t capacity,
        std::uint32_t* required)
    {
        return UnrealReflection.get_path_name(handle, buffer, capacity, required);
    }

    std::uint32_t __cdecl unreal_get_capabilities()
    {
        return UnrealReflection.capabilities();
    }

    std::int32_t __cdecl unreal_invoke_zero_parameter(
        std::uint64_t handle,
        const wchar_t* function_name)
    {
        return UnrealReflection.invoke_zero_parameter(handle, function_name);
    }

    std::int32_t __cdecl unreal_read_property(
        std::uint64_t handle,
        const wchar_t* property_name,
        std::uint32_t property_kind,
        RogueMod::UnrealValue* value)
    {
        return UnrealReflection.read_property(
            handle,
            property_name,
            property_kind,
            value);
    }

    std::int32_t __cdecl unreal_write_property(
        std::uint64_t handle,
        const wchar_t* property_name,
        std::uint32_t property_kind,
        const RogueMod::UnrealValue* value)
    {
        return UnrealReflection.write_property(
            handle,
            property_name,
            property_kind,
            value);
    }

    std::int32_t __cdecl unreal_invoke(
        std::uint64_t handle,
        const wchar_t* function_name,
        std::uint32_t parameter_count,
        RogueMod::UnrealParameter* parameters)
    {
        return UnrealReflection.invoke(handle, function_name, parameter_count, parameters);
    }

    std::int32_t __cdecl unreal_register_hook(
        const wchar_t* function_path,
        std::int32_t phase,
        std::int32_t priority,
        std::uint64_t instance_filter,
        std::uint32_t parameter_count,
        const RogueMod::UnrealParameter* parameters,
        RogueMod::UnrealHookCallback callback,
        std::uint64_t context,
        std::uint64_t* token)
    {
        return UnrealReflection.register_hook(
            function_path, phase, priority, instance_filter, parameter_count, parameters, callback, context, token);
    }

    std::int32_t __cdecl unreal_unregister_hook(std::uint64_t token)
    {
        return UnrealReflection.unregister_hook(token);
    }

    void log_result(const wchar_t* operation, std::int32_t result)
    {
        wchar_t message[256]{};
        swprintf_s(message, L"%ls: 0x%08X", operation, static_cast<unsigned int>(result));
        log_bridge(result < 0 ? 4 : 1, message);
    }

    std::filesystem::path current_module_path()
    {
        HMODULE module{};
        const auto address = reinterpret_cast<LPCWSTR>(&current_module_path);
        if (!GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                address,
                &module))
        {
            return {};
        }

        std::wstring buffer(32768, L'\0');
        const auto length = GetModuleFileNameW(module, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (length == 0 || length >= buffer.size())
        {
            return {};
        }

        buffer.resize(length);
        return std::filesystem::path{buffer};
    }

    std::filesystem::path find_hostfxr(const std::filesystem::path& dotnet_root)
    {
        const auto fxr_root = dotnet_root / L"host" / L"fxr";
        std::error_code error;
        if (!std::filesystem::is_directory(fxr_root, error))
        {
            return {};
        }

        std::filesystem::path selected;
        for (const auto& entry : std::filesystem::directory_iterator(fxr_root, error))
        {
            const auto candidate = entry.path() / L"hostfxr.dll";
            if (!error && std::filesystem::is_regular_file(candidate, error)
                && (selected.empty() || entry.path().filename() > selected.parent_path().filename()))
            {
                selected = candidate;
            }
        }

        return selected;
    }

    class ManagedRuntime
    {
    public:
        bool start(
            const std::filesystem::path& roguemod_root,
            const std::filesystem::path& game_mods_root)
        {
            if (m_shutdown != nullptr)
            {
                return true;
            }

            const auto runtime_root = roguemod_root / L"runtime";
            const auto managed_root = runtime_root / L"managed";
            const auto hostfxr_path = find_hostfxr(runtime_root / L"dotnet");
            if (hostfxr_path.empty())
            {
                log_bridge(4, L"Private Windows hostfxr.dll was not found.");
                return false;
            }

            log_bridge(1, L"Loading private Windows hostfxr.dll.");

            m_hostfxr = LoadLibraryW(hostfxr_path.c_str());
            if (m_hostfxr == nullptr)
            {
                log_bridge(4, L"Could not load private Windows hostfxr.dll.");
                return false;
            }
            log_bridge(1, L"hostfxr.dll loaded.");

            const auto initialize_for_config = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
                GetProcAddress(m_hostfxr, "hostfxr_initialize_for_runtime_config"));
            const auto get_delegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
                GetProcAddress(m_hostfxr, "hostfxr_get_runtime_delegate"));
            const auto close = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(m_hostfxr, "hostfxr_close"));
            if (initialize_for_config == nullptr || get_delegate == nullptr || close == nullptr)
            {
                log_bridge(4, L"hostfxr exports are incomplete.");
                return false;
            }

            hostfxr_handle context{};
            const auto runtime_config = managed_root / L"RogueMod.Runtime.runtimeconfig.json";
            log_bridge(1, L"Initializing hostfxr from runtimeconfig.json.");
            auto result = initialize_for_config(runtime_config.c_str(), nullptr, &context);
            log_result(L"hostfxr_initialize_for_runtime_config", result);
            if (result < 0 || context == nullptr)
            {
                log_bridge(4, L"hostfxr could not initialize RogueMod.Runtime.");
                return false;
            }

            load_assembly_and_get_function_pointer_fn load_assembly{};
            log_bridge(1, L"Requesting managed assembly loader.");
            result = get_delegate(
                context,
                hostfxr_delegate_type::load_assembly_and_get_function_pointer,
                reinterpret_cast<void**>(&load_assembly));
            log_result(L"hostfxr_get_runtime_delegate", result);
            close(context);
            if (result < 0 || load_assembly == nullptr)
            {
                log_bridge(4, L"hostfxr did not provide the managed assembly loader.");
                return false;
            }

            const auto assembly = managed_root / L"RogueMod.Runtime.dll";
            constexpr auto type_name = L"RogueMod.Runtime.NativeBootstrap, RogueMod.Runtime";
            initialize_managed_fn initialize{};
            log_bridge(1, L"Resolving managed Initialize entry point.");
            result = load_assembly(
                assembly.c_str(), type_name, L"Initialize", UnmanagedCallersOnly, nullptr,
                reinterpret_cast<void**>(&initialize));
            log_result(L"load managed Initialize", result);
            if (result < 0 || initialize == nullptr)
            {
                log_bridge(4, L"Managed Initialize entry point was not found.");
                return false;
            }

            log_bridge(1, L"Resolving managed DispatchGameEvent entry point.");
            result = load_assembly(
                assembly.c_str(), type_name, L"DispatchGameEvent", UnmanagedCallersOnly, nullptr,
                reinterpret_cast<void**>(&m_dispatch_game_event));
            log_result(L"load managed DispatchGameEvent", result);
            if (result < 0 || m_dispatch_game_event == nullptr)
            {
                log_bridge(4, L"Managed DispatchGameEvent entry point was not found.");
                return false;
            }

            log_bridge(1, L"Resolving managed Shutdown entry point.");
            result = load_assembly(
                assembly.c_str(), type_name, L"Shutdown", UnmanagedCallersOnly, nullptr,
                reinterpret_cast<void**>(&m_shutdown));
            log_result(L"load managed Shutdown", result);
            if (result < 0 || m_shutdown == nullptr)
            {
                log_bridge(4, L"Managed Shutdown entry point was not found.");
                return false;
            }

            RogueMod::HostApi api{
                sizeof(RogueMod::HostApi),
                RogueMod::HostAbiVersion,
                &log_from_managed,
                roguemod_root.c_str(),
                L"deadzone-rogue-steam",
                &unreal_is_available,
                &unreal_find_first_of,
                &unreal_is_valid,
                &unreal_get_class,
                &unreal_get_path_name,
                &unreal_get_capabilities,
                &unreal_invoke_zero_parameter,
                &unreal_read_property,
                &unreal_write_property,
                &unreal_invoke,
                game_mods_root.c_str(),
                &unreal_find_all_of,
                &unreal_register_hook,
                &unreal_unregister_hook,
                &unreal_create_object,
                &unreal_spawn_actor};
            log_bridge(1, L"Calling managed Initialize entry point.");
            result = initialize(&api);
            log_result(L"managed Initialize", result);
            if (result != 0)
            {
                m_shutdown = nullptr;
                log_bridge(4, L"Managed runtime rejected the native host ABI.");
                return false;
            }

            return true;
        }

        void dispatch_game_event(managed_game_event event)
        {
            if (m_dispatch_game_event == nullptr)
            {
                return;
            }

            const auto result = m_dispatch_game_event(static_cast<std::int32_t>(event));
            if (result != 0)
            {
                log_result(L"managed DispatchGameEvent", result);
                log_bridge(4, L"Managed game-event dispatch was disabled after an error.");
                m_dispatch_game_event = nullptr;
            }
        }

        void stop()
        {
            UnrealReflection.set_ready(false);
            if (m_shutdown != nullptr)
            {
                m_shutdown();
                m_shutdown = nullptr;
            }
            m_dispatch_game_event = nullptr;
        }

    private:
        HMODULE m_hostfxr{};
        dispatch_game_event_managed_fn m_dispatch_game_event{};
        shutdown_managed_fn m_shutdown{};
    };

    class RogueModBridge final : public RC::CppUserModBase
    {
    public:
        RogueModBridge()
        {
            ModName = L"RogueModBridge";
            ModVersion = L"0.1.0";
            ModDescription = L"Managed mod runtime bridge for RogueMod";
            ModAuthors = L"RogueMod contributors";
        }

        ~RogueModBridge() override
        {
            m_runtime.stop();
        }

        auto on_program_start() -> void override
        {
            const auto module = current_module_path();
            if (module.empty())
            {
                log_bridge(4, L"Could not resolve RogueMod.Bridge module path.");
                return;
            }

            const auto bridge_root = module.parent_path().parent_path();
            auto game_root = bridge_root;
            for (std::uint32_t level = 0; level < 6; ++level)
            {
                game_root = game_root.parent_path();
            }
            const auto roguemod_root = game_root / L"RogueMod";
            const auto game_mods_root = game_root / L"Mods";
            LogPath = roguemod_root / L"RogueMod.log";
            log_bridge(2, L"Starting RogueMod managed runtime.");
            const auto ue4ss_module = GetModuleHandleW(L"UE4SS.dll");
            if (UnrealReflection.resolve(ue4ss_module, write_log))
            {
                log_bridge(2, L"Resolved UE4SS reflection exports.");
            }
            else
            {
                log_bridge(3, L"UE4SS reflection exports are unavailable for this build.");
            }
            if (m_runtime.start(roguemod_root, game_mods_root))
            {
                m_runtime.dispatch_game_event(managed_game_event::program_started);
            }
        }

        auto on_unreal_init() -> void override
        {
            UnrealReflection.set_ready(true);
            m_runtime.dispatch_game_event(managed_game_event::unreal_initialized);
        }

        auto on_ui_init() -> void override
        {
            m_runtime.dispatch_game_event(managed_game_event::ui_initialized);
        }

        auto on_update() -> void override
        {
            UnrealReflection.set_ready(true);
            m_runtime.dispatch_game_event(managed_game_event::update);
        }

        auto on_cpp_mods_loaded() -> void override
        {
            m_runtime.dispatch_game_event(managed_game_event::cpp_mods_loaded);
        }

    private:
        ManagedRuntime m_runtime;
    };
}

#define ROGUEMOD_EXPORT __declspec(dllexport)

extern "C"
{
    ROGUEMOD_EXPORT RC::CppUserModBase* start_mod()
    {
        return new RogueModBridge();
    }

    ROGUEMOD_EXPORT void uninstall_mod(RC::CppUserModBase* mod)
    {
        delete mod;
    }
}
