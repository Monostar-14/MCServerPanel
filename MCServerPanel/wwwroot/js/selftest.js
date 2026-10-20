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

    // 4) 保存并回读
    await api.save();
    await wait(400);
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
