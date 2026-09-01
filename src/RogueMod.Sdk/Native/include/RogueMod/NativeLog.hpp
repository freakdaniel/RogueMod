/**
 * @file NativeLog.hpp
 * @brief Append-only UTF-8 logging for native mods built on the pinned lifecycle headers.
 */

#pragma once

#include <filesystem>
#include <string>
#include <string_view>

#include <Windows.h>

namespace RogueMod
{
    /**
     * @brief Minimal file logger for mods that use the pinned SDK headers.
     *
     * The pinned headers intentionally do not include UE4SS logging, so native mods write
     * their own log beside the deployment. Every line is UTF-8 encoded and appended with a
     * CRLF terminator. All methods are static and safe to call from any thread that owns
     * the game's logging cadence; RogueMod lifecycle callbacks arrive on the game thread.
     *
     * @note The log file lives inside the UE4SS deployment directory of the mod and is
     *       recreated when RogueMod redeploys the package (for example after an update).
     */
    class NativeLog
    {
      public:
        /**
         * @brief Appends one message to the log file.
         *
         * Silently does nothing when the module path cannot be resolved or the file
         * cannot be opened. The log is written next to the mod's `dlls/` directory.
         *
         * @param message The message to append. Converted from UTF-16 to UTF-8.
         * @param file_name The log file name inside the deployment directory.
         */
        static auto line(std::wstring_view message, std::wstring_view file_name = L"native-mod.log") -> void
        {
            const auto module_path = current_module_path();
            if (module_path.empty())
            {
                return;
            }

            append_utf8(module_path.parent_path().parent_path() / std::filesystem::path{file_name}, message);
        }

      private:
        /**
         * @brief Resolves the file system path of the module executing this code.
         * @return The module path, or an empty path when resolution fails.
         */
        static auto current_module_path() -> std::filesystem::path
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

        /**
         * @brief Opens the file for shared append and writes one UTF-8 line.
         * @param path The target log file.
         * @param message The message line, terminated with CRLF by this call.
         */
        static auto append_utf8(const std::filesystem::path& path, std::wstring_view message) -> void
        {
            const auto file = CreateFileW(
                path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE)
            {
                return;
            }

            std::wstring converted{message};
            converted += L"\r\n";
            const auto utf8_size = WideCharToMultiByte(
                CP_UTF8, 0, converted.data(), static_cast<int>(converted.size()), nullptr, 0, nullptr, nullptr);
            if (utf8_size > 0)
            {
                std::string utf8(static_cast<std::size_t>(utf8_size), '\0');
                WideCharToMultiByte(
                    CP_UTF8, 0, converted.data(), static_cast<int>(converted.size()), utf8.data(), utf8_size, nullptr, nullptr);
                DWORD bytes_written{};
                WriteFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &bytes_written, nullptr);
            }
            CloseHandle(file);
        }
    };
}
