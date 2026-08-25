[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $BridgePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeVersion = '10.0.10'
$runtimeArchiveName = "dotnet-runtime-$runtimeVersion-win-x64.zip"
$runtimeUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/$runtimeVersion/$runtimeArchiveName"
$runtimeSha512 = '2161dfa1cf027cdc074de7195b5f206b17ebd829ae415b9e7c9ee5f06d3952b6583030022dbe0d6e9221b5c577c411d7cd5322241f6d2299d9c886641215699b'
$downloadDirectory = Join-Path $repositoryRoot '.tools\downloads'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts\runtime\RogueMod'))
$expectedPackageRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts\runtime\RogueMod'))
$managedRoot = Join-Path $packageRoot 'runtime\managed'
$dotnetRoot = Join-Path $packageRoot 'runtime\dotnet'
$archivePath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts\runtime\RogueMod.Runtime-win-x64.zip'))

if (-not $packageRoot.Equals($expectedPackageRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $packageRoot.StartsWith($repositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace unexpected runtime package path: $packageRoot"
}

New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$downloadPath = Join-Path $downloadDirectory $runtimeArchiveName
if (-not (Test-Path -LiteralPath $downloadPath -PathType Leaf)) {
    Invoke-WebRequest -Uri $runtimeUrl -OutFile $downloadPath
}

$actualRuntimeSha512 = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA512).Hash.ToLowerInvariant()
if (-not $actualRuntimeSha512.Equals($runtimeSha512, [StringComparison]::Ordinal)) {
    throw "Windows .NET runtime checksum mismatch for '$downloadPath'."
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $managedRoot, $dotnetRoot, (Join-Path $packageRoot 'dlls') -Force | Out-Null
Expand-Archive -LiteralPath $downloadPath -DestinationPath $dotnetRoot

& dotnet publish (Join-Path $repositoryRoot 'src\RogueMod.Runtime\RogueMod.Runtime.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $managedRoot `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if ([string]::IsNullOrWhiteSpace($BridgePath)) {
    $bridge = Get-ChildItem (Join-Path $repositoryRoot '.build\native') -Filter 'RogueMod.Bridge.dll' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $bridge) {
        throw 'The native bridge has not been built. Build RogueModBridge and package again.'
    }
    $BridgePath = $bridge.FullName
}

$resolvedBridgePath = if ([IO.Path]::IsPathRooted($BridgePath)) {
    [IO.Path]::GetFullPath($BridgePath)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $BridgePath))
}
if (-not (Test-Path -LiteralPath $resolvedBridgePath -PathType Leaf)) {
    throw "Native bridge does not exist: $resolvedBridgePath"
}
Copy-Item -LiteralPath $resolvedBridgePath -Destination (Join-Path $packageRoot 'dlls\main.dll')

$requiredFiles = @(
    (Join-Path $packageRoot 'dlls\main.dll'),
    (Join-Path $managedRoot 'RogueMod.Runtime.dll'),
    (Join-Path $managedRoot 'RogueMod.Runtime.runtimeconfig.json')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Runtime package validation failed; missing file: $requiredFile"
    }
}

$hostFxr = Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Filter 'hostfxr.dll' -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
$coreClr = Get-ChildItem (Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App') -Filter 'coreclr.dll' -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -eq $hostFxr -or $null -eq $coreClr) {
    throw 'Runtime package validation failed; the private Windows .NET runtime is incomplete.'
}

$metadata = [ordered]@{
    schemaVersion = 1
    target = 'win-x64'
    compatibleHosts = @('windows', 'proton')
    dotnetRuntimeVersion = $runtimeVersion
    bridgeSha256 = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'dlls\main.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
    managedRuntimeSha256 = (Get-FileHash -LiteralPath (Join-Path $managedRoot 'RogueMod.Runtime.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'runtime-package.json') -Encoding utf8NoBOM

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $packageRoot,
    $archivePath,
    [IO.Compression.CompressionLevel]::Optimal,
    $true)

Write-Output "Runtime package: $packageRoot"
Write-Output "Runtime archive: $archivePath"
