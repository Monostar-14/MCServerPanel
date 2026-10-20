.PHONY: build test smoke verify package

build:
	dotnet build MCServerPanel.slnx -c Release

test:
	dotnet test MCServerPanel.Tests -c Release

# 认证/沙箱/保存完整性 + 浏览器自检（无浏览器时自动跳过 UI 部分）
smoke: build
	python scripts/smoke_auth.py --no-build

# 单元测试 + 端到端冒烟
verify: test smoke

# 打包 Windows + Linux 发布包（Windows 走 package.ps1，其它平台走 package.sh）
ifeq ($(OS),Windows_NT)
PACKAGE_CMD = powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
else
PACKAGE_CMD = bash scripts/package.sh
endif

package:
	$(PACKAGE_CMD)
