'use strict';

// All API calls go to /BcMoosic/api/
const API_BASE = '/BcMoosic/api';

// ============================================================
// State
// ============================================================
const state = {
  purchases: [],
  lastToken: null,
  moreAvailable: false,
  defaultFormat: 'mp3-320',
  pollTimer: null,
  collectionLoaded: false,
  wishlistLoaded: false,
  followingLoaded: false,
  followingBands: null,
};

function normalizeName(name) {
  return name.replace(/^the\s+/i, '').toLowerCase().trim();
}

// ============================================================
// Utilities
// ============================================================
function esc(s) {
  return String(s ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function setStatus(el, msg, type) {
  el.textContent = msg;
  el.className = `form-status ${type ?? ''}`;
}

async function api(path, opts = {}) {
  const r = await fetch(path, {
    headers: { 'Content-Type': 'application/json', ...opts.headers },
    ...opts,
  });
  if (!r.ok) {
    const detail = await r.json()
      .then(d => d.detail ?? d.title ?? JSON.stringify(d))
      .catch(() => r.statusText);
    throw new Error(detail);
  }
  return r.json();
}

// ============================================================
// Navigation
// ============================================================
document.querySelectorAll('.tab').forEach(tab => {
  tab.addEventListener('click', () => {
    const page = tab.dataset.page;
    document.querySelectorAll('.tab').forEach(t => {
      t.classList.toggle('active', t === tab);
      t.setAttribute('aria-selected', t === tab);
    });
    document.querySelectorAll('.page').forEach(p => {
      p.classList.toggle('active', p.id === `page-${page}`);
    });
    if (page === 'downloads') refreshDownloads();
    if (page === 'collection' && !state.collectionLoaded) loadCollection();
    if (page === 'settings') checkAuth();
  });
});

// ============================================================
// Bandcamp segment control
// ============================================================
document.querySelectorAll('.bc-seg').forEach(btn => {
  btn.addEventListener('click', () => {
    document.querySelectorAll('.bc-seg').forEach(b => b.classList.remove('active'));
    btn.classList.add('active');
    const seg = btn.dataset.seg;
    document.getElementById('bc-owned').style.display = seg === 'owned' ? '' : 'none';
    document.getElementById('bc-wishlist').style.display = seg === 'wishlist' ? '' : 'none';
    document.getElementById('bc-following').style.display = seg === 'following' ? '' : 'none';
    if (seg === 'wishlist' && !state.wishlistLoaded) loadWishlist();
    if (seg === 'following' && !state.followingLoaded) loadFollowing();
  });
});

// ============================================================
// Auth / status
// ============================================================
async function checkAuth() {
  try {
    const s = await api(`${API_BASE}/auth/status`);
    const badge = document.getElementById('auth-badge');
    if (s.authenticated) {
      badge.textContent = s.username || 'Connected';
      badge.className = 'badge ok';
    } else {
      badge.textContent = 'Sign in';
      badge.className = 'badge err';
    }
    if (s.defaultFormat) {
      state.defaultFormat = s.defaultFormat;
      document.getElementById('pref-format').value = s.defaultFormat;
    }
    if (s.musicDir) {
      document.getElementById('pref-music-dir').value = s.musicDir;
    }
    return s.authenticated;
  } catch {
    const badge = document.getElementById('auth-badge');
    badge.textContent = 'Offline';
    badge.className = 'badge err';
    return false;
  }
}

// Settings — cookie paste form
document.getElementById('cookie-form').addEventListener('submit', async e => {
  e.preventDefault();
  const btn = e.target.querySelector('button[type=submit]');
  const status = document.getElementById('cookie-status');
  const identity = document.getElementById('cookie-identity').value.trim();
  const username = document.getElementById('cookie-username').value.trim();
  if (!identity) { setStatus(status, 'Cookie value is required.', 'err'); return; }
  btn.disabled = true;
  btn.innerHTML = '<span class="spinner"></span> Saving…';
  try {
    const res = await api(`${API_BASE}/auth/cookies`, {
      method: 'POST',
      body: JSON.stringify({ identity, username }),
    });
    const who = res.username ? `Signed in as ${res.username}` : 'Saved!';
    setStatus(status, `${who} — loading purchases…`, 'ok');
    await checkAuth();
    state.wishlistLoaded = false;
    state.followingLoaded = false;
    state.followingBands = null;
    loadPurchases(true);
    document.querySelector('.tab[data-page="purchases"]').click();
  } catch (err) {
    setStatus(status, `Error: ${err.message}`, 'err');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Save & verify';
  }
});

// Settings — preferences
document.getElementById('prefs-form').addEventListener('submit', async e => {
  e.preventDefault();
  const status = document.getElementById('prefs-status');
  const fmt = document.getElementById('pref-format').value;
  const musicDir = document.getElementById('pref-music-dir').value.trim();
  try {
    await api(`${API_BASE}/settings`, {
      method: 'POST',
      body: JSON.stringify({ defaultFormat: fmt, musicDir: musicDir || null }),
    });
    state.defaultFormat = fmt;
    setStatus(status, 'Saved.', 'ok');
    setTimeout(() => setStatus(status, '', ''), 2000);
  } catch (err) {
    setStatus(status, `Error: ${err.message}`, 'err');
  }
});

// ============================================================
// Purchases
// ============================================================
async function loadPurchases(reset = false) {
  if (reset) {
    state.purchases = [];
    state.lastToken = null;
  }

  const filterEl = document.getElementById('filter-select');
  const filter = filterEl ? filterEl.value : 'all';
  const list = document.getElementById('purchases-list');

  if (reset) list.innerHTML = '<div class="empty-state">Loading…</div>';

  try {
    const data = await api(`${API_BASE}/purchases`);
    let items = data.items ?? [];
    if (filter === 'new') items = items.filter(i => !isDownloaded(i.saleItemId));

    state.purchases = reset ? items : [...state.purchases, ...items];
    state.lastToken = data.lastToken;
    state.moreAvailable = !!data.moreAvailable;
    renderPurchases();
    document.getElementById('btn-load-more').disabled = !state.moreAvailable;
  } catch (err) {
    list.innerHTML = `<div class="empty-state">Failed to load purchases.<br><small>${esc(err.message)}</small></div>`;
  }
}

function isDownloaded(saleId) {
  return !!document.querySelector(`.download-job.status-done[data-sale="${saleId}"]`);
}

function renderPurchases() {
  const list = document.getElementById('purchases-list');
  if (!state.purchases.length) {
    list.innerHTML = '<div class="empty-state">No purchases found.<br>Sign in via Settings if not already done.</div>';
    return;
  }
  list.innerHTML = '';
  state.purchases.forEach(item => list.appendChild(makePurchaseCard(item)));
}

function formatOptions() {
  const fmts = ['mp3-320', 'flac', 'aac-hi', 'vorbis', 'alac', 'wav', 'aiff-lossless'];
  return fmts.map(f =>
    `<option value="${f}"${f === state.defaultFormat ? ' selected' : ''}>${f}</option>`
  ).join('');
}

function makePurchaseCard(item) {
  const card = document.createElement('div');
  card.className = 'purchase-card';

  const img = document.createElement('img');
  img.className = 'purchase-art';
  img.src = item.artUrl || '';
  img.alt = '';
  img.loading = 'lazy';

  const info = document.createElement('div');
  info.className = 'purchase-info';
  info.innerHTML = `
    <div class="purchase-artist">${esc(item.artist)}</div>
    <div class="purchase-title">${esc(item.title)}</div>
    <div class="purchase-date">${esc((item.purchased ?? '').slice(0, 10))}</div>
  `;

  const actions = document.createElement('div');
  actions.className = 'purchase-actions';

  const sel = document.createElement('select');
  sel.className = 'format-select';
  sel.innerHTML = formatOptions();

  const btn = document.createElement('button');
  btn.className = 'btn-primary';
  btn.textContent = 'Get';
  btn.onclick = () => triggerDownload(item, sel.value, btn);

  actions.appendChild(sel);
  actions.appendChild(btn);
  card.appendChild(img);
  card.appendChild(info);
  card.appendChild(actions);
  return card;
}

async function triggerDownload(item, fmt, btn) {
  btn.disabled = true;
  btn.innerHTML = '<span class="spinner"></span>';
  try {
    await api(`${API_BASE}/downloads`, {
      method: 'POST',
      body: JSON.stringify({
        saleItemId: item.saleItemId,
        itemType: item.itemType ?? 'album',
        redownloadUrl: item.redownloadUrl,
        artist: item.artist,
        title: item.title,
        format: fmt,
      }),
    });
    btn.textContent = '✓';
    btn.style.background = 'var(--success)';
    document.querySelector('.tab[data-page="downloads"]').click();
    startPolling();
  } catch (err) {
    btn.disabled = false;
    btn.textContent = 'Retry';
    btn.title = err.message;
  }
}

// ============================================================
// Wishlist
// ============================================================
async function loadWishlist() {
  const list = document.getElementById('wishlist-list');
  list.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const data = await api(`${API_BASE}/wishlist`);
    console.log('[bcMoosic] wishlist response keys:', Object.keys(data), 'items:', data.items?.length, 'raw:', JSON.stringify(data).slice(0, 200));
    state.wishlistLoaded = true;
    if (!data.items?.length) {
      list.innerHTML = '<div class="empty-state">Wishlist is empty.</div>';
      return;
    }
    list.innerHTML = '';
    data.items.forEach(item => list.appendChild(makeWishlistCard(item)));
  } catch (err) {
    list.innerHTML = `<div class="empty-state">Failed to load wishlist.<br><small>${esc(err.message)}</small></div>`;
  }
}

function makeWishlistCard(item) {
  const card = document.createElement('div');
  card.className = 'purchase-card';

  const img = document.createElement('img');
  img.className = 'purchase-art';
  img.src = item.artUrl || '';
  img.alt = '';
  img.loading = 'lazy';

  const info = document.createElement('div');
  info.className = 'purchase-info';
  info.innerHTML = `
    <div class="purchase-artist">${esc(item.artist)}</div>
    <div class="purchase-title">${esc(item.title)}</div>
    <div class="purchase-date">${esc(item.itemType)}</div>
  `;

  const actions = document.createElement('div');
  actions.className = 'purchase-actions';

  const btn = document.createElement('a');
  btn.className = 'btn-ghost';
  btn.href = item.itemUrl || '#';
  btn.target = '_blank';
  btn.rel = 'noopener';
  btn.textContent = 'Buy \u2197';

  actions.appendChild(btn);
  card.appendChild(img);
  card.appendChild(info);
  card.appendChild(actions);
  return card;
}

// ============================================================
// Following
// ============================================================
async function loadFollowing() {
  const list = document.getElementById('following-list');
  list.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const data = await api(`${API_BASE}/following`);
    state.followingLoaded = true;
    state.followingBands = data.bands || [];
    if (!data.bands?.length) {
      list.innerHTML = '<div class="empty-state">Not following anyone yet.</div>';
      return;
    }
    list.innerHTML = '';
    data.bands.forEach(band => {
      const row = document.createElement('div');
      row.className = 'following-row';
      row.innerHTML = `
        <span class="following-name">${esc(band.name)}</span>
        <a class="btn-ghost" href="${esc(band.url)}" target="_blank" rel="noopener">Open \u2197</a>
      `;
      list.appendChild(row);
    });
  } catch (err) {
    list.innerHTML = `<div class="empty-state">Failed to load following.<br><small>${esc(err.message)}</small></div>`;
  }
}

// ============================================================
// Downloads
// ============================================================
async function refreshDownloads() {
  try {
    const jobs = await api(`${API_BASE}/downloads`);
    renderDownloads(jobs);
    const active = jobs.filter(j => ['queued', 'downloading', 'extracting', 'organizing'].includes(j.status));
    if (!active.length && state.pollTimer) {
      clearInterval(state.pollTimer);
      state.pollTimer = null;
    }
  } catch (err) {
    console.error('Poll failed:', err);
  }
}

function startPolling() {
  if (state.pollTimer) return;
  state.pollTimer = setInterval(refreshDownloads, 1500);
}

function renderDownloads(jobs) {
  const list = document.getElementById('downloads-list');
  if (!jobs.length) {
    list.innerHTML = '<div class="empty-state">No downloads yet.</div>';
    return;
  }
  list.innerHTML = '';
  jobs.forEach(job => list.appendChild(makeJobCard(job)));
}

const STATUS_LABELS = {
  queued:      'Queued',
  downloading: 'Downloading',
  extracting:  'Extracting',
  organizing:  'Organizing',
  done:        'Done',
  error:       'Error',
  cancelled:   'Cancelled',
};

function makeJobCard(job) {
  const card = document.createElement('div');
  card.className = `download-job status-${job.status}`;
  card.dataset.sale = job.saleItemId;

  const label = STATUS_LABELS[job.status] ?? job.status;
  const progress = job.status === 'done' ? 100
    : job.status === 'error' ? 100
    : job.progress ?? 0;

  const metaText = job.status === 'downloading'
    ? `${job.format} · ${label} ${progress}%`
    : `${job.format} · ${label}`;

  const errorText = job.error ? `<div class="job-path" style="color:var(--error)">${esc(job.error)}</div>` : '';
  const pathText  = job.destPath ? `<div class="job-path">→ ${esc(job.destPath)}</div>` : '';

  card.innerHTML = `
    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:3px">
      <div class="job-title">${esc(job.artist || '')}${job.artist && job.title ? ' — ' : ''}${esc(job.title || job.jobId)}</div>
      <span class="job-status-badge">${esc(label)}</span>
    </div>
    <div class="job-meta">${esc(metaText)}</div>
    <div class="progress-bar"><div class="progress-fill" style="width:${progress}%"></div></div>
    ${errorText}${pathText}
  `;
  return card;
}

// ============================================================
// Collection
// ============================================================
async function loadCollection() {
  const list = document.getElementById('collection-list');
  list.innerHTML = '<div class="empty-state">Loading…</div>';
  try {
    const [data, followingData] = await Promise.all([
      api(`${API_BASE}/collection/browse`),
      state.followingBands === null
        ? api(`${API_BASE}/following`).catch(() => ({ bands: [] }))
        : Promise.resolve({ bands: state.followingBands }),
    ]);
    if (state.followingBands === null) state.followingBands = followingData.bands || [];
    const followedMap = new Map(state.followingBands.map(b => [normalizeName(b.name), b.url]));

    state.collectionLoaded = true;
    if (!data.artists?.length) {
      list.innerHTML = '<div class="empty-state">No music in collection yet.</div>';
      return;
    }

    const sortKey = normalizeName;
    const sorted = [...data.artists].sort((a, b) => sortKey(a.name).localeCompare(sortKey(b.name)));

    const groups = {};
    const letterOrder = [];
    for (const artist of sorted) {
      const key = sortKey(artist.name);
      const letter = /^[a-z]/i.test(key) ? key[0].toUpperCase() : '#';
      if (!groups[letter]) { groups[letter] = []; letterOrder.push(letter); }
      groups[letter].push(artist);
    }

    const allLetters = ['#', ...Array.from({length: 26}, (_, i) => String.fromCharCode(65 + i))];
    const hasLetter = new Set(letterOrder);

    list.innerHTML = '';

    // Sticky A-Z nav
    const nav = document.createElement('div');
    nav.className = 'alpha-nav';
    for (const letter of allLetters) {
      const btn = document.createElement('button');
      btn.className = 'alpha-btn' + (hasLetter.has(letter) ? '' : ' empty');
      btn.textContent = letter;
      if (hasLetter.has(letter)) {
        btn.onclick = () => {
          const target = list.querySelector(`.alpha-letter[data-letter="${letter}"]`);
          if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        };
      }
      nav.appendChild(btn);
    }
    list.appendChild(nav);

    for (const letter of letterOrder) {
      const hdr = document.createElement('div');
      hdr.className = 'alpha-letter';
      hdr.dataset.letter = letter;
      hdr.textContent = letter;
      list.appendChild(hdr);

      for (const artist of groups[letter]) {
        const sec = document.createElement('div');
        sec.className = 'artist-section';
        const nameEl = document.createElement('div');
        nameEl.className = 'artist-name';
        nameEl.textContent = artist.name;
        const followedUrl = followedMap.get(normalizeName(artist.name));
        if (followedUrl) {
          nameEl.classList.add('bc-followed');
          const link = document.createElement('a');
          link.className = 'bc-follow-link';
          link.href = followedUrl;
          link.target = '_blank';
          link.rel = 'noopener';
          link.title = 'Following on Bandcamp';
          link.textContent = 'bc \u2197';
          nameEl.appendChild(link);
        }
        sec.appendChild(nameEl);
        artist.albums.forEach(album => {
          const row = document.createElement('div');
          row.className = 'album-row';
          row.innerHTML = `
            <span>${esc(album.name)}</span>
            <span class="album-tracks">${album.tracks} track${album.tracks !== 1 ? 's' : ''}</span>
          `;
          sec.appendChild(row);
        });
        list.appendChild(sec);
      }
    }
  } catch (err) {
    list.innerHTML = `<div class="empty-state">Failed to load collection.<br><small>${esc(err.message)}</small></div>`;
  }
}

// ============================================================
// Filter / load more
// ============================================================
document.getElementById('filter-select')?.addEventListener('change', () => loadPurchases(true));
document.getElementById('btn-load-more')?.addEventListener('click', () => loadPurchases(false));

// ============================================================
// Init
// ============================================================
(async () => {
  const authed = await checkAuth();
  if (!authed) {
    document.querySelector('.tab[data-page="settings"]').click();
  } else {
    loadPurchases(true);
  }
})();
