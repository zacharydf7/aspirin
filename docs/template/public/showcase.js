// The landing page's "Build up an AppHost" example. The page holds every piece of the AppHost, so it reads correctly
// without this script. The script hides the pieces of the features that start switched off, and types a feature's
// pieces in when the reader switches it on.

const startOn = ['settings'];
const frameMs = 16;
const msPerCharacter = 14;
const longestTypingMs = 1400;

export function initShowcase() {
  const root = document.querySelector('.ap-showcase');
  if (!root) {
    return;
  }

  const chips = [...root.querySelectorAll('.ap-chip')];
  const segments = [...root.querySelectorAll('.ap-seg[data-feature]')];
  const note = root.querySelector('.ap-showcase-note');
  const typing = root.querySelector('.ap-typing input');
  const running = new Map();

  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
    typing.checked = false;
  }

  for (const chip of chips) {
    setPressed(chip, startOn.includes(chip.dataset.feature));
    chip.addEventListener('click', () => toggle(chip));
  }

  for (const segment of segments) {
    segment.hidden = !startOn.includes(segment.dataset.feature);
  }

  note.textContent = note.dataset.default;

  function toggle(chip) {
    const on = chip.getAttribute('aria-pressed') !== 'true';
    setPressed(chip, on);
    note.textContent = on ? chip.dataset.note : note.dataset.default;

    const pieces = segments.filter(segment => segment.dataset.feature === chip.dataset.feature);
    const typeThem = on && typing.checked;
    for (const segment of pieces) {
      finish(segment);
      // A piece waiting to be typed stays hidden until its turn.
      segment.hidden = !on || (typeThem && segment !== pieces[0]);
    }

    if (typeThem) {
      typeInOrder(pieces);
    }
  }

  // Types each piece after the one before it, so a feature with two pieces reads top to bottom.
  function typeInOrder(pieces) {
    const [first, ...rest] = pieces;
    if (first) {
      type(first, () => typeInOrder(rest));
    }
  }

  function type(segment, done) {
    segment.hidden = false;
    const code = segment.querySelector('code');

    // DocFX highlights the code after this script starts. A piece typed before then would be put back without its
    // colors, so it appears at once and is highlighted with the rest.
    if (!code.dataset.highlighted) {
      done();
      return;
    }

    const html = code.innerHTML;
    const full = document.createElement('code');
    full.innerHTML = html;

    const total = full.textContent.length;
    const frames = Math.max(1, Math.min(total * msPerCharacter, longestTypingMs) / frameMs);
    const step = Math.ceil(total / frames);
    const caret = document.createElement('span');
    caret.className = 'ap-caret';

    let shown = 0;
    const frame = () => {
      shown = Math.min(total, shown + step);
      if (shown < total) {
        code.replaceChildren(...truncated(full, shown).childNodes, caret);
        running.set(segment, { timer: setTimeout(frame, frameMs), code, html });
      } else {
        running.delete(segment);
        code.innerHTML = html;
        done();
      }
    };

    code.replaceChildren(caret);
    running.set(segment, { timer: setTimeout(frame, frameMs), code, html });
  }

  // Stops a piece that is still being typed and shows it whole.
  function finish(segment) {
    const typed = running.get(segment);
    if (typed) {
      clearTimeout(typed.timer);
      typed.code.innerHTML = typed.html;
      running.delete(segment);
    }
  }
}

function setPressed(chip, on) {
  chip.setAttribute('aria-pressed', on ? 'true' : 'false');
}

// Returns a copy of the element with only its first `count` characters, keeping the highlighting around them.
function truncated(element, count) {
  const copy = element.cloneNode(true);
  const walker = document.createTreeWalker(copy, NodeFilter.SHOW_TEXT);
  const texts = [];
  while (walker.nextNode()) {
    texts.push(walker.currentNode);
  }

  let left = count;
  for (const text of texts) {
    if (left <= 0) {
      text.remove();
    } else if (text.data.length > left) {
      text.data = text.data.slice(0, left);
      left = 0;
    } else {
      left -= text.data.length;
    }
  }

  return copy;
}
