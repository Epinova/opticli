// The halftone: the site's one decorative mark, a printed dot screen drawn on a canvas.
//   loop   the front page's centerpiece: a ripple in one field's ink passes through every few seconds, in turn,
//          and the dots under the pointer swell a little. It stops when the visitor pauses the page's animation.
//   once   an area page's opening: one ripple in the area's ink as the page opens, then it stays still.
//   still  the shelf field: a still dot screen fading out from its corner.
// Colours come from the page's tokens, read from the element (so --c is the area's ink, and dark mode follows).
// It draws only while on screen and the tab is visible; with reduced motion it draws one still frame.

const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

// 3D value noise for the dots' slow drift.
const perm = new Uint8Array(512);
{ let s = 7; const p = [...Array(256).keys()]; for (let i = 255; i > 0; i--) { s = (s * 16807) % 2147483647; const j = s % (i + 1); [p[i], p[j]] = [p[j], p[i]]; } for (let i = 0; i < 512; i++) perm[i] = p[i & 255]; }
const hash = (x, y, z) => perm[perm[perm[x & 255] + (y & 255)] + (z & 255)] / 255;
const fade = (t) => t * t * (3 - 2 * t);
const lerp = (a, b, t) => a + (b - a) * t;
function noise(x, y, z) {
  const xi = Math.floor(x), yi = Math.floor(y), zi = Math.floor(z);
  const u = fade(x - xi), v = fade(y - yi), w = fade(z - zi);
  const c = (dx, dy, dz) => hash(xi + dx, yi + dy, zi + dz);
  return lerp(lerp(lerp(c(0, 0, 0), c(1, 0, 0), u), lerp(c(0, 1, 0), c(1, 1, 0), u), v),
    lerp(lerp(c(0, 0, 1), c(1, 0, 1), u), lerp(c(0, 1, 1), c(1, 1, 1), u), v), w);
}

const rgb = (v) => {
  let h = v.trim().replace('#', '');
  if (h.length === 3) h = [...h].map((c) => c + c).join('');
  const n = parseInt(h, 16);
  return [n >> 16 & 255, n >> 8 & 255, n & 255];
};
const rgba = ([r, g, b], a) => `rgba(${r},${g},${b},${a})`;
const mix = (a, b, t) => a.map((v, i) => Math.round(v + (b[i] - v) * t));

const SPEED = { loop: 0.06, once: 0.12 }; // px per ms: the opening's one ripple is done in about five seconds
const GAP = 13;

export function halftone(el, { mode = 'loop', base = '--ink', ripples = ['--p1-ink', '--p2-ink', '--p3-ink'], strength = 1, origin = [0.5, 0.5], toggle = null } = {}) {
  const canvas = document.createElement('canvas');
  el.append(canvas);
  const ctx = canvas.getContext('2d');
  let W = 0, H = 0, colors;
  let waves = [], made = 0, nextWave = 0, clock = 20000;
  let raf = 0, last = 0, visible = false, done = mode === 'still' || (mode === 'once' && reduced);
  let paused = toggle ? toggle.hasAttribute('data-paused') : false;
  const pointer = { x: -1e4, y: -1e4 };

  const read = () => {
    const cs = getComputedStyle(el);
    colors = { base: rgb(cs.getPropertyValue(base)), waves: ripples.map((n) => rgb(cs.getPropertyValue(n))) };
  };

  function draw(t) {
    ctx.clearRect(0, 0, W, H);
    const reach = Math.hypot(W, H);
    const speed = SPEED[mode] || SPEED.loop;
    waves = waves.filter((w) => (t - w.born) * speed < reach + 80);
    const z = mode === 'loop' ? t * 0.00012 : 2.4;
    for (let y = GAP / 2; y < H; y += GAP) for (let x = GAP / 2; x < W; x += GAP) {
      let best = 0, k = 0;
      for (const w of waves) {
        const rad = (t - w.born) * speed, d = Math.hypot(x - w.x, y - w.y);
        const v = Math.exp(-(((d - rad) / 26) ** 2)) * Math.max(0, 1 - rad / reach);
        if (v > best) { best = v; k = w.k; }
      }
      const drift = noise(x / 90, y / 90, z) * 0.35;
      const near = mode === 'loop' ? Math.exp(-((Math.hypot(x - pointer.x, y - pointer.y) / 70) ** 2)) * 0.5 : 0;
      const r = 1 + drift * 1.6 + best * 3 + near * 1.6;
      ctx.fillStyle = best > 0.05
        ? rgba(mix(colors.base, colors.waves[k], Math.min(1, best * 1.6)), (0.2 + best * 0.6) * strength)
        : rgba(colors.base, (0.16 + drift * 0.18 + near * 0.3) * strength);
      ctx.beginPath();
      ctx.arc(x, y, r, 0, Math.PI * 2);
      ctx.fill();
    }
  }

  function tick(now) {
    const dt = Math.min(64, now - (last || now));
    last = now;
    clock += dt;
    if (mode === 'loop' && clock >= nextWave) {
      waves.push({ x: W * (0.15 + Math.random() * 0.7), y: H * (0.2 + Math.random() * 0.6), born: clock, k: made++ % colors.waves.length });
      nextWave = clock + 2600;
    }
    draw(clock);
    if (mode === 'once' && !waves.length) { done = true; raf = 0; return; } // the one ripple has passed: rest here
    raf = requestAnimationFrame(tick);
  }

  function update() {
    const run = !done && visible && !paused && !document.hidden && !(reduced && !toggle) && (mode !== 'once' || made > 0);
    if (run && !raf) { last = 0; raf = requestAnimationFrame(tick); }
    if (!run && raf) { cancelAnimationFrame(raf); raf = 0; }
  }

  function resize() {
    const r = canvas.getBoundingClientRect();
    const dpr = Math.min(2, devicePixelRatio || 1);
    W = r.width; H = r.height;
    canvas.width = Math.round(W * dpr);
    canvas.height = Math.round(H * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    draw(clock);
    begin();
  }

  // The opening's ripple starts the first time the opening is seen (and has a size), from its origin.
  function begin() {
    if (mode === 'once' && !done && !made && visible && W > 0 && H > 0) { waves.push({ x: W * origin[0], y: H * origin[1], born: clock, k: 0 }); made = 1; }
    update();
  }

  read();
  new ResizeObserver(resize).observe(canvas);
  new IntersectionObserver(([e]) => {
    visible = e.isIntersecting;
    begin();
  }).observe(canvas);
  document.addEventListener('visibilitychange', update);
  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => { read(); draw(clock); });
  if (mode === 'loop') addEventListener('pointermove', (e) => { const r = canvas.getBoundingClientRect(); pointer.x = e.clientX - r.left; pointer.y = e.clientY - r.top; }, { passive: true });
  if (toggle) new MutationObserver(() => { paused = toggle.hasAttribute('data-paused'); update(); }).observe(toggle, { attributes: true, attributeFilter: ['data-paused'] });
}

// The area pages: one halftone in the opening's free space (between the lead and the install line, or under it),
// rippling once in the area's ink, and a still one in the corner of each shelf field.
export function decorate() {
  const pitch = document.querySelector('.hero .pitch');
  if (pitch) {
    const dots = document.createElement('div');
    dots.className = 'dots';
    dots.setAttribute('aria-hidden', 'true');
    // Wherever the opening has the most room: before the install line (the CLI, tests) or under it (editors).
    const get = pitch.querySelector('.get');
    const lead = pitch.querySelector('.lead');
    const p = pitch.getBoundingClientRect(), pad = parseFloat(getComputedStyle(pitch).paddingBottom);
    const before = get && lead ? get.getBoundingClientRect().top - lead.getBoundingClientRect().bottom : 0;
    const after = get ? p.bottom - pad - get.getBoundingClientRect().bottom : 0;
    if (get && before >= after) get.before(dots); else pitch.append(dots);
    halftone(dots, { mode: 'once', base: '--c', ripples: ['--c'], strength: 1.8, origin: [0.3, 0.5] });
  }
  for (const field of document.querySelectorAll('.shelf-field')) {
    const dots = document.createElement('div');
    dots.className = 'dots';
    dots.setAttribute('aria-hidden', 'true');
    field.prepend(dots);
    halftone(dots, { mode: 'still', base: '--shelf-ink', ripples: [], strength: 2.4 });
  }
}
