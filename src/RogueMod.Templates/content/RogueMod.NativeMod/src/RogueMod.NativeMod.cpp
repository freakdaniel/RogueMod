#include <RogueMod/NativeLog.hpp>
#include <RogueMod/NativeMod.hpp>

namespace
{
    class SampleNative final : public RC::CppUserModBase
    {
      public:
        SampleNative()
        {
            ModName = L"SampleNative";
            ModVersion = L"0.1.0";
            ModDescription = L"Sample native mod";
            ModAuthors = L"RogueMod contributors";
        }

        ~SampleNative() override
        {
            RogueMod::NativeLog::line(L"Sample native mod unloaded.", L"SampleNative.log");
        }

        auto on_program_start() -> void override
        {
            RogueMod::NativeLog::line(L"Sample native mod loaded on Deadzone: Rogue.", L"SampleNative.log");
        }
    };
}

ROGUEMOD_DEFINE_NATIVE_MOD(SampleNative)
