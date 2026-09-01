-- RogueMod.LuaMod: a Lua mod for Deadzone: Rogue scaffolded by RogueMod.
-- RogueMod packages, deploys, and activates this script. The Unreal object model,
-- hook registration, and utility globals come from the UE4SS Lua API:
-- https://ue4ss.org/docs/dev/api/globals

Log("Sample Lua mod loaded.")

-- Example: log the local player controller every five seconds while the game runs.
-- Uncomment to try it, then check UE4SS.log for the output.
-- LoopAsync(5000, function()
--     local controller = FindFirstOf("ValPlayerController")
--     if controller ~= nil then
--         Log("ValPlayerController: " .. controller:GetFullName())
--     end
-- end)
