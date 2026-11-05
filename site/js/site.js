// miracinema.app: the page is complete without this script (a screen of the sentence is then a link to its capture, the
// tour a link to its file). It puts this device's download first, and opens a screen or the tour big over the page.
const ua = navigator.userAgent;
const touchMac = /Macintosh/.test(ua) && navigator.maxTouchPoints > 1;
const device = /iPhone|iPad|iPod/.test(ua) || touchMac ? 'phone' : /Mac OS X|Macintosh/.test(ua) ? 'mac' : 'windows';
if (device !== 'windows') {
  document.querySelectorAll('.actions [data-for]').forEach(link => { link.hidden = !link.dataset.for.split(' ').includes(device); });
}
if (/Windows|Mac OS X|Macintosh|iPhone|iPad|iPod/.test(ua)) {
  document.querySelector(`.rows [data-for="${device}"]`)?.classList.add('yours');
}

const text = document.body.dataset;
const icons = document.querySelector('.tour use')?.getAttribute('href').replace(/#.*$/, '') ?? 'img/icons.svg';
const viewer = document.createElement('dialog');
viewer.className = 'viewer';
const figure = document.createElement('figure');
const close = document.createElement('button');
close.type = 'button';
close.className = 'close';
close.setAttribute('aria-label', text.close ?? 'Close');
close.innerHTML = `<svg class="icon" aria-hidden="true"><use href="${icons}#close"/></svg>`;
viewer.append(figure, close);
document.body.append(viewer);
close.addEventListener('click', () => viewer.close());
viewer.addEventListener('click', event => { if (event.target === viewer) viewer.close(); });
viewer.addEventListener('close', () => { figure.querySelector('video')?.pause(); figure.replaceChildren(); });

const show = link => {
  const kind = link.dataset.view;
  const caption = document.createElement('figcaption');
  let body;
  if (kind === 'tour') {
    body = document.createElement('video');
    const source = document.createElement('source');
    source.src = link.getAttribute('href');
    source.type = 'video/mp4; codecs=avc1.64002A';
    body.append(source);
    body.controls = true;
    body.playsInline = true;
    body.poster = link.dataset.poster;
    caption.textContent = link.dataset.caption;
    // Not played (a browser without this H.264, the file not reached): the file to download instead.
    const failed = () => {
      body.remove();
      caption.textContent = `${text.failed} `;
      const file = document.createElement('a');
      file.href = source.src;
      file.download = '';
      file.textContent = text.download;
      caption.append(file, ` ${text.size}`);
    };
    if (!body.canPlayType(source.type)) { figure.replaceChildren(caption); viewer.showModal(); failed(); return; }
    source.addEventListener('error', failed);
  } else {
    const shown = link.querySelector('img');
    const img = document.createElement('img');
    img.src = link.getAttribute('href');
    img.alt = shown.alt;
    caption.textContent = shown.alt;
    body = document.createElement('div');
    body.className = kind;
    if (kind === 'phone') {
      const screen = document.createElement('div');
      screen.className = 'screen';
      screen.append(img);
      body.append(screen);
    } else body.append(img);
  }
  figure.replaceChildren(body, caption);
  viewer.showModal();
  if (kind === 'tour') body.play().catch(() => {});
};
for (const link of document.querySelectorAll('[data-view]')) {
  link.addEventListener('click', event => {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    show(link);
  });
}
