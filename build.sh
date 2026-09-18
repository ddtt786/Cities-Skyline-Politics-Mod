#!/usr/bin/env bash
set -e

# ==============================================================================
# Cities: Skylines (Proton / Linux) Politics Mod Build & Install Script
# ==============================================================================

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CITIES_INSTALL="/home/user/.local/share/Steam/steamapps/common/Cities_Skylines"
MANAGED_DIR="${CITIES_INSTALL}/Cities_Data/Managed"
HARMONY_DLL="${PROJECT_DIR}/packages/Lib.Harmony.2.4.2/lib/net35/0Harmony.dll"
CITIES_HARMONY_API="${PROJECT_DIR}/packages/CitiesHarmony.API/CitiesHarmony.API.dll"
OUTPUT_DIR="${PROJECT_DIR}/build"
OUTPUT_DLL="${OUTPUT_DIR}/Cities-Skyline-Politics-Mod.dll"

echo "========================================================"
echo "  Cities: Skylines Politics Mod - Proton Build Script   "
echo "========================================================"

# 1. Check game directory
if [ ! -d "$MANAGED_DIR" ]; then
    echo "[!] Warning: Managed folder not found at: $MANAGED_DIR"
    echo "    Please verify that Cities: Skylines is installed at:"
    echo "    $CITIES_INSTALL"
    echo "    If installed on a different drive, edit CITIES_INSTALL in this script."
    exit 1
fi

echo "[✓] Found Cities: Skylines Managed directory at:"
    echo "    $MANAGED_DIR"

# 2. Find C# Compiler (Roslyn csc.dll in dotnet or mcs)
CSC_DLL=$(find /usr/lib/dotnet/sdk/ -name "csc.dll" 2>/dev/null | head -n 1)

if [ -n "$CSC_DLL" ] && command -v dotnet >/dev/null 2>&1; then
    COMPILER="dotnet_csc"
    echo "[✓] Using Roslyn C# compiler: $CSC_DLL"
elif command -v mcs >/dev/null 2>&1; then
    COMPILER="mcs"
    echo "[✓] Using Mono compiler: $(which mcs)"
else
    echo "[!] Error: No C# compiler found (neither 'dotnet' with csc.dll nor 'mcs')."
    echo "    Please install dotnet-sdk (e.g. apt install dotnet-sdk-8.0) or mono-devel."
    exit 1
fi

# 3. Prepare output directory
mkdir -p "$OUTPUT_DIR"

# 4. Collect source files
SOURCES=$(find "${PROJECT_DIR}/Cities-Skyline-Politics-Mod" -name "*.cs" -not -path "*/obj/*" -not -path "*/bin/*")

echo "[*] Compiling Politics Mod DLL..."

if [ "$COMPILER" = "dotnet_csc" ]; then
    dotnet exec "$CSC_DLL" \
        -target:library \
        -out:"$OUTPUT_DLL" \
        -nostdlib+ \
        -noconfig \
        -langversion:7.3 \
        -optimize+ \
        -reference:"${MANAGED_DIR}/mscorlib.dll" \
        -reference:"${MANAGED_DIR}/System.dll" \
        -reference:"${MANAGED_DIR}/System.Core.dll" \
        -reference:"${MANAGED_DIR}/System.Configuration.dll" \
        -reference:"${MANAGED_DIR}/System.Xml.dll" \
        -reference:"${MANAGED_DIR}/UnityEngine.dll" \
        -reference:"${MANAGED_DIR}/UnityEngine.UI.dll" \
        -reference:"${MANAGED_DIR}/Assembly-CSharp.dll" \
        -reference:"${MANAGED_DIR}/Assembly-CSharp-firstpass.dll" \
        -reference:"${MANAGED_DIR}/ColossalManaged.dll" \
        -reference:"${MANAGED_DIR}/ICities.dll" \
        -reference:"$HARMONY_DLL" \
        -reference:"$CITIES_HARMONY_API" \
        $SOURCES
else
    mcs -target:library \
        -out:"$OUTPUT_DLL" \
        -nostdlib+ \
        -r:"${MANAGED_DIR}/mscorlib.dll" \
        -r:"${MANAGED_DIR}/System.dll" \
        -r:"${MANAGED_DIR}/System.Core.dll" \
        -r:"${MANAGED_DIR}/System.Configuration.dll" \
        -r:"${MANAGED_DIR}/System.Xml.dll" \
        -r:"${MANAGED_DIR}/UnityEngine.dll" \
        -r:"${MANAGED_DIR}/UnityEngine.UI.dll" \
        -r:"${MANAGED_DIR}/Assembly-CSharp.dll" \
        -r:"${MANAGED_DIR}/Assembly-CSharp-firstpass.dll" \
        -r:"${MANAGED_DIR}/ColossalManaged.dll" \
        -r:"${MANAGED_DIR}/ICities.dll" \
        -r:"$HARMONY_DLL" \
        -r:"$CITIES_HARMONY_API" \
        $SOURCES
fi

echo "[✓] Build successful! Created:"
echo "    $OUTPUT_DLL"

# 5. Locate Proton compatdata (App ID: 255710) and Native mod folders
PROTON_PFX_DIR="/home/user/.local/share/Steam/steamapps/compatdata/255710/pfx"
NATIVE_MOD_DIR="/home/user/.local/share/Colossal Order/Cities_Skylines/Addons/Mods/PoliticsMod"

INSTALLED=0

# Install to Proton Wine prefix if exists
if [ -d "$PROTON_PFX_DIR" ]; then
    PROTON_MOD_DIR="${PROTON_PFX_DIR}/drive_c/users/steamuser/AppData/Local/Colossal Order/Cities_Skylines/Addons/Mods/PoliticsMod"
    mkdir -p "$PROTON_MOD_DIR"
    # Remove conflicting standalone 0Harmony.dll that causes Mono dynamic module crash
    rm -f "$PROTON_MOD_DIR/0Harmony.dll"
    cp -v "$OUTPUT_DLL" "$PROTON_MOD_DIR/"
    cp -v "$CITIES_HARMONY_API" "$PROTON_MOD_DIR/"
    echo "[✓] Installed to Proton prefix (Wine AppData):"
    echo "    $PROTON_MOD_DIR"
    INSTALLED=1
fi

# Also install to native directory as fallback
mkdir -p "$NATIVE_MOD_DIR"
rm -f "$NATIVE_MOD_DIR/0Harmony.dll"
cp -v "$OUTPUT_DLL" "$NATIVE_MOD_DIR/"
cp -v "$CITIES_HARMONY_API" "$NATIVE_MOD_DIR/"
echo "[✓] Copied to native mod directory:"
echo "    $NATIVE_MOD_DIR"

echo "========================================================"
echo "  Installation Complete!                                "
echo "  1. Launch Cities: Skylines via Steam.                 "
echo "  2. Go to Content Manager -> Mods.                     "
echo "  3. Enable Harmony and Politics & Elections Mod.       "
echo "  4. In game, press Ctrl+P to open the politics panel!  "
echo "========================================================"

