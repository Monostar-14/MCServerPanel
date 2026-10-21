# 安全策略

## 这是什么等级的工具

MCServerPanel 能读写 Minecraft 服务端 `plugins` 目录下的 YAML 配置，等价于**服务器管理员权限**：
配置里可能包含数据库密码、Webhook、RCON 密码；部分插件的配置可以间接执行命令。
请把它当成 SSH 一级的入口来对待，而不是一个普通的网页。

## 支持的部署方式

只支持以下方式（按推荐顺序）：

1. **仅本机回环**（默认）：面板只监听 `127.0.0.1`。
2. **私有组网**：Tailscale / WireGuard / ZeroTier，面板只监听回环或组网地址。
3. **HTTPS 反向代理 + 认证**：Caddy/NGINX 终止 TLS，面板仅监听回环，代理层再加一层认证。

**不支持、也不要做的**：把面板端口直接映射到公网（哪怕设置了密码）。这类部署不在维护范围内，
出了问题不会被当作安全漏洞处理。

## 内置的安全默认值

- 默认只监听 `127.0.0.1`。
- 首次运行没有密码：只允许回环，强制进入 `/setup.html`，用控制台打印的一次性令牌初始化。
- 要求开放局域网但还没设密码时，启动时**强制回退到回环**并打印警告（fail closed）。
- 密码用 PBKDF2-SHA256（600,000 次迭代）加盐哈希，只保存在 `auth.json`（Unix 上权限 0600）。
- 登录失败限速：按 IP 和用户名分别计数，5 次/5 分钟后短暂封禁；失败响应有固定延迟。
- 会话：HttpOnly + SameSite=Strict（HTTPS 下加 Secure），空闲 30 分钟 / 绝对 12 小时过期，改密后全部吊销。
- 所有写请求要求 `X-MCSP-CSRF: 1` 请求头（跨站表单无法携带自定义头）。
- 未登录时 `/api/health` 只返回 `{ok, authRequired}`，不泄露路径与端口。
- 路径沙箱：相对路径拒绝 `..`、绝对路径、盘符/ADS、Windows 保留名，并解析符号链接后再次确认在插件根内；
  可用 `--plugins-root` / `MCSP_PLUGINS_ROOT` 固定根目录，固定后网页不能改。
- YAML 解析上限：文件 4MB、节点 20 万、嵌套 200 层；重复键按 YAML 规范以最后一处为准并给出警告。
- 保存仍保持"只替换改动的标量、注释零改动"的三重校验，并写审计日志（不含配置值）。

## 已知限制

- 单管理员账号，没有多用户/角色，也没有 TOTP（计划中）。
- 编辑占用（多人协作锁）只在单个面板进程内有效；跨进程靠文件哈希校验兜底。
- 面板不校验配置的插件语义，只保证格式与注释不被破坏；改完需要重载插件。
- 审计日志与备份默认保存在**程序所在目录**（可用 `--data-dir` 改到别处），没有远程集中收集。
- `MCSP_TEST_MODE=1` 会跳过回环请求的登录校验，仅供自动化测试；生产环境不要设置。

## 报告漏洞

请**不要**在公开 issue 里贴漏洞细节。使用 GitHub 的
[私密漏洞报告](https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
（仓库 Security 标签页 → Report a vulnerability），或直接联系仓库维护者。

报告里请包含：版本号、部署方式（回环/Tailscale/反代）、复现步骤、影响范围。
我们会在 7 天内确认，并在修复发布后致谢（如果你希望署名）。

## 发布与供应链

- 依赖只有 YamlDotNet；发布附带 `SHA256SUMS`，构建产物由 GitHub Actions 从 tag 构建。
- 发布说明中会标注安全修复；升级前请先看 CHANGELOG。
