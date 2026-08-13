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

  function escHtml(s) {
    return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
  }

  // Counts issues inside a DOM subtree so the sidebar can show a caller
  // where to look without opening every one of the (often dozens of)
  // sub-report cards first.
  function sevCounts(el) {
    if (!el) return { crit: 0, warn: 0 };
    return {
      crit: el.querySelectorAll('.alert-crit, td.sev-crit').length,
      warn: el.querySelectorAll('.alert-warn, td.sev-warn').length,
    };
  }
  function addCounts(a, b) { return { crit: a.crit + b.crit, warn: a.warn + b.warn }; }
  function badgeHtml(c) {
    if (c.crit > 0) return '<span class="nav-badge nav-badge-crit" title="' + c.crit + ' critical">' + c.crit + '</span>';
    if (c.warn > 0) return '<span class="nav-badge nav-badge-warn" title="' + c.warn + ' warning">' + c.warn + '</span>';
    return '';
  }
  function renderLink(a, label) {
    a.innerHTML = '<span class="nav-label">' + escHtml(label) + '</span>';
  }
  function renderLinkWithBadge(a, label, counts) {
    a.innerHTML = '<span class="nav-label">' + escHtml(label) + '</span>' + badgeHtml(counts);
  }

  let curL1Div = null;
  let curL2Items = null;

  // Running totals so a chapter/group link's badge reflects everything
  // nested under it, not just its own (often empty) chapter body.
  let curL1TitleA = null, curL1Label = '', l1Totals = { crit: 0, warn: 0 };
  let curGroupTitleA = null, curGroupLabel = '', groupTotals = { crit: 0, warn: 0 };

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
      titleA.title = raw;
      curL1TitleA = titleA;
      curL1Label = shortTitle(raw);
      l1Totals = { crit: 0, warn: 0 };
      renderLink(titleA, curL1Label);
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

              let label;
              const hdr = c.querySelector('.card-title');
              if (hdr) {
                const cl = hdr.cloneNode(true);
                cl.querySelectorAll('.tip-wrap').forEach(e => e.remove());
                label = cl.textContent.replace('▾', '').trim();
              } else {
                label = c.id;
              }

              ca.title = label;
              const counts = sevCounts(c);
              l1Totals = addCounts(l1Totals, counts);
              renderLinkWithBadge(ca, label, counts);
              curL1Div.appendChild(ca);
            });
        }
      }

      renderLinkWithBadge(titleA, curL1Label, l1Totals);
      nav.appendChild(curL1Div);

    } else if (level === 2) {
      if (!curL1Div) return;

      if (h.dataset.navGroup === '1') {
        const subWrap = document.createElement('div');
        subWrap.className = 'nav-sub-chapters';

        const a = document.createElement('a');
        a.href = '#' + id;
        a.className = 'nav-sub-title';
        a.title = raw;
        curGroupTitleA = a;
        curGroupLabel = shortTitle(raw);
        groupTotals = { crit: 0, warn: 0 };
        renderLink(a, curGroupLabel);

        a.addEventListener('click', function (e) {
          e.preventDefault();
          // Always navigate to the chapter anchor
          const target = document.getElementById(id);
          if (target) target.scrollIntoView({ behavior: 'smooth', block: 'start' });
          // Only toggle open/close if sub-items are present (use local ref, not outer variable)
          const localItems = subWrap.querySelector('.nav-sub-items');
          if (localItems && localItems.children.length > 0) {
            const willOpen = !subWrap.classList.contains('open');
            subWrap.classList.toggle('open');
            subWrap.dataset.userOpen = willOpen ? '1' : '0';
            if (!willOpen) subWrap.dataset.autoOpen = '0';
          }
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
        a.title = raw;
        const label = shortTitle(raw);
        const counts = sevCounts(num ? document.getElementById('chb' + num) : null);
        groupTotals = addCounts(groupTotals, counts);
        l1Totals = addCounts(l1Totals, counts);
        renderLinkWithBadge(a, label, counts);
        curL2Items.appendChild(a);
        if (curGroupTitleA) renderLinkWithBadge(curGroupTitleA, curGroupLabel, groupTotals);
        if (curL1TitleA) renderLinkWithBadge(curL1TitleA, curL1Label, l1Totals);
      }
    } else if (level >= 3) {
      if (!curL2Items) return;
      const a = document.createElement('a');
      a.href = '#' + id;
      a.title = raw;
      const label = shortTitle(raw);
      const counts = sevCounts(num ? document.getElementById('chb' + num) : null);
      groupTotals = addCounts(groupTotals, counts);
      l1Totals = addCounts(l1Totals, counts);
      renderLinkWithBadge(a, label, counts);
      curL2Items.appendChild(a);
      if (curGroupTitleA) renderLinkWithBadge(curGroupTitleA, curGroupLabel, groupTotals);
      if (curL1TitleA) renderLinkWithBadge(curL1TitleA, curL1Label, l1Totals);
    }
  });

  // Post-process: nav-sub-chapters with no sub-items have no expand arrow,
  // so mark them so CSS can hide the toggle indicator.
  nav.querySelectorAll('.nav-sub-chapters').forEach(function (sc) {
    const items = sc.querySelector('.nav-sub-items');
    if (!items || items.children.length === 0) {
      sc.classList.add('nav-sub-empty');
    }
  });
})();

/* ── Auto-collapse clean sections ─────────────────────────────────────── *
 * #chb1 is always the first chapter body written to the document — for a
 * single-command report it's the only content; for `analyze --full` it's
 * the hand-curated Findings/Memory/... landing summary. Never auto-collapse
 * that. Every other card collapses unless it contains a critical/warning
 * signal, so opening the report shows only what needs attention instead of
 * dozens of fully expanded "nothing found here" sections.                 */
(function () {
  const root = document.getElementById('report-root');
  if (!root) return;

  root.querySelectorAll('.card').forEach(function (card) {
    if (card.closest('#chb1')) return;
    const hasIssue = card.querySelector('.alert-crit, .alert-warn, td.sev-crit, td.sev-warn');
    if (!hasIssue) card.classList.add('collapsed');
  });
})();

/* ── Triage banner ─────────────────────────────────────────────────────── *
 * A plain-language "start here" summary above all report content: how many
 * issues, how many sections were flagged, and one-click jumps — so a reader
 * doesn't have to scroll a wall of sections to find out what matters.      */
(function () {
  const root = document.getElementById('report-root');
  if (!root || document.getElementById('triage-banner')) return;

  const crit = root.querySelectorAll('.alert-crit, td.sev-crit').length;
  const warn = root.querySelectorAll('.alert-warn, td.sev-warn').length;
  const info = root.querySelectorAll('.alert-info, td.sev-info').length;

  const totalSections = root.querySelectorAll('.card').length;
  const cleanSections = root.querySelectorAll('.card.collapsed').length;
  const flaggedSections = totalSections - cleanSections;
  if (totalSections === 0) return;

  const banner = document.createElement('div');
  banner.id = 'triage-banner';

  const actions =
    (crit > 0 ? '<button class="tri-btn tri-btn-crit" onclick="jumpCrit()">Jump to critical ✗</button>' : '') +
    (warn > 0 ? '<button class="tri-btn tri-btn-warn" onclick="jumpWarn()">Jump to warning ⚠</button>' : '') +
    '<button class="exp-btn" onclick="expandAll()">⊞ Expand all</button>';

  if (crit > 0 || warn > 0) {
    const parts = [];
    if (crit > 0) parts.push(crit + ' critical');
    if (warn > 0) parts.push(warn + ' warning');
    banner.className = 'triage-banner triage-banner-issues';
    banner.innerHTML =
      '<div class="triage-icon">✗</div>' +
      '<div class="triage-text">' +
        '<div class="triage-headline">' + parts.join(', ') + ' issue' + ((crit + warn) > 1 ? 's' : '') + ' found — start here</div>' +
        '<div class="triage-sub">' + flaggedSections + ' of ' + totalSections + ' sections below are flagged and left expanded. ' +
        'The rest are collapsed because nothing stood out — click a title to expand it, or jump straight to what matters.</div>' +
      '</div>' +
      '<div class="triage-actions">' + actions + '</div>';
  } else {
    banner.className = 'triage-banner triage-banner-clean';
    banner.innerHTML =
      '<div class="triage-icon">✓</div>' +
      '<div class="triage-text">' +
        '<div class="triage-headline">No critical or warning signals found</div>' +
        '<div class="triage-sub">' + (info > 0 ? info + ' informational note(s) below for reference. ' : '') +
        'All ' + totalSections + ' sections analyzed came back clean.</div>' +
      '</div>' +
      '<div class="triage-actions">' + actions + '</div>';
  }

  root.insertBefore(banner, root.firstChild);
})();

/* ── Jump-to-evidence (by CLI command name) ──────────────────────────────
 * Correlation Signals / Action Queue cards reference the underlying
 * sub-report by its stable CLI command name (e.g. "heap-stats") rather than
 * a DOM id that depends on render order. Header() stamps that name onto the
 * chapter's hero via data-command; this resolves it at click time, expands
 * any collapsed ancestor card, and scrolls it into view.                  */
window.scrollToCommand = function (name) {
  if (!name) return;
  // A trend report with --full embeds one sub-report per dump, so the same
  // command name (e.g. "heap-stats") can appear once per dump. Dumps render in
  // chronological order, so the LAST match is always the most recent dump's
  // copy — exactly what Correlation Signals / Action Queue need, since both are
  // built from the latest snapshot. For a single-dump analyze report there's
  // only ever one match, so this is a no-op change there.
  const matches = document.querySelectorAll('.hero[data-command="' + name + '"]');
  const target = matches.length > 0 ? matches[matches.length - 1] : null;
  if (!target) return;
  const collapsed = target.closest('.card.collapsed');
  if (collapsed) collapsed.classList.remove('collapsed');
  target.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

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

/* ── Theme toggle ───────────────────────────────────────────────────── */
(function () {
  function isDark() {
    return document.documentElement.getAttribute('data-theme') === 'dark';
  }

  function setIcon() {
    var icon = document.getElementById('dark-icon');
    if (!icon) return;
    icon.textContent = isDark() ? '☀' : '🌙';
  }

  window.toggleDark = function () {
    var root = document.documentElement;
    if (isDark()) {
      root.removeAttribute('data-theme');
      localStorage.setItem('dd-theme', 'light');
    } else {
      root.setAttribute('data-theme', 'dark');
      localStorage.setItem('dd-theme', 'dark');
    }
    setIcon();
  };

  setIcon();
})();

/* ── Section card accordion controls ─────────────────────────────────── */
window.toggleCard = function (cardId) {
  const card = document.getElementById(cardId);
  if (!card || !card.classList.contains('card')) return;
  card.classList.toggle('collapsed');
};

window.expandAll = function () {
  document.querySelectorAll('#report-root .card.collapsed').forEach(function (card) {
    card.classList.remove('collapsed');
  });
};

window.collapseAll = function () {
  document.querySelectorAll('#report-root .card').forEach(function (card) {
    card.classList.add('collapsed');
  });
};

/* ── Critical alert jump ─────────────────────────────────────────────── */
(function () {
  var btn = document.getElementById('jump-crit');
  var lastIdx = -1;

  function getCrits() {
    var alerts = Array.from(document.querySelectorAll('#report-root .alert-crit'));
    var sevRows = Array.from(document.querySelectorAll('#report-root tr:has(td.sev-crit)'));
    // merge and sort by DOM order
    return alerts.concat(sevRows).sort(function (a, b) {
      return a.compareDocumentPosition(b) & 4 ? -1 : 1;
    });
  }

  function syncBtn() {
    if (!btn) return;
    btn.classList.toggle('vis', getCrits().length > 0);
  }

  window.jumpCrit = function () {
    var crits = getCrits();
    if (!crits.length) return;

    var nextIdx;
    if (lastIdx >= 0 && lastIdx < crits.length - 1) {
      nextIdx = lastIdx + 1;
    } else if (lastIdx >= crits.length - 1) {
      nextIdx = 0;
    } else {
      nextIdx = crits.findIndex(function (el) {
        return el.getBoundingClientRect().top > 80;
      });
      if (nextIdx < 0) nextIdx = 0;
    }

    var target = crits[nextIdx];
    if (!target) return;

    var collapsedCard = target.closest('.card.collapsed');
    if (collapsedCard) collapsedCard.classList.remove('collapsed');

    target.scrollIntoView({ behavior: 'smooth', block: 'center' });
    lastIdx = nextIdx;
  };

  syncBtn();
  window.addEventListener('load', syncBtn);
})();

/* ── Warning jump ────────────────────────────────────────────────────── */
(function () {
  var btn = document.getElementById('jump-warn');
  var lastIdx = -1;

  function getWarns() {
    var alerts = Array.from(document.querySelectorAll('#report-root .alert-warn'));
    var sevRows = Array.from(document.querySelectorAll('#report-root tr:has(td.sev-warn)'));
    return alerts.concat(sevRows).sort(function (a, b) {
      return a.compareDocumentPosition(b) & 4 ? -1 : 1;
    });
  }

  function syncBtn() {
    if (!btn) return;
    btn.classList.toggle('vis', getWarns().length > 0);
  }

  window.jumpWarn = function () {
    var items = getWarns();
    if (!items.length) return;
    var nextIdx;
    if (lastIdx >= 0 && lastIdx < items.length - 1) {
      nextIdx = lastIdx + 1;
    } else if (lastIdx >= items.length - 1) {
      nextIdx = 0;
    } else {
      nextIdx = items.findIndex(function (el) {
        return el.getBoundingClientRect().top > 80;
      });
      if (nextIdx < 0) nextIdx = 0;
    }
    var target = items[nextIdx];
    if (!target) return;
    var collapsedCard = target.closest('.card.collapsed');
    if (collapsedCard) collapsedCard.classList.remove('collapsed');
    target.scrollIntoView({ behavior: 'smooth', block: 'center' });
    lastIdx = nextIdx;
  };

  syncBtn();
  window.addEventListener('load', syncBtn);
})();

/* ── Info jump ───────────────────────────────────────────────────────── */
(function () {
  var btn = document.getElementById('jump-info');
  var lastIdx = -1;

  function getInfos() {
    var alerts = Array.from(document.querySelectorAll('#report-root .alert-info'));
    var sevRows = Array.from(document.querySelectorAll('#report-root tr:has(td.sev-info)'));
    return alerts.concat(sevRows).sort(function (a, b) {
      return a.compareDocumentPosition(b) & 4 ? -1 : 1;
    });
  }

  function syncBtn() {
    if (!btn) return;
    btn.classList.toggle('vis', getInfos().length > 0);
  }

  window.jumpInfo = function () {
    var items = getInfos();
    if (!items.length) return;
    var nextIdx;
    if (lastIdx >= 0 && lastIdx < items.length - 1) {
      nextIdx = lastIdx + 1;
    } else if (lastIdx >= items.length - 1) {
      nextIdx = 0;
    } else {
      nextIdx = items.findIndex(function (el) {
        return el.getBoundingClientRect().top > 80;
      });
      if (nextIdx < 0) nextIdx = 0;
    }
    var target = items[nextIdx];
    if (!target) return;
    var collapsedCard = target.closest('.card.collapsed');
    if (collapsedCard) collapsedCard.classList.remove('collapsed');
    target.scrollIntoView({ behavior: 'smooth', block: 'center' });
    lastIdx = nextIdx;
  };

  syncBtn();
  window.addEventListener('load', syncBtn);
})();

/* ── Severity summary bar (sidebar) ─────────────────────────────────── */
(function () {
  function countSev(alertCls, rowCls) {
    var a = document.querySelectorAll('#report-root .' + alertCls).length;
    var r = document.querySelectorAll('#report-root tr:has(td.' + rowCls + ')').length;
    return a + r;
  }

  function buildBar() {
    var bar = document.getElementById('sev-bar');
    if (!bar) return;
    bar.innerHTML = '';

    var specs = [
      { cls: 'sev-pill-crit', alertCls: 'alert-crit', rowCls: 'sev-crit', icon: '\u2717', jump: 'jumpCrit', label: 'Critical' },
      { cls: 'sev-pill-warn', alertCls: 'alert-warn', rowCls: 'sev-warn', icon: '\u26a0', jump: 'jumpWarn', label: 'Warning' },
      { cls: 'sev-pill-info', alertCls: 'alert-info', rowCls: 'sev-info', icon: '\u2139', jump: 'jumpInfo', label: 'Info' },
    ];

    var any = false;
    specs.forEach(function (s) {
      var n = countSev(s.alertCls, s.rowCls);
      if (n === 0) return;
      any = true;
      var pill = document.createElement('button');
      pill.className = 'sev-pill ' + s.cls;
      pill.title = s.label + ' — click to jump';
      pill.innerHTML = s.icon + ' ' + n;
      pill.onclick = function () { window[s.jump] && window[s.jump](); };
      bar.appendChild(pill);
    });

    bar.style.display = any ? '' : 'none';
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', buildBar);
  } else {
    buildBar();
  }
  window.addEventListener('load', buildBar);
})();

/* ── Call tree controls ──────────────────────────────────────────────── */
window.ctToggle = function (rowId, el) {
  var row = document.getElementById(rowId);
  if (!row) return;

  var isHidden = row.style.display === 'none' || getComputedStyle(row).display === 'none';
  row.style.display = isHidden ? 'table-row' : 'none';
  if (el && el.classList) el.classList.toggle('open', isHidden);

  if (!isHidden) {
    row.querySelectorAll('.ct-toggle.open').forEach(function (t) { t.classList.remove('open'); });
    row.querySelectorAll('.ct-children').forEach(function (child) { child.style.display = 'none'; });
  }
};

window.ctExpandAll = function (treeId) {
  var root = document.getElementById(treeId);
  if (!root) return;
  root.querySelectorAll('.ct-toggle').forEach(function (t) { t.classList.add('open'); });
  root.querySelectorAll('.ct-children').forEach(function (row) { row.style.display = 'table-row'; });
};

window.ctCollapseAll = function (treeId) {
  var root = document.getElementById(treeId);
  if (!root) return;
  root.querySelectorAll('.ct-toggle').forEach(function (t) { t.classList.remove('open'); });
  root.querySelectorAll('.ct-children').forEach(function (row) { row.style.display = 'none'; });
};

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

/* ── Sparkline hover tooltips ──────────────────────────────────────── */
(function () {
  var tip = null;

  function getTip() {
    if (!tip) {
      tip = document.createElement('div');
      tip.id = 'spark-tip';
      document.body.appendChild(tip);
    }
    return tip;
  }

  function fmtSize(v) {
    if (v >= 1073741824) return (v / 1073741824).toFixed(2) + ' GB';
    if (v >= 1048576) return (v / 1048576).toFixed(2) + ' MB';
    if (v >= 1024) return (v / 1024).toFixed(1) + ' KB';
    return v.toFixed(0) + ' B';
  }

  function fmtVal(v, unit, sizeMode) {
    return sizeMode ? fmtSize(v) : (v % 1 === 0 ? v.toFixed(0) : v.toFixed(1)) + (unit || '');
  }

  function initSvg(svg) {
    if (svg.dataset.hoverInit) return;
    svg.dataset.hoverInit = '1';

    var vals;
    try { vals = JSON.parse(svg.dataset.vals || '[]'); } catch (e) { return; }
    if (vals.length < 2) return;

    var unit = svg.dataset.unit || '';
    var sizeMode = svg.dataset.sizemode === '1';
    var color = svg.dataset.color || '#6366f1';
    var W = 400, Ht = 36;

    var min = vals[0], max = vals[0];
    for (var i = 1; i < vals.length; i++) {
      if (vals[i] < min) min = vals[i];
      if (vals[i] > max) max = vals[i];
    }
    var range = max - min || 1;

    var ns = 'http://www.w3.org/2000/svg';

    var xhair = svg.querySelector('.spark-xhair');
    var dot = svg.querySelector('.spark-dot');
    var overlay = svg.querySelector('.spark-overlay');
    if (!xhair || !dot || !overlay) return;

    // Apply per-series color
    xhair.style.stroke = color;
    dot.style.fill = color;

    function onMove(e) {
      var bbox = svg.getBoundingClientRect();
      var relX = Math.max(0, Math.min(1, (e.clientX - bbox.left) / bbox.width));
      var idx = Math.round(relX * (vals.length - 1));
      var v = vals[idx];
      var svgX = idx / (vals.length - 1) * W;
      var svgY = Ht - (v - min) / range * (Ht - 6) - 3;
      // Correct for non-uniform scaling: rx must cancel out the x-stretch
      var bbox2 = svg.getBoundingClientRect();
      var rx = bbox2.width > 0 ? (3.5 * W / bbox2.width) : 3.5;

      xhair.setAttribute('x1', svgX); xhair.setAttribute('x2', svgX);
      xhair.style.display = '';
      dot.setAttribute('cx', svgX); dot.setAttribute('cy', svgY);
      dot.setAttribute('rx', rx); dot.setAttribute('ry', 3.5);
      dot.style.display = '';

      var t = getTip();
      t.textContent = fmtVal(v, unit, sizeMode);
      t.style.display = 'block';
      t.style.left = (e.clientX + 14) + 'px';
      t.style.top = (e.clientY - 28) + 'px';
    }

    function onLeave() {
      xhair.style.display = 'none';
      dot.style.display = 'none';
      getTip().style.display = 'none';
    }

    overlay.addEventListener('mousemove', onMove);
    overlay.addEventListener('mouseleave', onLeave);
  }

  function initTooltips() {
    document.querySelectorAll('[data-tip]').forEach(function (el) {
      if (el.dataset.tipInit) return;
      if (el.classList.contains('tip-wrap')) return; // already wired to showTip/hideTip
      el.dataset.tipInit = '1';
      el.addEventListener('mouseenter', function (e) {
        var t = getTip();
        t.innerHTML = el.dataset.tip; // innerHTML so intentional <br> tags render
        t.style.display = 'block';
        t.style.left = (e.clientX + 14) + 'px';
        t.style.top = (e.clientY - 28) + 'px';
      });
      el.addEventListener('mousemove', function (e) {
        var t = getTip();
        t.style.left = (e.clientX + 14) + 'px';
        t.style.top = (e.clientY - 28) + 'px';
      });
      el.addEventListener('mouseleave', function () {
        getTip().style.display = 'none';
      });
    });
  }

  function initAll() {
    document.querySelectorAll('.mspark-svg[data-vals]').forEach(initSvg);
    initTooltips();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initAll);
  } else {
    initAll();
  }
})();
