local dumpStarted = false

local function DumpRogueModSdkMetadata()
    if dumpStarted then
        return
    end

    dumpStarted = true
    print("[RogueModSdkDumper] Starting type-only JMAP dump...")
    -- Blueprint types are required by the generated SDK. CDO property values are not.
    DumpJMAP(true, true)
end

RegisterKeyBind(Key.F5, {ModifierKey.CONTROL}, DumpRogueModSdkMetadata)

RegisterInitGameStatePostHook(function()
    print("[RogueModSdkDumper] Game state initialized; scheduling automatic SDK snapshot...")
    if ExecuteInGameThreadWithDelay ~= nil then
        ExecuteInGameThreadWithDelay(10000, DumpRogueModSdkMetadata)
    else
        DumpRogueModSdkMetadata()
    end
end)
