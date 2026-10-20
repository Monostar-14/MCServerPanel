/* 面板自检：仅当 URL 带 ?selftest=1 时加载。
   会在页面上跑一遍「滑块 / 步进 / 文本框 → 保存 → 校验注释不变」，结果写入 <pre id="selftest">。 */
(async function selftest() {
  const results = [];
  const check = (name, pass, info) => results.push({ name, pass: !!pass, info: info === undefined ? '' : String(info) });
  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const api = window.__mcpanel;
  const q = (p) => document.querySelector(`[data-path="${p}"]`);

  const finish = (fatal) => {
    if (fatal) check('自检未抛异常', false, fatal);
    const pass = results.every((r) => r.pass);
    document.title = pass ? 'SELFTEST_PASS' : 'SELFTEST_FAIL';
    const pre = document.createElement('pre');
    pre.id = 'selftest';
    pre.setAttribute('data-result', pass ? 'pass' : 'fail');
    pre.textContent = results
      .map((r) => `${r.pass ? 'PASS' : 'FAIL'}  ${r.name}${r.info ? '  [' + r.info + ']' : ''}`)
      .join('\n');
    document.body.appendChild(pre);
  };

  try {
    for (let i = 0; i < 80 && !api.state.doc; i++) await wait(100);
    const doc = api.state.doc;
    check('配置文件已加载', doc && !doc.parseError, doc && doc.file);
    if (!doc || doc.parseError) return finish();

    // 自检开始时先接管，排除上一次运行/其他测试残留的编辑占用
    await api.api('/api/config/claim', {
      method: 'POST',
      body: JSON.stringify({ file: doc.file, clientId: api.state.clientId, clientName: api.state.clientName, takeover: true })
    });
    api.state.lockBusy = false;
    api.state.lockHolder = null;
    api.renderAlertBar();
    api.updateSaveBar?.();
    await wait(50);

    const beforeText = doc.text;
    const commentsBefore = beforeText.split('\n').filter((l) => l.trim().startsWith('#'));
    const commentCountBefore = commentsBefore.length;

    // 1) 布尔 -> 滑块
    const boolRow = q('UpdateChecker/Enabled');
    check('布尔值渲染为滑块', boolRow && !!boolRow.querySelector('.switch'));
    const sw = boolRow.querySelector('.switch');
    const wasOn = sw.getAttribute('aria-checked') === 'true';
    sw.click();
    await wait(30);
    check('点击滑块切换状态', sw.getAttribute('aria-checked') === String(!wasOn));
    check('改动进入待保存', api.state.pending.size === 1, 'pending=' + api.state.pending.size);

    // 2) 数字 -> 步进输入框
    const numRow = q('UpdateChecker/AnnouncementThrottleMinutes');
    const numInput = numRow && numRow.querySelector('.num-input');
    check('数字渲染为步进输入框', !!numInput && !!numRow.querySelectorAll('.step-btn').length);
    const numBefore = Number(numInput.value);
    numRow.querySelectorAll('.step-btn')[1].click();
    await wait(30);
    check('步进 + 生效', Number(numInput.value) === numBefore + 1, `${numBefore} -> ${numInput.value}`);

    // 3) 字符串 -> 文本框
    const textRow = q('UpdateChecker/AdminPermission');
    const textInput = textRow && textRow.querySelector('.text-input');
    check('字符串渲染为文本框', !!textInput);
    textInput.value = 'snowygems.selftest';
    textInput.dispatchEvent(new Event('input', { bubbles: true }));
    await wait(30);
    check('三项改动都在待保存队列', api.state.pending.size === 3, 'pending=' + api.state.pending.size);

    // 4) 点真实的「保存到文件」按钮（覆盖事件参数 / force 序列化 / 请求体绑定）
    const hashBeforeSave = api.state.doc.hash;
    document.getElementById('btnSave').click();
    let savedByButton = false;
    for (let i = 0; i < 60; i++) {
      await wait(100);
      if (api.state.pending.size === 0 && api.state.doc.hash !== hashBeforeSave) { savedByButton = true; break; }
    }
    check('点击「保存到文件」按钮可写盘', savedByButton, 'pending=' + api.state.pending.size);
    check('保存没有弹出错误提示', document.querySelectorAll('#toastRoot .toast.err').length === 0);
    await wait(150);
    check('保存后待保存清空', api.state.pending.size === 0, 'pending=' + api.state.pending.size);

    const fresh = await api.api('/api/config?file=' + encodeURIComponent(doc.file));
    const get = (path) => path.split('/').reduce((n, k) => (n && n.children ? n.children.find((c) => c.key === k) : null), fresh.root);
    check('布尔值已写回', get('UpdateChecker/Enabled') && get('UpdateChecker/Enabled').value === !wasOn,
      String(get('UpdateChecker/Enabled')?.value));
    check('数字已写回', get('UpdateChecker/AnnouncementThrottleMinutes') && Number(get('UpdateChecker/AnnouncementThrottleMinutes').value) === numBefore + 1,
      String(get('UpdateChecker/AnnouncementThrottleMinutes')?.value));
    check('字符串已写回', get('UpdateChecker/AdminPermission') && get('UpdateChecker/AdminPermission').value === 'snowygems.selftest',
      get('UpdateChecker/AdminPermission')?.value);

    const afterText = fresh.text;
    const commentsAfter = afterText.split('\n').filter((l) => l.trim().startsWith('#'));
    check('注释行数不变', commentsAfter.length === commentCountBefore, `${commentCountBefore} -> ${commentsAfter.length}`);
    check('注释内容逐条一致', JSON.stringify(commentsBefore) === JSON.stringify(commentsAfter));
    check('行数不变', afterText.split('\n').length === beforeText.split('\n').length,
      `${beforeText.split('\n').length} -> ${afterText.split('\n').length}`);
    check('未改动的行保持原样', diffLines(beforeText, afterText) <= 3, 'changed=' + diffLines(beforeText, afterText));
    check('其他键仍在原位', afterText.includes('tagged-blocks:') && afterText.includes('- NETHERITE_BLOCK'));
    check('版本检测注释块保持完整', afterText.includes('#  版本更新检测 & 公告'));

    // ---- 主题 / 侧栏折叠 ----
    const themeBefore = document.documentElement.dataset.theme;
    api.applyTheme('light');
    await wait(40);
    const cardBg = getComputedStyle(document.querySelector('.card')).backgroundColor;
    check('亮色主题生效', document.documentElement.dataset.theme === 'light' && /255,\s*255,\s*255/.test(cardBg), cardBg);
    api.applyTheme(themeBefore === 'light' ? 'dark' : themeBefore || 'dark');
    await wait(30);
    check('主题可切回暗色', document.documentElement.dataset.theme !== 'light');
    document.getElementById('btnTheme').click();
    await wait(30);
    check('主题按钮可用', document.documentElement.dataset.theme === 'light');
    api.applyTheme(themeBefore || 'dark');

    document.getElementById('btnSidebar').click();
    await wait(40);
    check('侧栏可折叠', document.documentElement.classList.contains('sidebar-collapsed'));
    document.getElementById('btnSidebar').click();
    await wait(40);
    check('侧栏可展开', !document.documentElement.classList.contains('sidebar-collapsed'));

    // ---- 外部改动横幅（注入模拟状态，验证渲染与保存拦截） ----
    api.state.external = { hash: 'deadbeefcafebabe', size: 1, lastWrite: new Date().toISOString() };
    api.renderAlertBar();
    await wait(30);
    const bar = document.getElementById('alertBar');
    check('外部改动横幅出现', !bar.hidden && bar.textContent.includes('磁盘上被改动'));
    check('外部改动时保存按钮暂停', document.getElementById('btnSave').disabled === true);
    api.state.external = null;
    api.renderAlertBar();
    await wait(20);
    check('处理后可恢复', bar.textContent.includes('磁盘上被改动') === false);

    // ---- 协作占用：另一个客户端 / 第二个人不能同时写 ----
    await api.api('/api/config/release', { method: 'POST', body: JSON.stringify({ clientId: api.state.clientId }) });
    const other = await api.api('/api/config/claim', {
      method: 'POST',
      body: JSON.stringify({ file: doc.file, clientId: 'selftest-other', clientName: '测试同事', takeover: false })
    });
    check('他人可登记编辑占用', other.ok === true);

    const mine = await api.api('/api/config/claim', {
      method: 'POST',
      body: JSON.stringify({ file: doc.file, clientId: api.state.clientId, clientName: api.state.clientName, takeover: false })
    });
    check('同一文件第二人被拦下', mine.ok === false && mine.holder && mine.holder.clientName === '测试同事',
      mine.holder ? mine.holder.clientName : 'no holder');

    let blockedStatus = 0;
    try {
      // 此时「测试同事」持有占用，本客户端保存应被服务端以 423 拒绝
      await api.api('/api/config/save', {
        method: 'POST',
        body: JSON.stringify({
          file: doc.file,
          baseHash: fresh.hash,
          force: false,
          clientId: api.state.clientId,
          edits: [{
            path: ['UpdateChecker', 'AdminPermission'],
            type: 'string',
            value: 'hacked-by-other',
            expectedRaw: get('UpdateChecker/AdminPermission').raw
          }]
        })
      });
    } catch (e) { blockedStatus = e.status; }
    check('占用冲突时保存被 423 拦截', blockedStatus === 423, 'status=' + blockedStatus);

    const afterBlock = await api.api('/api/config?file=' + encodeURIComponent(doc.file));
    const permission = afterBlock.root.children
      .find((c) => c.key === 'UpdateChecker').children
      .find((c) => c.key === 'AdminPermission');
    check('被拦截的写入没有落地', permission.value === 'snowygems.selftest', permission.value);

    const back = await api.api('/api/config/claim', {
      method: 'POST',
      body: JSON.stringify({ file: doc.file, clientId: api.state.clientId, clientName: api.state.clientName, takeover: true })
    });
    check('接管编辑成功', back.ok === true && back.previous && back.previous.clientName === '测试同事');

    finish();
  } catch (e) {
    finish((e && (e.stack || e.message)) || String(e));
  }

  function diffLines(a, b) {
    const x = a.split('\n');
    const y = b.split('\n');
    let n = 0;
    for (let i = 0; i < Math.max(x.length, y.length); i++) if (x[i] !== y[i]) n++;
    return n;
  }
})();
