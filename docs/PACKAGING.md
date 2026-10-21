# 打包与分发（Windows / Linux）

前置条件：本机装好 **.NET 10 SDK**（`dotnet --version` 能输出版本号）。
以下命令都在仓库根目录执行（含 `MCServerPanel.slnx` 的那一层）。

## 最快的方式：一条命令

仓库里已经带了一键脚本，Windows 上执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

默认输出到 `E:\build\mcsp-release`（可加 `-Version 1.0.1 -OutDir E:\build\mcsp` 覆盖），
会生成 `MCServerPanel-<版本>-win-x64.zip`、`MCServerPanel-<版本>-linux-x64.tar.gz` 和 `SHA256SUMS.txt`。
Linux 上对应 `./scripts/package.sh 1.0.0`。

> 脚本会先检查 `wwwroot/index.html` 是否在产物里，缺了直接报错；Linux 包用 `scripts/make_tar.py`
> 写成 0755 权限，解压即可执行（本机没有 python 时退回系统 tar，需要用户自己 `chmod +x`）。

下面是手工步骤（想理解每一步时看）。


## 0. 先选发布方式

| 方式 | 体积 | 目标机要求 | 建议 |
|---|---|---|---|
| **自包含单文件**（推荐） | Windows ≈ 100 MB，Linux ≈ 97 MB | 什么都不用装 | 发给别人用这个 |
| 框架依赖 | ≈ 3 MB | 必须装 ASP.NET Core Runtime 10 | 只适合自己机器 |

自包含单文件的 exe 里**不包含 `wwwroot`**（HTML/CSS/JS 是普通文件），所以必须整个目录分发。

## 1. Windows x64

```powershell
dotnet publish MCServerPanel/MCServerPanel.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none -p:DebugSymbols=false `
  -p:Version=1.0.0 `
  -p:PublishDir=../scripts/dist/win-x64/
```

产物在 `scripts/dist/win-x64/`（跟源码同一棵目录树，.gitignore 的 `dist/` 规则已覆盖）：

```
scripts/dist/win-x64/
├─ MCServerPanel.exe          ← 主程序（单文件，自包含运行时）
├─ appsettings.json
├─ appsettings.Development.json   （可删）
├─ web.config                     （IIS 用，可删）
├─ MCServerPanel.staticwebassets.endpoints.json
└─ wwwroot/                   ← 必须一起发，否则页面 404
   ├─ index.html / login.html / setup.html
   ├─ css/app.css
   └─ js/*.js
```

首次运行后，主程序会在同目录自动生成 `settings.json`（服务端目录/端口/局域网开关）、`auth.json`
（管理员账号，PBKDF2 加盐哈希）、`logs/`（审计日志）、`backups/`（保存前的旧文件备份，保留 30 天）。
不需要预先创建；整个目录可整体拷走做备份或迁移。

压缩：

```powershell
Compress-Archive -Path scripts/dist/win-x64/* -DestinationPath scripts/dist/MCServerPanel-1.0.0-win-x64.zip
```

## 2. Linux x64

```bash
dotnet publish MCServerPanel/MCServerPanel.csproj -c Release -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none -p:DebugSymbols=false \
  -p:Version=1.0.0 \
  -p:PublishDir=../scripts/dist/linux-x64/
```

打包时要保留可执行位：

```bash
chmod +x scripts/dist/linux-x64/MCServerPanel
tar -czf scripts/dist/MCServerPanel-1.0.0-linux-x64.tar.gz -C scripts/dist/linux-x64 .
```

> 在 Windows 上直接生成 tar.gz 时执行位会丢失；用户解压后执行 `chmod +x MCServerPanel` 即可，
> 或者在 WSL / Linux / CI 上构建 Linux 包更稳妥。

## 3. 其他平台

| 平台 | RID |
|---|---|
| Alpine（musl） | `linux-musl-x64` |
| ARM64 服务器 / 树莓派 | `linux-arm64` |
| Windows on ARM | `win-arm64` |

## 4. 目标机首次运行

```bash
# Linux
./MCServerPanel --plugins-root /srv/minecraft/plugins --data-dir /var/lib/mcpanel --port 8989 --no-browser

# Windows
MCServerPanel.exe --plugins-root "D:\Server\plugins" --data-dir "C:\ProgramData\MCServerPanel" --port 8989 --no-browser
```

控制台会打印一次性初始化令牌，用浏览器打开 `http://127.0.0.1:8989/setup.html` 设置密码。

数据默认写在**程序所在目录**（exe 旁边的 `settings.json` / `auth.json` / `logs/` / `backups/`）。
上面两条命令显式用了 `--data-dir`，因为以服务方式运行时程序目录常常不可写（Program Files、`/usr/local/bin`），
把数据放到 `C:\ProgramData\MCServerPanel` 或 `/var/lib/mcpanel` 这类可写目录更稳妥。
程序目录不可写时，程序会自动回退到 `%LOCALAPPDATA%\MCServerPanel`（Linux 为 `~/.local/share/MCServerPanel`）
并在控制台说明原因。

以服务方式运行时看不到控制台，令牌会进日志：

- Linux：`journalctl -u mcserverpanel -n 30`
- Windows WinSW/NSSM：服务包装器自己的日志文件

忘记密码：`MCServerPanel --data-dir <同一目录> --reset-password`（或设 `MCSP_NEW_PASSWORD` 环境变量）。

## 5. 注册为服务

### Linux（systemd）

`/etc/systemd/system/mcserverpanel.service`：

```ini
[Unit]
Description=MCServerPanel
After=network-online.target

[Service]
Type=simple
User=mcpanel
Group=mcpanel
WorkingDirectory=/opt/mcserverpanel
Environment=HOME=/var/lib/mcpanel
ExecStart=/opt/mcserverpanel/MCServerPanel --plugins-root /srv/minecraft/plugins --data-dir /var/lib/mcpanel --port 8989 --no-browser
Restart=always
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/srv/minecraft/plugins /var/lib/mcpanel

[Install]
WantedBy=multi-user.target
```

```bash
sudo useradd -r -s /usr/sbin/nologin mcpanel
sudo mkdir -p /opt/mcserverpanel /var/lib/mcpanel
sudo cp -r scripts/dist/linux-x64/* /opt/mcserverpanel/
sudo chown -R root:root /opt/mcserverpanel
sudo chown -R mcpanel:mcpanel /var/lib/mcpanel
# plugins 目录给 mcpanel 写权限（按实际属组调整）
sudo systemctl daemon-reload && sudo systemctl enable --now mcserverpanel
sudo journalctl -u mcserverpanel -f
```

### Windows（WinSW）

1. 下载 `WinSW-x64.exe`，重命名为 `MCServerPanel-service.exe`，和主程序放同一目录。
2. 同目录建 `MCServerPanel-service.xml`：

```xml
<service>
  <id>MCServerPanel</id>
  <name>MCServerPanel</name>
  <executable>C:\MCServerPanel\MCServerPanel.exe</executable>
  <arguments>--plugins-root "D:\Server\plugins" --data-dir "C:\ProgramData\MCServerPanel" --port 8989 --no-browser</arguments>
  <workingdirectory>C:\MCServerPanel</workingdirectory>
  <log mode="append" />
  <onfailure action="restart" delay="5 sec" />
  <serviceaccount>
    <domain>.\</domain>
    <user>mcpanel</user>
    <password>换成该账号密码</password>
  </serviceaccount>
</service>
```

3. 管理员命令行：`MCServerPanel-service.exe install` → `MCServerPanel-service.exe start`。
   日志在同目录的 `MCServerPanel-service.out.log`（里面能找到初始化令牌）。

## 6. 常见坑

1. **输出路径不能含单引号**：家目录里带 `'` 的账号（形如 `C:\Users\O'Brien\`）直接
   `-p:PublishDir=C:/Users/O'Brien/...` 会报 `MSB3094`。用相对路径（`../scripts/dist/win-x64/`）或任何
   不含 `'` 的绝对路径。
2. **只发 exe 不发 `wwwroot`**：页面和接口 404，登录页打不开。
3. **不要开 `PublishTrimmed=true`**：ASP.NET Core 的动态加载/静态资源会被裁掉，运行时才炸。
4. **Linux 缺执行位**：`chmod +x MCServerPanel`；zip 不保留权限，用 tar 或解压后补。
5. **`/tmp` 挂载了 noexec**：单文件需要解压原生库，设
   `DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/mcpanel/extract` 指向可执行目录。
6. **服务账号权限**：数据目录（默认就是程序目录，`--data-dir` 可改）必须可写，面板要在里面建
   `settings.json`、`auth.json`、`logs/`、`backups/`；装进 `Program Files` / `/usr/local/bin` 这类只读位置时
   必须显式 `--data-dir`，否则会回退到用户目录（多用户机器上会各写各的）。账号只应拥有 `plugins` 的写权限，
   不要用 root/Administrator 跑。
7. **端口被占用会自动顺延**：启动日志里看实际监听端口，别假设一定是 8989。
8. **默认只监听 `127.0.0.1`**：要在局域网/Tailscale 访问，先在设置里设好密码再打开「允许局域网」，
   或者用 `tailscale serve` 反代到回环。不要把端口直接映射到公网。
9. **ReadyToRun**：`-p:PublishReadyToRun=true` 能加快启动，但跨平台构建可能失败，建议在目标平台构建或不开。
10. **杀软误报**：单文件自解压容易被误报；可以改用非单文件发布（体积更大）或做代码签名。

## 7. 发出去之前自检

```bash
dotnet test MCServerPanel.Tests -c Release
python scripts/smoke_auth.py --skip-ui     # 认证/保存完整性
```

然后跑一次发布产物（用临时目录，别碰真实服务端）：

```bash
scripts/dist/win-x64/MCServerPanel.exe --no-browser --port 8990 --data-dir /tmp/mcsp-check --plugins-root /tmp/mcsp-plugins
curl http://127.0.0.1:8990/api/health
curl -o /dev/null -w "%{http_code}\n" http://127.0.0.1:8990/login.html   # 应为 200
```

生成校验和：

```powershell
Get-FileHash scripts/dist/MCServerPanel-1.0.0-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum scripts/dist/MCServerPanel-1.0.0-linux-x64.tar.gz > scripts/dist/SHA256SUMS
```

## 8. 建议

- 版本号统一从命令行传：`-p:Version=1.0.0`，压缩包名带版本和 RID。
- 在 CI 上用矩阵分别构建（`windows-latest` 出 win 包，`ubuntu-latest` 出 linux 包），
  避免跨平台细节差异，也方便附校验和与 artifact attestation。
- 产物统一放 `scripts/dist/`（`.gitignore` 里的 `dist/` 规则覆盖它），不要把产物提交进仓库，发 GitHub Release。
