// The mark of Mira for iPhone's launch screen (src/Mira.iOS/Mira/Resources/Assets.xcassets/LaunchMark.imageset): the
// « m » of MiraMark (src/Mira.iOS/Mira/Design/Theme.swift, mark.svg of the extension's pages) in Palette.accent, its
// 24 × 24 box drawn OpeningMark.size (144) points wide, at 2× and 3×. iOS centres it on the launch screen; Mira's opening
// (src/Mira.iOS/Mira/App/Opening.swift) starts from it, at the same place and size: change both together.
//   PLAYWRIGHT=<.../playwright/index.mjs> node tools/ios/launch-mark.mjs
const { chromium } = await import(process.env.PLAYWRIGHT ?? 'playwright');

const points = 144;
const folder = 'src/Mira.iOS/Mira/Resources/Assets.xcassets/LaunchMark.imageset';
const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${points}" height="${points}" viewBox="0 0 24 24">
  <path fill="none" stroke="#f5f5f7" stroke-width="2.65" stroke-linecap="round" stroke-linejoin="round"
    d="M6.3 16.5V10.6C6.3 6.3 12 6.3 12 10.6V15.6M12 10.6C12 6.3 17.7 6.3 17.7 10.6V16.5"/>
</svg>`;

const browser = await chromium.launch();
for (const scale of [2, 3]) {
  const page = await browser.newPage({ viewport: { width: points, height: points }, deviceScaleFactor: scale });
  await page.setContent(`<!doctype html><style>html,body{margin:0;background:transparent}svg{display:block}</style>${svg}`);
  await page.screenshot({ path: `${folder}/LaunchMark@${scale}x.png`, omitBackground: true });
  await page.close();
}
await browser.close();
