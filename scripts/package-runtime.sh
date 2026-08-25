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
        'The native bridge has not been built, so a usable runtime package cannot be created.' \
        'Run scripts/native-toolchain.sh build and package again.' >&2
    exit 1
fi

hostfxr=$(find "$dotnet_root/host/fxr" -type f -name hostfxr.dll -print -quit 2>/dev/null || true)
coreclr=$(find "$dotnet_root/shared/Microsoft.NETCore.App" -type f -name coreclr.dll -print -quit 2>/dev/null || true)
for required in \
    "$package_root/dlls/main.dll" \
    "$managed_root/RogueMod.Runtime.dll" \
    "$managed_root/RogueMod.Runtime.runtimeconfig.json" \
    "$hostfxr" \
    "$coreclr"; do
    if [[ -z "$required" || ! -f "$required" ]]; then
        printf 'Runtime package validation failed: %s\n' "${required:-missing private runtime component}" >&2
        exit 1
    fi
done

bridge_sha256=$(sha256sum "$package_root/dlls/main.dll" | cut -d ' ' -f 1)
managed_sha256=$(sha256sum "$managed_root/RogueMod.Runtime.dll" | cut -d ' ' -f 1)
cat > "$package_root/runtime-package.json" <<EOF
{
  "schemaVersion": 1,
  "target": "win-x64",
  "compatibleHosts": ["windows", "proton"],
  "dotnetRuntimeVersion": "$runtime_version",
  "bridgeSha256": "$bridge_sha256",
  "managedRuntimeSha256": "$managed_sha256"
}
EOF

archive="$repository_root/.artifacts/runtime/RogueMod.Runtime-win-x64.zip"
rm -f "$archive"
if command -v zip >/dev/null 2>&1; then
    (
        cd "$(dirname "$package_root")"
        zip -qr "$archive" "$(basename "$package_root")"
    )
elif command -v 7z >/dev/null 2>&1; then
    (
        cd "$(dirname "$package_root")"
        7z a -tzip -bd -bso0 "$archive" "$(basename "$package_root")"
    )
else
    printf '%s\n' 'Runtime archive creation requires either zip or 7z.' >&2
    exit 1
fi

printf 'Runtime package: %s\nRuntime archive: %s\n' "$package_root" "$archive"
