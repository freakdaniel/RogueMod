[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $GamePath,

    [switch] $ReplaceUe4ss,

    [switch] $InstallRuntime,

    [string] $RuntimePackage
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkConfigurationPath = Join-Path $repositoryRoot 'config\GameSdk\deadzone-rogue.json'
$sdkConfiguration = Get-Content -LiteralPath $sdkConfigurationPath -Raw | ConvertFrom-Json
$profilePath = Join-Path $repositoryRoot $sdkConfiguration.gameProfile
$profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
$gameRoot = [IO.Path]::GetFullPath($GamePath)
$gameExecutable = [IO.Path]::GetFullPath((Join-Path $gameRoot $profile.executableRelativePath))
$gameRootPrefix = $gameRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $gameExecutable.StartsWith($gameRootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $gameExecutable -PathType Leaf)) {
    throw "Deadzone: Rogue executable was not found under the supplied game root: $gameExecutable"
}

$actualGameVersion = (Get-Item -LiteralPath $gameExecutable).VersionInfo.FileVersion
if (-not $actualGameVersion.Equals($sdkConfiguration.gameVersion, [StringComparison]::Ordinal)) {
    throw "Game version mismatch. Expected $($sdkConfiguration.gameVersion), found $actualGameVersion. Update the SDK configuration before dumping a new game build."
}

$win64Directory = [IO.Path]::GetFullPath((Split-Path -Parent $gameExecutable))
$expectedWin64Directory = [IO.Path]::GetFullPath((Join-Path $gameRoot 'Valhalla\Binaries\Win64'))
if (-not $win64Directory.Equals($expectedWin64Directory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install UE4SS outside the expected game binary directory: $win64Directory"
}

function Set-EngineVersionOverride([string] $Path, [int] $MajorVersion, [int] $MinorVersion) {
    $lines = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Get-Content -LiteralPath $Path | ForEach-Object { $lines.Add($_) }
    }

    $sectionStart = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($lines[$index].Trim().Equals('[EngineVersionOverride]', [StringComparison]::OrdinalIgnoreCase)) {
            $sectionStart = $index
            break
        }
    }
    if ($sectionStart -lt 0) {
        if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Length -ne 0) {
            $lines.Add('')
        }
        $lines.Add('[EngineVersionOverride]')
        $sectionStart = $lines.Count - 1
    }

    $sectionEnd = $lines.Count
    for ($index = $sectionStart + 1; $index -lt $lines.Count; $index++) {
        $trimmed = $lines[$index].Trim()
        if ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']')) {
            $sectionEnd = $index
            break
        }
    }

    foreach ($entry in @(
        @{ Key = 'MajorVersion'; Value = $MajorVersion }
        @{ Key = 'MinorVersion'; Value = $MinorVersion }
    )) {
        $found = $false
        for ($index = $sectionStart + 1; $index -lt $sectionEnd; $index++) {
            if ($lines[$index] -match "^\s*$([Regex]::Escape($entry.Key))\s*=") {
                $lines[$index] = "$($entry.Key) = $($entry.Value)"
                $found = $true
                break
            }
        }
        if (-not $found) {
            $lines.Insert($sectionEnd, "$($entry.Key) = $($entry.Value)")
            $sectionEnd++
        }
    }

    [IO.File]::WriteAllLines($Path, [string[]]$lines, [Text.UTF8Encoding]::new($false))
}

$downloadDirectory = Join-Path $repositoryRoot '.tools\downloads'
$archivePath = Join-Path $downloadDirectory $sdkConfiguration.ue4ss.archiveName
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
    Invoke-WebRequest -Uri $sdkConfiguration.ue4ss.downloadUrl -OutFile $archivePath
}

$actualArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
if (-not $actualArchiveHash.Equals($sdkConfiguration.ue4ss.sha256, [StringComparison]::Ordinal)) {
    throw "UE4SS archive checksum mismatch for '$archivePath'."
}

$transaction = [Guid]::NewGuid().ToString('N')
$stageRoot = [IO.Path]::GetFullPath((Join-Path $win64Directory ".roguemod-sdk-stage-$transaction"))
$backupRoot = [IO.Path]::GetFullPath((Join-Path $win64Directory ".roguemod-sdk-backup-$transaction"))
$proxyPath = Join-Path $win64Directory 'dwmapi.dll'
$ue4ssRoot = Join-Path $win64Directory 'ue4ss'
$existingTargets = @(@($proxyPath, $ue4ssRoot) | Where-Object { Test-Path -LiteralPath $_ })
if ($existingTargets.Count -gt 0 -and -not $ReplaceUe4ss) {
    throw "UE4SS is already present. Pass -ReplaceUe4ss to replace the pinned installation: $($existingTargets -join ', ')"
}

New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
try {
    Expand-Archive -LiteralPath $archivePath -DestinationPath $stageRoot
    $stagedProxy = Join-Path $stageRoot 'dwmapi.dll'
    $stagedUe4ss = Join-Path $stageRoot 'ue4ss'
    if (-not (Test-Path -LiteralPath $stagedProxy -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $stagedUe4ss 'UE4SS.dll') -PathType Leaf)) {
        throw 'The verified UE4SS archive does not contain the expected proxy layout.'
    }

    if ($null -ne $profile.ue4ss.engineVersionOverride) {
        Set-EngineVersionOverride `
            -Path (Join-Path $stagedUe4ss 'UE4SS-settings.ini') `
            -MajorVersion $profile.ue4ss.engineVersionOverride.majorVersion `
            -MinorVersion $profile.ue4ss.engineVersionOverride.minorVersion
    }

    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'config\Compatibility\DeadzoneRogue\VTableLayout.ini') `
        -Destination (Join-Path $stagedUe4ss 'VTableLayout.ini')
    $dumperScripts = Join-Path $stagedUe4ss 'Mods\RogueModSdkDumper\Scripts'
    New-Item -ItemType Directory -Path $dumperScripts -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'src\RogueMod.Tooling.SdkDumper\Scripts\main.lua') `
        -Destination (Join-Path $dumperScripts 'main.lua')

    @(
        'CheatManagerEnablerMod : 0'
        'ConsoleCommandsMod : 0'
        'ConsoleEnablerMod : 0'
        'SplitScreenMod : 0'
        'LineTraceMod : 0'
        'BPML_GenericFunctions : 0'
        'BPModLoaderMod : 0'
        'RogueModSdkDumper : 1'
        'Keybinds : 1'
    ) | Set-Content -LiteralPath (Join-Path $stagedUe4ss 'Mods\mods.txt') -Encoding utf8NoBOM

    if ($existingTargets.Count -gt 0) {
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        foreach ($target in $existingTargets) {
            Move-Item -LiteralPath $target -Destination (Join-Path $backupRoot (Split-Path -Leaf $target))
        }
    }

    try {
        Move-Item -LiteralPath $stagedProxy -Destination $proxyPath
        Move-Item -LiteralPath $stagedUe4ss -Destination $ue4ssRoot
    }
    catch {
        if (Test-Path -LiteralPath $proxyPath -PathType Leaf) {
            Remove-Item -LiteralPath $proxyPath -Force
        }
        if (Test-Path -LiteralPath $ue4ssRoot -PathType Container) {
            Remove-Item -LiteralPath $ue4ssRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $backupRoot -PathType Container) {
            Get-ChildItem -LiteralPath $backupRoot | ForEach-Object {
                Move-Item -LiteralPath $_.FullName -Destination (Join-Path $win64Directory $_.Name)
            }
        }
        throw
    }
}
finally {
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $backupRoot -PathType Container) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }
}

if ($InstallRuntime) {
    if ([string]::IsNullOrWhiteSpace($RuntimePackage)) {
        $RuntimePackage = Join-Path $repositoryRoot '.artifacts\runtime\RogueMod'
    }
    $runtimeArguments = @(
        'run', '--project', (Join-Path $repositoryRoot 'src\RogueMod.Cli'),
        '--configuration', 'Release', '--no-build', '--',
        'install-runtime', '--game', $gameRoot, '--package', ([IO.Path]::GetFullPath($RuntimePackage))
    )
    if ((Test-Path -LiteralPath (Join-Path $gameRoot 'RogueMod') -PathType Container) -or
        (Test-Path -LiteralPath (Join-Path $ue4ssRoot 'Mods\RogueModBridge') -PathType Container)) {
        $runtimeArguments += '--replace'
    }
    & dotnet @runtimeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "RogueMod runtime installation failed with exit code $LASTEXITCODE."
    }
}

Write-Output "Prepared maintainer SDK capture for Deadzone: Rogue $actualGameVersion."
Write-Output "UE4SS: $($sdkConfiguration.ue4ss.version)"
Write-Output "Automatic dump mod: $ue4ssRoot\Mods\RogueModSdkDumper"
Write-Output "Launch the game once; the JMAP snapshot starts automatically after game-state initialization."
