#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
ue4ss_commit=a1e7f571c789f63f3de6773d056be6f778c14dc8
ue4ss_dir="$repository_root/.deps/RE-UE4SS"
tools_dir="$repository_root/.tools"
xwin_dir="$tools_dir/xwin-sdk"
xwin_version=0.9.0
xwin_archive="xwin-${xwin_version}-x86_64-unknown-linux-musl.tar.gz"
xwin_sha256=31e1033f30608ba6b821d17f1461042bd54c23424813c9b4e9ae15b6d32fa4cd
xwin_binary="$tools_dir/xwin"
build_dir="$repository_root/.build/native/Game__Shipping__Win64"

usage() {
    printf '%s\n' \
        'RogueMod native toolchain' \
        '' \
        'Usage:' \
        '  scripts/native-toolchain.sh doctor' \
        '  scripts/native-toolchain.sh fetch-ue4ss  # optional full UE4SS C++ SDK' \
        '  scripts/native-toolchain.sh install-xwin' \
        '  scripts/native-toolchain.sh bootstrap-xwin --accept-microsoft-license' \
        '  scripts/native-toolchain.sh configure' \
        '  scripts/native-toolchain.sh build'
}

has_command() {
    command -v "$1" >/dev/null 2>&1
}

resolve_tool() {
    local name=$1
    local version
    if has_command "$name"; then
        command -v "$name"
        return 0
    fi
    for version in 22 21 20 19 18 17 16 15; do
        if has_command "${name}-${version}"; then
            command -v "${name}-${version}"
            return 0
        fi
    done
    return 1
}

doctor() {
    local failed=0
    local command_name tool_path
    for command_name in git cmake ninja clang-cl lld-link llvm-rc llvm-ranlib cargo rustc; do
        if tool_path=$(resolve_tool "$command_name"); then
            printf '[PASS] %-12s %s\n' "$command_name" "$tool_path"
        else
            printf '[FAIL] %-12s missing\n' "$command_name"
            failed=1
        fi
    done

    if tool_path=$(resolve_tool llvm-lib) || tool_path=$(resolve_tool llvm-ar); then
        printf '[PASS] %-12s %s\n' llvm-lib/ar "$tool_path"
    else
        printf '[FAIL] %-12s missing\n' llvm-lib/ar
        failed=1
    fi

    if has_command rustup && rustup target list --installed | grep -qx x86_64-pc-windows-msvc; then
        printf '[PASS] %-12s installed\n' rust-msvc
    else
        printf '[FAIL] %-12s run: rustup target add x86_64-pc-windows-msvc\n' rust-msvc
        failed=1
    fi

    if [[ -x "$xwin_binary" ]]; then
        printf '[PASS] %-12s %s\n' xwin "$xwin_binary"
    else
        printf '[FAIL] %-12s run bootstrap-xwin\n' xwin
        failed=1
    fi

    if [[ -f "$xwin_dir/sdk/include/um/Windows.h" ]]; then
        printf '[PASS] %-12s %s\n' Windows-SDK "$xwin_dir"
    else
        printf '[FAIL] %-12s run bootstrap-xwin\n' Windows-SDK
        failed=1
    fi

    if (( failed != 0 )); then
        printf '\nLinux packages required on Debian/Ubuntu:\n'
        printf '  sudo apt install cmake ninja-build clang lld llvm\n'
        printf '  rustup target add x86_64-pc-windows-msvc\n'
    fi
    return "$failed"
}

fetch_ue4ss() {
    mkdir -p "$(dirname "$ue4ss_dir")"
    if [[ ! -d "$ue4ss_dir/.git" ]]; then
        git clone --filter=blob:none https://github.com/UE4SS-RE/RE-UE4SS.git "$ue4ss_dir"
    fi

    git -C "$ue4ss_dir" fetch --depth 1 origin "$ue4ss_commit"
    git -C "$ue4ss_dir" checkout --detach "$ue4ss_commit"

    if ! git -C "$ue4ss_dir" submodule update --init --recursive --depth 1; then
        printf '%s\n' \
            'Could not fetch UE4SS submodules.' \
            'Link GitHub to an Epic Games account and configure GitHub SSH access,' \
            'then run fetch-ue4ss again.' >&2
        return 1
    fi
}

install_xwin() {
    if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
        printf 'The pinned xwin bootstrap currently supports Linux x86_64 only.\n' >&2
        return 1
    fi

    mkdir -p "$tools_dir/downloads"
    local archive="$tools_dir/downloads/$xwin_archive"
    if [[ ! -f "$archive" ]]; then
        curl --fail --location --retry 3 \
            "https://github.com/Jake-Shadle/xwin/releases/download/$xwin_version/$xwin_archive" \
            --output "$archive"
    fi

    printf '%s  %s\n' "$xwin_sha256" "$archive" | sha256sum --check --status
    local unpack_dir
    unpack_dir=$(mktemp -d "$tools_dir/xwin-unpack.XXXXXX")
    trap 'rm -rf "$unpack_dir"' RETURN
    tar -xzf "$archive" -C "$unpack_dir"
    install -m 0755 "$unpack_dir/xwin-${xwin_version}-x86_64-unknown-linux-musl/xwin" "$xwin_binary"
    trap - RETURN
    rm -rf "$unpack_dir"
}

bootstrap_xwin() {
    if [[ "${1:-}" != '--accept-microsoft-license' ]]; then
        printf '%s\n' \
            'This command downloads Microsoft CRT and Windows SDK files.' \
            'Read their license terms, then explicitly pass --accept-microsoft-license.' >&2
        return 64
    fi

    install_xwin
    "$xwin_binary" --accept-license --arch x86_64 --http-retry 5 --timeout 180 splat --output "$xwin_dir"
}

configure() {
    doctor
    XWIN_DIR="$xwin_dir" cmake \
        -S "$repository_root/src/RogueMod.Native" \
        -B "$build_dir" \
        -G Ninja \
        -DCMAKE_BUILD_TYPE=Game__Shipping__Win64 \
        -DCMAKE_TOOLCHAIN_FILE="$repository_root/src/RogueMod.Native/cmake/xwin-clang-cl-toolchain.cmake"
}

build() {
    if [[ ! -f "$build_dir/CMakeCache.txt" ]]; then
        configure
    fi
    XWIN_DIR="$xwin_dir" cmake --build "$build_dir" --target RogueModBridge PackageHelloNativeMod
}

case "${1:-}" in
doctor)
    doctor
    ;;
fetch-ue4ss)
    fetch_ue4ss
    ;;
install-xwin)
    install_xwin
    ;;
bootstrap-xwin)
    bootstrap_xwin "${2:-}"
    ;;
configure)
    configure
    ;;
build)
    build
    ;;
-h|--help|help|'')
    usage
    ;;
*)
    printf 'Unknown command: %s\n\n' "$1" >&2
    usage >&2
    exit 64
    ;;
esac
