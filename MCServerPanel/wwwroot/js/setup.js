(() => {
  const form = document.getElementById('setupForm');
  const token = document.getElementById('token');
  const user = document.getElementById('username');
  const pass = document.getElementById('password');
  const pass2 = document.getElementById('password2');
  const btn = document.getElementById('submitBtn');
  const errorBox = document.getElementById('error');

  function showError(msg) {
    errorBox.hidden = false;
    errorBox.textContent = msg;
  }
  function hideError() {
    errorBox.hidden = true;
    errorBox.textContent = '';
  }

  async function post(url, body) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-MCSP-CSRF': '1' },
      body: JSON.stringify(body)
    });
    let data = {};
    try { data = await res.json(); } catch { /* ignore */ }
    return { res, data };
  }

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    hideError();
    if (!token.value.trim()) return showError('请填写控制台里打印的初始化令牌。');
    if (!user.value.trim()) return showError('请填写管理员用户名。');
    if (pass.value.length < 10) return showError('密码至少 10 位。');
    if (pass.value !== pass2.value) return showError('两次输入的密码不一致。');

    btn.disabled = true;
    btn.textContent = '正在初始化…';
    try {
      const { res, data } = await post('/api/auth/setup', {
        token: token.value.trim(),
        username: user.value.trim(),
        password: pass.value
      });
      if (!res.ok) {
        showError(data.error || `初始化失败（HTTP ${res.status}）`);
        return;
      }
      location.href = '/';
    } catch (err) {
      showError('无法连接面板：' + err.message);
    } finally {
      btn.disabled = false;
      btn.textContent = '设置密码并进入';
    }
  });

  (async () => {
    try {
      const res = await fetch('/api/auth/session');
      const data = await res.json();
      if (!data.setupRequired && data.authenticated) location.href = '/';
      token.focus();
    } catch { /* 允许手动重试 */ }
  })();
})();
