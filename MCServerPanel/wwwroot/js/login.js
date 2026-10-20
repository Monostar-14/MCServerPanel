(() => {
  const form = document.getElementById('loginForm');
  const user = document.getElementById('username');
  const pass = document.getElementById('password');
  const btn = document.getElementById('submitBtn');
  const errorBox = document.getElementById('error');
  const notice = document.getElementById('notice');

  function showError(msg) {
    errorBox.hidden = false;
    errorBox.textContent = msg;
  }
  function hideError() {
    errorBox.hidden = true;
    errorBox.textContent = '';
  }
  function showNotice(msg) {
    notice.hidden = false;
    notice.textContent = msg;
  }
  function nextUrl() {
    const raw = new URLSearchParams(location.search).get('next');
    if (!raw) return null;
    if (!raw.startsWith('/') || raw.startsWith('//')) return null;
    return raw;
  }

  async function post(url, body) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-MCSP-CSRF': '1' },
      body: JSON.stringify(body)
    });
    let data = {};
    try { data = await res.json(); } catch { /* 非 JSON 响应 */ }
    return { res, data };
  }

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    hideError();
    if (!user.value.trim() || !pass.value) {
      showError('请输入用户名和密码。');
      return;
    }
    btn.disabled = true;
    btn.textContent = '登录中…';
    try {
      const { res, data } = await post('/api/auth/login', {
        username: user.value.trim(),
        password: pass.value
      });
      if (!res.ok) {
        showError(data.error || `登录失败（HTTP ${res.status}）`);
        if (res.status === 429 && data.retryAfter) showError(`${data.error}（${data.retryAfter} 秒）`);
        return;
      }
      location.href = nextUrl() || '/';
    } catch (err) {
      showError('无法连接面板：' + err.message);
    } finally {
      btn.disabled = false;
      btn.textContent = '登录';
    }
  });

  (async () => {
    try {
      const res = await fetch('/api/auth/session');
      const data = await res.json();
      if (data.setupRequired) {
        location.href = '/setup.html';
        return;
      }
      if (data.authenticated) {
        location.href = nextUrl() || '/';
        return;
      }
      user.focus();
    } catch (err) {
      showNotice('无法连接面板服务：' + err.message);
    }
  })();
})();
