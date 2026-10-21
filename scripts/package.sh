#!/usr/bin/env bash
# 一键打包 Windows + Linux 发布包（在 Linux/macOS 上运行）
# 用法： ./scripts/package.sh [版本号] [输出目录]
set -euo pipefail

VERSION="${1:-1.0.0}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
# 默认输出到仓库内的 scripts/dist，产物跟源码放在同一棵目录树下（.gitignore 的 dist/ 规则已覆盖）
OUT="${2:-$REPO/scripts/dist}"
PROJ="$REPO/MCServerPanel/MCServerPanel.csproj"

cd "$REPO"
rm -rf "$OUT"
mkdir -p "$OUT"

publish() {
  local rid="$1"
  echo "==> 发布 $rid"
  dotnet publish "$PROJ" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -p:DebugSymbols=false \
    -p:Version="$VERSION" \
    -p:PublishDir="$OUT/$rid/"
  test -f "$OUT/$rid/wwwroot/index.html" || { echo "缺少 wwwroot，停止打包" >&2; exit 1; }
}

publish linux-x64
publish win-x64

chmod +x "$OUT/linux-x64/MCServerPanel"
tar -czf "$OUT/MCServerPanel-$VERSION-linux-x64.tar.gz" -C "$OUT/linux-x64" .

if command -v zip >/dev/null 2>&1; then
  (cd "$OUT/win-x64" && zip -qr "$OUT/MCServerPanel-$VERSION-win-x64.zip" .)
else
  (cd "$OUT/win-x64" && python3 -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.')" "$OUT/MCServerPanel-$VERSION-win-x64")
fi

( cd "$OUT" && sha256sum MCServerPanel-"$VERSION"-* > SHA256SUMS.txt )

echo
echo "完成，产物在 $OUT"
ls -lh "$OUT"/MCServerPanel-"$VERSION"-* "$OUT"/SHA256SUMS.txt
