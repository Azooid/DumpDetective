/* ── Navigation builder ───────────────────────────────────────────────── */
(function () {
  const nav = document.getElementById('nav-list');
  const root = document.getElementById('report-root');
  if (!nav || !root) return;

  function shortTitle(t) {
    return (t || '')
      .replace(/^Dump Detective\s*[—\-]\s*/i, '')
      .replace(/^Per-Dump\s+/i, '');
  }

  let curL1Div = null;
  let curL2Items = null;

  root.querySelectorAll('.hero').forEach(function (h) {
    const id = h.id;
    if (!id) return;

    const num = id.match(/\d+$/)?.[0];
    const raw = h.querySelector('.hero-title')?.textContent?.trim() ?? '';
    const level = parseInt(h.dataset.navLevel ?? '1', 10) || 1;

    if (level === 1) {
      curL1Div = document.createElement('div');
      curL1Div.className = 'nav-chapter';
      curL2Items = null;

      const titleA = document.createElement('a');
      titleA.href = '#' + id;
      titleA.className = 'nav-title';
      titleA.textContent = shortTitle(raw);
      titleA.title = raw;
      curL1Div.appendChild(titleA);

      if (num) {
        const chBody = document.getElementById('chb' + num);
        if (chBody) {
          Array.from(chBody.children)
            .filter(c => c.classList?.contains('card'))
            .forEach(function (c) {
              const ca = document.createElement('a');
              ca.href = '#' + c.id;
              ca.className = 'nav-card';

              const hdr = c.querySelector('.card-title');
              if (hdr) {
                const cl = hdr.cloneNode(true);
                cl.querySelectorAll('.tip-wrap').forEach(e => e.remove());
                ca.textContent = cl.textContent.replace('▾', '').trim();
              } else {
                ca.textContent = c.id;
              }

              ca.title = ca.textContent;
              curL1Div.appendChild(ca);
            });
        }
      }

      nav.appendChild(curL1Div);

    } else if (level === 2) {
      if (!curL1Div) return;

      if (h.dataset.navGroup === '1') {
        const subWrap = document.createElement('div');
        subWrap.className = 'nav-sub-chapters';

        const a = document.createElement('a');
        a.href = '#' + id;
        a.className = 'nav-sub-title';
        a.textContent = shortTitle(raw);
        a.title = raw;

        a.addEventListener('click', function (e) {
          e.preventDefault();
          const willOpen = !subWrap.classList.contains('open');
          subWrap.classList.toggle('open');
          subWrap.dataset.userOpen = willOpen ? '1' : '0';
          if (!willOpen) subWrap.dataset.autoOpen = '0';
        });

        subWrap.dataset.userOpen = '0';
        subWrap.dataset.autoOpen = '0';
        subWrap.appendChild(a);

        curL2Items = document.createElement('div');
        curL2Items.className = 'nav-sub-items';
        subWrap.appendChild(curL2Items);

        curL1Div.appendChild(subWrap);
      } else {
        if (!curL2Items) return;
        const a = document.createElement('a');
        a.href = '#' + id;
        a.textContent = shortTitle(raw);
        a.title = raw;
        curL2Items.appendChild(a);
      }
    } else if (level >= 3) {
      if (!curL2Items) return;
      const a = document.createElement('a');
      a.href = '#' + id;
      a.textContent = shortTitle(raw);
      a.title = raw;
      curL2Items.appendChild(a);
    }
  });
})();

/* ── Nav filter ───────────────────────────────────────────────────────── */
function filterNav(q) {
  q = (q || '').trim();
  const btn = document.getElementById('search-clear');
  if (btn) btn.classList.toggle('vis', q.length > 0);

  q = q.toLowerCase();
  const allLinks = document.querySelectorAll('#nav-list a');

  if (!q) {
    allLinks.forEach(a => a.style.display = '');
    document.querySelectorAll('.nav-sub-chapters').forEach(sc => sc.style.display = '');
    return;
  }

  allLinks.forEach(a => {
    a.style.display = a.textContent.toLowerCase().includes(q) ? '' : 'none';
  });

  document.querySelectorAll('.nav-sub-chapters').forEach(sc => {
    const anyVis = Array.from(sc.querySelectorAll('a')).some(a => a.style.display !== 'none');
    sc.style.display = anyVis ? 'block' : 'none';
    if (anyVis) sc.classList.add('open');
  });
}

function clearNavSearch() {
  const sb = document.getElementById('search-box');
  if (sb) {
    sb.value = '';
    sb.focus();
    filterNav('');
  }
}

/* ── Active nav highlight (track only generated nav-track elements with nav links) ─── */
(function () {
  const navLinks = Array.from(document.querySelectorAll('#nav-list a'));
  if (!navLinks.length) return;

  const navIdSet = new Set(
    navLinks
      .map(a => (a.getAttribute('href') || '').replace(/^#/, ''))
      .filter(Boolean)
  );

  // Observe a single generated class from HtmlSink, then filter to ids that exist in nav.
  const targets = Array.from(document.querySelectorAll('.nav-track')).filter(el => navIdSet.has(el.id));
  if (!targets.length) return;

  let activeId = null;
  const visible = new Map();

  function syncOpenGroups(activeLink) {
    document.querySelectorAll('.nav-sub-chapters').forEach(group => {
      const hasActive = !!activeLink && group.contains(activeLink);

      if (hasActive) {
        if (!group.classList.contains('open')) {
          group.classList.add('open');
          if (group.dataset.userOpen !== '1') group.dataset.autoOpen = '1';
        }
      } else if (group.dataset.autoOpen === '1' && group.dataset.userOpen !== '1') {
        group.classList.remove('open');
        group.dataset.autoOpen = '0';
      }
    });
  }

  function applyActive(id) {
    if (!id) return;
    if (!navIdSet.has(id)) return;

    if (id === activeId) {
      const current = navLinks.find(a => a.getAttribute('href') === '#' + id);
      syncOpenGroups(current || null);
      return;
    }

    activeId = id;
    let current = null;

    navLinks.forEach(a => {
      const on = a.getAttribute('href') === '#' + id;
      a.classList.toggle('active', on);
      if (on) current = a;
    });

    syncOpenGroups(current);
  }

  function chooseBestVisible() {
    if (visible.size === 0) return null;

    let best = null;
    let bestScore = Infinity;

    visible.forEach(entry => {
      const rect = entry.target.getBoundingClientRect();
      const isHero = entry.target.dataset.trackKind === 'hero';
      const topBias = isHero ? -90 : -130;
      const dist = Math.abs(rect.top - topBias);

      // Prefer the closest top-aligned element, but keep sections slightly stickier.
      const adjusted = dist + (isHero ? 12 : 0);
      if (adjusted < bestScore) {
        bestScore = adjusted;
        best = entry.target;
      }
    });

    return best;
  }

  const observer = new IntersectionObserver((entries) => {
    entries.forEach(entry => {
      const id = entry.target.id;
      if (!id || !navIdSet.has(id)) return;
      if (entry.isIntersecting) visible.set(id, entry);
      else visible.delete(id);
    });

    const best = chooseBestVisible();
    if (best?.id) {
      applyActive(best.id);
      return;
    }

    // No currently intersecting section: keep existing active until next target activates.
    if (activeId) applyActive(activeId);
  }, {
    root: null,
    rootMargin: '-18% 0px -68% 0px',
    threshold: [0, 0.2, 0.4, 0.6]
  });

  targets.forEach(t => observer.observe(t));

  requestAnimationFrame(() => {
    const initial = targets.find(t => t.getBoundingClientRect().top >= 0) || targets[0];
    if (initial?.id) applyActive(initial.id);
  });
})();

/* ── Tooltip ───────────────────────────────────────────────────────── */
(function () {
  const ftip = document.createElement('div');
  ftip.id = 'ftip';
  document.body.appendChild(ftip);

  window.showTip = function (el) {
    const t = el?.dataset?.tip;
    if (!t) return;

    ftip.innerHTML = t;
    ftip.style.visibility = 'hidden';
    ftip.style.display = 'block';

    const tw = ftip.offsetWidth;
    const th = ftip.offsetHeight;

    ftip.style.visibility = 'visible';

    const r = el.getBoundingClientRect();
    let x = r.right + 10;
    if (x + tw > window.innerWidth - 8) x = r.left - tw - 10;

    let y = r.top + r.height / 2 - th / 2;
    y = Math.max(8, Math.min(y, window.innerHeight - th - 8));

    ftip.style.left = x + 'px';
    ftip.style.top = y + 'px';
  };

  window.hideTip = () => ftip.style.display = 'none';
})();

/* ── Back-to-top ───────────────────────────────────────────────────── */
(function () {
  const btn = document.getElementById('back-top');
  if (!btn) return;

  window.addEventListener('scroll', () => {
    btn.classList.toggle('vis', window.scrollY > 400);
  }, { passive: true });
})();

/* ── Table sort + paging ───────────────────────────────────────────── */
var _sortState = {};
var _tableState = {};
var _tablePageSize = 50;

function parsePageSize(v) {
  var n = parseInt(v, 10);
  if (!isFinite(n) || n <= 0) return 50;
  return n;
}

window.setGlobalPageSize = function (v) {
  _tablePageSize = parsePageSize(v);
  Object.keys(_tableState).forEach(function (k) {
    var st = _tableState[k];
    if (st.pageSizeOverride) return;
    st.pageSize = _tablePageSize;
    st.page = 1;
    applyTablePage(st);
  });
};

window.setTablePageSize = function (tid, v) {
  var st = ensureTableState(tid);
  if (!st) return;

  if (v === 'global') {
    st.pageSizeOverride = 0;
    st.pageSize = _tablePageSize;
  } else {
    var n = parsePageSize(v);
    st.pageSizeOverride = n;
    st.pageSize = n;
  }

  st.page = 1;
  applyTablePage(st);
};

function ensureTableState(tid) {
  if (_tableState[tid]) return _tableState[tid];
  var tbl = document.getElementById('t' + tid);
  if (!tbl || !tbl.tBodies || !tbl.tBodies[0]) return null;

  var st = {
    tid: tid,
    tbl: tbl,
    rows: Array.from(tbl.tBodies[0].rows),
    filtered: [],
    page: 1,
    pages: 1,
    pageSize: _tablePageSize,
    pageSizeOverride: 0,
    query: ''
  };

  st.filtered = st.rows.slice();
  _tableState[tid] = st;
  ensurePagerUi(st);
  applyTablePage(st);
  return st;
}

function ensurePagerUi(st) {
  var toolbar = document.getElementById('ts' + st.tid)?.closest('.table-toolbar');
  if (!toolbar) return;
  if (document.getElementById('tp' + st.tid)) return;

  var pager = document.createElement('div');
  pager.id = 'tp' + st.tid;
  pager.className = 'tbl-pager';
  pager.innerHTML =
    '<select class="tbl-page-size-select" id="tpSize' + st.tid + '" title="Rows per page for this table">' +
    '<option value="global">Global</option>' +
    '<option value="10">10</option>' +
    '<option value="50">50</option>' +
    '<option value="100">100</option>' +
    '<option value="500">500</option>' +
    '<option value="1000">1000</option>' +
    '</select>' +
    '<button class="tbl-page-btn" id="tpPrev' + st.tid + '" title="Previous page">‹</button>' +
    '<select class="tbl-page-select" id="tpSel' + st.tid + '" title="Select page"></select>' +
    '<button class="tbl-page-btn" id="tpNext' + st.tid + '" title="Next page">›</button>' +
    '<span class="tbl-page-meta" id="tpMeta' + st.tid + '"></span>';
  toolbar.appendChild(pager);

  document.getElementById('tpSize' + st.tid).onchange = function (e) { setTablePageSize(st.tid, e.target.value); };
  document.getElementById('tpPrev' + st.tid).onclick = function () { gotoPage(st.tid, st.page - 1); };
  document.getElementById('tpNext' + st.tid).onclick = function () { gotoPage(st.tid, st.page + 1); };
  document.getElementById('tpSel' + st.tid).onchange = function (e) { gotoPage(st.tid, parseInt(e.target.value, 10)); };
}

function gotoPage(tid, page) {
  var st = ensureTableState(tid);
  if (!st) return;
  var p = Math.max(1, Math.min(page || 1, st.pages));
  if (st.page !== p) st.page = p;
  applyTablePage(st);
}

function updatePagerUi(st) {
  var total = st.filtered.length;
  st.pages = Math.max(1, Math.ceil(total / st.pageSize));
  if (st.page > st.pages) st.page = st.pages;

  var pager = document.getElementById('tp' + st.tid);
  var prev = document.getElementById('tpPrev' + st.tid);
  var next = document.getElementById('tpNext' + st.tid);
  var sel = document.getElementById('tpSel' + st.tid);
  var sizeSel = document.getElementById('tpSize' + st.tid);
  var meta = document.getElementById('tpMeta' + st.tid);
  if (!pager || !prev || !next || !sel || !meta || !sizeSel) return;

  pager.style.display = total > 0 ? 'flex' : 'none';

  var opts = [];
  for (var i = 1; i <= st.pages; i++) opts.push('<option value="' + i + '">Page ' + i + '</option>');
  sel.innerHTML = opts.join('');
  sel.value = String(st.page);
  sizeSel.value = st.pageSizeOverride ? String(st.pageSizeOverride) : 'global';

  prev.disabled = st.page <= 1;
  next.disabled = st.page >= st.pages;

  var start = total === 0 ? 0 : ((st.page - 1) * st.pageSize + 1);
  var end = Math.min(st.page * st.pageSize, total);
  meta.textContent = start.toLocaleString() + '-' + end.toLocaleString() + ' of ' + total.toLocaleString();
}

function applyTablePage(st) {
  var total = st.filtered.length;
  var start = (st.page - 1) * st.pageSize;
  var end = start + st.pageSize;

  st.rows.forEach(function (r) { r.style.display = 'none'; });
  st.filtered.slice(start, end).forEach(function (r) { r.style.display = ''; });

  updatePagerUi(st);

  var rc = document.getElementById('rc' + st.tid);
  if (rc) {
    if (!st.query) rc.textContent = total.toLocaleString() + ' rows';
    else rc.textContent = total.toLocaleString() + ' of ' + st.rows.length.toLocaleString() + ' rows';
  }
}

function sortTable(tid, col) {
  const st = ensureTableState(tid);
  if (!st) return;
  const tbl = st.tbl;
  const key = tid + '_' + col;
  const asc = _sortState[key] !== true;
  _sortState[key] = asc;

  tbl.querySelectorAll('th').forEach(function (th, i) {
    th.classList.remove('sort-asc', 'sort-desc');
    if (i === col) th.classList.add(asc ? 'sort-asc' : 'sort-desc');
  });

  const tbody = tbl.tBodies[0];
  const rows = Array.from(tbody.rows);
  rows.sort(function (a, b) {
    const av = (a.cells[col]?.textContent ?? '').trim();
    const bv = (b.cells[col]?.textContent ?? '').trim();

    const toBytes = function (s) {
      const m = s.match(/^([\d,.]+)\s*(B|KB|MB|GB|TB)$/i);
      if (!m) return NaN;
      const mul = { B: 1, KB: 1024, MB: 1048576, GB: 1073741824, TB: 1099511627776 };
      return parseFloat(m[1].replace(/,/g, '')) * (mul[m[2].toUpperCase()] || 1);
    };

    const ab = toBytes(av), bb = toBytes(bv);
    if (!isNaN(ab) && !isNaN(bb)) return asc ? ab - bb : bb - ab;

    const an = parseFloat(av.replace(/[, ]/g, ''));
    const bn = parseFloat(bv.replace(/[, ]/g, ''));
    if (!isNaN(an) && !isNaN(bn)) return asc ? an - bn : bn - an;

    return asc ? av.localeCompare(bv) : bv.localeCompare(av);
  });

  rows.forEach(function (r) { tbody.appendChild(r); });

  st.rows = Array.from(tbody.rows);
  const q = (document.getElementById('ts' + tid)?.value ?? '').trim().toLowerCase();
  st.query = q;
  st.filtered = st.rows.filter(function (r) { return !q || r.textContent.toLowerCase().includes(q); });
  st.page = 1;
  applyTablePage(st);
}

function filterTable(tid) {
  const st = ensureTableState(tid);
  if (!st) return;

  const q = (document.getElementById('ts' + tid)?.value ?? '').trim().toLowerCase();
  st.query = q;
  st.filtered = st.rows.filter(function (r) { return !q || r.textContent.toLowerCase().includes(q); });
  st.page = 1;
  applyTablePage(st);
}

/* ── Table pager init ───────────────────────────────────────────────── */
(function () {
  var gs = document.getElementById('global-page-size');
  if (gs) {
    _tablePageSize = parsePageSize(gs.value);
    gs.value = String(_tablePageSize);
  }

  document.querySelectorAll('table.data-table[id^="t"]').forEach(function (tbl) {
    var tid = parseInt(tbl.id.slice(1), 10);
    if (!isNaN(tid)) ensureTableState(tid);
  });
})();

/* ── CSV export ───────────────────────────────────────────────────── */
window.exportCsv = function (tid) {
  const tbl = document.getElementById('t' + tid);
  if (!tbl) return;

  const lines = [];

  lines.push(Array.from(tbl.querySelectorAll('thead th')).map(function (th) {
    return '"' + th.textContent.replace(/[⇅↑↓]/g, '').trim().replace(/"/g, '""') + '"';
  }).join(','));

  Array.from(tbl.tBodies[0].rows).forEach(function (r) {
    if (r.style.display === 'none') return;
    lines.push(Array.from(r.cells).map(function (c) {
      return '"' + c.textContent.trim().replace(/"/g, '""') + '"';
    }).join(','));
  });

  const blob = new Blob([lines.join('\r\n')], { type: 'text/csv' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'export_t' + tid + '.csv';
  a.click();
  URL.revokeObjectURL(a.href);
};