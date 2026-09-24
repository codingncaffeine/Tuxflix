# Sourced by every tool script: keeps temporary files inside the project and picks the SDK.
# TUXFLIX_DOTNET names a dotnet to use instead of the one on PATH.
TUXFLIX_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TUXFLIX_ROOT
mkdir -p "$TUXFLIX_ROOT/scratch/tmp"
export TMPDIR="$TUXFLIX_ROOT/scratch/tmp"
DOTNET="${TUXFLIX_DOTNET:-$(command -v dotnet)}"
export DOTNET
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
