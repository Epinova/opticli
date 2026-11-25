// A tiny sequencer for the examples that play themselves (front page, editors, tests): no library, just timers, CSS transitions and the Web Animations API.
// A loop plays only while it is on screen, the tab is visible and the visitor hasn't paused it.
// With reduced motion it starts paused; Play still works, and the steps then change without movement.
export const reduced = matchMedia('(prefers-reduced-motion: reduce)').matches;

export function sleep(ms, signal) {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    const t = setTimeout(resolve, ms);
    signal.addEventListener('abort', () => { clearTimeout(t); reject(signal.reason); }, { once: true });
  });
}

export const show = (...els) => els.flat().forEach((e) => e && (e.dataset.m = 'show'));
export const hide = (...els) => els.flat().forEach((e) => e && (e.dataset.m = 'hide'));
export const state = (els, s) => [els].flat().forEach((e) => (s ? (e.dataset.state = s) : delete e.dataset.state));

// Wraps each word of an element in a span, keeping its inner markup, once.
export function splitWords(el) {
  if (!el.hasAttribute('data-split')) {
    const walk = (node) => {
      for (const child of [...node.childNodes]) {
        if (child.nodeType === Node.TEXT_NODE) {
          const frag = document.createDocumentFragment();
          for (const part of child.textContent.split(/(\s+)/)) {
            if (!part) continue;
            if (/^\s+$/.test(part)) { frag.append(part); continue; }
            const w = document.createElement('span');
            w.className = 'w';
            w.textContent = part;
            frag.append(w);
          }
          child.replaceWith(frag);
        } else if (child.nodeType === Node.ELEMENT_NODE) walk(child);
      }
    };
    walk(el);
    el.setAttribute('data-split', '');
  }
  return [...el.querySelectorAll('.w')];
}

export async function words(el, signal, per = 55) {
  for (const w of splitWords(el)) { w.dataset.m = 'show'; await sleep(per, signal); }
}

// Applies a reset with no transitions, so it never shows.
export function snap(root, fn) {
  root.classList.add('m-instant');
  fn();
  root.getBoundingClientRect();
  root.classList.remove('m-instant');
}

// The start of a cycle: from what's showing to the empty start. When a loop starts on its own (on load, scrolled
// into view, the tab shown again) that happens at once; when it follows a cycle or a click, it fades.
export async function begin({ root, reset, instant, signal, fade = 600 }) {
  if (instant) { snap(root, reset); await sleep(250, signal); }
  else { reset(); await sleep(fade, signal); }
}

export function loop({ root, toggle, cycle, rest }) {
  let ctrl = null;
  let paused = reduced;
  let visible = false;
  const running = () => !paused && visible && !document.hidden;
  const stop = () => {
    if (ctrl) { ctrl.abort(); ctrl = null; }
    rest();
  };
  const start = (instant) => {
    if (ctrl || !running()) return;
    const c = (ctrl = new AbortController());
    (async () => {
      try {
        while (!c.signal.aborted) {
          // A cycle's reset runs before its first await, so the page is at its start when the pre-paint hiding lifts.
          const played = cycle(c.signal, instant);
          document.documentElement.classList.remove('m-pre');
          instant = false;
          await played;
        }
      } catch (e) { if (!c.signal.aborted) console.error(e); }
    })();
  };
  const update = (instant) => (running() ? start(instant) : stop());
  const label = () => {
    toggle.querySelector('.lbl').textContent = paused ? toggle.dataset.play : toggle.dataset.pause;
    toggle.toggleAttribute('data-paused', paused);
  };
  toggle.addEventListener('click', () => { paused = !paused; label(); update(false); });
  label();
  // A margin, so a loop scrolled to resets just before it comes into view rather than in front of the visitor.
  new IntersectionObserver(([e]) => { visible = e.isIntersecting; update(true); }, { rootMargin: '120px 0px' }).observe(root);
  document.addEventListener('visibilitychange', () => update(true));
  return {
    get running() { return !!ctrl; },
    restart() { if (!ctrl) return; ctrl.abort(); ctrl = null; rest(); start(false); },
  };
}

export const toggleMarkup = `<svg class="i-pause" viewBox="0 0 12 12" aria-hidden="true"><rect x="2" y="1.5" width="2.6" height="9" rx="1" fill="currentColor"/><rect x="7.4" y="1.5" width="2.6" height="9" rx="1" fill="currentColor"/></svg><svg class="i-play" viewBox="0 0 12 12" aria-hidden="true"><path d="M3 1.6v8.8L10.4 6z" fill="currentColor"/></svg><span class="lbl"></span>`;
