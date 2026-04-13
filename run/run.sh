#!/usr/bin/env bash
set -euo pipefail

# Runs the published CapstoneController GUI (Linux ARM64).
# Assumes you've already run: ./run/publish.sh

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

APP_DIR="${APP_DIR:-${REPO_ROOT}/build/app/linux-arm64}"
APP_BIN="${APP_DIR}/CapstoneController"
APP_DLL="${APP_DIR}/CapstoneController.dll"
DOTNET="${DOTNET:-}"
DOTNET_ROOT="${DOTNET_ROOT:-}"

usage() {
  cat <<EOF
Usage: ./run.sh [r] [args...]

Flags:
  r         Run on the Pi's local display (do NOT use SSH X forwarding).
            This forces DISPLAY=:0 for X11.

Environment variables:
  APP_DIR   Override published app directory (default: build/app/linux-arm64)
  DOTNET    Path to dotnet (only needed if running the .dll)

Tip:
  If the app isn't published yet, run: ./run/publish.sh --clean
EOF
}

resolve_dotnet() {
  if [[ -n "${DOTNET}" ]]; then
    return 0
  fi

  if command -v dotnet >/dev/null 2>&1; then
    DOTNET="dotnet"
    return 0
  fi

  if [[ -x "${HOME}/.dotnet/dotnet" ]]; then
    DOTNET="${HOME}/.dotnet/dotnet"
    DOTNET_ROOT="${DOTNET_ROOT:-${HOME}/.dotnet}"
    return 0
  fi

  return 1
}

for arg in "$@"; do
  case "$arg" in
    -h|--help)
      usage
      exit 0
      ;;
  esac
done

# Optional "run local" flag: `./run.sh r`
RUN_LOCAL=0
APP_ARGS=("$@")
if [[ ${#APP_ARGS[@]} -gt 0 && "${APP_ARGS[0]}" == "r" ]]; then
  RUN_LOCAL=1
  APP_ARGS=("${APP_ARGS[@]:1}")
fi

if [[ ! -d "${APP_DIR}" ]]; then
  echo "Published app directory not found: ${APP_DIR}" >&2
  echo "Run: ${REPO_ROOT}/run/publish.sh --clean" >&2
  exit 2
fi

# Best-effort: ensure the primary Pi I2C bus device node exists.
# The app can still run without accel, but this avoids confusing I2C errors.
if [[ ! -e "/dev/i2c-1" ]]; then
  if command -v sudo >/dev/null 2>&1 && sudo -n true >/dev/null 2>&1; then
    sudo -n modprobe i2c-dev 2>/dev/null || true
    sudo -n modprobe i2c_bcm2835 2>/dev/null || true
    sudo -n modprobe i2c_bcm2708 2>/dev/null || true
  fi
fi

# If requested, force the app to use the Pi's local X11 display instead of SSH forwarding.
if [[ "${RUN_LOCAL}" -eq 1 ]]; then
  export DISPLAY=":0"
  unset WAYLAND_DISPLAY

  # Best-effort: helps when launched via SSH and X needs an auth file.
  if [[ -z "${XAUTHORITY:-}" && -f "${HOME}/.Xauthority" ]]; then
    export XAUTHORITY="${HOME}/.Xauthority"
  fi
fi

# This is a GUI app. If we're headless (common over SSH), fail fast with guidance.
if [[ -z "${DISPLAY:-}" && -z "${WAYLAND_DISPLAY:-}" ]]; then
  cat <<'EOF' >&2
No GUI display detected (DISPLAY/WAYLAND_DISPLAY is unset).

Run this from a desktop session, or over SSH with X forwarding (e.g. ssh -X),
or otherwise ensure a working X11/Wayland display is available.
EOF
  exit 3
fi

# Ensure the native library can be found at runtime.
export LD_LIBRARY_PATH="${APP_DIR}${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"

cd "${APP_DIR}"

if [[ -f "${APP_DLL}" ]] && resolve_dotnet; then
  if [[ -n "${DOTNET_ROOT}" ]]; then
    export DOTNET_ROOT
  fi
  exec "${DOTNET}" "${APP_DLL}" "${APP_ARGS[@]}"
fi

if [[ -x "${APP_BIN}" ]]; then
  # This requires a system-installed .NET runtime (unless DOTNET_ROOT points to one).
  exec "${APP_BIN}" "${APP_ARGS[@]}"
fi

if [[ -f "${APP_DLL}" ]]; then
  echo "dotnet not found; cannot run ${APP_DLL}" >&2
  echo "Install .NET 9 runtime/SDK, or set DOTNET=/path/to/dotnet." >&2
  exit 2
fi

echo "Neither ${APP_BIN} nor ${APP_DLL} found in ${APP_DIR}" >&2
echo "Run: ${REPO_ROOT}/run/publish.sh --clean" >&2
exit 2
