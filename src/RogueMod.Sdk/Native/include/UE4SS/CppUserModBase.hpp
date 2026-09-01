/**
 * @file CppUserModBase.hpp
 * @brief Pinned lifecycle ABI declaration of the RE-UE4SS C++ mod base class.
 */

#pragma once

#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace RC
{
    using StringType = std::wstring;
    using StringViewType = std::wstring_view;

    namespace GUI
    {
        class GUITab;
    }

    namespace LuaMadeSimple
    {
        class Lua;
    }

    /**
     * @brief Base class every UE4SS C++ mod derives from.
     *
     * This declaration pins the lifecycle ABI of RE-UE4SS build `a1e7f571`; the
     * `static_assert` at the bottom fails the build when the layout changes. Override the
     * `on_*` methods to participate in the UE4SS lifecycle. Callbacks run on Unreal's game
     * thread; keep them short and non-throwing across the destructors.
     *
     * @note These declarations intentionally describe only the lifecycle surface. Unreal
     *       types, hooks, and reflection require the complete UE4SS SDK from the same commit.
     */
    class CppUserModBase
    {
      protected:
        /// GUI tabs registered by the mod and shown in the UE4SS GUI.
        std::vector<std::shared_ptr<GUI::GUITab>> GUITabs{};

      public:
        /// Displayed mod name; keep it aligned with the UE4SS loader directory name.
        StringType ModName{};
        /// Displayed mod version.
        StringType ModVersion{};
        /// Displayed mod description.
        StringType ModDescription{};
        /// Displayed author list.
        StringType ModAuthors{};
        /// UE4SS SDK version the mod was built against.
        StringType ModIntendedSDKVersion{};

        CppUserModBase() = default;
        virtual ~CppUserModBase() = default;

        /// Called once per game frame while the game runs.
        virtual auto on_update() -> void {}
        /// Called when the Unreal object system becomes available.
        virtual auto on_unreal_init() -> void {}
        /// Called when the UE4SS GUI is initialized.
        virtual auto on_ui_init() -> void {}
        /// Called when the game finishes its startup sequence.
        virtual auto on_program_start() -> void {}

        /// Called when a Lua state starts; the per-mod overload carries the state's name.
        virtual auto on_lua_start(
            StringViewType,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            std::vector<LuaMadeSimple::Lua*>&) -> void {}
        /// Called when a Lua state starts; the global overload applies to all states.
        virtual auto on_lua_start(
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            std::vector<LuaMadeSimple::Lua*>&) -> void {}
        /// Called when a Lua state stops; the per-mod overload carries the state's name.
        virtual auto on_lua_stop(
            StringViewType,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            std::vector<LuaMadeSimple::Lua*>&) -> void {}
        /// Called when a Lua state stops; the global overload applies to all states.
        virtual auto on_lua_stop(
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            std::vector<LuaMadeSimple::Lua*>&) -> void {}
        /// Called after a game DLL is loaded; the parameter carries the DLL path.
        virtual auto on_dll_load(StringViewType) -> void {}
        /// Called to render the mod's GUI tab each GUI frame.
        virtual auto render_tab() -> void {}
        /// Called when a Lua state starts; the optional trailing state is the shared per-mod state.
        virtual auto on_lua_start(
            StringViewType,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua*) -> void {}
        /// Called when a Lua state starts; the optional trailing state is the shared per-mod state.
        virtual auto on_lua_start(
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua*) -> void {}
        /// Called when a Lua state stops; the optional trailing state is the shared per-mod state.
        virtual auto on_lua_stop(
            StringViewType,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua*) -> void {}
        /// Called when a Lua state stops; the optional trailing state is the shared per-mod state.
        virtual auto on_lua_stop(
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua&,
            LuaMadeSimple::Lua*) -> void {}
        /// Called after every UE4SS C++ mod has completed loading.
        virtual auto on_cpp_mods_loaded() -> void {}
    };

    static_assert(sizeof(CppUserModBase) == 192, "Pinned UE4SS CppUserModBase ABI changed.");
}
