#include <filesystem>
#include <string>

#include <Windows.h>

#include <RogueMod/NativeMod.hpp>

namespace
{
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

    void append_log(const wchar_t* message)
    {
        const auto module = current_module_path();
        if (module.empty())
        {
            return;
        }

        const auto path = module.parent_path().parent_path() / L"HelloNativeMod.log";
        const auto file = CreateFileW(
            path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        std::wstring line = message;
        line += L"\r\n";
        const auto utf8_size = WideCharToMultiByte(
            CP_UTF8, 0, line.data(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        if (utf8_size > 0)
        {
            std::string utf8(static_cast<std::size_t>(utf8_size), '\0');
            WideCharToMultiByte(
                CP_UTF8, 0, line.data(), static_cast<int>(line.size()), utf8.data(), utf8_size, nullptr, nullptr);
            DWORD bytes_written{};
            WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &bytes_written, nullptr);
        }
        CloseHandle(file);
    }

    class HelloNativeMod final : public RC::CppUserModBase
    {
      public:
        HelloNativeMod()
        {
            ModName = L"HelloNativeMod";
            ModVersion = L"0.1.0";
            ModDescription = L"Minimal RogueMod native SDK sample";
            ModAuthors = L"RogueMod contributors";
        }

        ~HelloNativeMod() override
        {
            append_log(L"Hello native mod unloaded.");
        }

        auto on_program_start() -> void override
        {
            append_log(L"Hello from sample.hello-native on Deadzone: Rogue.");
        }
    };
}

ROGUEMOD_DEFINE_NATIVE_MOD(HelloNativeMod)
