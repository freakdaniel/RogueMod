#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
feed="$repository_root/.artifacts/sdk/packages"
game_sdk_package="$feed/DeadzoneRogue.Sdk.0.1.0.nupkg"
restore_packages="$(mktemp -d "${TMPDIR:-/tmp}/roguemod-game-sdk-samples.XXXXXX")"
sample_projects=(
    "$repository_root/src/RogueMod.Sample.TypedHooks/RogueMod.Sample.TypedHooks.csproj"
    "$repository_root/src/RogueMod.Sample.Invulnerability/RogueMod.Sample.Invulnerability.csproj"
)

cleanup() {
    rm -rf -- "$restore_packages"
}
trap cleanup EXIT

if [[ ! -f "$game_sdk_package" ]]; then
    printf 'Missing generated game SDK package: %s\n' "$game_sdk_package" >&2
    printf 'Run scripts/build-game-sdk.ps1 for the verified Deadzone: Rogue build first.\n' >&2
    exit 1
fi

mkdir -p "$feed"

dotnet pack "$repository_root/src/RogueMod.Abstractions/RogueMod.Abstractions.csproj" \
    --configuration Release --output "$feed" --nologo
dotnet pack "$repository_root/src/RogueMod.Sdk/RogueMod.Sdk.csproj" \
    --configuration Release --output "$feed" --nologo

for sample_project in "${sample_projects[@]}"; do
    dotnet restore "$sample_project" --source "$feed" --packages "$restore_packages" --nologo
    dotnet build "$sample_project" --configuration Release --target PackageRogueMod --no-restore --nologo
done
