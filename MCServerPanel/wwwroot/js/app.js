'use strict';

(() => {
  /* ============================ 基础工具 ============================ */
  const $ = (sel, root = document) => root.querySelector(sel);

  const state = {
    settings: null,
    catalog: null,
    doc: null,
    pending: new Map(),   // pathKey -> { path, type, value, raw, label }
    collapsed: new Set(),
    search: '',
    uid: 0,
    rows: new Map(),      // uid -> row element
    bus: false,
    user: '',
    // 主题 / 侧栏
    theme: 'dark',
    sidebarCollapsed: false,
    // 协作
    clientId: '',
    clientName: '',
    locks: new Map(),     // 相对路径 -> LockDto
    lockBusy: false,
    lockHolder: null,
    external: null,       // 磁盘被外部改动时记录 { hash, size, lastWrite }
    watchTimer: null
  };

  const LS = {
    theme: 'mcpanel-theme',
    sidebar: 'mcpanel-sidebar',
    client: 'mcpanel-client-id',
    name: 'mcpanel-client-name'
  };

  function initIdentity() {
    let id = localStorage.getItem(LS.client);
    if (!id) {
      id = 'c-' + Math.random().toString(36).slice(2, 10);
      localStorage.setItem(LS.client, id);
    }
    state.clientId = id;
    state.clientName = localStorage.getItem(LS.name) || ('用户-' + id.slice(-4).toUpperCase());
  }

  function applyTheme(theme, persist = true) {
    state.theme = theme === 'light' ? 'light' : 'dark';
    document.documentElement.dataset.theme = state.theme;
    const icon = $('#themeIcon');
    if (icon) icon.textContent = state.theme === 'dark' ? '☀' : '☾';
    const btn = $('#btnTheme');
    if (btn) btn.title = state.theme === 'dark' ? '切换到亮色主题（Ctrl+Shift+L）' : '切换到暗色主题（Ctrl+Shift+L）';
    if (persist) localStorage.setItem(LS.theme, state.theme);
  }

  function toggleTheme() {
    applyTheme(state.theme === 'dark' ? 'light' : 'dark');
  }

  function applySidebar(collapsed, persist = true) {
    state.sidebarCollapsed = !!collapsed;
    document.documentElement.classList.toggle('sidebar-collapsed', state.sidebarCollapsed);
    document.documentElement.classList.remove('sidebar-collapsed-init');
    const btn = $('#btnSidebar');
    if (btn) btn.title = state.sidebarCollapsed ? '展开插件列表（Ctrl+B）' : '折叠插件列表（Ctrl+B）';
    if (persist) localStorage.setItem(LS.sidebar, state.sidebarCollapsed ? 'collapsed' : 'open');
  }

  function h(tag, props, ...kids) {
    const node = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') node.className = v;
      else if (k === 'dataset') Object.assign(node.dataset, v);
      else if (k === 'style') Object.assign(node.style, v);
      else if (k.startsWith('on') && typeof v === 'function') node.addEventListener(k.slice(2).toLowerCase(), v);
      else if (v === true) node.setAttribute(k, '');
      else node.setAttribute(k, String(v));
    }
    for (const kid of kids.flat(4)) {
      if (kid === null || kid === undefined || kid === false) continue;
      node.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
    }
    return node;
  }

  function svg(path, size = 15) {
    const ns = 'http://www.w3.org/2000/svg';
    const s = document.createElementNS(ns, 'svg');
    s.setAttribute('viewBox', '0 0 24 24');
    s.setAttribute('width', size);
    s.setAttribute('height', size);
    const p = document.createElementNS(ns, 'path');
    p.setAttribute('fill', 'currentColor');
    p.setAttribute('d', path);
    s.append(p);
    return s;
  }

  const ICON = {
    chevron: 'M6.7 8.6 12 13.9l5.3-5.3 1.4 1.4L12 16.7 5.3 10Z',
    folder: 'M3 6a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z'
  };

  /* ============================ 网络 ============================ */
  async function api(path, opts = {}) {
    const init = { ...opts, headers: { ...(opts.headers || {}), 'X-MCSP-CSRF': '1' } };
    if (init.body) init.headers['Content-Type'] = 'application/json';
    const res = await fetch(path, init);
    if (res.status === 401) {
      const next = encodeURIComponent(location.pathname + location.hash);
      location.href = '/login.html?next=' + next;
      throw new Error('未登录或会话已过期');
    }
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = { error: text }; }
    if (!res.ok) {
      const err = new Error((data && data.error) || `请求失败（HTTP ${res.status}）`);
      err.status = res.status;
      throw err;
    }
    return data;
  }

  /* ============================ 提示 ============================ */
  function toast(kind, title, body) {
    const root = $('#toastRoot');
    const mark = kind === 'ok' ? '✓' : kind === 'err' ? '✕' : '!';
    const t = h('div', { class: `toast ${kind}` },
      h('div', { class: 't-mark' }, mark),
      h('div', { class: 't-body' }, h('div', { class: 't-title' }, title), body ? h('div', {}, body) : null)
    );
    root.append(t);
    const ttl = kind === 'err' ? 9000 : 4200;
    setTimeout(() => { t.style.opacity = '0'; t.style.transition = 'opacity .25s'; setTimeout(() => t.remove(), 260); }, ttl);
  }

  function showBootError(msg) {
    const box = $('#bootError');
    if (!box) return;
    box.hidden = false;
    box.textContent = String(msg);
  }

  /* ============================ 弹窗 ============================ */
  function modal({ title, sub, wide = false, body, footer }) {
    const mask = h('div', { class: 'modal-mask' });
    const close = () => { mask.remove(); document.removeEventListener('keydown', onKey); };
    const onKey = (e) => { if (e.key === 'Escape') close(); };
    document.addEventListener('keydown', onKey);

    const box = h('div', { class: `modal${wide ? ' wide' : ''}` },
      h('div', { class: 'modal-head' },
        h('div', {}, h('div', { class: 'modal-title' }, title), sub ? h('div', { class: 'modal-sub' }, sub) : null),
        h('button', { class: 'modal-close', title: '关闭', onclick: close }, '✕')
      ),
      h('div', { class: 'modal-body' }, body),
      footer ? h('div', { class: 'modal-foot' }, footer) : null
    );
    mask.append(box);
    mask.addEventListener('mousedown', (e) => { if (e.target === mask) close(); });
    $('#modalRoot').append(mask);
    return { close, mask, box };
  }

  function confirmDialog({ title, message, confirmText = '确定', cancelText = '取消', danger = false }) {
    return new Promise((resolve) => {
      let settled = false;
      const done = (v) => { if (settled) return; settled = true; dlg.close(); resolve(v); };
      const dlg = modal({
        title,
        body: h('div', { style: { whiteSpace: 'pre-wrap', lineHeight: '1.75', color: 'var(--text-dim)', fontSize: '13px' } }, message),
        footer: [
          h('div', { class: 'spacer' }),
          h('button', { class: 'ghost-btn', onclick: () => done(false) }, cancelText),
          h('button', { class: danger ? 'danger-btn' : 'primary-btn', onclick: () => done(true) }, confirmText)
        ]
      });
      dlg.mask.addEventListener('mousedown', (e) => { if (e.target === dlg.mask) done(false); });
    });
  }

  /* ============================ 设置 / 目录 ============================ */
  function updateServerChip() {
    const label = $('#serverPathLabel');
    const dot = $('#serverDot');
    const s = state.settings;
    if (!s) return;
    if (s.pluginsPath) {
      dot.classList.add('ok');
      label.textContent = s.serverPath || s.pluginsPath;
      label.title = `服务端：${s.serverPath || '(未知)'}\n插件目录：${s.pluginsPath}`;
    } else {
      dot.classList.remove('ok');
      label.textContent = '尚未设置服务端';
      label.title = s.error || '点击设置服务端目录';
    }
  }

  async function loadSettings() {
    state.settings = await api('/api/settings');
    updateServerChip();
    return state.settings;
  }

  async function loadCatalog() {
    if (!state.settings?.pluginsPath) { state.catalog = null; renderSidebar(); return; }
    state.catalog = await api('/api/catalog');
    renderSidebar();
  }

  function openSettings() {
    const s = state.settings || {};
    const rootLocked = !!s.pluginsRootLocked;
    const pathInput = h('input', {
      class: 'text-input', type: 'text', value: s.serverPath || '',
      placeholder: '例如 D:\\Servers\\paper',
      readonly: rootLocked ? true : null,
      title: rootLocked ? '插件根由启动参数/环境变量固定' : null
    });
    const portInput = h('input', { class: 'text-input', type: 'number', min: '1', max: '65535', value: String(s.port || 8989), style: { width: '140px', flex: '0 0 auto' } });
    const lanSwitch = h('button', { class: 'switch', role: 'switch', 'aria-checked': String(!!s.allowLan), title: '允许局域网访问' },
      h('span', { class: 'knob' }));
    const lanText = h('span', { class: 'switch-text' }, s.allowLan ? '已开启' : '仅本机');
    lanSwitch.addEventListener('click', () => {
      const on = lanSwitch.getAttribute('aria-checked') !== 'true';
      lanSwitch.setAttribute('aria-checked', String(on));
      lanText.textContent = on ? '已开启' : '仅本机';
    });

    const urlsBox = h('div', { class: 'urls' }, ...(s.urls || []).map(u =>
      h('div', { class: 'url-line' }, u, h('button', { class: 'copy-btn', onclick: () => copyText(u) }, '复制'))));

    const nameInput = h('input', {
      class: 'text-input', type: 'text', maxlength: '24', value: state.clientName,
      placeholder: '例如 小王 / 服主'
    });

    const statusBox = h('div', {});
    if (s.error) statusBox.append(h('div', { class: 'banner warn' }, h('div', {}, h('div', { class: 'b-title' }, '还没找到插件目录'), s.error)));
    if (s.hints?.length) {
      statusBox.append(h('div', { class: 'banner', style: { border: '1px solid var(--line)', background: 'rgba(148,173,214,.05)', color: 'var(--text-dim)' } },
        h('div', {}, ...s.hints.map(t => h('div', {}, '· ' + t)))));
    }

    let dlg;
    const saveBtn = h('button', { class: 'primary-btn' }, '保存设置');
    const restartBtn = h('button', {
      class: 'ghost-btn', style: { display: s.restartRequired ? 'inline-flex' : 'none' },
      onclick: async () => {
        const newPort = Number(portInput.value) || 8989;
        const ok = await confirmDialog({
          title: '重启面板？',
          message: `端口或监听范围有改动，需要重启面板才能生效。\n重启后请访问 http://localhost:${newPort}\n\n（如果你正在 Visual Studio 里调试运行，重启会结束本次调试会话。）`,
          confirmText: '立即重启'
        });
        if (!ok) return;
        try {
          await api('/api/settings/restart', { method: 'POST' });
          toast('warn', '面板正在重启…', '稍后会自动跳转到新地址');
          setTimeout(() => { location.href = `${location.protocol}//${location.hostname || 'localhost'}:${newPort}/`; }, 2600);
        } catch (e) { toast('err', '重启失败', e.message); }
      }
    }, '立即重启面板');

    saveBtn.addEventListener('click', async () => {
      saveBtn.disabled = true;
      try {
        state.settings = await api('/api/settings', {
          method: 'POST',
          body: JSON.stringify({
            serverPath: pathInput.value.trim() || null,
            port: Number(portInput.value) || 8989,
            allowLan: lanSwitch.getAttribute('aria-checked') === 'true'
          })
        });
        const newName = nameInput.value.trim();
        if (newName) {
          state.clientName = newName;
          localStorage.setItem(LS.name, newName);
        }
        updateServerChip();
        await loadCatalog();
        toast('ok', '设置已保存', state.settings.pluginsPath
          ? `插件目录：${state.settings.pluginsPath}`
          : '但还没能定位到插件目录');
        if (state.settings.restartRequired) {
          restartBtn.style.display = 'inline-flex';
          toast('warn', '需要重启才能生效', '端口或监听范围改动后，请点「立即重启面板」。');
        }
        dlg.close();
      } catch (e) {
        toast('err', '保存失败', e.message);
      } finally {
        saveBtn.disabled = false;
      }
    });

    const browsed = h('button', { class: 'ghost-btn', onclick: async () => {
      const picked = await browseDialog(pathInput.value.trim());
      if (picked) pathInput.value = picked;
    } }, '浏览…');
    if (rootLocked) {
      browsed.disabled = true;
      browsed.style.display = 'none';
    }

    dlg = modal({
      title: '设置',
      sub: s.settingsFile ? `设置文件：${s.settingsFile}` : null,
      body: [
        statusBox,
        h('div', { class: 'field' },
          h('label', {}, 'Minecraft 服务端目录'),
          h('div', { class: 'field-row' }, pathInput, browsed),
          h('div', { class: 'hint' }, rootLocked
            ? '插件根目录由启动参数 / 环境变量固定，网页中不能修改（这是防止越权访问其他目录的安全设计）。'
            : '选择服务端根目录（含 server.properties 与 plugins 的那一层），也可以直接选 plugins 目录。程序会扫描其中的 .yml / .yaml。')
        ),
        h('div', { class: 'flex-2' },
          h('div', { class: 'field' },
            h('label', {}, '网页端口（对外）'),
            portInput,
            h('div', { class: 'hint' }, '默认 8989。端口被占用时会自动顺延，实际端口显示在下方。')
          ),
          h('div', { class: 'field' },
            h('label', {}, '局域网访问'),
            h('div', { class: 'switch-line' }, lanSwitch, lanText),
            h('div', { class: 'hint' }, s.allowLan ? '同一局域网内的设备可打开下面地址' : '只监听 127.0.0.1')
          )
        ),
        h('div', { class: 'field' },
          h('label', {}, '你的显示名称（协作提示用）'),
          nameInput,
          h('div', { class: 'hint' }, '同事打开同一份文件时，会在顶部看到「谁正在编辑」；只保存在本机浏览器里。')
        ),
        h('div', { class: 'field' },
          h('label', {}, '当前访问地址'),
          urlsBox,
          h('div', { class: 'hint' }, `配置里的端口 ${s.port || 8989}，实际监听 ${s.actualPort || '—'}。面板已启用密码登录；开放局域网/公网前请确认使用强密码，并只监听可信网络。`)
        )
      ],
      footer: [
        h('div', { class: 'spacer' }),
        h('button', { class: 'ghost-btn', onclick: openChangePassword }, '修改密码'),
        restartBtn,
        saveBtn
      ]
    });
  }

  function openChangePassword() {
    const cur = h('input', { class: 'text-input', type: 'password', autocomplete: 'current-password' });
    const nw = h('input', { class: 'text-input', type: 'password', autocomplete: 'new-password' });
    const nw2 = h('input', { class: 'text-input', type: 'password', autocomplete: 'new-password' });
    const err = h('div', { class: 'field-error', hidden: true });
    const submit = h('button', { class: 'primary-btn' }, '修改密码');

    const dlg = modal({
      title: '修改管理员密码',
      sub: '修改后所有已登录会话都会失效，需要重新登录',
      body: [
        err,
        h('div', { class: 'field' }, h('label', {}, '当前密码'), cur),
        h('div', { class: 'field' }, h('label', {}, '新密码（至少 10 位）'), nw),
        h('div', { class: 'field' }, h('label', {}, '再输一次新密码'), nw2)
      ],
      footer: [h('div', { class: 'spacer' }), h('button', { class: 'ghost-btn', onclick: () => dlg.close() }, '取消'), submit]
    });

    submit.addEventListener('click', async () => {
      err.hidden = true;
      if (nw.value.length < 10) { err.hidden = false; err.textContent = '新密码至少 10 位。'; return; }
      if (nw.value !== nw2.value) { err.hidden = false; err.textContent = '两次输入的新密码不一致。'; return; }
      submit.disabled = true;
      try {
        await api('/api/auth/change-password', {
          method: 'POST',
          body: JSON.stringify({ currentPassword: cur.value, newPassword: nw.value })
        });
        dlg.close();
        toast('ok', '密码已修改', '请用新密码重新登录。');
        setTimeout(() => { location.href = '/login.html'; }, 900);
      } catch (e) {
        err.hidden = false;
        err.textContent = e.message;
      } finally {
        submit.disabled = false;
      }
    });
  }

  async function browseDialog(startPath) {
    let current = startPath || '';
    const drivesBox = h('div', { class: 'dir-toolbar' });
    const pathBox = h('div', { class: 'dir-path' }, '选择驱动器…');
    const listBox = h('div', { class: 'dir-list' });
    let resolver;
    const promise = new Promise((res) => { resolver = res; });

    const dlg = modal({
      title: '选择服务端目录',
      sub: '带「服务端」标记的目录含 server.properties；带「插件」标记的目录含 plugins',
      wide: true,
      body: [drivesBox, pathBox, listBox],
      footer: [
        h('div', { class: 'spacer' }),
        h('button', { class: 'ghost-btn', onclick: () => { resolver(null); dlg.close(); } }, '取消'),
        h('button', { class: 'primary-btn', onclick: () => { resolver(current || null); dlg.close(); } }, '选择此目录')
      ]
    });
    dlg.mask.addEventListener('mousedown', (e) => { if (e.target === dlg.mask) { resolver(null); dlg.close(); } });

    async function load(path) {
      listBox.textContent = '';
      try {
        const data = await api('/api/browse' + (path ? '?path=' + encodeURIComponent(path) : ''));
        if (!path) {
          current = '';
          pathBox.textContent = '选择驱动器…';
          drivesBox.textContent = '';
          for (const d of data.drives || []) {
            drivesBox.append(h('button', { class: 'dir-drive', onclick: () => load(d) }, d));
          }
          listBox.append(h('div', { class: 'dir-empty' }, '先选择一个驱动器，或直接在上面输入路径'));
          return;
        }
        current = data.path || path;
        pathBox.textContent = current;
        drivesBox.textContent = '';
        for (const d of data.drives || []) {
          drivesBox.append(h('button', { class: 'dir-drive' + (current.startsWith(d) ? ' active' : ''), onclick: () => load(d) }, d));
        }
        if (data.parent) {
          drivesBox.append(h('button', { class: 'dir-drive', onclick: () => load(data.parent) }, '↑ 上一级'));
        }
        listBox.textContent = '';
        if (data.error) {
          listBox.append(h('div', { class: 'dir-empty' }, data.error));
          return;
        }
        if (!data.directories.length) {
          listBox.append(h('div', { class: 'dir-empty' }, '这里没有子文件夹'));
          return;
        }
        for (const d of data.directories) {
          listBox.append(h('button', { class: 'dir-item', onclick: () => load(d.path) },
            svg(ICON.folder, 14),
            h('span', { style: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' } }, d.name),
            d.isServerRoot ? h('span', { class: 'tag server' }, '服务端') : null,
            !d.isServerRoot && d.hasPlugins ? h('span', { class: 'tag plugins' }, `插件 ${d.configCount}`) : null,
            d.configCount && d.isServerRoot ? h('span', { class: 'tag plugins' }, `插件 ${d.configCount}`) : null
          ));
        }
      } catch (e) {
        listBox.textContent = '';
        listBox.append(h('div', { class: 'dir-empty' }, e.message));
      }
    }

    await load(current || null);
    return promise;
  }

  function copyText(text) {
    navigator.clipboard?.writeText(text).then(
      () => toast('ok', '已复制', text),
      () => toast('err', '复制失败', '浏览器拒绝了剪贴板访问')
    );
  }

  /* ============================ 侧栏 ============================ */
  function renderSidebar() {
    const list = $('#pluginList');
    list.textContent = '';
    const q = state.search.trim().toLowerCase();
    const catalog = state.catalog;

    if (!state.settings?.pluginsPath) {
      list.append(h('div', { class: 'placeholder' },
        '还没有设置服务端目录。',
        h('br'), h('br'),
        h('button', { class: 'ghost-btn', onclick: openSettings }, '去设置')));
      return;
    }
    if (!catalog) return;

    let shown = 0;
    for (const plugin of catalog.plugins || []) {
      const files = (plugin.files || []).filter(f => {
        if (!q) return true;
        return plugin.name.toLowerCase().includes(q) || f.name.toLowerCase().includes(q) ||
          f.relativePath.toLowerCase().includes(q);
      });
      if (!files.length) continue;
      shown += files.length;
      const group = h('div', { class: 'plugin-group' },
        h('div', { class: 'plugin-head' },
          h('span', {}, plugin.name),
          plugin.version ? h('span', { class: 'plugin-ver' }, plugin.version) : null
        )
      );
      for (const f of files) {
        const active = state.doc?.file === f.relativePath;
        const dirty = active && state.pending.size > 0;
        const lock = state.locks?.get(f.relativePath);
        const relParts = f.relativePath.split('/');
        const label = relParts.length > 1 ? relParts.slice(1).join('/') : f.name;

        const badges = h('span', { class: 'file-badges' });
        if (lock && !lock.mine) {
          badges.append(h('span', {
            class: 'lock-dot',
            title: `${lock.clientName} 正在编辑（${lock.ageSeconds} 秒前还有操作）`
          }));
        }
        if (dirty) badges.append(h('span', { class: 'dirty', title: '有未保存的改动' }));

        group.append(h('button', {
          class: 'file-btn' + (active ? ' active' : ''),
          title: `${f.relativePath}\n${fmtBytes(f.size)} · 修改于 ${fmtTime(f.lastWrite)}`
            + (lock && !lock.mine ? `\n${lock.clientName} 正在编辑` : ''),
          onclick: () => openFile(f.relativePath)
        },
          h('span', { class: 'fname' }, label),
          f.isMain ? h('span', { class: 'main-tag' }, '主配置') : null,
          badges.childNodes.length ? badges : null
        ));
      }
      list.append(group);
    }

    if (!shown) {
      list.append(h('div', { class: 'placeholder' },
        q ? '没有匹配的插件或文件。' : '这个 plugins 目录里没有找到 .yml 文件。'));
    }
  }

  /* ============================ 多人与哈希检测 ============================ */
  async function claimFile(file, takeover = false) {
    if (!file) return null;
    try {
      const res = await api('/api/config/claim', {
        method: 'POST',
        body: JSON.stringify({ file, clientId: state.clientId, clientName: state.clientName, takeover })
      });
      state.lockBusy = !res.ok;
      state.lockHolder = res.holder || null;
      return res;
    } catch {
      return null; // 协作检测不可用时不影响本地编辑
    }
  }

  function releaseFile() {
    try {
      fetch('/api/config/release', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ clientId: state.clientId }),
        keepalive: true
      });
    } catch { /* ignore */ }
  }

  function startWatch() {
    stopWatch();
    const interval = new URLSearchParams(location.search).has('selftest') ? 1500 : 5000;
    state.watchTimer = setInterval(pollWatch, interval);
    setTimeout(pollWatch, 400);
  }

  function stopWatch() {
    if (state.watchTimer) clearInterval(state.watchTimer);
    state.watchTimer = null;
  }

  async function pollWatch() {
    if (!state.doc || document.hidden) return;
    try {
      const qs = new URLSearchParams({
        file: state.doc.file,
        clientId: state.clientId,
        clientName: state.clientName,
        since: state.doc.hash
      });
      const w = await api('/api/config/watch?' + qs.toString());

      state.locks = new Map((w.locks || []).map((l) => [l.file, l]));
      const holderChanged = (w.holder?.clientId || null) !== (state.lockHolder?.clientId || null);
      state.lockBusy = !!w.lockBusy;
      state.lockHolder = w.holder || null;

      if (w.changed) {
        state.external = { hash: w.hash, size: w.size, lastWrite: w.lastWrite };
      } else if (state.external) {
        state.external = null; // 重新加载后磁盘与当前一致，横幅撤掉
      }

      renderAlertBar();
      if (holderChanged) renderSidebar();
      updateSaveBar();
    } catch { /* 轮询失败静默，不打断编辑 */ }
  }

  async function takeOver() {
    if (!state.doc) return;
    const res = await claimFile(state.doc.file, true);
    if (res?.ok) {
      renderAlertBar();
      updateSaveBar();
      toast('ok', '已接管编辑', res.previous ? `「${res.previous.clientName}」的编辑权已转给你，对方保存会被拦截。` : '');
    } else {
      toast('err', '接管失败', res?.holder ? `「${res.holder.clientName}」仍占用这个文件。` : '服务端没有响应。');
    }
  }

  function renderAlertBar() {
    const bar = $('#alertBar');
    if (!bar) return;
    bar.textContent = '';
    bar.className = 'alert-bar';

    const rows = [];
    if (state.external) {
      rows.push({
        kind: 'warn',
        text: h('div', {},
          h('b', {}, '文件已在磁盘上被改动'),
          `（当前磁盘哈希 ${String(state.external.hash || '').slice(0, 8)}，与你打开时不同）。`,
          '可能是其他同事或工具直接改了它 —— 继续保存会覆盖对方的改动。'),
        actions: [
          { label: '重新加载（丢弃我的改动）', cls: 'ghost-btn', fn: () => openFile(state.doc.file, { force: true }) },
          { label: '强制覆盖保存', cls: 'danger-btn', fn: () => save(true) }
        ]
      });
    }
    if (state.lockBusy && state.lockHolder) {
      const hd = state.lockHolder;
      rows.push({
        kind: 'info',
        text: h('div', {},
          h('b', {}, `「${hd.clientName}」正在编辑这个文件`),
          `（${hd.ageSeconds} 秒前还有操作）。为避免互相覆盖，你的保存已暂停。`),
        actions: [
          { label: '接管编辑', cls: 'ghost-btn', fn: takeOver },
          { label: '放弃并重新加载', cls: 'ghost-btn', fn: () => openFile(state.doc.file, { force: true }) }
        ]
      });
    }

    if (!rows.length) {
      bar.hidden = true;
      return;
    }
    bar.hidden = false;
    bar.className = 'alert-bar ' + (rows.some((r) => r.kind === 'warn') ? 'error' : 'info');
    for (const row of rows) {
      bar.append(
        h('span', { class: 'a-icon' }, row.kind === 'warn' ? '⚠' : '◉'),
        h('span', { class: 'a-text' }, row.text),
        h('span', { class: 'a-actions' }, ...row.actions.map((a) =>
          h('button', { class: a.cls, onclick: a.fn }, a.label)))
      );
    }
  }

  /* ============================ 打开文件 ============================ */
  async function openFile(relPath, opts = {}) {
    if (!opts.force && state.pending.size > 0 && state.doc?.file !== relPath) {
      const ok = await confirmDialog({
        title: '还有未保存的改动',
        message: `「${state.doc.file}」有 ${state.pending.size} 项改动尚未保存。\n切换文件会丢弃这些改动。`,
        confirmText: '丢弃并切换',
        danger: true
      });
      if (!ok) return;
    }
    setContentLoading();
    try {
      if (state.doc?.file && state.doc.file !== relPath) releaseFile();
      const data = await api('/api/config?file=' + encodeURIComponent(relPath));
      state.doc = data;
      state.pending.clear();
      state.collapsed.clear();
      state.rows.clear();
      state.external = null;
      state.lockBusy = false;
      state.lockHolder = null;
      location.hash = '#/f/' + encodeURIComponent(relPath);
      renderSidebar();
      renderDoc();
      updateSaveBar();
      await claimFile(data.file);
      startWatch();
      renderAlertBar();
      updateSaveBar();
    } catch (e) {
      toast('err', '打不开这个文件', e.message);
      renderWelcome();
    }
  }

  function setContentLoading() {
    const content = $('#content');
    content.textContent = '';
    content.append(h('div', { class: 'doc' },
      h('div', { class: 'skeleton', style: { width: '40%', height: '20px', margin: '6px 0 18px' } }),
      h('div', { class: 'card' },
        ...Array.from({ length: 7 }, (_, i) => h('div', { class: 'skeleton', style: { width: `${90 - i * 7}%` } })))
    ));
  }

  function renderWelcome() {
    const content = $('#content');
    content.textContent = '';
    content.append(h('div', { class: 'welcome' },
      h('div', { class: 'welcome-inner' },
        h('h1', {}, '让插件配置一眼看懂'),
        h('p', {}, '选择左侧任意 .yml 文件：布尔值渲染成滑块，数字是带步进的输入框，字符串是文本框，多行文本是文本域。'),
        h('p', { class: 'welcome-note' }, '保存时只替换你改过的那一个值，', h('strong', {}, '注释、空行、缩进、键顺序全部保持原样'), '。')
      )
    ));
  }

  /* ============================ 文档渲染 ============================ */
  function renderDoc() {
    const content = $('#content');
    const doc = state.doc;
    content.textContent = '';
    if (!doc) { renderWelcome(); return; }

    const parts = doc.file.split('/');
    const pluginName = parts.length > 1 ? parts[0] : '服务端通用';
    const crumbs = $('#crumbs');
    crumbs.textContent = '';
    crumbs.append(
      h('span', { class: 'crumb-plugin' }, pluginName),
      h('span', { class: 'muted' }, '/'),
      h('span', { class: 'crumb-file' }, parts.slice(1).join('/') || doc.name),
      h('span', { class: 'badge' }, `${doc.lineCount} 行`)
    );
    $('#btnRaw').disabled = false;
    $('#btnReload').disabled = false;

    const wrap = h('div', { class: 'doc' },
      h('div', { class: 'doc-head' },
        h('div', {},
          h('div', { class: 'doc-title' }, doc.name),
          h('div', { class: 'doc-sub' }, doc.file)
        ),
        h('div', { class: 'doc-meta' },
          h('span', { class: 'meta-chip' }, fmtBytes(doc.size)),
          h('span', { class: 'meta-chip' }, '修改于 ' + fmtTime(doc.lastWrite)),
          h('span', { class: 'meta-chip' }, 'SHA ' + doc.hash.slice(0, 8))
        )
      )
    );

    if (doc.parseError) {
      wrap.append(h('div', { class: 'banner error' },
        h('div', {},
          h('div', { class: 'b-title' }, '这个文件有 YAML 语法错误，暂时不能可视化编辑'),
          h('div', {}, doc.parseError),
          doc.errorLine ? h('div', {}, `大约在第 ${doc.errorLine} 行`) : null,
          h('div', { style: { marginTop: '8px', color: 'var(--text-faint)' } }, '修正语法、或点右上角「重新加载」后再试。')
        )));
      wrap.append(h('div', { class: 'card' }, h('pre', { class: 'raw', style: { margin: '14px' } }, doc.text)));
      content.append(wrap);
      return;
    }

    for (const w of doc.warnings || []) {
      wrap.append(h('div', { class: 'banner warn' }, h('div', {}, w)));
    }
    if (!doc.root) {
      wrap.append(h('div', { class: 'banner warn' }, h('div', {}, '文件是空的，没有可编辑的配置项。')));
      content.append(wrap);
      return;
    }

    state.rows.clear();
    state.uidKeys = new Map();
    state.uid = 0;
    const card = h('div', { class: 'card' });
    const root = doc.root;

    if (root.kind === 'map') {
      for (const child of root.children || []) card.append(renderNode(child, 0));
    } else if (root.kind === 'seq') {
      card.append(renderSequence(root, 0));
    } else {
      card.append(renderScalarRow(root, 0));
    }

    wrap.append(card);
    content.append(wrap);
  }

  function renderNode(node, depth) {
    if (node.kind === 'map') return renderGroup(node, depth);
    if (node.kind === 'seq') return renderSequence(node, depth);
    return renderScalarRow(node, depth);
  }

  function renderGroup(node, depth) {
    const collapsible = (node.children || []).length > 0;
    const collapsed = state.collapsed.has(node.pathKey);
    const chev = svg(ICON.chevron, 13);
    chev.classList.add('chev');
    const head = h('button', { class: 'group-head' + (collapsed ? ' collapsed' : '') },
      collapsible ? chev : h('span', { style: { width: '13px' } }),
      h('span', { class: 'gkey' }, node.key ?? (node.index !== undefined && node.index !== null ? '内容' : '（根）')),
      h('span', { class: 'gcount' }, String((node.children || []).length)),
      node.comment ? h('span', { class: 'gcomment', title: node.comment }, node.comment) : null
    );
    const body = h('div', { class: 'group-body' + (collapsed ? ' hidden' : '') });
    if (collapsible && !collapsed) {
      for (const child of node.children) body.append(renderNode(child, depth + 1));
    } else if (!collapsible) {
      body.append(h('div', { class: 'row', style: { color: 'var(--text-faint)' } },
        h('div', { class: 'row-label' }, h('span', { class: 'k' }, '（空）'))));
    }
    if (collapsible) {
      head.addEventListener('click', () => {
        if (state.collapsed.has(node.pathKey)) state.collapsed.delete(node.pathKey);
        else state.collapsed.add(node.pathKey);
        renderDoc();
      });
    }
    return h('section', { class: `group depth-${Math.min(depth, 5)}` }, head, body);
  }

  function renderSequence(node, depth) {
    const collapsed = state.collapsed.has(node.pathKey);
    const chev = svg(ICON.chevron, 13);
    chev.classList.add('chev');
    const head = h('button', { class: 'group-head' + (collapsed ? ' collapsed' : '') },
      chev,
      h('span', { class: 'gkey' }, node.key ?? '（列表）'),
      h('span', { class: 'gcount' }, `${(node.children || []).length} 项`),
      node.comment ? h('span', { class: 'gcomment', title: node.comment }, node.comment) : null
    );
    const body = h('div', { class: 'group-body' + (collapsed ? ' hidden' : '') });
    if (!collapsed) {
      if (!(node.children || []).length) {
        body.append(h('div', { class: 'row', style: { paddingLeft: '30px' } },
          h('div', { class: 'row-label' }, h('span', { class: 'row-comment' }, '（空列表，暂无可编辑项）'))));
      }
      for (const item of node.children || []) {
        if (item.kind === 'scalar') {
          body.append(renderScalarRow(item, depth + 1, { seqIndex: item.index }));
        } else {
          body.append(h('div', { class: `seq-item depth-${Math.min(depth + 1, 5)}` },
            h('div', { class: 'seq-idx' }, `#${(item.index ?? 0) + 1}`),
            h('div', {}, renderNode(item, depth + 1))
          ));
        }
      }
    }
    head.addEventListener('click', () => {
      if (state.collapsed.has(node.pathKey)) state.collapsed.delete(node.pathKey);
      else state.collapsed.add(node.pathKey);
      renderDoc();
    });
    return h('section', { class: `group depth-${Math.min(depth, 5)}` }, head, body);
  }

  const TYPE_LABEL = {
    bool: '布尔', int: '整数', float: '小数', string: '文本',
    null: '空值', alias: '锚点引用', map: '分组', seq: '列表'
  };

  function renderScalarRow(node, depth, opts = {}) {
    const key = node.pathKey;
    const pend = state.pending.get(key);
    const value = pend ? pend.value : node.value;
    const uid = ++state.uid;

    const row = h('div', {
      class: `row depth-${Math.min(depth, 5)}` + (pend ? ' changed' : ''),
      dataset: { uid: String(uid), path: (node.path || []).join('/') }
    });

    const label = h('div', { class: 'row-label' },
      h('span', { class: 'k' }, opts.seqIndex !== undefined && opts.seqIndex !== null ? `#${opts.seqIndex + 1}` : (node.key ?? '（值）')),
      h('span', { class: 'tp' }, TYPE_LABEL[node.type] || node.type)
    );
    if (node.comment) label.append(h('div', { class: 'row-comment', title: node.comment }, node.comment));
    if (node.docComment) {
      const doc = h('div', { class: 'row-doc', title: node.docComment + '\n（点击展开 / 收起）' }, node.docComment);
      doc.addEventListener('click', () => doc.classList.toggle('expanded'));
      label.append(doc);
    }

    const control = h('div', { class: 'row-control' });
    control.append(buildControl(node, value, uid));

    const tools = h('div', { class: 'row-tools' });
    if (pend) tools.append(h('button', {
      class: 'revert-btn', title: '还原这一项',
      onclick: () => { state.pending.delete(key); refreshRow(uid); }
    }, '↺'));

    row.append(label, control, tools);
    state.rows.set(uid, row);
    return row;
  }

  function refreshRow(uid) {
    const row = state.rows.get(uid);
    if (!row) return;
    // 重新渲染整份文档最稳妥，但会丢焦点；这里只更新变更标记与还原按钮
    const key = findPathKeyByUid(uid);
    const pend = key ? state.pending.get(key) : null;
    row.classList.toggle('changed', !!pend);
    const tools = row.querySelector('.row-tools');
    if (tools) {
      tools.textContent = '';
      if (pend && key) {
        tools.append(h('button', {
          class: 'revert-btn', title: '还原这一项',
          onclick: () => { state.pending.delete(key); refreshRow(uid); }
        }, '↺'));
      }
    }
    // 控件本身的值可能也要回退
    const input = row.querySelector('.text-input, .num-input, .text-area');
    if (input && key && !pend) {
      const node = findNode(key);
      if (node) {
        input.value = node.type === 'null' ? '' : (node.value ?? '');
        if (input.tagName === 'TEXTAREA') {
          input.style.height = 'auto';
          input.style.height = Math.min(Math.max(input.scrollHeight + 2, 34), 460) + 'px';
        }
      }
    }
    const sw = row.querySelector('.switch');
    if (sw && key) {
      const node = findNode(key);
      const v = pend ? pend.value : node?.value;
      sw.setAttribute('aria-checked', String(!!v));
      const txt = row.querySelector('.switch-text');
      if (txt) txt.textContent = v ? '启用' : '禁用';
    }
    updateSaveBar();
    renderSidebar();
  }

  function findNode(pathKey) {
    if (!state.doc?.root) return null;
    let found = null;
    const walk = (n) => {
      if (found) return;
      if (n.pathKey === pathKey) { found = n; return; }
      for (const c of n.children || []) walk(c);
    };
    walk(state.doc.root);
    return found;
  }

  function findPathKeyByUid(uid) {
    const row = state.rows.get(uid);
    if (!row) return null;
    // uid -> pathKey 的映射在 buildControl 时缓存
    return state.uidKeys?.get(uid) || null;
  }

  /* ---------- 控件 ---------- */
  function buildControl(node, value, uid) {
    state.uidKeys = state.uidKeys || new Map();
    state.uidKeys.set(uid, node.pathKey);

    if (node.type === 'alias') {
      return h('span', { class: 'readonly-chip', title: 'YAML 锚点引用，修改请去被引用的原始配置' }, `${node.raw}（只读）`);
    }

    if (node.type === 'bool') {
      const sw = h('button', { class: 'switch', role: 'switch', 'aria-checked': String(!!value), title: '点击切换' },
        h('span', { class: 'knob' }));
      const txt = h('span', { class: 'switch-text' }, value ? '启用' : '禁用');
      sw.addEventListener('click', () => {
        const next = sw.getAttribute('aria-checked') !== 'true';
        sw.setAttribute('aria-checked', String(next));
        txt.textContent = next ? '启用' : '禁用';
        setPending(node, next, 'bool');
      });
      return h('div', { class: 'switch-wrap' }, sw, txt);
    }

    if (node.type === 'int' || node.type === 'float') {
      const step = node.step || (node.type === 'int' ? 1 : 0.1);
      const input = h('input', {
        class: 'num-input', type: 'number', value: value ?? 0,
        step: String(step), inputmode: 'decimal'
      });
      const commit = () => {
        const raw = input.value.trim();
        if (raw === '' || Number.isNaN(Number(raw))) return;
        let num = Number(raw);
        if (node.type === 'int') num = Math.trunc(num);
        else num = Math.round(num * 1e6) / 1e6;
        setPending(node, num, node.type);
      };
      input.addEventListener('input', commit);
      input.addEventListener('blur', () => {
        const raw = input.value.trim();
        if (raw === '' || Number.isNaN(Number(raw))) {
          input.value = value ?? 0;
        }
      });
      const bump = (dir) => {
        let cur = Number(input.value);
        if (Number.isNaN(cur)) cur = Number(node.value) || 0;
        let next = cur + dir * step;
        next = node.type === 'int' ? Math.round(next) : Math.round(next * 1e6) / 1e6;
        input.value = String(next);
        setPending(node, next, node.type);
      };
      return h('div', { class: 'stepper' },
        h('button', { class: 'step-btn', title: `减少 ${step}`, onclick: () => bump(-1) }, '−'),
        input,
        h('button', { class: 'step-btn', title: `增加 ${step}`, onclick: () => bump(1) }, '+')
      );
    }

    if (node.multiline) {
      const ta = h('textarea', { class: 'text-area', rows: '3', spellcheck: 'false' });
      ta.value = value ?? '';
      const autosize = () => {
        ta.style.height = 'auto';
        ta.style.height = Math.min(Math.max(ta.scrollHeight + 2, 92), 460) + 'px';
      };
      ta.addEventListener('input', () => setPending(node, ta.value, 'block'));
      requestAnimationFrame(autosize);
      return h('div', { class: 'text-wrap' }, ta);
    }

    // 字符串 / 空值：短值单行输入；长句用可换行文本域，避免只看到半截
    const text = value ?? '';
    const placeholder = node.type === 'null' ? '（空值，输入内容即可赋值）' : '';
    const swatch = colorSwatch(node.type === 'string' ? text : '');

    if (textUnits(text) > 52) {
      const ta = h('textarea', {
        class: 'text-area compact', rows: '2', spellcheck: 'false', title: text, placeholder
      });
      ta.value = text;
      const autosize = () => {
        ta.style.height = 'auto';
        ta.style.height = Math.min(Math.max(ta.scrollHeight + 2, 34), 200) + 'px';
      };
      ta.addEventListener('input', () => {
        setPending(node, ta.value, 'string');
        autosize();
      });
      ta.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); ta.blur(); }
      });
      requestAnimationFrame(autosize);
      const wrapLong = h('div', { class: 'text-wrap' }, ta);
      if (swatch) wrapLong.prepend(swatch);
      return wrapLong;
    }

    const input = h('input', {
      class: 'text-input', type: 'text', spellcheck: 'false',
      value: text, title: text, placeholder
    });
    input.addEventListener('input', () => setPending(node, input.value, 'string'));
    const wrap = h('div', { class: 'text-wrap' }, input);
    if (swatch) wrap.prepend(swatch);
    return wrap;
  }

  // 估算字符串在等宽字体下的显示宽度（ASCII=1，CJK/全角/emoji=2）
  function textUnits(s) {
    let w = 0;
    for (const ch of String(s || '')) {
      const c = ch.codePointAt(0);
      const wide = (c >= 0x1100 && (
        c <= 0x115f || (c >= 0x2e80 && c <= 0xa4cf) || (c >= 0xac00 && c <= 0xd7a3) ||
        (c >= 0xf900 && c <= 0xfaff) || (c >= 0xfe30 && c <= 0xfe6f) ||
        (c >= 0xff00 && c <= 0xff60) || (c >= 0xffe0 && c <= 0xffe6) ||
        (c >= 0x1f300 && c <= 0x1faff)));
      w += wide ? 2 : 1;
    }
    return w;
  }

  const MC_COLORS = {
    '0': '#000000', '1': '#0000aa', '2': '#00aa00', '3': '#00aaaa', '4': '#aa0000',
    '5': '#aa00aa', '6': '#ffaa00', '7': '#aaaaaa', '8': '#555555', '9': '#5555ff',
    'a': '#55ff55', 'b': '#55ffff', 'c': '#ff5555', 'd': '#ff55ff', 'e': '#ffff55', 'f': '#ffffff'
  };

  function colorSwatch(text) {
    if (!text || text.length > 120) return null;
    if (!/&[0-9a-fk-or]/i.test(text)) return null;
    const stops = [];
    let pos = 0;
    for (const seg of text.split(/(?=&[0-9a-fk-or])/i)) {
      const m = /^&([0-9a-f])/i.exec(seg);
      if (m) {
        const color = MC_COLORS[m[1].toLowerCase()];
        const step = 100 / Math.max(text.length, 1);
        if (color) stops.push(`${color} ${pos}%`, `${color} ${Math.min(pos + step * seg.length, 100)}%`);
      }
      pos += step2(text, seg);
    }
    if (!stops.length) stops.push('#888 0%', '#888 100%');
    return h('span', {
      class: 'swatch',
      title: text,
      style: { background: `linear-gradient(90deg, ${stops.join(',')})` }
    });

    function step2(all, seg) { return (seg.length / all.length) * 100; }
  }

  function setPending(node, value, type) {
    const same = node.type === 'null'
      ? String(value) === ''
      : String(value) === String(node.value ?? '');
    if (same) state.pending.delete(node.pathKey);
    else {
      state.pending.set(node.pathKey, {
        path: node.path,
        type: type || node.type,
        value,
        raw: node.raw,
        label: node.key || '(值)'
      });
    }
    const uid = [...(state.uidKeys || new Map()).entries()].find(([, k]) => k === node.pathKey)?.[0];
    if (uid !== undefined) refreshRow(uid);
    else updateSaveBar();
  }

  function updateSaveBar() {
    const n = state.pending.size;
    $('#changeCount').textContent = n ? `${n} 项待保存` : '没有待保存的改动';
    $('#pendingDot').classList.toggle('on', n > 0);
    const blocked = state.lockBusy || !!state.external;
    const btn = $('#btnSave');
    btn.disabled = !n || state.bus || blocked;
    btn.title = blocked
      ? (state.external ? '磁盘上的文件已被改动，请先处理顶部提示' : '其他人正在编辑这个文件，请先接管')
      : '保存到文件（Ctrl+S）';
    $('#btnDiscard').disabled = !n;
  }

  /* ============================ 保存 ============================ */
  async function save(force = false) {
    force = force === true; // 防止把事件对象当成 force 传进来
    if (!state.doc || !state.pending.size || state.bus) return;
    if (!force && (state.lockBusy || state.external)) {
      toast('warn', '保存已暂停', state.external
        ? '磁盘上的文件已被改动。请先在顶部横幅里选择「重新加载」或「强制覆盖」。'
        : `「${state.lockHolder?.clientName || '其他人'}」正在编辑这个文件，请先接管编辑。`);
      return;
    }
    const edits = [...state.pending.values()].map(p => ({
      path: p.path, type: p.type, value: p.value, expectedRaw: p.raw
    }));
    state.bus = true;
    const btn = $('#btnSave');
    const original = btn.innerHTML;
    btn.textContent = '保存中…';
    updateSaveBar();
    try {
      const res = await api('/api/config/save', {
        method: 'POST',
        body: JSON.stringify({
          file: state.doc.file,
          baseHash: state.doc.hash,
          force,
          clientId: state.clientId,
          edits
        })
      });
      state.doc = res.config;
      state.pending.clear();
      state.external = null;
      state.lockBusy = false;
      state.lockHolder = null;
      renderDoc();
      renderSidebar();
      renderAlertBar();
      updateSaveBar();
      await claimFile(state.doc.file);
      toast('ok', `已保存 ${res.applied.length} 项改动`,
        (res.warnings || []).join('\n') || '注释、缩进与键顺序都保持原样，插件可直接读取。');
    } catch (e) {
      if (e.status === 409) {
        const choice = await conflictDialog(e.message);
        if (choice === 'reload') await openFile(state.doc.file, { force: true });
        else if (choice === 'force') await saveForced(edits);
      } else if (e.status === 423) {
        state.lockBusy = true;
        renderAlertBar();
        toast('err', '保存被拦截', e.message);
      } else {
        toast('err', '保存失败', e.message);
      }
    } finally {
      state.bus = false;
      btn.innerHTML = original;
      updateSaveBar();
    }
  }

  async function saveForced(edits) {
    try {
      const res = await api('/api/config/save', {
        method: 'POST',
        body: JSON.stringify({
          file: state.doc.file, baseHash: state.doc.hash, force: true,
          clientId: state.clientId, edits
        })
      });
      state.doc = res.config;
      state.pending.clear();
      state.external = null;
      state.lockBusy = false;
      state.lockHolder = null;
      renderDoc(); renderSidebar(); renderAlertBar(); updateSaveBar();
      await claimFile(state.doc.file);
      toast('ok', '已强制覆盖并保存', `共 ${res.applied.length} 项；旧文件已备份到本机备份目录。`);
    } catch (err) {
      toast('err', '覆盖保存失败', err.message);
    }
  }

  function conflictDialog(message) {
    return new Promise((resolve) => {
      let settled = false;
      const done = (v) => { if (settled) return; settled = true; dlg.close(); resolve(v); };
      const dlg = modal({
        title: '文件已被外部修改',
        body: h('div', { style: { whiteSpace: 'pre-wrap', lineHeight: '1.75', color: 'var(--text-dim)', fontSize: '13px' } },
          message + '\n\n重新加载可获得磁盘上的最新内容；强制覆盖会用当前界面上的值覆盖磁盘（旧文件已自动备份）。'),
        footer: [
          h('div', { class: 'spacer' }),
          h('button', { class: 'ghost-btn', onclick: () => done(null) }, '取消'),
          h('button', { class: 'danger-btn', onclick: () => done('force') }, '强制覆盖'),
          h('button', { class: 'primary-btn', onclick: () => done('reload') }, '重新加载')
        ]
      });
      dlg.mask.addEventListener('mousedown', (e) => { if (e.target === dlg.mask) done(null); });
    });
  }

  /* ============================ 源码 ============================ */
  function viewRaw() {
    if (!state.doc) return;
    const pre = h('pre', { class: 'raw' }, state.doc.text);
    const m = modal({
      title: '源文件',
      sub: state.doc.file,
      wide: true,
      body: pre,
      footer: [
        h('div', { class: 'spacer' }),
        h('button', { class: 'ghost-btn', onclick: () => copyText(state.doc.text) }, '复制全文'),
        h('button', { class: 'primary-btn', onclick: () => m.close() }, '关闭')
      ]
    });
  }

  /* ============================ 初始化 ============================ */
  function bindGlobal() {
    $('#btnSettings').addEventListener('click', openSettings);
    $('#serverChip').addEventListener('click', openSettings);
    $('#btnTheme').addEventListener('click', toggleTheme);
    $('#btnSidebar').addEventListener('click', () => applySidebar(!state.sidebarCollapsed));
    $('#btnCollapse').addEventListener('click', () => applySidebar(true));
    $('#btnLogout').addEventListener('click', async () => {
      if (state.pending.size) {
        const ok = await confirmDialog({
          title: '退出登录？',
          message: '还有未保存的改动，退出后会丢失。',
          confirmText: '仍然退出',
          danger: true
        });
        if (!ok) return;
      }
      try { await api('/api/auth/logout', { method: 'POST' }); } catch { /* 会话可能已过期 */ }
      location.href = '/login.html';
    });
    $('#btnRefresh').addEventListener('click', async () => {
      try {
        await loadSettings();
        await loadCatalog();
        if (state.doc) await openFile(state.doc.file, { force: true });
      } catch (e) { toast('err', '刷新失败', e.message); }
    });
    $('#btnSave').addEventListener('click', () => save());
    $('#btnDiscard').addEventListener('click', async () => {
      if (!state.pending.size) return;
      const ok = await confirmDialog({
        title: '放弃改动？',
        message: `${state.pending.size} 项改动将恢复成文件里的值。`,
        confirmText: '放弃改动',
        danger: true
      });
      if (!ok) return;
      state.pending.clear();
      renderDoc();
      renderSidebar();
      updateSaveBar();
    });
    $('#btnReload').addEventListener('click', async () => {
      if (state.pending.size) {
        const ok = await confirmDialog({
          title: '重新加载？',
          message: '磁盘上的最新内容会覆盖当前界面，未保存的改动会丢失。',
          confirmText: '重新加载',
          danger: true
        });
        if (!ok) return;
      }
      await openFile(state.doc.file, { force: true });
    });
    $('#btnRaw').addEventListener('click', viewRaw);
    $('#searchInput').addEventListener('input', (e) => {
      state.search = e.target.value;
      renderSidebar();
    });
    document.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        save();
      }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'b') {
        e.preventDefault();
        applySidebar(!state.sidebarCollapsed);
      }
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'l') {
        e.preventDefault();
        toggleTheme();
      }
    });
    window.addEventListener('beforeunload', (e) => {
      releaseFile();
      if (state.pending.size) { e.preventDefault(); e.returnValue = ''; }
    });
    window.addEventListener('hashchange', () => {
      const m = location.hash.match(/^#\/f\/(.+)$/);
      if (!m) return;
      const rel = decodeURIComponent(m[1]);
      if (state.doc?.file !== rel) openFile(rel);
    });
  }

  function fmtBytes(n) {
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(1) + ' MB';
  }

  function fmtTime(v) {
    if (!v) return '—';
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return String(v);
    const pad = (x) => String(x).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }

  window.addEventListener('error', (e) => showBootError(`${e.message}\n${e.filename}:${e.lineno}`));
  window.addEventListener('unhandledrejection', (e) => showBootError(e.reason?.stack || String(e.reason)));

  // 调试/自检入口（也方便在控制台里排查）
  window.__mcpanel = { state, api, save, openFile, renderDoc, renderAlertBar, pollWatch, toast, applyTheme, applySidebar, updateSaveBar };

  (async function boot() {
    initIdentity();
    applyTheme(document.documentElement.dataset.theme || 'dark', false);
    const forcedTheme = new URLSearchParams(location.search).get('theme');
    if (forcedTheme === 'light' || forcedTheme === 'dark') applyTheme(forcedTheme, true);
    applySidebar(localStorage.getItem(LS.sidebar) === 'collapsed', false);
    bindGlobal();
    try {
      const session = await api('/api/auth/session');
      if (session.setupRequired && !session.authenticated) {
        location.href = '/setup.html';
        return;
      }
      if (!session.authenticated) {
        location.href = '/login.html?next=' + encodeURIComponent(location.pathname + location.hash);
        return;
      }
      state.user = session.username || '';
      const userLabel = $('#userLabel');
      if (userLabel && state.user) {
        userLabel.hidden = false;
        $('#userName').textContent = state.user;
      }

      await loadSettings();
      await loadCatalog();
      const m = location.hash.match(/^#\/f\/(.+)$/);
      if (m) await openFile(decodeURIComponent(m[1]), { force: true });
      updateSaveBar();
      if (!state.settings.pluginsPath) {
        setTimeout(() => openSettings(), 350);
      }
      if (new URLSearchParams(location.search).has('selftest')) {
        const s = document.createElement('script');
        s.src = 'js/selftest.js?v=20261020b';
        document.body.append(s);
      }
    } catch (e) {
      showBootError('初始化失败：' + (e.stack || e.message));
      renderSidebar();
    }
  })();
})();
