const $ = id => document.getElementById(id);
const state = { id: null, token: null, timer: null, dashboard: null, runnerCommand: null };

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

$('generate-tests-form').onsubmit = async event => {
  event.preventDefault();
  const button = $('generate-tests-button');
  button.disabled = true;
  button.textContent = 'Designing tests…';
  testMessage('Azure AI is creating observable happy-path and risk-focused cases.');
  try {
    const response = await fetch(`/api/sessions/${state.id}/test-cases/generate`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Dashboard-Token': state.token },
      body: JSON.stringify({ feature: $('test-feature').value, count: Number($('test-count').value) })
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.detail || data.error || 'AI test generation failed.');
    testMessage(`Created ${data.testCases.length} executable test cases.`);
    await refresh();
  } catch (error) { testMessage(error.message, true); }
  finally { button.disabled = false; button.textContent = 'Generate with Azure AI'; }
};

$('download-report').onclick = async () => {
  const response = await fetch(`/api/sessions/${state.id}/report.md`, { headers: { 'X-Dashboard-Token': state.token } });
  if (!response.ok) return testMessage('The report could not be exported.', true);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement('a');
  link.href = url;
  link.download = `devbridge-${state.id}-report.md`;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
};

$('enroll-runner').onclick = async () => {
  const button = $('enroll-runner');
  button.disabled = true;
  try {
    const response = await fetch(`/api/sessions/${state.id}/automation/enrollment`, {
      method: 'POST', headers: { 'X-Dashboard-Token': state.token }
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || 'Could not create runner enrollment.');
    const baseUrl = new URL(data.exchangeUrl).origin;
    state.runnerCommand = `$env:DEVBRIDGE_URL = '${baseUrl}'\n$env:DEVBRIDGE_ENROLLMENT_TOKEN = '${data.enrollmentToken}'\n$env:ANDROID_DEVICE_NAME = 'emulator-5554'\nnpm start`;
    $('runner-command').textContent = state.runnerCommand;
    $('runner-enrollment').classList.remove('hidden');
    testMessage(`Runner enrollment expires ${new Date(data.expiresAt).toLocaleTimeString()}.`);
  } catch (error) { testMessage(error.message, true); }
  finally { button.disabled = false; }
};

$('copy-runner-command').onclick = async () => {
  if (!state.runnerCommand) return;
  await navigator.clipboard.writeText(state.runnerCommand);
  testMessage('Runner command copied. Treat the one-use token as a secret.');
};

async function refresh() {
  if (!state.id) return;
  const response = await fetch(`/api/sessions/${state.id}/dashboard`, { headers: { 'X-Dashboard-Token': state.token } });
  if (!response.ok) return;
  const data = await response.json();
  state.dashboard = data;
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
  renderTesting(data);
  renderIssues(data.issues);
  renderEvents(data.events);
}

function renderTesting(data) {
  const quality = data.quality || {};
  const verdict = quality.verdict || 'incomplete';
  $('quality-verdict').textContent = verdict;
  $('quality-verdict').className = `pill ${esc(verdict)}`;
  $('quality-summary').textContent = quality.total
    ? `${quality.executed} of ${quality.total} runs completed · ${quality.passed} passed · ${quality.failed} failed · ${quality.blocked} blocked · ${quality.passRate}% pass rate`
    : 'Generate test cases, schedule them to a paired device, and collect evidence-backed results.';
  const devices = data.devices || [];
  const builds = data.builds || [];
  $('test-cases').innerHTML = data.testCases?.length ? data.testCases.map(item => {
    const deviceOptions = devices.map(device => `<option value="${esc(device.id)}">${esc(device.name)} · ${esc(device.platform)}</option>`).join('');
    const buildOptions = `<option value="">Latest / no build</option>${builds.map(build => `<option value="${esc(build.buildId)}">${esc(build.versionName)} (${build.versionCode})</option>`).join('')}`;
    const controls = devices.length ? `<div class="schedule"><select id="device-${item.id}" aria-label="Test device">${deviceOptions}</select><select id="build-${item.id}" aria-label="Build">${buildOptions}</select><button type="button" onclick="scheduleRun('${esc(item.id)}', this)">Send to device</button></div>` : '<small>Pair a device before scheduling.</small>';
    const automation = item.automationSteps?.length ? ` · ${item.automationSteps.length} automatic steps` : ' · guided only';
    return `<article class="test-card"><header><h4>${esc(item.title)}</h4><span class="priority">${esc(item.priority)}${item.aiGenerated ? ' · AI' : ''}</span></header><p>${esc(item.instructions)}</p><small>Expected: ${esc(item.expectedResult)}${automation}</small>${controls}</article>`;
  }).join('') : '<div class="empty">No test cases yet.</div>';
  $('test-runs').innerHTML = data.testRuns?.length ? data.testRuns.map(run => `<article class="test-card"><header><h4>${esc(run.testCase?.title || 'Test case')}</h4><span class="run-status ${esc(run.status)}">${esc(run.status)}</span></header><small>${esc(deviceName(run.deviceId, devices))}${run.testCase?.expectedResult ? ` · Expected: ${esc(run.testCase.expectedResult)}` : ''}</small>${run.totalSteps ? `<span class="automation-progress">Step ${Number(run.currentStep)} of ${Number(run.totalSteps)}${run.lastStepMessage ? ` · ${esc(run.lastStepMessage)}` : ''}</span>` : ''}${run.notes ? `<p>Tester: ${esc(run.notes)}</p>` : ''}${run.failureFingerprint ? `<button class="secondary" type="button" onclick="focusEvent('${esc(run.failureFingerprint)}')">Open failure evidence</button>` : ''}</article>`).join('') : '<div class="empty">No test runs scheduled.</div>';
}

async function scheduleRun(testCaseId, button) {
  const deviceId = $(`device-${testCaseId}`).value;
  const buildId = $(`build-${testCaseId}`).value || null;
  button.disabled = true;
  try {
    const response = await fetch(`/api/sessions/${state.id}/test-runs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Dashboard-Token': state.token },
      body: JSON.stringify({ testCaseId, deviceId, buildId })
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || 'Could not schedule the test.');
    testMessage('Test sent to the companion app.');
    await refresh();
  } catch (error) { testMessage(error.message, true); }
  finally { button.disabled = false; }
}

function deviceName(id, devices) { return devices.find(device => device.id === id)?.name || 'Assigned device'; }
function testMessage(text, error = false) { $('test-message').textContent = text; $('test-message').classList.remove('hidden'); $('test-message').classList.toggle('error', error); }

function renderDevices(devices) {
  $('devices').innerHTML = devices.length ? devices.map(device => {
    const screen = device.context?.screenWidthDp && device.context?.screenHeightDp ? `${device.context.screenWidthDp}×${device.context.screenHeightDp} dp` : '';
    return `<div class="device"><div class="avatar">${esc(device.name[0])}</div><div><strong>${esc(device.name)}</strong><small>${esc(device.platform)} ${esc(device.platformVersion || '')}${screen ? ` · ${esc(screen)}` : ''}</small></div><span class="online">Online</span></div>`;
  }).join('') : '<div class="empty">No device paired yet.</div>';
}

function renderIssues(issues) {
  $('issues').innerHTML = issues.length ? issues.map(issue => `<div class="issue-wrap"><button class="issue ${esc(issue.level)}" type="button" onclick="focusEvent('${esc(issue.fingerprint)}')"><span class="severity">${esc(issue.level)}</span><div><strong>${esc(issue.message)}</strong><small>${esc(issue.category)} · ${issue.count} occurrence${issue.count === 1 ? '' : 's'} · ${issue.affectedDevices} device${issue.affectedDevices === 1 ? '' : 's'}</small></div><time>${time(issue.lastSeenAt)}</time></button><div class="ai-actions"><button class="ai-button" type="button" onclick="analyzeIssue('${esc(issue.fingerprint)}', this, ${Boolean(issue.analysis)})">${issue.analysis ? 'Refresh AI diagnosis' : 'Analyze with AI'}</button></div><div id="analysis-${esc(issue.fingerprint)}">${issue.analysis ? analysisCard(issue.analysis) : ''}</div></div>`).join('') : '<div class="empty success-empty">No diagnostic issues reported.</div>';
}

async function analyzeIssue(fingerprint, button, refresh) {
  button.disabled = true;
  button.textContent = 'Analyzing evidence…';
  const target = $(`analysis-${fingerprint}`);
  try {
    const response = await fetch(`/api/sessions/${state.id}/issues/${encodeURIComponent(fingerprint)}/analysis?refresh=${refresh}`, {
      method: 'POST', headers: { 'X-Dashboard-Token': state.token }
    });
    let data = {};
    try { data = await response.json(); } catch { }
    if (!response.ok) throw new Error(data.detail || data.error || 'AI analysis is unavailable.');
    target.innerHTML = analysisCard(data);
    button.textContent = 'Refresh AI diagnosis';
    button.onclick = () => analyzeIssue(fingerprint, button, true);
  } catch (error) {
    target.innerHTML = `<div class="message error">${esc(error.message)}</div>`;
    button.textContent = 'Retry AI analysis';
  } finally { button.disabled = false; }
}

function analysisCard(value) {
  return `<article class="ai-analysis"><header><div><span class="eyebrow">AZURE AI DIAGNOSIS</span><h3>${esc(value.summary)}</h3></div><span class="confidence">${Number(value.confidence)}% confidence</span></header><dl><div><dt>User impact</dt><dd>${esc(value.userImpact)}</dd></div><div><dt>Probable cause</dt><dd>${esc(value.probableCause)}</dd></div><div><dt>Problem location</dt><dd>${esc(value.problemLocation)}</dd></div><div><dt>Suggested solution</dt><dd>${esc(value.suggestedFix)}</dd></div></dl>${analysisList('Evidence', value.evidence)}${analysisList('Reproduction', value.reproductionSteps)}${analysisList('Limitations', value.limitations)}<footer>${esc(value.model)} · ${time(value.analyzedAt)}</footer></article>`;
}

function analysisList(title, items) {
  return items?.length ? `<section><strong>${esc(title)}</strong><ul>${items.map(item => `<li>${esc(item)}</li>`).join('')}</ul></section>` : '';
}

function renderEvents(events) {
  $('events').innerHTML = events.length ? events.map(item => `<div class="event ${esc(item.level)}" data-fingerprint="${esc(item.fingerprint || '')}"><i></i><div><header><strong>${esc(item.eventType || item.category)}</strong><small>${esc(item.deviceName)} · ${time(item.occurredAt)}</small></header><p>${esc(item.message)}</p>${item.screen ? `<span class="context-chip">${esc(item.screen)}</span>` : ''}${metrics(item.metrics)}${evidenceContext(item.context)}${attachment(item.context, item.eventType)}${item.details ? `<pre>${esc(item.details)}</pre>` : ''}${item.stackTrace ? `<details><summary>Stack trace</summary><pre>${esc(item.stackTrace)}</pre></details>` : ''}</div></div>`).join('') : '<div class="empty">Events will appear here.</div>';
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

function evidenceContext(context) {
  if (!context) return '';
  const values = [];
  const breadcrumbs = Object.keys(context).filter(key => key.startsWith('breadcrumb.')).length;
  if (breadcrumbs) values.push(`${breadcrumbs} pre-failure breadcrumbs`);
  for (const key of ['lastAction', 'networkStatus', 'captureId']) if (context[key]) values.push(`${key}: ${context[key]}`);
  return values.map(value => `<span class="context-chip">${esc(value)}</span>`).join('');
}

function attachment(context, eventType) {
  if (!context?.attachmentId) return '';
  const label = context.contentType?.startsWith('video/') ? 'recording' : eventType?.includes('frame') ? 'recording frame' : 'screenshot';
  return `<button class="attachment-button" type="button" onclick="openAttachment('${esc(context.attachmentId)}')">Open ${esc(label)}</button>`;
}

async function openAttachment(id) {
  const response = await fetch(`/api/sessions/${state.id}/attachments/${id}`, { headers: { 'X-Dashboard-Token': state.token } });
  if (!response.ok) return alert('Attachment is unavailable or the session expired.');
  const url = URL.createObjectURL(await response.blob());
  window.open(url, '_blank', 'noopener');
  setTimeout(() => URL.revokeObjectURL(url), 60000);
}

function message(text, error = false) { $('upload-message').textContent = text; $('upload-message').classList.remove('hidden'); $('upload-message').classList.toggle('error', error); }
function finishUpload(button, progress) { button.disabled = false; button.textContent = 'Upload APK'; setTimeout(() => progress.classList.add('hidden'), 1200); }
function time(value) { return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); }
function fileSize(bytes) { return bytes >= 1048576 ? `${(bytes / 1048576).toFixed(1)} MB` : `${Math.max(1, Math.ceil(bytes / 1024))} KB`; }
function esc(value) { return String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[character])); }
