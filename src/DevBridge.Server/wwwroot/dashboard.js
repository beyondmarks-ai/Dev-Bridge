const $ = id => document.getElementById(id);
const state = { id: null, token: null, timer: null };

$('session-form').onsubmit = async event => {
  event.preventDefault();
  const response = await fetch('/api/sessions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-DevBridge-Access-Key': $('access-key').value },
    body: JSON.stringify({ projectName: $('project-name').value, appName: $('app-name').value, packageName: $('package-name').value })
  });
  if (!response.ok) {
    let data = {};
    try { data = await response.json(); } catch { }
    alert(response.status === 401 ? 'Invalid developer access key.' : data.error || 'Could not create session.');
    return;
  }
  const data = await response.json();
  state.id = data.sessionId;
  state.token = data.dashboardToken;
  $('qr').innerHTML = data.qrSvg;
  $('pair-link').href = data.pairingUrl;
  $('start').classList.add('hidden');
  $('workspace').classList.remove('hidden');
  await refresh();
  state.timer = setInterval(refresh, 1500);
};

$('apk').onchange = () => {
  const file = $('apk').files[0];
  $('apk-name').textContent = file ? `${file.name} · ${fileSize(file.size)}` : 'No APK selected';
};

$('build-form').onsubmit = event => {
  event.preventDefault();
  const file = $('apk').files[0];
  if (!file || !state.id) return;
  const body = new FormData();
  body.append('apk', file);
  body.append('versionName', $('version-name').value);
  body.append('versionCode', $('version-code').value);
  const button = $('upload-button');
  const progress = $('upload-progress');
  const bar = progress.querySelector('span');
  const request = new XMLHttpRequest();
  button.disabled = true;
  button.textContent = 'Uploading…';
  progress.classList.remove('hidden');
  bar.style.width = '0%';
  message(`Preparing ${fileSize(file.size)} upload…`);
  request.open('POST', `/api/sessions/${state.id}/builds`);
  request.setRequestHeader('X-Dashboard-Token', state.token);
  request.timeout = 10 * 60 * 1000;
  request.upload.onprogress = upload => {
    if (!upload.lengthComputable) return;
    const percent = Math.min(100, Math.round(upload.loaded / upload.total * 100));
    bar.style.width = `${percent}%`;
    message(percent < 100 ? `Uploading ${percent}% · ${fileSize(upload.loaded)} of ${fileSize(upload.total)}` : 'Upload complete. Verifying SHA-256…');
  };
  request.onload = () => {
    let data = {};
    try { data = JSON.parse(request.responseText || '{}'); } catch { }
    const ok = request.status >= 200 && request.status < 300;
    message(ok ? `Published ${data.versionName}. SHA-256 ${data.sha256.slice(0, 16)}…` : data.error || `Upload rejected (HTTP ${request.status}).`, !ok);
    if (ok) {
      bar.style.width = '100%';
      $('version-code').value = Number($('version-code').value) + 1;
      refresh();
    }
    finishUpload(button, progress);
  };
  request.onerror = () => { message('Upload connection failed. Check internet access and retry.', true); finishUpload(button, progress); };
  request.ontimeout = () => { message('Upload timed out after 10 minutes.', true); finishUpload(button, progress); };
  request.send(body);
};

async function refresh() {
  if (!state.id) return;
  const response = await fetch(`/api/sessions/${state.id}/dashboard`, { headers: { 'X-Dashboard-Token': state.token } });
  if (!response.ok) return;
  const data = await response.json();
  $('device-count').textContent = data.devices.length;
  $('build-count').textContent = data.builds.length;
  $('issue-count').textContent = data.issues.length;
  $('crash-count').textContent = data.summary.crashes;
  $('anr-count').textContent = data.summary.anrs;
  $('install-failure-count').textContent = data.summary.installFailures;
  $('event-count').textContent = `${data.events.length} events`;
  $('pair-status').textContent = data.pairingConsumed ? 'Paired' : 'Waiting';
  $('pair-status').classList.toggle('ok', data.pairingConsumed);
  $('session-expiry').textContent = `Expires ${time(data.expiresAt)}`;
  renderDevices(data.devices);
  renderIssues(data.issues);
  renderEvents(data.events);
}

function renderDevices(devices) {
  $('devices').innerHTML = devices.length ? devices.map(device => {
    const screen = device.context?.screenWidthDp && device.context?.screenHeightDp ? `${device.context.screenWidthDp}×${device.context.screenHeightDp} dp` : '';
    return `<div class="device"><div class="avatar">${esc(device.name[0])}</div><div><strong>${esc(device.name)}</strong><small>${esc(device.platform)} ${esc(device.platformVersion || '')}${screen ? ` · ${esc(screen)}` : ''}</small></div><span class="online">Online</span></div>`;
  }).join('') : '<div class="empty">No device paired yet.</div>';
}

function renderIssues(issues) {
  $('issues').innerHTML = issues.length ? issues.map(issue => `<button class="issue ${esc(issue.level)}" type="button" onclick="focusEvent('${esc(issue.fingerprint)}')"><span class="severity">${esc(issue.level)}</span><div><strong>${esc(issue.message)}</strong><small>${esc(issue.category)} · ${issue.count} occurrence${issue.count === 1 ? '' : 's'} · ${issue.affectedDevices} device${issue.affectedDevices === 1 ? '' : 's'}</small></div><time>${time(issue.lastSeenAt)}</time></button>`).join('') : '<div class="empty success-empty">No diagnostic issues reported.</div>';
}

function renderEvents(events) {
  $('events').innerHTML = events.length ? events.map(item => `<div class="event ${esc(item.level)}" data-fingerprint="${esc(item.fingerprint || '')}"><i></i><div><header><strong>${esc(item.eventType || item.category)}</strong><small>${esc(item.deviceName)} · ${time(item.occurredAt)}</small></header><p>${esc(item.message)}</p>${item.screen ? `<span class="context-chip">${esc(item.screen)}</span>` : ''}${metrics(item.metrics)}${item.details ? `<pre>${esc(item.details)}</pre>` : ''}${item.stackTrace ? `<details><summary>Stack trace</summary><pre>${esc(item.stackTrace)}</pre></details>` : ''}</div></div>`).join('') : '<div class="empty">Events will appear here.</div>';
}

function focusEvent(fingerprint) {
  const target = [...document.querySelectorAll('[data-fingerprint]')].find(item => item.dataset.fingerprint === fingerprint);
  if (!target) return;
  target.scrollIntoView({ behavior: 'smooth', block: 'center' });
  target.classList.add('highlight');
  setTimeout(() => target.classList.remove('highlight'), 1800);
}

function metrics(values) {
  const entries = Object.entries(values || {});
  return entries.length ? `<div class="metric-chips">${entries.map(([key, value]) => `<span>${esc(key)} <b>${Number(value).toFixed(1)}</b></span>`).join('')}</div>` : '';
}

function message(text, error = false) { $('upload-message').textContent = text; $('upload-message').classList.remove('hidden'); $('upload-message').classList.toggle('error', error); }
function finishUpload(button, progress) { button.disabled = false; button.textContent = 'Upload APK'; setTimeout(() => progress.classList.add('hidden'), 1200); }
function time(value) { return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
function fileSize(bytes) { return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.ceil(bytes / 1024))} KB`; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character])); }
