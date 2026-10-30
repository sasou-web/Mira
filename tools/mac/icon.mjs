// Mira's Mac icon, drawn from the mark of Mira web (src/Mira.Web/img/mark.svg) as on the iPhone: the off-white « m » on
// Mira's blacks (#252528 at the top, #0d0d0f at the bottom).
// - src/Mira.Mac/Icon/Mira.icon/Assets/m.png: the « m » alone on Icon Composer's 1024 px canvas, the only layer of
//   Mira.icon (icon.json beside it gives the fill). macOS 26 draws the shape, its edge and glass itself: Xcode 26's actool
//   compiles it into Assets.car (tools/mac/package.sh). Without it, macOS 26 puts an icon on a grey tile.
// - src/Mira.Mac/Icon/Mira.png: the whole icon on Apple's grid for macOS 15 (an 824 px rounded square centred on the
//   1024 px canvas, its shadow below), from which tools/mac/package.sh makes Mira.icns.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/mac/icon.mjs
const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');

// The « m » of mark.svg, on its 24-unit grid; scaled to a square of side `size` at (x, y).
const m = (x, y, size) => `<g transform="translate(${x} ${y}) scale(${size / 24})"><path fill="none" stroke="#f5f5f7"
  stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round"
  d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/></g>`;

// Apple's rounded square: close to a superellipse of exponent 5 (Big Sur's template, measured on its own icons).
function tile(x, y, size) {
  const r = size / 2, cx = x + r, cy = y + r, points = [];
  for (let i = 0; i < 720; i++) {
    const t = (i / 720) * 2 * Math.PI, c = Math.cos(t), s = Math.sin(t);
    points.push(`${(cx + r * Math.sign(c) * Math.abs(c) ** 0.4).toFixed(2)},${(cy + r * Math.sign(s) * Math.abs(s) ** 0.4).toFixed(2)}`);
  }
  return `M${points.join('L')}Z`;
}

const layer = `<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">${m(0, 0, 1024)}</svg>`;
const shape = tile(100, 100, 824);
const full = `<svg xmlns="http://www.w3.org/2000/svg" width="1024" height="1024" viewBox="0 0 1024 1024">
  <defs>
    <linearGradient id="fill" x1="0" y1="100" x2="0" y2="924" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#252528"/><stop offset="1" stop-color="#0d0d0f"/>
    </linearGradient>
    <filter id="shadow" x="-10%" y="-10%" width="120%" height="125%">
      <feGaussianBlur in="SourceAlpha" stdDeviation="10"/><feOffset dy="10"/>
      <feComponentTransfer><feFuncA type="linear" slope="0.3"/></feComponentTransfer>
      <feMerge><feMergeNode/><feMergeNode in="SourceGraphic"/></feMerge>
    </filter>
    <!-- A faint grain over the gradient: its 24 shades would otherwise show as bands. -->
    <filter id="grain"><feTurbulence type="fractalNoise" baseFrequency="0.9" seed="7"/><feColorMatrix type="saturate" values="0"/></filter>
    <clipPath id="inside"><path d="${shape}"/></clipPath>
  </defs>
  <g filter="url(#shadow)"><path d="${shape}" fill="url(#fill)"/></g>
  <rect width="1024" height="1024" filter="url(#grain)" opacity=".035" clip-path="url(#inside)"/>
  <!-- A light edge inside the tile, so that it stays apart from a dark Dock. -->
  <path d="${shape}" fill="none" stroke="rgba(245,245,247,.10)" stroke-width="4" clip-path="url(#inside)"/>
  ${m(100, 100, 824)}
</svg>`;

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1024, height: 1024 } });
for (const [svg, file] of [[layer, 'src/Mira.Mac/Icon/Mira.icon/Assets/m.png'], [full, 'src/Mira.Mac/Icon/Mira.png']]) {
  await page.setContent(`<!doctype html><style>html,body{margin:0;background:transparent}svg{display:block}</style>${svg}`);
  await page.screenshot({ path: file, omitBackground: true });
}
await browser.close();
