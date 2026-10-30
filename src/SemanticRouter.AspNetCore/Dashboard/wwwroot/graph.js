/* Force-directed graph on a canvas, for the memory map. Plain JavaScript, no dependencies.
   MemoryGraph.create(canvas, { onSelect(node), onHover(node, x, y) }) returns
   { setData(nodes, links), setStyle(fn), setVisible(fn), select(id), centerOn(id), pulse(id, color), refreshColors(), fit(), destroy() }.
   Nodes are objects with at least { id, title }; links are [fromIndex, toIndex] pairs. Positions survive setData by id. */
(function () {
  'use strict';

  const REPULSION = 2600;      // strength of node-node repulsion (within one grid cell)
  const CELL = 110;            // grid cell size: repulsion reaches about this far
  const LINK_LENGTH = 62;
  const LINK_STRENGTH = 0.07;
  const GRAVITY = 0.004;
  const DAMPING = 0.55;
  const PULSE_MS = 1800;

  function create(canvas, options) {
    const opts = options || {};
    const ctx = canvas.getContext('2d');
    let nodes = [];
    let links = [];
    let byId = new Map();
    let neighbours = new Map();
    let view = { x: 0, y: 0, k: 1 };
    let userMoved = false;
    let alpha = 1;
    let raf = 0;
    let hover = null;
    let selected = null;
    let pointer = null;   // { mode: 'drag'|'pan', node, startX, startY, moved, vx, vy }
    let dpr = window.devicePixelRatio || 1;
    let width = 0;
    let height = 0;
    let style = () => ({ fill: colors.faint });
    let visible = () => true;
    const pulses = new Map();
    let colors = readColors();

    function readColors() {
      const css = getComputedStyle(document.documentElement);
      const v = (name, fallback) => (css.getPropertyValue(name) || '').trim() || fallback;
      return {
        accent: v('--accent', '#228fa7'),
        signal: v('--signal', '#d08c34'),
        text: v('--text', '#e8edf0'),
        muted: v('--text-muted', '#94a3ac'),
        faint: v('--text-faint', '#66757e'),
        link: v('--border-strong', '#37434c'),
        bg: v('--bg-elevated', '#181d21'),
        font: v('--font-body', 'system-ui, sans-serif'),
      };
    }

    function resize() {
      const rect = canvas.getBoundingClientRect();
      dpr = window.devicePixelRatio || 1;
      width = Math.max(1, rect.width);
      height = Math.max(1, rect.height);
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      if (!userMoved) fit(true);
      request();
    }

    function setData(newNodes, newLinks) {
      const previous = byId;
      // A node keeps its position when it was in the previous data, or is the same object handed to a new graph.
      const placed = (n) => previous.get(n.id) || (typeof n.x === 'number' && isFinite(n.x) ? { x: n.x, y: n.y, fx: n.fx, fy: n.fy } : null);
      const settled = newNodes.length > 0 && newNodes.every(placed);
      nodes = newNodes.map((n, i) => {
        const old = placed(n);
        const angle = i * 2.399963;
        const r = 12 * Math.sqrt(i + 1);
        return Object.assign(n, {
          index: i,
          x: old ? old.x : Math.cos(angle) * r,
          y: old ? old.y : Math.sin(angle) * r,
          vx: 0, vy: 0,
          fx: old ? old.fx : null, fy: old ? old.fy : null,
          degree: 0,
        });
      });
      byId = new Map(nodes.map((n) => [n.id, n]));
      neighbours = new Map(nodes.map((n) => [n, new Set()]));
      links = [];
      for (const [a, b] of newLinks) {
        const s = nodes[a]; const t = nodes[b];
        if (!s || !t || s === t) continue;
        links.push({ s, t });
        s.degree++; t.degree++;
        neighbours.get(s).add(t); neighbours.get(t).add(s);
      }
      for (const n of nodes) n.r = Math.min(16, 3.2 + Math.sqrt(n.degree) * 1.7);
      if (hover && !byId.has(hover.id)) hover = null;
      if (selected) selected = byId.get(selected.id) || null;
      alpha = settled ? Math.min(alpha, 0.05) : 1;
      request();
    }

    function tick() {
      // Repulsion between nodes in the same or adjacent grid cells: O(n) per tick instead of O(n²).
      const grid = new Map();
      for (const n of nodes) {
        const key = Math.floor(n.x / CELL) + ',' + Math.floor(n.y / CELL);
        let bucket = grid.get(key);
        if (!bucket) grid.set(key, bucket = []);
        bucket.push(n);
      }
      const cutoff = CELL * CELL;
      for (const a of nodes) {
        const cx = Math.floor(a.x / CELL); const cy = Math.floor(a.y / CELL);
        for (let gx = cx - 1; gx <= cx + 1; gx++) {
          for (let gy = cy - 1; gy <= cy + 1; gy++) {
            const bucket = grid.get(gx + ',' + gy);
            if (!bucket) continue;
            for (const b of bucket) {
              if (b.index <= a.index) continue;
              let dx = a.x - b.x; let dy = a.y - b.y;
              let d2 = dx * dx + dy * dy;
              if (d2 > cutoff) continue;
              if (d2 < 0.01) { dx = Math.random() - 0.5; dy = Math.random() - 0.5; d2 = dx * dx + dy * dy; }
              const d = Math.sqrt(d2);
              const f = REPULSION * alpha / Math.max(d2, 16);
              const fx = dx / d * f; const fy = dy / d * f;
              a.vx += fx; a.vy += fy; b.vx -= fx; b.vy -= fy;
            }
          }
        }
      }
      for (const l of links) {
        const dx = l.t.x - l.s.x; const dy = l.t.y - l.s.y;
        const d = Math.sqrt(dx * dx + dy * dy) || 1;
        const f = (d - LINK_LENGTH) / d * LINK_STRENGTH * alpha;
        l.s.vx += dx * f; l.s.vy += dy * f;
        l.t.vx -= dx * f; l.t.vy -= dy * f;
      }
      for (const n of nodes) {
        const g = GRAVITY * (n.degree ? 1 : 2.5) * alpha;
        n.vx -= n.x * g; n.vy -= n.y * g;
        if (n.fx != null) { n.x = n.fx; n.y = n.fy; n.vx = n.vy = 0; continue; }
        n.vx *= DAMPING; n.vy *= DAMPING;
        n.x += n.vx; n.y += n.vy;
      }
      alpha *= 0.985;
    }

    function fit(immediate) {
      const shown = nodes.filter(visible);
      if (!shown.length) { view = { x: width / 2, y: height / 2, k: 1 }; return; }
      let minX = Infinity; let minY = Infinity; let maxX = -Infinity; let maxY = -Infinity;
      for (const n of shown) { minX = Math.min(minX, n.x); maxX = Math.max(maxX, n.x); minY = Math.min(minY, n.y); maxY = Math.max(maxY, n.y); }
      // Padding in screen pixels, so small graphs are not shrunk by a margin meant for big ones.
      const k = Math.min(2.4, Math.max(0.05, Math.min((width - 90) / Math.max(1, maxX - minX), (height - 110) / Math.max(1, maxY - minY))));
      const target = { k, x: width / 2 - (minX + maxX) / 2 * k, y: height / 2 - (minY + maxY) / 2 * k };
      if (immediate) { view = target; return false; }
      view = { k: view.k + (target.k - view.k) * 0.15, x: view.x + (target.x - view.x) * 0.15, y: view.y + (target.y - view.y) * 0.15 };
      // Still moving: keep animating until the view has settled on the target.
      return Math.abs(target.k - view.k) > 0.002 * target.k || Math.abs(target.x - view.x) > 0.5 || Math.abs(target.y - view.y) > 0.5;
    }

    function draw(now) {
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      ctx.clearRect(0, 0, width, height);
      ctx.save();
      ctx.translate(view.x, view.y);
      ctx.scale(view.k, view.k);

      const focus = hover || selected;
      const near = focus ? neighbours.get(focus) : null;
      const lit = (n) => !focus || n === focus || (near && near.has(n));

      ctx.lineWidth = 0.9 / view.k;
      ctx.strokeStyle = colors.link;
      ctx.globalAlpha = focus ? 0.25 : 0.7;
      ctx.beginPath();
      for (const l of links) {
        if (!visible(l.s) || !visible(l.t)) continue;
        if (focus && (l.s === focus || l.t === focus)) continue;
        ctx.moveTo(l.s.x, l.s.y); ctx.lineTo(l.t.x, l.t.y);
      }
      ctx.stroke();
      if (focus) {
        ctx.globalAlpha = 0.95;
        ctx.strokeStyle = colors.accent;
        ctx.lineWidth = 1.6 / view.k;
        ctx.beginPath();
        for (const l of links) {
          if ((l.s === focus || l.t === focus) && visible(l.s) && visible(l.t)) { ctx.moveTo(l.s.x, l.s.y); ctx.lineTo(l.t.x, l.t.y); }
        }
        ctx.stroke();
      }

      for (const n of nodes) {
        const shown = visible(n);
        const s = style(n);
        ctx.globalAlpha = !shown ? 0.07 : (lit(n) ? 1 : 0.22);
        ctx.beginPath();
        ctx.arc(n.x, n.y, n.r, 0, Math.PI * 2);
        if (s.fill) { ctx.fillStyle = s.fill; ctx.fill(); }
        if (s.stroke) { ctx.lineWidth = (s.strokeWidth || 1.4) / view.k; ctx.strokeStyle = s.stroke; ctx.stroke(); }
        if (n === selected) {
          ctx.globalAlpha = 1;
          ctx.lineWidth = 2 / view.k;
          ctx.strokeStyle = colors.text;
          ctx.beginPath(); ctx.arc(n.x, n.y, n.r + 3 / view.k, 0, Math.PI * 2); ctx.stroke();
        }
      }

      for (const [id, pulse] of pulses) {
        const n = byId.get(id);
        const t = (now - pulse.start) / PULSE_MS;
        if (!n || t >= 1) { pulses.delete(id); continue; }
        ctx.globalAlpha = 1 - t;
        ctx.strokeStyle = pulse.color || colors.signal;
        ctx.lineWidth = 2.5 / view.k;
        ctx.beginPath(); ctx.arc(n.x, n.y, n.r + (4 + t * 26) / view.k, 0, Math.PI * 2); ctx.stroke();
      }
      ctx.restore();

      // Labels in screen space so they stay readable at any zoom.
      ctx.globalAlpha = 1;
      ctx.font = `11px ${colors.font}`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'top';
      // Focused and pulsing notes first, then the biggest; a label that would overlap one already drawn is skipped.
      const candidates = [];
      for (const n of nodes) {
        if (!visible(n)) continue;
        const important = n === focus || (near && near.has(n)) || pulses.has(n.id);
        if (!important && (focus || n.r * view.k < 6.5)) continue;
        candidates.push({ n, important });
      }
      candidates.sort((a, b) => (b.important - a.important) || (b.n.r - a.n.r));
      const placed = [];
      for (const { n, important } of candidates.slice(0, 400)) {
        const sx = n.x * view.k + view.x; const sy = (n.y + n.r) * view.k + view.y + 3;
        if (sx < -100 || sx > width + 100 || sy < -20 || sy > height + 20) continue;
        const label = n.title.length > 32 ? n.title.slice(0, 31) + '…' : n.title;
        const w = ctx.measureText(label).width;
        const box = { x: sx - w / 2 - 2, y: sy - 1, w: w + 4, h: 14 };
        if (!important && placed.some((p) => box.x < p.x + p.w && p.x < box.x + box.w && box.y < p.y + p.h && p.y < box.y + box.h)) continue;
        placed.push(box);
        ctx.fillStyle = colors.bg;
        ctx.globalAlpha = 0.75;
        ctx.fillRect(box.x, box.y, box.w, box.h);
        ctx.globalAlpha = 1;
        ctx.fillStyle = important ? colors.text : colors.muted;
        ctx.fillText(label, sx, sy);
      }
    }

    function frame(now) {
      raf = 0;
      if (alpha > 0.008) tick();
      const fitting = !userMoved && fit(false);
      draw(now || performance.now());
      if (alpha > 0.008 || fitting || pulses.size || pointer) request();
    }

    function request() { if (!raf) raf = requestAnimationFrame(frame); }

    function toWorld(sx, sy) { return { x: (sx - view.x) / view.k, y: (sy - view.y) / view.k }; }

    function nodeAt(sx, sy) {
      const p = toWorld(sx, sy);
      let best = null; let bestD = Infinity;
      for (const n of nodes) {
        if (!visible(n)) continue;
        const dx = n.x - p.x; const dy = n.y - p.y;
        const d = dx * dx + dy * dy;
        const reach = n.r + 5 / view.k;
        if (d < reach * reach && d < bestD) { best = n; bestD = d; }
      }
      return best;
    }

    function local(ev) { const rect = canvas.getBoundingClientRect(); return { x: ev.clientX - rect.left, y: ev.clientY - rect.top }; }

    function onPointerDown(ev) {
      const p = local(ev);
      const node = nodeAt(p.x, p.y);
      canvas.setPointerCapture(ev.pointerId);
      pointer = { mode: node ? 'drag' : 'pan', node, startX: p.x, startY: p.y, moved: false, vx: view.x, vy: view.y };
      if (node) { node.fx = node.x; node.fy = node.y; }
      request();
    }

    function onPointerMove(ev) {
      const p = local(ev);
      if (pointer) {
        if (Math.abs(p.x - pointer.startX) + Math.abs(p.y - pointer.startY) > 3) pointer.moved = true;
        if (pointer.mode === 'drag' && pointer.moved) {
          const w = toWorld(p.x, p.y);
          pointer.node.fx = w.x; pointer.node.fy = w.y;
          alpha = Math.max(alpha, 0.25);
        } else if (pointer.mode === 'pan' && pointer.moved) {
          view.x = pointer.vx + p.x - pointer.startX; view.y = pointer.vy + p.y - pointer.startY;
          userMoved = true;
        }
        request();
        return;
      }
      const node = nodeAt(p.x, p.y);
      if (node !== hover) { hover = node; canvas.style.cursor = node ? 'pointer' : 'grab'; request(); }
      if (opts.onHover) opts.onHover(node, p.x, p.y);
    }

    function onPointerUp() {
      if (!pointer) return;
      const { node, moved, mode } = pointer;
      pointer = null;
      if (!moved) {
        if (node) { node.fx = node.fy = null; }
        selected = mode === 'drag' ? node : null;
        if (opts.onSelect) opts.onSelect(selected);
      }
      request();
    }

    function onWheel(ev) {
      ev.preventDefault();
      const p = local(ev);
      const factor = Math.exp(-ev.deltaY * 0.0015);
      const k = Math.min(8, Math.max(0.03, view.k * factor));
      view.x = p.x - (p.x - view.x) * (k / view.k);
      view.y = p.y - (p.y - view.y) * (k / view.k);
      view.k = k;
      userMoved = true;
      request();
    }

    function onDoubleClick(ev) {
      const p = local(ev);
      const node = nodeAt(p.x, p.y);
      if (node) { node.fx = node.fy = null; alpha = Math.max(alpha, 0.2); } else { userMoved = false; fit(true); }
      request();
    }

    function onLeave() { if (hover) { hover = null; request(); } if (opts.onHover) opts.onHover(null, 0, 0); }

    canvas.addEventListener('pointerdown', onPointerDown);
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerup', onPointerUp);
    canvas.addEventListener('pointercancel', onPointerUp);
    canvas.addEventListener('pointerleave', onLeave);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('dblclick', onDoubleClick);
    canvas.style.cursor = 'grab';
    canvas.style.touchAction = 'none';
    const observer = new ResizeObserver(resize);
    observer.observe(canvas);
    resize();

    return {
      setData,
      setStyle(fn) { style = fn; request(); },
      setVisible(fn) { visible = fn; request(); },
      select(id) { selected = id ? byId.get(id) || null : null; request(); },
      centerOn(id) {
        const n = byId.get(id);
        if (!n) return;
        userMoved = true;
        view.k = Math.max(view.k, 1.2);
        view.x = width / 2 - n.x * view.k; view.y = height / 2 - n.y * view.k;
        request();
      },
      pulse(id, color) { if (byId.has(id)) { pulses.set(id, { start: performance.now(), color }); request(); } },
      refreshColors() { colors = readColors(); request(); },
      fit() { userMoved = false; fit(true); request(); },
      redraw: request,
      destroy() {
        if (raf) cancelAnimationFrame(raf);
        raf = 0;
        observer.disconnect();
        canvas.removeEventListener('pointerdown', onPointerDown);
        canvas.removeEventListener('pointermove', onPointerMove);
        canvas.removeEventListener('pointerup', onPointerUp);
        canvas.removeEventListener('pointercancel', onPointerUp);
        canvas.removeEventListener('pointerleave', onLeave);
        canvas.removeEventListener('wheel', onWheel);
        canvas.removeEventListener('dblclick', onDoubleClick);
      },
    };
  }

  window.MemoryGraph = { create };
})();
