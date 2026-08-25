[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $GamePath,

    [string] $Jmap,

    [ValidateRange(0, 3600)]
    [int] $WaitForDumpSeconds = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkConfigurationPath = Join-Path $repositoryRoot 'config\GameSdk\deadzone-rogue.json'
$sdkConfiguration = Get-Content -LiteralPath $sdkConfigurationPath -Raw | ConvertFrom-Json
$profile = Get-Content -LiteralPath (Join-Path $repositoryRoot $sdkConfiguration.gameProfile) -Raw | ConvertFrom-Json
$gameRoot = [IO.Path]::GetFullPath($GamePath)
$ue4ssRoot = [IO.Path]::GetFullPath((Join-Path $gameRoot $profile.ue4ss.rootRelativePath))

function Test-JmapReady([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -eq 0) {
        return $false
    }
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $stream.Dispose()
        return $true
    }
    catch [IO.IOException] {
        return $false
    }
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds($WaitForDumpSeconds)
if ([string]::IsNullOrWhiteSpace($Jmap)) {
    do {
        $dump = Get-ChildItem -LiteralPath $ue4ssRoot -Filter '*.jmap' -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $dump -and (Test-JmapReady $dump.FullName)) {
            $Jmap = $dump.FullName
            break
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            break
        }
        Start-Sleep -Seconds 2
    } while ($true)
}
elseif ($WaitForDumpSeconds -gt 0) {
    while (-not (Test-JmapReady $Jmap) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Seconds 2
    }
}

if ([string]::IsNullOrWhiteSpace($Jmap) -or -not (Test-JmapReady $Jmap)) {
    throw "No JMAP snapshot was found in '$ue4ssRoot'. Prepare the capture and launch the game once."
}

$output = Join-Path $repositoryRoot '.artifacts\sdk\deadzone-rogue'
$packageOutput = Join-Path $repositoryRoot '.artifacts\sdk\packages'
$feed = Join-Path $repositoryRoot '.artifacts\sdk\feed'
New-Item -ItemType Directory -Path $output, $packageOutput, $feed -Force | Out-Null

& dotnet run --project (Join-Path $repositoryRoot 'src\RogueMod.Cli') `
    --configuration Release --no-build -- `
    generate-sdk --jmap ([IO.Path]::GetFullPath($Jmap)) --output $output `
    --namespace $sdkConfiguration.rootNamespace `
    --package-id $sdkConfiguration.packageId `
    --package-version $sdkConfiguration.packageVersion `
    --roguemod-version $sdkConfiguration.rogueModVersion `
    --game-version $sdkConfiguration.gameVersion `
    --standalone
if ($LASTEXITCODE -ne 0) {
    throw "Game SDK generation failed with exit code $LASTEXITCODE."
}

& dotnet pack (Join-Path $repositoryRoot 'src\RogueMod.Abstractions\RogueMod.Abstractions.csproj') `
    --configuration Release --output $feed --nologo
if ($LASTEXITCODE -ne 0) {
    throw "RogueMod.Abstractions packing failed with exit code $LASTEXITCODE."
}

$generatedProject = Join-Path $output 'DeadzoneRogue.Sdk.csproj'
& dotnet restore $generatedProject --source $feed --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Generated game SDK restore failed with exit code $LASTEXITCODE."
}
& dotnet pack $generatedProject --configuration Release --no-restore --output $packageOutput --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Generated game SDK packing failed with exit code $LASTEXITCODE."
}

$generatedAssembly = Join-Path $output 'bin\Release\net10.0\DeadzoneRogue.Sdk.dll'
$generatedManifest = Join-Path $output 'RogueMod.GameSdk.json'
$sharedDestinations = @(
    (Join-Path $repositoryRoot '.artifacts\runtime\RogueMod\runtime\shared'),
    (Join-Path $GamePath 'RogueMod\runtime\shared')
)
foreach ($sharedDestination in $sharedDestinations) {
    if (Test-Path -LiteralPath (Split-Path -Parent $sharedDestination) -PathType Container) {
        New-Item -ItemType Directory -Path $sharedDestination -Force | Out-Null
        Copy-Item -LiteralPath $generatedAssembly -Destination (Join-Path $sharedDestination 'DeadzoneRogue.Sdk.dll') -Force
        Copy-Item -LiteralPath $generatedManifest -Destination (Join-Path $sharedDestination 'DeadzoneRogue.Sdk.json') -Force
    }
}

$runtimePackageRoot = Join-Path $repositoryRoot '.artifacts\runtime\RogueMod'
$runtimeMetadataPath = Join-Path $runtimePackageRoot 'runtime-package.json'
if (Test-Path -LiteralPath $runtimeMetadataPath -PathType Leaf) {
    $runtimeMetadata = Get-Content -LiteralPath $runtimeMetadataPath -Raw | ConvertFrom-Json
    $runtimeMetadata | Add-Member -MemberType NoteProperty -Name gameSdk -Value ([ordered]@{
        packageId = $sdkConfiguration.packageId
        packageVersion = $sdkConfiguration.packageVersion
        gameVersion = $sdkConfiguration.gameVersion
        assemblySha256 = (Get-FileHash -LiteralPath $generatedAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
    }) -Force
    $runtimeMetadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $runtimeMetadataPath -Encoding utf8NoBOM

    $runtimeArchive = Join-Path $repositoryRoot '.artifacts\runtime\RogueMod.Runtime-win-x64.zip'
    if (Test-Path -LiteralPath $runtimeArchive -PathType Leaf) {
        Remove-Item -LiteralPath $runtimeArchive -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $runtimePackageRoot,
        $runtimeArchive,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)
}

$modsFile = Join-Path $ue4ssRoot 'Mods\mods.txt'
if (Test-Path -LiteralPath $modsFile -PathType Leaf) {
    $updatedMods = Get-Content -LiteralPath $modsFile |
        ForEach-Object { if ($_ -match '^\s*RogueModSdkDumper\s*:') { 'RogueModSdkDumper : 0' } else { $_ } }
    $updatedMods | Set-Content -LiteralPath $modsFile -Encoding utf8NoBOM
}

Write-Output "Game SDK package: $packageOutput\$($sdkConfiguration.packageId).$($sdkConfiguration.packageVersion).nupkg"
Write-Output 'The shared game SDK was installed once into RogueMod runtime; individual mod packages exclude it.'
Write-Output 'The JMAP snapshot remains a maintainer artifact; mod authors consume only the generated NuGet package.'
