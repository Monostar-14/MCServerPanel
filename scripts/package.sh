#!/usr/bin/env bash
# 一键打包 Windows + Linux 发布包（在 Linux/macOS 上运行）
# 用法： ./scripts/package.sh [版本号] [输出目录]
#   版本号省略时取仓库根 Directory.Build.props 里的 <Version>（发版只改那一行）
set -euo pipefail

VERSION="${1:-}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
# 默认输出到仓库内的 scripts/dist，产物跟源码放在同一棵目录树下（.gitignore 的 dist/ 规则已覆盖）
OUT="${2:-$REPO/scripts/dist}"
PROJ="$REPO/MCServerPanel/MCServerPanel.csproj"

cd "$REPO"
rm -rf "$OUT"
mkdir -p "$OUT"

# Windows 的 git-bash(MSYS) 下 dotnet / python 是原生程序，不认 /e/... 这种 POSIX 路径，
# 必须转成 E:\...；Linux/macOS 上 cygpath 不存在，原样返回。
winpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

# 找一个真能跑的 python：Windows 上 `python3` 常常是 Microsoft Store 的占位符，
# 它只打印一句提示就退出（退出码还是 0），会让打包“静默成功但没产物”。
find_python() {
  local cand
  for cand in python3 py python; do
    if command -v "$cand" >/dev/null 2>&1 && "$cand" -c "import shutil" >/dev/null 2>&1; then
      printf '%s' "$cand"; return 0
    fi
  done
  return 1
}

# 版本号：命令行参数 > Directory.Build.props / csproj 里的 <Version>
if [ -z "$VERSION" ]; then
  VERSION="$(dotnet msbuild "$(winpath "$PROJ")" -getProperty:Version -nologo 2>/dev/null | tail -1 | tr -d '\r' | tr -d ' ')"
  [ -n "$VERSION" ] || { echo "拿不到版本号：请在 Directory.Build.props 里设置 <Version>，或传参" >&2; exit 1; }
  echo "==> 版本：$VERSION（来自 Directory.Build.props）"
else
  echo "==> 版本：$VERSION（命令行覆盖）"
fi

publish() {
  local rid="$1"
  echo "==> 发布 $rid"
  dotnet publish "$(winpath "$PROJ")" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=none -p:DebugSymbols=false \
    -p:Version="$VERSION" \
    -p:PublishDir="$(winpath "$OUT/$rid")/"
  test -f "$OUT/$rid/wwwroot/index.html" || { echo "缺少 wwwroot，停止打包" >&2; exit 1; }
}

publish linux-x64
publish win-x64

chmod +x "$OUT/linux-x64/MCServerPanel"
TGZ="$OUT/MCServerPanel-$VERSION-linux-x64.tar.gz"

# 执行位：Windows 上 `chmod +x` 只是 MSYS 的假权限，直接 tar 会把模式写成 0644，
# Linux 用户解压后跑不起来。有 python 就用 make_tar.py 显式写 0755（跟 package.ps1 一致）。
if tgz_py="$(find_python)"; then
  "$tgz_py" "$(winpath "$REPO/scripts/make_tar.py")" "$(winpath "$OUT/linux-x64")" "$(winpath "$TGZ")"
else
  tar -czf "$TGZ" -C "$OUT/linux-x64" .
  echo "警告：没有可用的 python，tar.gz 里的 MCServerPanel 可能缺少执行位，Linux 用户需 chmod +x" >&2
fi

make_zip() {
  local src="$1" base="$2"   # base 不带 .zip
  if command -v zip >/dev/null 2>&1; then
    (cd "$src" && zip -qr "$base.zip" .)
  else
    local py
    if py="$(find_python)"; then
      (cd "$src" && "$py" -c "import shutil,sys; shutil.make_archive(sys.argv[1], 'zip', '.')" "$(winpath "$base")")
    elif command -v powershell >/dev/null 2>&1; then
      powershell -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$src")\\*' -DestinationPath '$(cygpath -w "$base.zip")' -Force"
    else
      echo "无法创建 zip：zip / python / powershell 都不可用" >&2
      return 1
    fi
  fi
  test -f "$base.zip" || { echo "zip 没有生成：$base.zip" >&2; return 1; }
}

make_zip "$OUT/win-x64" "$OUT/MCServerPanel-$VERSION-win-x64"

# 两个包都必须真的存在，缺一个就报错，不要“看起来成功了”
test -f "$OUT/MCServerPanel-$VERSION-linux-x64.tar.gz" || { echo "tar.gz 没有生成" >&2; exit 1; }
test -f "$OUT/MCServerPanel-$VERSION-win-x64.zip" || { echo "win zip 没有生成" >&2; exit 1; }

( cd "$OUT" && sha256sum MCServerPanel-"$VERSION"-* > SHA256SUMS.txt )

echo
echo "完成，产物在 $OUT"
ls -lh "$OUT"/MCServerPanel-"$VERSION"-* "$OUT"/SHA256SUMS.txt
