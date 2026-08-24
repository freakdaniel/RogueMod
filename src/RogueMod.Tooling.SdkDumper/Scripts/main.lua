local function DumpRogueModSdkMetadata()
    print("[RogueModSdkDumper] Starting type-only JMAP dump...")
    -- Blueprint types are required by the generated SDK. CDO property values are not.
    DumpJMAP(true, true)
end

RegisterKeyBind(Key.F5, {ModifierKey.CONTROL}, DumpRogueModSdkMetadata)
