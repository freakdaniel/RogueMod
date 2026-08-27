[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$feed = Join-Path $repositoryRoot '.artifacts\sdk\packages'
$gameSdkPackage = Join-Path $feed 'DeadzoneRogue.Sdk.0.1.0.nupkg'
$restorePackages = Join-Path ([IO.Path]::GetTempPath()) ("RogueMod.GameSdkSamples." + [Guid]::NewGuid().ToString('N'))
$sampleProjects = @(
    (Join-Path $repositoryRoot 'src\RogueMod.Sample.TypedHooks\RogueMod.Sample.TypedHooks.csproj'),
    (Join-Path $repositoryRoot 'src\RogueMod.Sample.Invulnerability\RogueMod.Sample.Invulnerability.csproj')
)

if (-not (Test-Path -LiteralPath $gameSdkPackage -PathType Leaf)) {
    throw "Missing generated game SDK package: $gameSdkPackage. Run scripts/build-game-sdk.ps1 for the verified Deadzone: Rogue build first."
}

New-Item -ItemType Directory -Path $feed -Force | Out-Null

& dotnet pack (Join-Path $repositoryRoot 'src\RogueMod.Abstractions\RogueMod.Abstractions.csproj') `
    --configuration Release --output $feed --nologo
if ($LASTEXITCODE -ne 0) {
    throw "RogueMod.Abstractions packing failed with exit code $LASTEXITCODE."
}

& dotnet pack (Join-Path $repositoryRoot 'src\RogueMod.Sdk\RogueMod.Sdk.csproj') `
    --configuration Release --output $feed --nologo
if ($LASTEXITCODE -ne 0) {
    throw "RogueMod.Sdk packing failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $restorePackages | Out-Null
try {
    foreach ($sampleProject in $sampleProjects) {
        & dotnet restore $sampleProject --source $feed --packages $restorePackages --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Game SDK sample restore failed with exit code $LASTEXITCODE."
        }

        & dotnet build $sampleProject --configuration Release --target PackageRogueMod --no-restore --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Game SDK sample build failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $restorePackages) {
        Remove-Item -LiteralPath $restorePackages -Recurse -Force
    }
}
