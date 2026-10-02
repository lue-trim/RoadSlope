#!/usr/bin/env bash
# 编译 RoadSlopeViewer v0.4（CS1 本地 mod，net35，对本机 Managed 程序集编译）
set -euo pipefail
export PATH=/opt/data/dotnet:$PATH
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
MANAGED=/opt/data/workspace/savefile_save/Managed
SRC=/opt/data/workspace/road_slope_mod/src
OUT=${1:-/opt/data/workspace/road_slope_mod/out/RoadSlopeViewer.dll}
SDKVER=$(ls /opt/data/dotnet/sdk | sort -V | tail -1)
CSC="/opt/data/dotnet/sdk/$SDKVER/Roslyn/bincore/csc.dll"
mkdir -p "$(dirname "$OUT")"
dotnet exec "$CSC" -nologo -nostdlib -noconfig -target:library -langversion:latest \
  -out:"$OUT" \
  -r:"$MANAGED/mscorlib.dll" -r:"$MANAGED/System.dll" -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/Assembly-CSharp.dll" -r:"$MANAGED/ColossalManaged.dll" \
  -r:"$MANAGED/UnityEngine.dll" -r:"$MANAGED/ICities.dll" \
  "$SRC/RoadSlopeViewer.cs" "$SRC/OptionsUI.cs" "$SRC/SvgExporter.cs"
echo "OK: $OUT"
