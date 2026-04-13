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

# Output goes into the repo-root build/ directory (per project convention)
OUT_DIR="${REPO_ROOT}/build"
NATIVE_OUT_DIR="${OUT_DIR}/native/linux-arm64"
APP_OUT_DIR="${OUT_DIR}/app/linux-arm64"

CXX="${CXX:-g++}"
AR="${AR:-ar}"
DOTNET="${DOTNET:-}"

resolve_dotnet() {
  if [[ -n "${DOTNET}" ]]; then
    return 0
  fi

  if command -v dotnet >/dev/null 2>&1; then
    DOTNET="dotnet"
    return 0
  fi

  # Common local install location from dotnet-install.sh
  if [[ -x "${HOME}/.dotnet/dotnet" ]]; then
    DOTNET="${HOME}/.dotnet/dotnet"
    return 0
  fi

  return 1
}

require_tool() {
  local tool="$1"
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "ERROR: Required tool not found in PATH: $tool" >&2
    exit 1
  fi
}

check_cxx_header() {
  local header="$1"
  # Use the compiler preprocessor to verify headers are available.
  if ! echo "#include <$header>" | "$CXX" -E -x c++ - >/dev/null 2>&1; then
    return 1
  fi
  return 0
}

ensure_corelib_deps() {
  if ! check_cxx_header "alsa/asoundlib.h"; then
    cat >&2 <<'EOF'
ERROR: Missing ALSA development headers (alsa/asoundlib.h).

On Raspberry Pi OS / Debian/Ubuntu, install:
  sudo apt-get update
  sudo apt-get install -y libasound2-dev

Then re-run ./publish.sh
EOF
    exit 1
  fi

  if ! check_cxx_header "linux/i2c-dev.h"; then
    cat >&2 <<'EOF'
ERROR: Missing Linux I2C dev header (linux/i2c-dev.h).

On Raspberry Pi OS / Debian/Ubuntu, try installing the libc kernel headers:
  sudo apt-get update
  sudo apt-get install -y linux-libc-dev

Then re-run ./publish.sh
EOF
    exit 1
  fi
}

usage() {
  cat <<'EOF'
Usage: ./publish.sh [--corelib-only | --gui-only] [--clean]

Builds the native corelib library and publishes the GUI app for linux-arm64.

Options:
  --corelib-only   Build only the C++ corelib library
  --gui-only       Publish only the GUI app
  --clean          Remove build/{native,app} before building
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
  # Only remove the publish outputs, not any other build/ content.
  rm -rf "${OUT_DIR}/native" "${OUT_DIR}/app"
fi

mkdir -p "$NATIVE_OUT_DIR" "$APP_OUT_DIR"

build_corelib() {
  echo "==> Building corelib (native, linux-arm64)"

  require_tool "$CXX"
  require_tool "$AR"
  ensure_corelib_deps

  local alsa_cflags=""
  local alsa_libs="-lasound"
  if command -v pkg-config >/dev/null 2>&1; then
    if pkg-config --exists alsa 2>/dev/null; then
      alsa_cflags="$(pkg-config --cflags alsa)"
      alsa_libs="$(pkg-config --libs alsa)"
    fi
  fi

  local obj_dir="${NATIVE_OUT_DIR}/obj"
  mkdir -p "$obj_dir"

  # Only compile the library sources (exclude main.cpp, which is a sample/CLI entry).
  "$CXX" -std=c++17 -O2 -DNDEBUG -fPIC -pthread \
    -I"${CORELIB_DIR}" \
    ${alsa_cflags} \
    -c "${CORELIB_DIR}/corelib_c_api.cpp" \
    -o "${obj_dir}/corelib_c_api.o"

  # Shared library for DllImport("corelib") on Linux -> libcorelib.so
  "$CXX" -shared -pthread -Wl,--no-undefined \
    -o "${NATIVE_OUT_DIR}/libcorelib.so" \
    "${obj_dir}/corelib_c_api.o" \
    ${alsa_libs}

  # Optional static library
  "$AR" rcs "${NATIVE_OUT_DIR}/libcorelib.a" "${obj_dir}/corelib_c_api.o"

  echo "Built: ${NATIVE_OUT_DIR}/libcorelib.so"
}

publish_gui() {
  echo "==> Publishing GUI (dotnet, linux-arm64)"

  if ! resolve_dotnet; then
    cat <<'EOF'
dotnet SDK not found.

Install .NET 9 SDK (example using dotnet-install.sh):
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel 9.0 --quality ga --install-dir "$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"

Or set DOTNET=/path/to/dotnet when running this script.
EOF
    return 1
  fi

  require_tool "$DOTNET"

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

# If we built corelib, place the native library alongside the published app
# so the runtime loader can find it without extra LD_LIBRARY_PATH configuration.
# This also supports --corelib-only rebuilds (as long as the app dir exists).
if [[ "$DO_CORELIB" -eq 1 ]]; then
  if [[ -d "${APP_OUT_DIR}" ]]; then
    echo "==> Copying libcorelib.so next to published app"
    cp -f "${NATIVE_OUT_DIR}/libcorelib.so" "${APP_OUT_DIR}/libcorelib.so"
  fi
fi

echo "==> Done"
