# MCServerPanel

把 Minecraft 服务端里每个插件的 `.yml` 配置渲染成网页，按类型给出合适的控件：

| YAML 里的值 | 网页上的控件 |
|---|---|
| `true` / `false`（含 `yes/no`、`on/off`） | 滑块开关（保留原来 yes/no 的写法） |
| 整数 / 小数 | 带 `−` `+` 步进按钮的数字输入框 |
| 普通字符串 | 文本框（含 `&a` 颜色码时附带颜色预览条） |
| 多行文本（`|` / `>`） | 文本域 |
| 空值 | 文本框，留空即保持为空 |
| 嵌套段 / 列表 | 可折叠分组 |
| 锚点别名 `*ref` | 只读展示 |

**保存时只替换你改过的那一个值的原始字符区间**，所以注释、空行、缩进、键顺序、未改动项的引号风格都不会变——插件重新加载后照常运行。

> 这个面板能改配置，等价于服务器管理员入口。默认只监听 `127.0.0.1`，首次使用必须先设置密码。
> 请**不要**把端口直接暴露到公网；推荐 Tailscale 组网或 HTTPS 反向代理。详见 [SECURITY.md](SECURITY.md)。

---

## 快速开始

### 1. 运行

```bash
# 源码运行（需要 .NET 10 SDK）
cd MCServerPanel\MCServerPanel
dotnet run

# 或者用发布产物（Windows）
MCServerPanel.exe
```

Windows 用户也可以直接用 Visual Studio 打开 `MCServerPanel.slnx` 按 F5。

### 2. 首次初始化（必须）

第一次启动没有密码，控制台会打印一段一次性令牌：

```
  ⚠ 还没有管理员密码。请打开 /setup.html，用下面的一次性令牌初始化：
    初始化令牌：X-2kYCefdn4s0JOPeA0Gjt1I1lbEXTUq
```

浏览器打开 `http://localhost:8989/setup.html`，填入令牌 + 用户名 + 密码（≥10 位）。
设置完会自动登录；令牌随即失效，重启会重新生成。

### 3. 选服务端目录

登录后在「设置」里选择服务端根目录（含 `server.properties` 与 `plugins` 的那一层），
也可以直接选 `plugins` 目录。想彻底固定目录、不允许网页修改，用启动参数：

```bash
MCServerPanel.exe --plugins-root "D:\Server 1.21.11\plugins"
```

### 4. 改配置

左侧选插件 → 选文件 → 改值 → 右下角「保存到文件」（或 `Ctrl+S`）。
保存后需要让插件重载配置（`/reload confirm` 或重启服务端）才生效。

## 启动参数

只影响本次运行，不写回设置：

```bash
--port 9000                 # 临时换端口（占用时自动顺延）
--plugins-root <dir>        # 固定插件根目录（也可用环境变量 MCSP_PLUGINS_ROOT）
--data-dir <dir>            # 用另一份 settings.json / auth.json（便于并行调试）
--no-browser                # 不自动打开浏览器
--reset-password            # 本机重置管理员密码（读 MCSP_NEW_PASSWORD 或交互输入）
```

## 安全默认值（阶段 1 已实现）

- **默认只监听 `127.0.0.1`**；开放局域网/公网前必须已设置密码，否则启动时强制回退到回环。
- **强制登录**：除登录页、初始化、`/api/auth/session`、最小化 `/api/health` 外，所有 API 需要会话。
- 密码只存 **PBKDF2-SHA256（60 万次迭代）** 加盐哈希，`auth.json` 在 Unix 上权限 0600。
- **登录限速**：按 IP + 用户名分别计数（5 次/5 分钟），超限返回 429 并记录；失败响应有固定延迟。
- **会话**：HttpOnly + SameSite=Strict（HTTPS 下加 Secure），空闲 30 分钟 / 绝对 12 小时过期；
  改密码吊销全部会话。
- **CSRF**：所有写请求必须带 `X-MCSP-CSRF: 1` 请求头。
- **路径沙箱**：拒绝 `..`、绝对路径、盘符/ADS、Windows 保留名；解析符号链接后再次确认在插件根内。
- **解析上限**：文件 4MB、节点 20 万、嵌套 200 层；重复键按 YAML 规范以最后一处为准并提示。
- **安全响应头**：CSP（禁止内联脚本）、X-Frame-Options、nosniff、no-referrer；API 响应 no-store。
- **审计日志**：登录成功/失败/限速、初始化、保存（文件、键名、前后哈希，不含配置值）、接管、改设置、重启，
  写到 `<data-dir>/logs/audit-YYYYMM.jsonl`。
- 未登录的 `/api/health` 只返回 `{"ok":true,"authRequired":true}`，不泄露路径与端口。

## 部署建议

打包成 Windows / Linux 可执行包（自包含单文件、服务注册、常见坑）见 **[docs/PACKAGING.md](docs/PACKAGING.md)**。

- **推荐**：服务器和家里电脑加入同一个 Tailscale 网络，面板只监听 `127.0.0.1`，
  用 `tailscale serve` 暴露成 `https://<机器名>.<tailnet>.ts.net`。公网不开放任何端口。
- **公网访问**：Caddy/NGINX 终止 TLS + 额外的代理层认证 + fail2ban，面板仍只监听回环。
- **Linux**：systemd 专用用户，只给 `plugins` 目录写权限；不要用 root 跑面板或 MC 服务端。
- **Windows**：WinSW/NSSM 注册服务，用专用本地账号；防火墙只允许回环/组网接口。

## 界面

- **左栏**：按插件分组列出 `.yml` / `.yaml`（`config.yml` 带「主配置」标记，子目录文件显示相对路径）；
  支持搜索；未保存时文件旁有黄色点，被他人编辑时是蓝色点。
- **折叠左栏**：顶栏 ☰ 或 `Ctrl+B`；折叠状态记在本机。
- **亮色 / 暗色**：顶栏 ☀/☾ 或 `Ctrl+Shift+L`；`?theme=light` 可直接以亮色打开。
- **配置区**：键上方注释作为说明显示（可点击展开），行尾注释在键下方；改动的行有黄色左边框和「↺ 还原」。
- **查看源码 / 重新加载 / 退出登录**：右上角与左栏底部；设置里可「修改密码」。
- **保存栏**：待保存数量、放弃改动（二次确认）、`Ctrl+S`。

## 多人同时改同一份文件

1. **编辑占用**：打开文件即登记（名称可在设置里改），每 5 秒心跳，90 秒无心跳释放；
   别人打开同一文件会看到顶部横幅并可「接管编辑」。
2. **保存时校验占用**：非当前占用者保存返回 `423`，不会落盘。
3. **磁盘哈希检测**：每 5 秒比对 SHA-256；被外部修改时红色横幅提示，可选重新加载/强制覆盖（覆盖前自动备份）。

占用只在单个面板进程内有效，跨进程靠哈希校验兜底。

## 注释为什么不会丢

1. 用 YamlDotNet 事件流解析，记录每个标量 token 的**精确字符下标**（含引号、跳过 `&anchor` / `!!tag`）。
2. 只对那一段下标做替换；新值按原风格编码（单/双引号、plain、`yes` 保持 `yes`、块标量保持缩进）。
3. 写盘前三重校验，任一不过就放弃保存：改后仍可解析、注释序列逐条相同、未改动 token 与键结构逐一相同。
4. 同目录临时文件 + 原子替换；编码（UTF-8/BOM/UTF-16/GB18030）与换行风格（CRLF/LF）沿用原文件。
5. 旧文件备份到 `<data-dir>/backups/YYYYMMDD/`，保留 30 天。

## API

所有 `/api/*`（除 `auth/*` 与最小化 `health`）都需要会话 Cookie；写请求需要 `X-MCSP-CSRF: 1`。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/health` | 未登录：最小信息；登录后：端口/地址/设置路径 |
| GET | `/api/auth/session` | `{authenticated, setupRequired, username}` |
| POST | `/api/auth/setup` | `{token, username, password}` 首次初始化 |
| POST | `/api/auth/login` | `{username, password}` |
| POST | `/api/auth/logout` | 退出并吊销当前会话 |
| POST | `/api/auth/change-password` | `{currentPassword, newPassword}` |
| GET | `/api/settings` | 当前设置 + 目录探测 + `pluginsRootLocked` |
| POST | `/api/settings` | `{serverPath, port, allowLan}`（固定根时拒绝改目录） |
| POST | `/api/settings/restart` | 重启面板进程 |
| GET | `/api/browse?path=` | 目录浏览（固定根后返回 403） |
| GET | `/api/catalog` | 插件与配置文件清单 |
| GET | `/api/config?file=SnowyGems/config.yml` | 节点树 + 原文 + 哈希 |
| POST | `/api/config/save` | `{file, baseHash, force, clientId, edits:[{path,type,value,expectedRaw}]}` |
| GET | `/api/config/watch?...` | 轮询：磁盘哈希、外部改动、占用状态 |
| POST | `/api/config/claim` | 登记 / 接管编辑占用 |
| POST | `/api/config/release` | 释放占用 |
| GET | `/api/locks?clientId=` | 当前所有编辑占用 |
| GET | `/api/raw?file=` | 纯文本原文 |

`path` 是键/下标数组，例如 `["UpdateChecker","Enabled"]`、`["settings","worlds",0]`。

## 测试

```bash
cd MCServerPanel.Tests
dotnet test                     # 47 个单元测试：哈希/会话/限速/路径沙箱/解析上限/注释保持/编辑占用

# 端到端冒烟（认证链路 + 路径沙箱 + 保存完整性 + 浏览器自检）
python scripts/smoke_auth.py            # 自动 build、自建临时插件目录，不碰真实配置
python scripts/smoke_auth.py --skip-ui  # CI 里没有浏览器时
```

冒烟脚本使用 `tests/fixtures/plugins` 里的合成夹具；浏览器自检（33 项）需要 Edge/Chrome，
也可以用测试模式手工跑：

```bash
# 仅测试用：MCSP_TEST_MODE=1 时回环请求跳过登录校验
set MCSP_TEST_MODE=1
MCServerPanel.exe --port 8993 --data-dir <临时目录> --plugins-root tests\fixtures\plugins
# 打开 http://localhost:8993/?selftest=1#/f/DemoPlugin/config.yml
```

## 路线图

- 阶段 1（已完成）：认证/会话/CSRF/限速、默认回环 + fail closed、路径沙箱、解析上限、安全头、审计。
- 阶段 2：文件锁与故障注入测试、属性测试、双平台 CI 矩阵。
- 阶段 3：GitHub Actions 双平台发布 + 校验和/attestation、CodeQL、Dependabot、部署文档。
- 阶段 4：TOTP、只读模式、保存前 diff 预览、git 快照回滚、多用户角色。

## 已知限制

- 只做"改值"，不做新增/删除键或列表项；一行式空集合只显示"空列表"。
- 锚点别名只读；多文档 YAML 只加载第一个并提示。
- 单管理员账号，暂无 TOTP / 多用户（阶段 4）。
- 不校验插件语义，只保证格式与注释；改完需重载插件。
- 编辑占用只在单个面板进程内生效。

## 许可证

MIT，见 [LICENSE](LICENSE)。与 Mojang / Microsoft 无关。
