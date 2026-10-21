#!/usr/bin/env python3
"""MCServerPanel 冒烟验证：认证链路 + 路径沙箱 + 保存完整性（+ 可选浏览器自检）。

用法（在仓库根目录）：
    python scripts/smoke_auth.py            # 先 build，再验证
    python scripts/smoke_auth.py --no-build # 已有 Release 产物时跳过构建
    python scripts/smoke_auth.py --skip-ui  # CI 无浏览器时跳过前端自检

脚本自建临时数据目录与插件目录（复制 tests/fixtures/plugins），不会碰任何真实服务端配置。
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(ROOT, "MCServerPanel")
RELEASE = os.path.join(PROJECT, "bin", "Release", "net10.0")
EXE = os.path.join(RELEASE, "MCServerPanel.exe" if os.name == "nt" else "MCServerPanel")
FIXTURE = os.path.join(ROOT, "tests", "fixtures", "plugins")
FILE = "DemoPlugin/config.yml"
PASSWORD = "smoke-test-password-2026"
results: list[tuple[bool, str, str]] = []


def check(name: str, ok: bool, info: object = "") -> None:
    results.append((bool(ok), name, "" if info == "" else str(info)))
    print(("PASS  " if ok else "FAIL  ") + name + (f"  [{info}]" if info != "" else ""))


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def http(port: int, path: str, method: str = "GET", body=None, cookie: str | None = None,
         csrf: bool = True, timeout: int = 20):
    headers, data = {}, None
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if csrf and method != "GET":
        headers["X-MCSP-CSRF"] = "1"
    if cookie:
        headers["Cookie"] = cookie
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8", "replace")
            return r.status, (json.loads(raw) if raw else None), r.headers.get_all("Set-Cookie") or []
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            body = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            body = {"error": raw}
        return e.code, body, e.headers.get_all("Set-Cookie") or []


def wait_port(port: int, seconds: int = 45) -> bool:
    for _ in range(seconds * 4):
        try:
            socket.create_connection(("127.0.0.1", port), timeout=0.5).close()
            return True
        except OSError:
            time.sleep(0.25)
    return False


def start(port: int, data_dir: str, plugins_root: str, test_mode: bool = False):
    log = os.path.join(data_dir, "stdout.log")
    env = dict(os.environ)
    if test_mode:
        env["MCSP_TEST_MODE"] = "1"
    args = [EXE, "--no-browser", "--port", str(port), "--data-dir", data_dir, "--plugins-root", plugins_root]
    return subprocess.Popen(args, cwd=RELEASE, env=env, stdout=open(log, "wb"), stderr=subprocess.STDOUT), log


def stop(proc) -> None:
    try:
        proc.terminate()
        proc.wait(timeout=8)
    except Exception:
        subprocess.run(["taskkill", "/PID", str(proc.pid), "/F"], capture_output=True)


def read_token(log: str) -> str | None:
    for _ in range(80):
        try:
            text = open(log, "rb").read().decode("utf-8", "replace")
        except OSError:
            text = ""
        m = re.search(r"初始化令牌：([A-Za-z0-9_\-]+)", text)
        if m:
            return m.group(1)
        time.sleep(0.25)
    return None


def find_browser() -> str | None:
    candidates = [
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    ]
    candidates += [shutil.which(x) or "" for x in ("msedge", "chromium", "chromium-browser", "google-chrome")]
    return next((c for c in candidates if c and os.path.exists(c)), None)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--skip-ui", action="store_true")
    args = ap.parse_args()

    if not args.no_build:
        code = subprocess.run(["dotnet", "build", os.path.join(ROOT, "MCServerPanel.slnx"), "-c", "Release", "-v", "q"],
                              cwd=ROOT).returncode
        check("dotnet build -c Release", code == 0, code)
        if code:
            return 1

    tmp = tempfile.mkdtemp(prefix="mcsp-smoke-")
    plugins = os.path.join(tmp, "plugins")
    shutil.copytree(FIXTURE, plugins)
    config_file = os.path.join(plugins, "DemoPlugin", "config.yml")
    original = open(config_file, "rb").read()
    data_dir = os.path.join(tmp, "data")
    os.makedirs(data_dir)
    port = free_port()
    proc = None
    try:
        proc, log = start(port, data_dir, plugins)
        check("实例启动", wait_port(port), f"port={port}")

        st, data, _ = http(port, "/api/health")
        check("未登录 health 最小化", st == 200 and data.get("authRequired") and "settingsFile" not in data, data)
        st, _, _ = http(port, "/api/config?file=" + urllib.parse.quote(FILE))
        check("未登录读配置 401", st == 401, st)
        st, _, _ = http(port, "/api/auth/login", "POST", {"username": "admin", "password": "x"}, csrf=False)
        check("缺少 CSRF 头 403", st == 403, st)

        token = read_token(log)
        check("控制台打印初始化令牌", bool(token))
        st, _, _ = http(port, "/api/auth/setup", "POST", {"token": "wrong", "username": "admin", "password": PASSWORD})
        check("错误令牌 403", st == 403, st)
        st, _, _ = http(port, "/api/auth/setup", "POST", {"token": token, "username": "admin", "password": "short"})
        check("弱密码 400", st == 400, st)
        st, data, cookies = http(port, "/api/auth/setup", "POST",
                                 {"token": token, "username": "admin", "password": PASSWORD})
        cookie = next((c.split(";")[0] for c in cookies if c.startswith("mcsp_session=")), None)
        check("初始化成功并下发 Cookie", st == 200 and bool(cookie), st)
        check("Cookie HttpOnly + SameSite=Strict",
              any("httponly" in c.lower() and "samesite=strict" in c.lower() for c in cookies))

        st, doc, _ = http(port, "/api/config?file=" + urllib.parse.quote(FILE), cookie=cookie)
        check("登录后读配置", st == 200 and doc.get("root"), st)
        uc = next(c for c in doc["root"]["children"] if c["key"] == "UpdateChecker")
        node = next(c for c in uc["children"] if c["key"] == "Enabled")
        st, saved, _ = http(port, "/api/config/save", "POST", {
            "file": FILE, "baseHash": doc["hash"], "force": False, "clientId": "smoke",
            "edits": [{"path": ["UpdateChecker", "Enabled"], "type": "bool",
                       "value": not node["value"], "expectedRaw": node["raw"]}]}, cookie=cookie)
        check("登录后保存成功", st == 200 and len(saved.get("applied") or []) == 1, st)

        new_text = open(config_file, encoding="utf-8", newline="").read()
        old_lines, new_lines = doc["text"].split("\n"), new_text.split("\n")
        diff = sum(1 for i in range(max(len(old_lines), len(new_lines)))
                   if (old_lines[i] if i < len(old_lines) else None) != (new_lines[i] if i < len(new_lines) else None))
        check("只改 1 行", diff == 1, f"diff={diff}")
        check("注释逐条不变",
              [l for l in new_text.split("\n") if l.strip().startswith("#")] ==
              [l for l in doc["text"].split("\n") if l.strip().startswith("#")])

        # 备份必须落在数据目录里（曾经错写在 %LOCALAPPDATA%，换目录后要盯住）
        bak_root = os.path.join(data_dir, "backups")
        baks = [os.path.join(r, f) for r, _, fs in os.walk(bak_root) for f in fs if f.endswith(".bak")]
        inside = all(os.path.commonpath([b, data_dir]) == os.path.normpath(data_dir) for b in baks)
        check("备份写在数据目录内", len(baks) >= 1 and inside, f"count={len(baks)}")

        st, _, _ = http(port, "/api/browse", cookie=cookie)
        check("固定插件根后关闭目录浏览", st == 403, st)
        st, _, _ = http(port, "/api/settings", "POST",
                        {"serverPath": os.path.join(tmp, "somewhere-else"), "port": 8989, "allowLan": False},
                        cookie=cookie)
        check("固定插件根后拒绝改目录", st in (400, 403), st)

        http(port, "/api/auth/logout", "POST", {}, cookie=cookie)
        st, _, _ = http(port, "/api/config?file=" + urllib.parse.quote(FILE), cookie=cookie)
        check("退出后 Cookie 失效", st == 401, st)

        codes = [http(port, "/api/auth/login", "POST", {"username": "admin", "password": f"bad{i}"})[0]
                 for i in range(5)]
        st, data, _ = http(port, "/api/auth/login", "POST", {"username": "admin", "password": PASSWORD})
        check("连续失败触发限速", st == 429 and data.get("retryAfter"), f"{codes} -> {st}")

        logs = os.path.join(data_dir, "logs")
        audit = "".join(open(os.path.join(logs, f), encoding="utf-8").read() for f in os.listdir(logs))
        check("审计日志记录关键事件",
              '"setup"' in audit and '"save"' in audit and '"login_failed"' in audit)

        stop(proc)
        proc = None

        if args.skip_ui:
            print("SKIP  浏览器自检（--skip-ui）")
        else:
            browser = find_browser()
            if not browser:
                print("SKIP  浏览器自检（未找到 Edge/Chrome）")
            else:
                ui_port = free_port()
                ui_data = os.path.join(tmp, "ui-data")
                os.makedirs(ui_data)
                ui_proc, _ = start(ui_port, ui_data, plugins, test_mode=True)
                try:
                    check("测试模式实例启动", wait_port(ui_port), f"port={ui_port}")
                    profile = tempfile.mkdtemp(prefix="mcsp-smoke-edge-")
                    url = f"http://127.0.0.1:{ui_port}/?selftest=1#/f/" + urllib.parse.quote(FILE, safe="")
                    run = subprocess.run([browser, "--headless=new", "--disable-gpu", "--no-first-run",
                                          "--user-data-dir=" + profile, "--virtual-time-budget=25000",
                                          "--window-size=1500,1000", "--dump-dom", url],
                                         capture_output=True, text=True, encoding="utf-8",
                                         errors="replace", timeout=240)
                    m = re.search(r'<pre id="selftest"[^>]*>(.*?)</pre>', run.stdout or "", re.S)
                    report = (m.group(1) if m else "").replace("&gt;", ">").replace("&lt;", "<")
                    check("浏览器自检全绿", bool(report) and "FAIL" not in report,
                          f"PASS={report.count('PASS')}")
                    shutil.rmtree(profile, ignore_errors=True)
                finally:
                    stop(ui_proc)
    finally:
        if proc:
            stop(proc)
        with open(config_file, "wb") as f:
            f.write(original)
        shutil.rmtree(tmp, ignore_errors=True)

    failed = [r for r in results if not r[0]]
    print(f"\n{len(results) - len(failed)}/{len(results)} checks passed")
    return 0 if not failed else 1


if __name__ == "__main__":
    sys.exit(main())
