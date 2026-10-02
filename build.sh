#!/usr/bin/env bash
# 编译 RoadSlopeViewer（CS1 本地 mod，net35）
# 需要对游戏 Managed 程序集编译；默认使用容器内路径，可用环境变量覆盖：
#   MANAGED=<游戏 Managed 目录>  DOTNET_DIR=<dotnet 安装目录>  bash build.sh
set -euo pipefail
DOTNET_DIR="${DOTNET_DIR:-/opt/data/dotnet}"
MANAGED="${MANAGED:-/opt/data/workspace/savefile_save/Managed}"
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="${SRC:-$HERE/src}"
OUT="${1:-$HERE/out/RoadSlopeViewer.dll}"
export PATH="$DOTNET_DIR:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
SDKVER=$(ls "$DOTNET_DIR/sdk" | sort -V | tail -1)
CSC="$DOTNET_DIR/sdk/$SDKVER/Roslyn/bincore/csc.dll"
mkdir -p "$(dirname "$OUT")"
dotnet exec "$CSC" -nologo -nostdlib -noconfig -target:library -langversion:latest \
  -out:"$OUT" \
  -r:"$MANAGED/mscorlib.dll" -r:"$MANAGED/System.dll" -r:"$MANAGED/System.Core.dll" \
  -r:"$MANAGED/Assembly-CSharp.dll" -r:"$MANAGED/ColossalManaged.dll" \
  -r:"$MANAGED/UnityEngine.dll" -r:"$MANAGED/ICities.dll" \
  "$SRC/RoadSlopeViewer.cs" "$SRC/OptionsUI.cs" "$SRC/SvgExporter.cs"
echo "OK: $OUT"
