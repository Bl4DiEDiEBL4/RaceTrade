#!/usr/bin/env bash
# ============================================================================
#  RaceTrade - produce a SHIPPABLE build.
#
#  bin/Release/ is the BUILD output and always contains loose DLLs; that is not
#  what you ship. `dotnet publish` produces the single self-contained binary.
#
#  Result:  Release/linux-x64/RaceTrade      (one file, no .NET install needed)
#           Release/linux-arm64/RaceTrade    (Raspberry Pi 5 / ARM64 Linux)
#           Release/win-x64/RaceTrade.exe
# ============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$ROOT/Release"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/racetrade-publish.XXXXXX")"
PROJ="$ROOT/RaceTrade.Web/RaceTrade.Web.csproj"

mkdir -p "$OUT"
find "$OUT" -maxdepth 1 -type f -delete

cleanup() {
    rm -rf "$WORK"
}
trap cleanup EXIT

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet SDK not found. Install the .NET 8 SDK:" >&2
    echo "  https://dotnet.microsoft.com/download/dotnet/8.0" >&2
    exit 1
fi

# Default to the host Linux architecture; pass runtimes explicitly to cross-publish:
#   ./publish.sh linux-x64 linux-arm64 win-x64
RIDS=("$@")
if [ ${#RIDS[@]} -eq 0 ]; then
    case "$(uname -m)" in
        aarch64|arm64) RIDS=(linux-arm64) ;;
        *) RIDS=(linux-x64) ;;
    esac
fi

echo "=== Restoring ==="
dotnet restore "$PROJ"

for RID in "${RIDS[@]}"; do
    echo
    echo "=== Publishing $RID ==="

    # Wiped first: publish does not remove files left by an earlier run.
    rm -rf "$OUT/$RID"
    rm -rf "$WORK/bin/$RID"

    dotnet publish "$PROJ" \
        -c Release \
        -r "$RID" \
        --self-contained true \
        -p:BaseOutputPath="$WORK/bin/$RID/" \
        -p:PublishSingleFile=true \
        -p:IncludeAllContentForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:PublishTrimmed=false \
        -p:DebugType=none \
        -o "$OUT/$RID"

    rm -f "$OUT/$RID"/*.pdb
    rm -f "$OUT/$RID"/appsettings*.json
    rm -f "$OUT/$RID"/*.staticwebassets*.json
    rm -f "$OUT/$RID"/web.config
    rm -rf "$OUT/$RID/wwwroot"
    rm -rf "$OUT/$RID/bin"
    [ -f "$OUT/$RID/RaceTrade" ] && chmod +x "$OUT/$RID/RaceTrade"
done

echo
echo "=== Done ==="
ls -lh "$OUT"/*/RaceTrade* 2>/dev/null || true
echo
echo "  Ship only the per-platform executable."
echo "  data/ is created on first run next to the executable."
