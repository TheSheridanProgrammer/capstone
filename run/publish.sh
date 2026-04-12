#!/usr/bin/env bash
set -euo pipefail

# Publishes Capstone for Raspberry Pi 5 (Linux ARM64).
# - Builds native `corelib` as `libcorelib.so` (and `libcorelib.a`)
# - Publishes Avalonia GUI for `linux-arm64`
#
# Run on the Pi 5 (recommended) or in a Linux ARM64 environment.
# If you run this on x64 Linux, you will build x64 native binaries unless you provide a proper cross-toolchain.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

CORELIB_DIR="${REPO_ROOT}/corelib"
GUI_CSPROJ="${REPO_ROOT}/GUI/CapstoneController/CapstoneController.csproj"

OUT_DIR="${SCRIPT_DIR}/artifacts"
NATIVE_OUT_DIR="${OUT_DIR}/native/linux-arm64"
APP_OUT_DIR="${OUT_DIR}/app/linux-arm64"

CXX="${CXX:-g++}"
AR="${AR:-ar}"
DOTNET="${DOTNET:-dotnet}"

usage() {
  cat <<'EOF'
Usage: ./publish.sh [--corelib-only | --gui-only] [--clean]

Builds the native corelib library and publishes the GUI app for linux-arm64.

Options:
  --corelib-only   Build only the C++ corelib library
  --gui-only       Publish only the GUI app
  --clean          Remove run/artifacts before building
EOF
}

DO_CORELIB=1
DO_GUI=1
DO_CLEAN=0

for arg in "$@"; do
  case "$arg" in
    --corelib-only) DO_GUI=0 ;;
    --gui-only) DO_CORELIB=0 ;;
    --clean) DO_CLEAN=1 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $arg"; usage; exit 2 ;;
  esac
done

if [[ "$DO_CLEAN" -eq 1 ]]; then
  rm -rf "$OUT_DIR"
fi

mkdir -p "$NATIVE_OUT_DIR" "$APP_OUT_DIR"

build_corelib() {
  echo "==> Building corelib (native, linux-arm64)"

  local obj_dir="${NATIVE_OUT_DIR}/obj"
  mkdir -p "$obj_dir"

  # Only compile the library sources (exclude main.cpp, which is a sample/CLI entry).
  "$CXX" -std=c++17 -O2 -DNDEBUG -fPIC \
    -I"${CORELIB_DIR}" \
    -c "${CORELIB_DIR}/corelib_c_api.cpp" \
    -o "${obj_dir}/corelib_c_api.o"

  # Shared library for DllImport("corelib") on Linux -> libcorelib.so
  "$CXX" -shared \
    -o "${NATIVE_OUT_DIR}/libcorelib.so" \
    "${obj_dir}/corelib_c_api.o"

  # Optional static library
  "$AR" rcs "${NATIVE_OUT_DIR}/libcorelib.a" "${obj_dir}/corelib_c_api.o"

  echo "Built: ${NATIVE_OUT_DIR}/libcorelib.so"
}

publish_gui() {
  echo "==> Publishing GUI (dotnet, linux-arm64)"

  "$DOTNET" publish "$GUI_CSPROJ" \
    -c Release \
    -r linux-arm64 \
    --self-contained false \
    -o "$APP_OUT_DIR"

  echo "Published to: ${APP_OUT_DIR}"
}

if [[ "$DO_CORELIB" -eq 1 ]]; then
  build_corelib
fi

if [[ "$DO_GUI" -eq 1 ]]; then
  publish_gui
fi

# If we built both, place the native library alongside the published app
# so the runtime loader can find it without extra LD_LIBRARY_PATH configuration.
if [[ "$DO_CORELIB" -eq 1 && "$DO_GUI" -eq 1 ]]; then
  echo "==> Copying libcorelib.so next to published app"
  cp -f "${NATIVE_OUT_DIR}/libcorelib.so" "${APP_OUT_DIR}/libcorelib.so"
fi

echo "==> Done"
