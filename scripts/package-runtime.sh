#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
configuration=${1:-Release}
runtime_version=10.0.10
runtime_archive="dotnet-runtime-${runtime_version}-win-x64.zip"
runtime_url="https://builds.dotnet.microsoft.com/dotnet/Runtime/${runtime_version}/${runtime_archive}"
runtime_sha512=2161dfa1cf027cdc074de7195b5f206b17ebd829ae415b9e7c9ee5f06d3952b6583030022dbe0d6e9221b5c577c411d7cd5322241f6d2299d9c886641215699b
download_dir="$repository_root/.tools/downloads"
package_root="$repository_root/.artifacts/runtime/RogueMod"
managed_root="$package_root/runtime/managed"
dotnet_root="$package_root/runtime/dotnet"

mkdir -p "$download_dir"
archive="$download_dir/$runtime_archive"
if [[ ! -f "$archive" ]]; then
    curl --fail --location --retry 3 "$runtime_url" --output "$archive"
fi
printf '%s  %s\n' "$runtime_sha512" "$archive" | sha512sum --check --status

rm -rf "$package_root"
mkdir -p "$managed_root" "$dotnet_root" "$package_root/dlls"
unzip -q "$archive" -d "$dotnet_root"

dotnet publish "$repository_root/src/RogueMod.Runtime/RogueMod.Runtime.csproj" \
    --configuration "$configuration" \
    --runtime win-x64 \
    --self-contained false \
    --output "$managed_root" \
    --nologo

bridge=$(find "$repository_root/.build/native" -type f -name 'RogueMod.Bridge.dll' -print -quit 2>/dev/null || true)
if [[ -n "$bridge" ]]; then
    cp "$bridge" "$package_root/dlls/main.dll"
else
    printf '%s\n' \
        'Managed runtime was packaged, but the native bridge has not been built yet.' \
        'Run scripts/native-toolchain.sh build and package again.' >&2
fi

printf 'Runtime pack: %s\n' "$package_root"
