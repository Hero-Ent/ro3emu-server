// RO3Emu web panel: a small client-side router over the panel's JSON API (/api/...). Every value from the server
// reaches the page through textContent or an attribute, never as HTML.
'use strict';

const state = { session: null, status: null };
const view = document.getElementById('view');

// ---------------------------------------------------------------- DOM helpers

function h(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value == null || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value === true ? '' : value);
  }
  append(node, children);
  return node;
}

function append(node, children) {
  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

const SVG = 'http://www.w3.org/2000/svg';
function icon(name) {
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('class', 'i');
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS(SVG, 'use');
  use.setAttribute('href', '#i-' + name);
  svg.append(use);
  return svg;
}

const link = (href, attrs, ...children) => h('a', { href, 'data-link': true, ...attrs }, ...children);
const spin = () => h('i', { class: 'spin' });

function render(...children) {
  view.replaceChildren(...children.flat(Infinity).filter(Boolean));
  view.focus({ preventScroll: true });
}

function loading() { render(h('div', { class: 'loading' }, spin())); }

function emptyState(iconName, title, text) {
  return h('div', { class: 'empty' }, h('span', { class: 'badge blue' }, icon(iconName)), h('b', null, title), text && h('p', null, text));
}

function failure(error) {
  return h('section', { class: 'paper card' }, emptyState('pulse', 'Something went wrong', error.message));
}

let toastTimer = 0;
function toast(text) {
  let node = document.querySelector('.toast');
  if (!node) { node = h('div', { class: 'toast', role: 'status' }); document.body.append(node); }
  node.textContent = text;
  node.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => node.classList.remove('show'), 2600);
}

// ---------------------------------------------------------------- formatting

const numberFormat = new Intl.NumberFormat('en-US');
const dateFormat = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
const timeFormat = new Intl.DateTimeFormat('en-GB', { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });
const num = n => numberFormat.format(n ?? 0);
const date = iso => iso ? dateFormat.format(new Date(iso)) : '-';
const dateTime = iso => iso ? timeFormat.format(new Date(iso)) : '-';

function duration(seconds) {
  const d = Math.floor(seconds / 86400), hr = Math.floor(seconds % 86400 / 3600), m = Math.floor(seconds % 3600 / 60);
  if (d > 0) return `${d} d ${hr} h`;
  if (hr > 0) return `${hr} h ${m} min`;
  return `${Math.max(m, 0)} min`;
}

function ago(iso) {
  if (!iso) return 'never';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  if (s < 86400 * 30) return `${Math.round(s / 86400)} d ago`;
  return date(iso);
}

const bytes = n => n >= 1 << 30 ? (n / (1 << 30)).toFixed(2) + ' GB' : Math.round(n / (1 << 20)) + ' MB';

// ---------------------------------------------------------------- API

class ApiError extends Error {
  constructor(message, status) { super(message); this.status = status; }
}

async function api(method, path, body, retried) {
  const options = { method, headers: { Accept: 'application/json' }, credentials: 'same-origin' };
  if (method === 'POST') {
    options.headers['Content-Type'] = 'application/json';
    options.headers['X-CSRF-Token'] = state.session?.csrf ?? '';
    options.body = JSON.stringify(body ?? {});
  }
  const response = await fetch(path, options);
  const data = await response.json().catch(() => ({}));
  // An expired token (a restart, a login elsewhere): take a fresh one and send once more.
  if (response.status === 403 && method === 'POST' && !retried) {
    await loadSession();
    return api(method, path, body, true);
  }
  if (!response.ok) throw new ApiError(data.error || `The server answered ${response.status}.`, response.status);
  return data;
}

async function loadSession() {
  state.session = await api('GET', '/api/session');
  drawChrome();
  return state.session;
}

function adopt(session) {
  state.session = session;
  drawChrome();
}

// ---------------------------------------------------------------- chrome

function drawChrome() {
  const s = state.session;
  const name = s?.server?.name || 'RO3Emu';
  document.getElementById('brand-name').textContent = name;
  document.getElementById('foot-name').textContent = name;
  document.getElementById('nav-admin').hidden = !s?.admin;
  const who = document.getElementById('who');
  if (s?.account) {
    who.replaceChildren(
      link('/account', { class: 'pill', title: 'Your account' }, h('i', { class: 'avatar' }, icon('user')), h('span', null, s.account)),
      h('button', { type: 'button', class: 'ctl', title: 'Log out', 'aria-label': 'Log out', onclick: logout }, icon('out')));
  } else {
    who.replaceChildren(
      link('/login', { class: 'btn ghost small' }, 'Log in'),
      s?.registration ? link('/register', { class: 'btn ink small' }, 'Sign up') : null);
  }
}

async function logout() {
  try { await api('POST', '/api/logout'); } catch { /* the session is gone either way */ }
  await loadSession();
  navigate('/');
  toast('You are logged out.');
}

function markNav(section) {
  for (const a of document.querySelectorAll('[data-nav]'))
    if (a.dataset.nav === section) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current');
}

const menu = document.getElementById('menu');
const nav = document.getElementById('nav');
menu.addEventListener('click', () => {
  const open = !nav.classList.contains('open');
  nav.classList.toggle('open', open);
  menu.setAttribute('aria-expanded', String(open));
});
function closeMenu() { nav.classList.remove('open'); menu.setAttribute('aria-expanded', 'false'); }

const topBar = document.getElementById('top');
addEventListener('scroll', () => topBar.classList.toggle('scrolled', scrollY > 8), { passive: true });

// ---------------------------------------------------------------- router

const routes = [
  [/^\/$/, homePage, 'home'],
  [/^\/online$/, onlinePage, 'online'],
  [/^\/rankings$/, rankingsPage, 'rankings'],
  [/^\/news$/, newsPage, 'news'],
  [/^\/login$/, loginPage, null],
  [/^\/register$/, registerPage, null],
  [/^\/account$/, accountPage, null],
  [/^\/admin$/, adminDashboard, 'admin'],
  [/^\/admin\/accounts$/, adminAccounts, 'admin'],
  [/^\/admin\/accounts\/([^/]+)$/, adminAccount, 'admin'],
  [/^\/admin\/audit$/, adminAudit, 'admin'],
];

let routeToken = 0;
async function route() {
  closeMenu();
  const path = location.pathname.replace(/\/+$/, '') || '/';
  const match = routes.map(([re, page, section]) => [path.match(re), page, section]).find(([m]) => m);
  const token = ++routeToken;
  if (!match) { markNav(null); render(notFound()); return; }
  const [m, page, section] = match;
  markNav(section);
  loading();
  try {
    const content = await page(...m.slice(1).map(decodeURIComponent));
    if (token === routeToken && content) render(content);
  } catch (error) {
    if (token !== routeToken) return;
    if (error.status === 401) { navigate('/login?next=' + encodeURIComponent(location.pathname + location.search), true); return; }
    render(failure(error));
  }
  document.title = (state.session?.server?.name || 'RO3Emu') + (section && section !== 'home' ? ' · ' + titles[section] : '');
}

const titles = { online: "Who's online", rankings: 'Rankings', news: 'News', admin: 'Admin' };

function navigate(to, replace) {
  if (replace) history.replaceState(null, '', to); else history.pushState(null, '', to);
  scrollTo({ top: 0 });
  route();
}

document.addEventListener('click', event => {
  const a = event.target.closest('a[data-link]');
  if (!a || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
  event.preventDefault();
  navigate(a.getAttribute('href'));
});
addEventListener('popstate', route);

function notFound() {
  return h('section', { class: 'paper card' }, emptyState('search', 'This page does not exist', 'The link may be old.'),
    h('div', { class: 'actions' }, link('/', { class: 'btn ghost' }, icon('back'), 'Back home')));
}

function pageHead(kicker, title, lead, ...aside) {
  return h('div', { class: 'page-head' },
    h('div', null, h('p', { class: 'kicker' }, kicker), h('h1', { class: 'title' }, title), lead && h('p', { class: 'lead' }, lead)),
    aside.length ? h('div', { class: 'row' }, aside) : null);
}

// ---------------------------------------------------------------- home

async function homePage() {
  const [status, news] = await Promise.all([api('GET', '/api/status'), api('GET', '/api/news?limit=5')]);
  state.status = status;
  const s = state.session;
  const actions = s?.account
    ? [link('/account', { class: 'btn amber big' }, 'My account', icon('arrow')), link('/rankings', { class: 'btn ghost big' }, 'Rankings')]
    : [s?.registration ? link('/register', { class: 'btn amber big' }, 'Create an account', icon('arrow')) : null,
      link('/login', { class: 'btn ghost big' }, 'Log in')];
  const hero = h('section', { class: 'hero' },
    h('div', null,
      h('p', { class: 'kicker' }, 'Server · ' + status.area),
      h('h1', { class: 'title' }, 'Welcome to ', status.name),
      h('p', { class: 'lead' }, 'Your account, your characters and the realm\'s rankings, in one place. Log in with the same account name and password as in the game.'),
      h('div', { class: 'actions' }, actions),
      h('div', { class: 'server' }, h('i', { class: 'dot on' }), h('span', null, 'Server ', h('b', null, 'online'), ' · ', status.area))),
    h('div', { class: 'frame', 'aria-label': 'Server status' },
      h('i', { class: 'cloud f1' }), h('i', { class: 'cloud f2' }),
      h('span', { class: 'frame-note' }, h('i', { class: 'dot on' }), 'Online now'),
      h('div', { class: 'stats' },
        stat('Players online', num(status.players), 'in the world'),
        stat('Uptime', duration(status.uptimeSeconds), 'since ' + dateTime(status.startedUtc)),
        stat('Version', status.version || '-', 'RO3Emu server'))));
  const list = news.news.length
    ? h('ul', { class: 'items' }, news.news.map(n => h('li', null, link('/news', { class: 'item' },
      h('span', { class: 't' }, n.title), h('span', { class: 'm' }, h('span', { class: 'tag ' + n.tag }, n.tag), h('i'), h('time', null, date(n.published))), icon('arrow')))))
    : emptyState('book', 'No news yet', 'Announcements from the team will show up here.');
  return [hero, h('div', { class: 'grid side' },
    h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, 'Latest news'), link('/news', { class: 'linkish' }, 'All news')), list),
    h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, 'Explore')),
      h('ul', { class: 'items' },
        h('li', null, link('/online', { class: 'item' }, h('span', { class: 't' }, "Who's online"), h('span', { class: 'm' }, 'Characters in the world right now'), icon('arrow'))),
        h('li', null, link('/rankings', { class: 'item' }, h('span', { class: 't' }, 'Rankings'), h('span', { class: 'm' }, 'Levels, power, towers and guilds'), icon('arrow'))),
        h('li', null, link(s?.account ? '/account' : '/login', { class: 'item' }, h('span', { class: 't' }, 'My characters'), h('span', { class: 'm' }, 'Classes, levels and where they are'), icon('arrow'))))))];
}

function stat(label, value, note) {
  return h('div', { class: 'stat' }, h('span', { class: 'label' }, label), h('b', { title: value }, value), note && h('small', null, note));
}

// ---------------------------------------------------------------- who's online

async function onlinePage() {
  const { players } = await api('GET', '/api/online');
  const admin = state.session?.admin;
  const table = players.length
    ? h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
      h('thead', null, h('tr', null, h('th', null, 'Name'), h('th', null, 'Class'), h('th', { class: 'r' }, 'Level'), h('th', { class: 'opt' }, 'Map'), admin && h('th', { class: 'opt' }, 'Account'))),
      h('tbody', null, players.map(p => h('tr', null,
        h('td', { class: 'name' }, p.name), h('td', null, p.class), h('td', { class: 'r num' }, h('span', { class: 'lv' }, p.level)),
        h('td', { class: 'opt muted' }, p.map), admin && h('td', { class: 'opt' }, link('/admin/accounts/' + encodeURIComponent(p.account), { class: 'linkish' }, p.account)))))))
    : emptyState('users', 'Nobody is in the world right now', 'Characters show up here while they play.');
  return [pageHead('Live', "Who's online", players.length === 1 ? '1 character is in the world.' : `${num(players.length)} characters are in the world.`,
    h('button', { type: 'button', class: 'btn ghost small', onclick: route }, 'Refresh')),
  h('section', { class: 'paper card' }, table)];
}

// ---------------------------------------------------------------- rankings

async function rankingsPage() {
  const params = new URLSearchParams(location.search);
  const { boards, classes } = await api('GET', '/api/rankings');
  const current = boards.find(b => b.id === params.get('board')) || boards[0];
  const cls = current.byClass ? params.get('class') || '' : '';
  const board = await api('GET', `/api/rankings/${encodeURIComponent(current.id)}${cls ? '?class=' + encodeURIComponent(cls) : ''}`);
  const go = (id, c) => navigate('/rankings?board=' + encodeURIComponent(id) + (c ? '&class=' + encodeURIComponent(c) : ''));
  const tabs = h('div', { class: 'tabs', role: 'tablist', 'aria-label': 'Ranking' }, boards.map(b =>
    h('button', { type: 'button', class: 'tab', role: 'tab', 'aria-selected': String(b.id === current.id), onclick: () => go(b.id, b.byClass ? cls : '') }, b.title)));
  const filter = current.byClass && classes.length
    ? h('select', { class: 'input', 'aria-label': 'Class', onchange: e => go(current.id, e.target.value) },
      h('option', { value: '' }, 'Every class'), classes.map(c => h('option', { value: String(c.id), selected: String(c.id) === cls }, c.name)))
    : null;
  const rows = board.rows;
  const table = rows.length
    ? h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
      h('thead', null, h('tr', null, h('th', null, '#'), h('th', null, current.guild ? 'Guild' : 'Name'), !current.guild && h('th', { class: 'opt' }, 'Class'),
        h('th', { class: 'r' }, current.guild ? 'Guild level' : 'Level'), h('th', { class: 'r' }, current.score))),
      h('tbody', null, rows.map(r => h('tr', null,
        h('td', null, h('span', { class: 'rank' + (r.rank <= 3 ? ' r' + r.rank : '') }, r.rank)),
        h('td', { class: 'name' }, r.name), !current.guild && h('td', { class: 'opt muted' }, r.class),
        h('td', { class: 'r num' }, h('span', { class: 'lv' }, r.level)), h('td', { class: 'r num' }, num(r.score)))))))
    : emptyState('trophy', 'Nobody is ranked here yet', 'The board fills as characters play.');
  return [pageHead('Leaderboards', 'Rankings', 'Updated every few seconds from the realm itself.'),
    h('section', { class: 'paper card' }, h('div', { class: 'toolbar' }, tabs, filter), table)];
}

// ---------------------------------------------------------------- news

async function newsPage() {
  const { news } = await api('GET', '/api/news?limit=50');
  return [pageHead('From the team', 'News'),
    news.length
      ? news.map(n => h('article', { class: 'paper card article' },
        h('div', { class: 'm item' }, h('span', { class: 'm' }, h('span', { class: 'tag ' + n.tag }, n.tag), h('i'), h('time', null, date(n.published)), n.author && [h('i'), n.author])),
        h('h2', null, n.title), h('p', { class: 'body' }, n.body)))
      : h('section', { class: 'paper card' }, emptyState('book', 'No news yet', 'Announcements from the team will show up here.'))];
}

// ---------------------------------------------------------------- forms

function form(fields, submitLabel, onsubmit, look = 'ink') {
  const error = h('p', { class: 'form-error', role: 'alert', hidden: true });
  const button = h('button', { type: 'submit', class: 'btn big ' + look }, h('i', { class: 'spin' }), submitLabel);
  const node = h('form', { novalidate: true }, fields, error, button);
  node.addEventListener('submit', async event => {
    event.preventDefault();
    error.hidden = true;
    button.classList.add('busy');
    try { await onsubmit(Object.fromEntries(new FormData(node)), node); }
    catch (e) { error.textContent = e.message; error.hidden = false; }
    finally { button.classList.remove('busy'); }
  });
  return node;
}

function field(label, name, type, extra) {
  return h('label', { class: 'field' }, h('span', { class: 'label' }, label),
    h('input', { class: 'input', name, type, required: true, spellcheck: 'false', autocapitalize: 'off', ...extra }));
}

function nextPath() {
  const next = new URLSearchParams(location.search).get('next') || '/account';
  return next.startsWith('/') && !next.startsWith('//') ? next : '/account';
}

async function loginPage() {
  if (state.session?.account) { navigate(nextPath(), true); return null; }
  const node = form([
    field('Account name', 'account', 'text', { autocomplete: 'username', maxlength: '64' }),
    field('Password', 'password', 'password', { autocomplete: 'current-password' })],
  'Log in', async values => {
    adopt(await api('POST', '/api/login', { account: values.account, password: values.password }));
    navigate(nextPath(), true);
    toast('Welcome back, ' + state.session.account + '.');
  });
  return h('section', { class: 'paper sheet' }, h('p', { class: 'kicker' }, 'Your account'), h('h1', null, 'Log in'),
    h('p', { class: 'lead' }, 'Use the account name and password you play with.'), node,
    state.session?.registration && h('p', { class: 'form-foot' }, 'New here? ', link('/register', { class: 'linkish' }, 'Create an account')));
}

async function registerPage() {
  if (state.session?.account) { navigate('/account', true); return null; }
  if (!state.session?.registration)
    return h('section', { class: 'paper sheet' }, h('h1', null, 'Sign-up is closed'),
      h('p', { class: 'lead' }, 'On this server your first login in the game creates your account.'));
  const node = form([
    field('Account name', 'account', 'text', { autocomplete: 'username', maxlength: '64' }),
    field('Password', 'password', 'password', { autocomplete: 'new-password', minlength: '6', maxlength: '128' }),
    field('Password again', 'again', 'password', { autocomplete: 'new-password' })],
  'Create account', async values => {
    if (values.password !== values.again) throw new Error('The two passwords are not the same.');
    adopt(await api('POST', '/api/register', { account: values.account, password: values.password }));
    navigate('/account', true);
    toast('Your account is ready. Log in to the game with it.');
  });
  return h('section', { class: 'paper sheet' }, h('p', { class: 'kicker' }, 'Join'), h('h1', null, 'Create an account'),
    h('p', { class: 'lead' }, 'This is the account you log in to the game with. Characters are created in the game.'), node,
    h('p', { class: 'form-foot' }, 'Already have one? ', link('/login', { class: 'linkish' }, 'Log in')));
}

// ---------------------------------------------------------------- my account

async function accountPage() {
  const account = await api('GET', '/api/account');
  const facts = h('div', { class: 'facts' },
    fact('Account', account.name), fact('Created', date(account.created)), fact('Last played', ago(account.lastLogin)),
    fact('Characters', num(account.characters.length)));
  const chars = account.characters.length
    ? h('div', { class: 'chars' }, account.characters.map(characterCard))
    : emptyState('user', 'No characters yet', 'Create one in the game; it shows up here.');
  const password = form([
    field('Current password', 'current', 'password', { autocomplete: 'current-password' }),
    field('New password', 'next', 'password', { autocomplete: 'new-password', minlength: '6', maxlength: '128' }),
    field('New password again', 'again', 'password', { autocomplete: 'new-password' })],
  'Change password', async (values, node) => {
    if (values.next !== values.again) throw new Error('The two new passwords are not the same.');
    await api('POST', '/api/account/password', { current: values.current, next: values.next });
    node.reset();
    toast('Password changed. Use the new one in the game too.');
  });
  return [pageHead('Your account', account.name, null, account.admin && link('/admin', { class: 'btn ghost small' }, icon('shield'), 'Admin')),
    h('div', { class: 'grid side' },
      h('div', { class: 'stack' },
        h('section', { class: 'paper card' }, facts),
        h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, 'Characters')), chars)),
      h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, 'Password')),
        h('p', { class: 'muted' }, 'Other browsers signed in to this panel are signed out when you change it.'), h('div', { class: 'field' }), password))];
}

function fact(label, value) {
  return h('div', { class: 'fact' }, h('span', { class: 'label' }, label), h('b', null, value));
}

function characterCard(c) {
  return h('article', { class: 'char' },
    c.online ? h('span', { class: 'chip green' }, h('i', { class: 'dot on' }), 'Online') : null,
    h('h3', { title: c.name }, c.name), h('p', { class: 'cls' }, c.class),
    h('dl', null,
      h('dt', null, 'Level'), h('dd', null, h('span', { class: 'lv' }, c.level), ' · job ', c.jobLevel),
      h('dt', null, 'Map'), h('dd', { title: c.map }, c.map),
      h('dt', null, 'Guild'), h('dd', null, c.guild || '-'),
      h('dt', null, 'Played'), h('dd', null, ago(c.lastLogin))));
}

// ---------------------------------------------------------------- admin

function adminTabs(current) {
  const tab = (href, id, label, name) => link(href, { class: 'tab', role: 'tab', 'aria-selected': String(id === current) }, icon(name), label);
  return h('div', { class: 'tabs', role: 'tablist', 'aria-label': 'Admin' },
    tab('/admin', 'dashboard', 'Dashboard', 'pulse'), tab('/admin/accounts', 'accounts', 'Accounts', 'users'), tab('/admin/audit', 'audit', 'Audit log', 'list'));
}

function adminHead(current, title, lead) {
  return [pageHead('Admin', title, lead), h('div', { class: 'toolbar' }, adminTabs(current))];
}

function requireAdmin() {
  if (!state.session?.account) throw new ApiError('Please log in.', 401);
  if (!state.session.admin) throw new ApiError('Only an admin can open this page.', 403);
}

async function adminDashboard() {
  requireAdmin();
  const d = await api('GET', '/api/admin/dashboard');
  const tile = (label, value, note, warn) => h('div', { class: 'tile' + (warn ? ' warn' : '') }, h('span', { class: 'label' }, label), h('b', null, value), note && h('small', null, note));
  const most = Math.max(1, ...d.maps.map(m => m.players));
  const maps = d.maps.length
    ? h('ul', { class: 'bars' }, d.maps.map(m => h('li', null, h('span', null, m.map), h('b', { class: 'num' }, num(m.players)),
      h('span', { class: 'bar' }, h('i', { 'data-width': String(Math.round(m.players / most * 100)) })))))
    : emptyState('users', 'Nobody is in the world', null);
  for (const bar of maps.querySelectorAll('[data-width]')) bar.style.width = bar.dataset.width + '%';
  return [adminHead('dashboard', 'Dashboard', 'The server\'s own numbers since it started (or since the last metrics reset).'),
    h('section', { class: 'tiles' },
      tile('In the world', num(d.inWorld), `${num(d.loading)} loading · ${num(d.login)} on the character screen`),
      tile('Accounts', num(d.accounts), `${num(d.characters)} characters`),
      tile('Connections', num(d.connections), 'micro and game gates'),
      tile('Tick p99', d.tickP99Ms.toFixed(1) + ' ms', `p50 ${d.tickP50Ms.toFixed(1)} · max ${d.tickMaxMs.toFixed(0)} ms`, d.tickP99Ms > 50),
      tile('Ticks / s', d.ticksPerSecond.toFixed(1), `${num(d.overruns)} overruns · queue ${num(d.queueLength)}`),
      tile('Memory', bytes(d.workingSetBytes), `${d.cpuSeconds.toFixed(0)} s CPU · ${num(d.gen2Collections)} gen 2 GCs`),
      tile('Errors', num(d.exceptions + d.errorLogs), `${num(d.exceptions)} exceptions · ${num(d.errorLogs)} error logs`, d.exceptions + d.errorLogs > 0),
      tile('Loop', d.phase, `${num(d.dbCommits)} database commits`)),
    h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, 'Busiest maps')), maps)];
}

async function adminAccounts() {
  requireAdmin();
  const params = new URLSearchParams(location.search);
  const q = params.get('q') || '', page = Number(params.get('page')) || 1;
  const result = await api('GET', `/api/admin/accounts?q=${encodeURIComponent(q)}&page=${page}`);
  const search = h('form', { class: 'row', role: 'search' },
    h('span', { class: 'search' }, icon('search'), h('input', { class: 'input', name: 'q', value: q, placeholder: 'Account name', 'aria-label': 'Account name', spellcheck: 'false', autocapitalize: 'off' })),
    h('button', { type: 'submit', class: 'btn ink' }, 'Search'));
  search.addEventListener('submit', e => { e.preventDefault(); navigate('/admin/accounts?q=' + encodeURIComponent(new FormData(search).get('q').trim())); });
  const open = name => navigate('/admin/accounts/' + encodeURIComponent(name));
  const table = result.accounts.length
    ? h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
      h('thead', null, h('tr', null, h('th', null, 'Account'), h('th', { class: 'opt' }, 'Created'), h('th', { class: 'r' }, 'Characters'), h('th', { class: 'opt' }, 'Last played'), h('th', null, 'Status'))),
      h('tbody', null, result.accounts.map(a => h('tr', { class: 'clickable', onclick: () => open(a.name) },
        h('td', { class: 'name' }, link('/admin/accounts/' + encodeURIComponent(a.name), null, a.name)), h('td', { class: 'opt muted' }, date(a.created)),
        h('td', { class: 'r num' }, num(a.characters)), h('td', { class: 'opt muted' }, ago(a.lastLogin)),
        h('td', null, a.banned ? h('span', { class: 'chip red' }, 'Banned') : h('span', { class: 'chip' }, 'Active')))))))
    : emptyState('search', q ? 'No account matches' : 'No accounts yet', q ? 'Try part of the name.' : null);
  return [adminHead('accounts', 'Accounts', `${num(result.total)} account${result.total === 1 ? '' : 's'}${q ? ' match' : ''}.`),
    h('section', { class: 'paper card' }, h('div', { class: 'toolbar' }, search), table, pager(result, p => '/admin/accounts?q=' + encodeURIComponent(q) + '&page=' + p))];
}

function pager(result, href) {
  const pages = Math.max(1, Math.ceil(result.total / result.pageSize));
  if (pages <= 1) return null;
  return h('div', { class: 'pager' },
    result.page > 1 ? link(href(result.page - 1), { class: 'btn ghost small' }, icon('back'), 'Newer') : h('span'),
    h('span', null, `Page ${result.page} of ${pages}`),
    result.page < pages ? link(href(result.page + 1), { class: 'btn ghost small' }, 'Older', icon('arrow')) : h('span'));
}

async function adminAccount(name) {
  requireAdmin();
  const { account, admin } = await api('GET', '/api/admin/accounts/' + encodeURIComponent(name));
  const self = account.name === state.session.account;
  const facts = h('div', { class: 'facts' }, fact('Account', account.name), fact('Created', date(account.created)),
    fact('Last played', ago(account.lastLogin)), fact('Role', admin ? 'Admin (GM)' : 'Player'));
  const chars = account.characters.length
    ? h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
      h('thead', null, h('tr', null, h('th', null, 'Name'), h('th', { class: 'opt' }, 'Class'), h('th', { class: 'r' }, 'Level'), h('th', { class: 'opt' }, 'Map'), h('th', { class: 'opt' }, 'Guild'), h('th', null, ''))),
      h('tbody', null, account.characters.map(c => h('tr', null, h('td', { class: 'name' }, c.name), h('td', { class: 'opt muted' }, c.class),
        h('td', { class: 'r num' }, h('span', { class: 'lv' }, c.level)), h('td', { class: 'opt muted' }, c.map), h('td', { class: 'opt muted' }, c.guild || '-'),
        h('td', null, c.online ? h('span', { class: 'chip green' }, 'Online') : h('span', { class: 'muted' }, ago(c.lastLogin))))))))
    : emptyState('user', 'No characters', null);
  return [h('div', null, link('/admin/accounts', { class: 'crumb' }, icon('back'), 'Accounts')),
    pageHead('Account', account.name, null, account.ban ? h('span', { class: 'chip red' }, icon('ban'), 'Banned') : h('span', { class: 'chip green' }, 'Active')),
    h('div', { class: 'grid side' },
      h('div', { class: 'stack' },
        h('section', { class: 'paper card' }, facts),
        h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, 'Characters')), chars)),
      h('section', { class: 'paper card' }, h('div', { class: 'card-head' }, h('h2', null, account.ban ? 'Ban' : 'Ban this account')),
        self ? h('p', { class: 'muted' }, 'This is your own account.') : account.ban ? unbanForm(account) : banForm(account)))];
}

function banForm(account) {
  const durations = [['24', '1 day'], ['72', '3 days'], ['168', '7 days'], ['720', '30 days'], ['', 'No end']];
  return h('div', null,
    h('p', { class: 'muted' }, 'The player is disconnected at once and cannot log in to the game or this panel until the ban ends.'), h('div', { class: 'field' }),
    form([
      h('label', { class: 'field' }, h('span', { class: 'label' }, 'Reason'), h('textarea', { class: 'input', name: 'reason', required: true, maxlength: '300' })),
      h('label', { class: 'field' }, h('span', { class: 'label' }, 'How long'),
        h('select', { class: 'input', name: 'hours' }, durations.map(([value, label]) => h('option', { value, selected: value === '168' }, label))))],
    'Ban account', async values => {
      const result = await api('POST', `/api/admin/accounts/${encodeURIComponent(account.name)}/ban`,
        { reason: values.reason, hours: values.hours ? Number(values.hours) : null });
      toast(result.kicked ? `Banned. ${result.kicked} game session closed.` : 'Banned.');
      route();
    }, 'danger'));
}

function unbanForm(account) {
  const ban = account.ban;
  return h('div', null,
    h('div', { class: 'ban-box' }, h('b', null, ban.until ? 'Banned until ' + dateTime(ban.until) : 'Banned with no end'),
      h('p', null, ban.reason || 'No reason given.'), h('p', { class: 'muted' }, 'By ', ban.by || 'unknown', ' on ', dateTime(ban.banned))),
    form([h('label', { class: 'field' }, h('span', { class: 'label' }, 'Note (optional)'), h('textarea', { class: 'input', name: 'reason', maxlength: '300' }))],
      'Lift the ban', async values => {
        await api('POST', `/api/admin/accounts/${encodeURIComponent(account.name)}/unban`, { reason: values.reason });
        toast('The ban is lifted.');
        route();
      }));
}

async function adminAudit() {
  requireAdmin();
  const page = Number(new URLSearchParams(location.search).get('page')) || 1;
  const result = await api('GET', '/api/admin/audit?page=' + page);
  const table = result.entries.length
    ? h('div', { class: 'table-wrap' }, h('table', { class: 'data' },
      h('thead', null, h('tr', null, h('th', null, 'When'), h('th', null, 'Admin'), h('th', null, 'Action'), h('th', null, 'Account'), h('th', { class: 'opt' }, 'Detail'))),
      h('tbody', null, result.entries.map(e => h('tr', null, h('td', { class: 'muted num' }, dateTime(e.utc)), h('td', { class: 'name' }, e.admin),
        h('td', null, h('span', { class: 'chip ' + (e.action === 'ban' ? 'red' : 'blue') }, e.action)),
        h('td', null, link('/admin/accounts/' + encodeURIComponent(e.target), { class: 'linkish' }, e.target)), h('td', { class: 'opt muted' }, e.detail))))))
    : emptyState('list', 'Nothing recorded yet', 'Every admin action is listed here: who, what and when.');
  return [adminHead('audit', 'Audit log', 'Every change made in the admin area.'),
    h('section', { class: 'paper card' }, table, pager(result, p => '/admin/audit?page=' + p))];
}

// ---------------------------------------------------------------- start

(async () => {
  try { await loadSession(); } catch { /* the pages show the error */ }
  await route();
})();
