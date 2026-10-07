// Integrove TP admin console. Vanilla JS, no build step, no external resources.
// Every value from the API is inserted as text (textContent / text nodes only; no HTML-parsing APIs are used), so
// request-log bodies, subjects and user names can never inject markup. Credentials: the browser sends the cached Basic credentials on same-origin fetches.
(() => {
  'use strict';

  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];

  /** Tiny element factory: h('td', {class: 'x'}, 'text', childNode). */
  function h(tag, attrs, ...children) {
    const element = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (value === false || value == null) continue;
      if (key.startsWith('on')) element.addEventListener(key.slice(2), value);
      else element.setAttribute(key, value === true ? '' : value);
    }
    for (const child of children.flat()) {
      if (child == null || child === false) continue;
      element.append(child.nodeType ? child : document.createTextNode(String(child)));
    }
    return element;
  }

  const fmt = (iso) => (iso || '').replace('T', ' ').replace('Z', '');
  const short = (urn) => (urn || '').split(':').pop();

  // ---- API -------------------------------------------------------------------------------------

  async function api(path, options = {}) {
    const init = { method: options.method || 'GET', headers: { Accept: 'application/json' } };
    if (options.body !== undefined) {
      init.headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(options.body);
    }
    const response = await fetch('/admin/api' + path, init);
    const text = await response.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }
    if (!response.ok) throw new ApiError(response.status, data);
    return data;
  }

  class ApiError extends Error {
    constructor(status, data) {
      const details = data && data.errors ? data.errors.map((e) => `- ${e.field}: ${e.message}`).join('\n') : '';
      super(`${status} ${(data && (data.title || data.detail)) || 'request failed'}${details ? '\n' + details : ''}`);
      this.status = status;
    }
  }

  const query = (params) => {
    const q = new URLSearchParams();
    for (const [k, v] of Object.entries(params)) if (v !== '' && v != null) q.set(k, v);
    const s = q.toString();
    return s ? '?' + s : '';
  };

  function banner(message, ok = false) {
    const el = $('#banner');
    el.textContent = message || '';
    el.classList.toggle('ok', ok);
    el.hidden = !message;
    if (ok) setTimeout(() => { if (el.textContent === message) el.hidden = true; }, 4000);
  }

  /** Runs an async action, shows API errors in the banner instead of throwing into the console. */
  async function guarded(action) {
    try { banner(''); return await action(); } catch (error) { banner(error.message); return undefined; }
  }

  const show = (pre, value) => { pre.textContent = typeof value === 'string' ? value : JSON.stringify(value, null, 2); pre.hidden = false; };
  const formData = (form) => Object.fromEntries(new FormData(form).entries());
  const csv = (value) => (value || '').split(',').map((s) => s.trim()).filter(Boolean);
  const localToIso = (value) => (value ? new Date(value).toISOString() : null);

  // ---- tabs ------------------------------------------------------------------------------------

  const loaders = {};
  let refreshTimer = null;

  function activate(name) {
    for (const button of $$('#tabs button')) button.classList.toggle('active', button.dataset.tab === name);
    for (const section of $$('.tab')) section.hidden = section.dataset.tab !== name;
    clearInterval(refreshTimer);
    if (name === 'requests') refreshTimer = setInterval(() => { if ($('#auto-refresh').checked) loaders.requests(); }, 5000);
    if (loaders[name]) loaders[name]();
    history.replaceState(null, '', '#' + name);
  }

  $('#tabs').addEventListener('click', (event) => { if (event.target.dataset.tab) activate(event.target.dataset.tab); });

  // ---- tasks -----------------------------------------------------------------------------------

  const taskState = { skip: 0, top: 25, total: 0 };

  loaders.tasks = () => guarded(async () => {
    const f = formData($('#task-filter'));
    const data = await api('/tasks' + query({ ...f, top: taskState.top, skip: taskState.skip }));
    taskState.total = data.total;
    const body = $('#task-table tbody');
    body.replaceChildren(...data.items.map((t) => h('tr', { class: 'clickable', onclick: () => openTask(t.urn) },
      h('td', {}, t.subject), h('td', {}, h('span', { class: 'badge ' + t.status }, t.status)), h('td', {}, t.priority),
      h('td', {}, t.definition), h('td', {}, t.processor ? short(t.processor).slice(0, 8) : ''), h('td', {}, fmt(t.modifiedAt)),
      h('td', {}, `${t.recipientUsers}u/${t.recipientGroups}g`),
      h('td', {}, h('a', { href: '/app/tasks/' + encodeURIComponent(t.urn), target: '_blank', rel: 'noopener', onclick: (e) => e.stopPropagation() }, 'open')))));
    $('#task-range').textContent = data.total === 0 ? 'no tasks' : `${taskState.skip + 1}-${taskState.skip + data.items.length} of ${data.total}`;
    $('#task-prev').disabled = taskState.skip === 0;
    $('#task-next').disabled = taskState.skip + taskState.top >= data.total;
  });

  $('#task-filter').addEventListener('submit', (e) => { e.preventDefault(); taskState.skip = 0; loaders.tasks(); });
  $('#task-prev').addEventListener('click', () => { taskState.skip = Math.max(0, taskState.skip - taskState.top); loaders.tasks(); });
  $('#task-next').addEventListener('click', () => { taskState.skip += taskState.top; loaders.tasks(); });

  async function openTask(urn) {
    await guarded(async () => {
      const detail = await api('/tasks/' + encodeURIComponent(urn));
      const task = detail.task;
      const panel = $('#task-detail');
      const act = (label, path, danger, body) => h('button', { class: 'small' + (danger ? ' danger' : ''), onclick: () => guarded(async () => {
        await api('/tasks/' + encodeURIComponent(urn) + path, { method: 'POST', body: body || {} });
        banner(`${label}: done`, true);
        await Promise.all([openTask(urn), loaders.tasks()]);
      }) }, label);

      const completeUser = h('select', {}, ...detail.users.filter((u) => u.active).map((u) => h('option', { value: u.globalUserId }, u.displayName || u.userName)));
      panel.replaceChildren(
        h('h2', {}, task.subject[0].text), h('p', { class: 'muted' }, task.urn),
        h('div', { class: 'actions' },
          act('Cancel', '/cancel', true), act('Deactivate', '/deactivate'), act('Reactivate', '/reactivate'),
          detail.users.length ? [completeUser, h('button', { class: 'small primary', onclick: () => guarded(async () => {
            await api('/tasks/' + encodeURIComponent(urn) + '/complete', { method: 'POST', body: { userId: completeUser.value, comment: 'completed from the admin console' } });
            banner('Completed', true); await Promise.all([openTask(urn), loaders.tasks()]);
          }) }, 'Complete as user')] : null),
        h('div', { class: 'detail-cols' },
          h('div', {}, h('h3', {}, 'SPI representation'), h('pre', { class: 'result' }, JSON.stringify(task, null, 2))),
          h('div', {},
            h('h3', {}, 'Recipients'), h('ul', {}, ...detail.users.map((u) => h('li', {}, `${u.displayName || u.userName} (${u.email || 'no e-mail'})${u.active ? '' : ' - inactive'}`)), ...detail.groups.map((g) => h('li', {}, 'group ' + g))),
            h('h3', {}, 'Descriptions'), ...detail.descriptions.map((d) => h('pre', { class: 'result' }, `[${d.languageCode} ${d.contentType}]\n${d.body}`)),
            h('h3', {}, 'Operations'), h('ul', {}, ...detail.operations.map((o) => h('li', {}, `${fmt(o.at)} ${o.kind} ${o.code} - ${o.outcome}${o.errorCode ? ' (' + o.errorCode + ')' : ''}`)))
          )));
      panel.hidden = false;
      panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    });
  }

  // ---- new task / generator ----------------------------------------------------------------------

  let definitions = [];

  async function loadDefinitions() {
    if (definitions.length) return;
    definitions = await api('/definitions');
    const options = definitions.map((d) => h('option', { value: d.localId }, `${d.localId} - ${d.name}`));
    $('#new-task [name=definitionLocalId]').replaceChildren(...options.map((o) => o.cloneNode(true)));
    $('#generate [name=definitionLocalId]').append(...options.map((o) => o.cloneNode(true)));
    renderAttributeFields();
  }

  function renderAttributeFields() {
    const definition = definitions.find((d) => d.localId === $('#new-task [name=definitionLocalId]').value);
    $('#attribute-fields').replaceChildren(...(definition ? definition.attributes : []).map((a) =>
      h('label', {}, `${a.name} (${a.type})`, h('input', { 'data-attribute': a.code, placeholder: a.type === 'DATE' ? 'yyyy-MM-dd' : a.type === 'DATETIME' ? 'yyyy-MM-ddTHH:mm:ss.fffZ' : '' }))));
  }

  loaders.new = () => guarded(loadDefinitions);
  $('#new-task [name=definitionLocalId]').addEventListener('change', renderAttributeFields);

  $('#new-task').addEventListener('submit', (event) => {
    event.preventDefault();
    guarded(async () => {
      const f = formData(event.target);
      const attributes = {};
      for (const input of $$('[data-attribute]', event.target)) if (input.value.trim()) attributes[input.dataset.attribute] = input.value.trim();
      const body = {
        definitionLocalId: f.definitionLocalId,
        recipients: { users: csv(f.users), groups: csv(f.groups) },
        subject: { 'en-US': f.subjectEn, ...(f.subjectDe ? { 'de-DE': f.subjectDe } : {}) },
        priority: f.priority, dueAt: localToIso(f.dueAt), customAttributes: attributes,
      };
      if (f.description.trim()) body.description = { 'en-US': { contentType: 'text/html', body: f.description } };
      const created = await api('/tasks', { method: 'POST', body });
      show($('#new-result'), created);
      banner('Task created: ' + created.localId, true);
    });
  });

  $('#generate').addEventListener('submit', (event) => {
    event.preventDefault();
    guarded(async () => {
      const f = formData(event.target);
      const result = await api('/tasks/generate', { method: 'POST', body: {
        count: Number(f.count), definitionLocalId: f.definitionLocalId || null, sameTimestamp: f.sameTimestamp === 'on',
        recipients: { users: csv(f.users), groups: csv(f.groups) },
      } });
      show($('#generate-result'), result);
      banner(`Generated ${result.created} tasks`, true);
    });
  });

  // ---- users, groups, operations ------------------------------------------------------------------

  loaders.users = () => guarded(async () => {
    const data = await api('/users' + query({ ...formData($('#user-filter')), top: 200 }));
    $('#user-table tbody').replaceChildren(...data.items.map((u) => h('tr', { class: u.missingGlobalUserId ? 'warn' : null },
      h('td', {}, u.userName), h('td', {}, u.displayName || ''), h('td', {}, u.email || ''),
      h('td', {}, u.missingGlobalUserId ? 'MISSING' : u.globalUserId), h('td', {}, u.active ? 'yes' : 'no'), h('td', {}, u.groups.join(', ')))));
    $('#user-total').textContent = `${data.items.length} of ${data.total} users` + (data.items.some((u) => u.missingGlobalUserId) ? ' - highlighted rows have no Global User ID' : '');
  });
  $('#user-filter').addEventListener('submit', (e) => { e.preventDefault(); loaders.users(); });

  loaders.groups = () => guarded(async () => {
    const data = await api('/groups?top=200');
    $('#group-table tbody').replaceChildren(...data.items.map((g) => h('tr', {},
      h('td', {}, g.displayName), h('td', {}, g.memberCount), h('td', {}, g.members.map((m) => m.userName).join(', ')))));
  });

  loaders.operations = () => guarded(async () => {
    const data = await api('/operations?top=200');
    $('#operation-table tbody').replaceChildren(...data.items.map((o) => h('tr', { class: o.outcome === 'OK' ? null : 'failed' },
      h('td', {}, fmt(o.at)), h('td', {}, short(o.taskUrn)), h('td', {}, `${o.kind.toLowerCase()}: ${o.code}`),
      h('td', {}, short(o.userId).slice(0, 8)), h('td', {}, o.outcome + (o.errorCode ? ' ' + o.errorCode : '')), h('td', {}, o.comment || ''))));
  });

  // ---- requests (live) ---------------------------------------------------------------------------

  const expanded = new Set();

  loaders.requests = () => guarded(async () => {
    const f = formData($('#request-filter'));
    const rows = await api('/requests' + query({ prefix: f.prefix, status: f.status, limit: 100 }));
    const body = $('#request-table tbody');
    body.replaceChildren(...rows.flatMap((r) => {
      const key = r.correlationId + r.timestamp;
      const summary = h('tr', { class: 'clickable', onclick: () => { expanded.has(key) ? expanded.delete(key) : expanded.add(key); loaders.requests(); } },
        h('td', {}, fmt(r.timestamp)), h('td', {}, r.method), h('td', {}, r.path + r.query), h('td', {}, r.status), h('td', {}, r.durationMs), h('td', {}, r.clientId || ''));
      if (!expanded.has(key)) return [summary];
      const block = (title, headers, text) => h('div', {}, h('h3', {}, title),
        h('pre', {}, Object.entries(headers).map(([k, v]) => `${k}: ${v}`).join('\n')), h('pre', {}, text || '(no body)'));
      return [summary, h('tr', {}, h('td', { colspan: 6 }, h('div', { class: 'detail-cols' },
        block('Request (redacted)', r.requestHeaders, r.requestBody), block('Response', r.responseHeaders, r.responseBody),
        h('p', { class: 'muted' }, 'correlation ' + r.correlationId))))];
    }));
  });
  $('#request-filter').addEventListener('submit', (e) => { e.preventDefault(); loaders.requests(); });

  // ---- diagnostics -------------------------------------------------------------------------------

  $('#get-token').addEventListener('click', () => guarded(async () => show($('#token-result'), await api('/diagnostics/technical-token', { method: 'POST', body: {} }))));

  $('#simulate').addEventListener('submit', (event) => {
    event.preventDefault();
    guarded(async () => {
      const f = formData(event.target);
      const body = { modifiedAfter: f.modifiedAfter || null, lastId: f.lastId || null, top: Number(f.top) || 10, languages: f.languages || null };
      show($('#simulate-result'), await api('/diagnostics/simulate-pull', { method: 'POST', body }));
    });
  });

  $('#decode').addEventListener('submit', (event) => {
    event.preventDefault();
    guarded(async () => show($('#decode-result'), await api('/diagnostics/decode-assertion', { method: 'POST', body: { assertion: formData(event.target).assertion } })));
  });

  // ---- start ---------------------------------------------------------------------------------------

  const statuses = ['READY', 'RESERVED', 'IN_PROGRESS', 'FOR_RESUBMISSION', 'INACTIVE', 'COMPLETED', 'CANCELED'];
  $('#task-filter [name=status]').append(...statuses.map((s) => h('option', { value: s }, s)));
  activate(location.hash.slice(1) && $(`#tabs [data-tab="${location.hash.slice(1)}"]`) ? location.hash.slice(1) : 'tasks');
})();
