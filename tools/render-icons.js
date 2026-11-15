// Renders the ThixInstall logo (branding/*.svg) into every file the apps need, with the installed Microsoft Edge (headless).
//
//   cd tools && npm install && node render-icons.js
//
// Detail levels (see branding/README.md):
//   16-32 px   logo-small.svg  (thick arrow into a tray)
//   48+ px     logo-icon.svg   (arrow dropping into an open box)
//   tray       logo-tray.svg   (one colour; macOS template image)
//   cloud      logo-cloud.svg  (full-bleed tile with a cloud badge: PWA icons, maskable)
// Output:
//   branding/png/*                         every size as PNG (the macOS packaging script builds the .icns from them)
//   src/ThixInstall/Assets/thixinstall.ico       Windows program and window icon (16-256)
//   src/ThixInstall/Assets/*.png              window logo, tray icons
//   src/ThixInstall.Cloud/wwwroot/img/*       favicon.ico, PWA icons, apple-touch-icon
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const root = path.resolve(__dirname, '..');
const branding = path.join(root, 'branding');
const pngDir = path.join(branding, 'png');
const appAssets = path.join(root, 'src', 'ThixInstall', 'Assets');
const cloudImg = path.join(root, 'src', 'ThixInstall.Cloud', 'wwwroot', 'img');

const svg = (name) => fs.readFileSync(path.join(branding, name), 'utf8');

// An SVG drawn at a size, optionally scaled inside the canvas (macOS icons keep a margin around the tile).
function page(source, size, scale = 1) {
  const inner = size * scale;
  const offset = (size - inner) / 2;
  const sized = source
    .replace(/<svg([^>]*?)\swidth="[^"]*"/, '<svg$1')
    .replace(/<svg([^>]*?)\sheight="[^"]*"/, '<svg$1')
    .replace('<svg', `<svg width="${inner}" height="${inner}"`);
  return `<!doctype html><html><head><style>html,body{margin:0;padding:0;background:transparent}` +
    `div{position:absolute;left:${offset}px;top:${offset}px;width:${inner}px;height:${inner}px}svg{display:block}</style></head>` +
    `<body><div>${sized}</div></body></html>`;
}

async function render(browser, source, size, scale = 1) {
  const context = await browser.newContext({ viewport: { width: size, height: size }, deviceScaleFactor: 1 });
  const tab = await context.newPage();
  await tab.setContent(page(source, size, scale));
  const bytes = await tab.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: size, height: size } });
  await context.close();
  return bytes;
}

// ICO with PNG entries (Windows Vista and later).
function ico(images) {
  const header = Buffer.alloc(6 + 16 * images.length);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(images.length, 4);
  let offset = header.length;
  images.forEach(({ size, bytes }, i) => {
    const at = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, at);
    header.writeUInt8(size >= 256 ? 0 : size, at + 1);
    header.writeUInt8(0, at + 2);
    header.writeUInt8(0, at + 3);
    header.writeUInt16LE(1, at + 4);
    header.writeUInt16LE(32, at + 6);
    header.writeUInt32LE(bytes.length, at + 8);
    header.writeUInt32LE(offset, at + 12);
    offset += bytes.length;
  });
  return Buffer.concat([header, ...images.map((image) => image.bytes)]);
}

(async () => {
  for (const dir of [pngDir, appAssets, cloudImg]) fs.mkdirSync(dir, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const small = svg('logo-small.svg');
    const icon = svg('logo-icon.svg');
    const tray = svg('logo-tray.svg');
    const cloud = svg('logo-cloud.svg');
    const forSize = (size) => (size <= 32 ? small : icon);

    // App icon in every size.
    const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024];
    const bySize = {};
    for (const size of sizes) {
      bySize[size] = await render(browser, forSize(size), size);
      fs.writeFileSync(path.join(pngDir, `thixinstall-${size}.png`), bySize[size]);
    }

    // macOS app icon: the tile keeps Apple's margin (824 of 1024).
    for (const size of [16, 32, 64, 128, 256, 512, 1024]) {
      const bytes = await render(browser, forSize(size), size, 0.92);
      fs.writeFileSync(path.join(pngDir, `thixinstall-mac-${size}.png`), bytes);
    }

    // Windows program icon.
    const icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    fs.writeFileSync(path.join(appAssets, 'thixinstall.ico'), ico(icoSizes.map((size) => ({ size, bytes: bySize[size] }))));
    fs.writeFileSync(path.join(appAssets, 'logo-256.png'), bySize[256]);
    fs.writeFileSync(path.join(appAssets, 'logo-64.png'), bySize[64]);

    // Tray: Windows uses the small colour icon (light and dark task bars), macOS the template glyph (22 pt menu bar).
    fs.writeFileSync(path.join(appAssets, 'tray-windows.ico'), ico([16, 20, 24, 32].map((size) => ({ size, bytes: bySize[size] }))));
    fs.writeFileSync(path.join(appAssets, 'tray-macos.png'), await render(browser, tray, 44, 0.82));
    fs.writeFileSync(path.join(pngDir, 'thixinstall-tray-44.png'), await render(browser, tray, 44, 0.82));

    // Cloud (PWA): favicon, home screen icons (maskable = full bleed), apple-touch-icon (no transparency).
    fs.writeFileSync(path.join(cloudImg, 'favicon.ico'), ico([16, 32, 48].map((size) => ({ size, bytes: bySize[size] }))));
    fs.copyFileSync(path.join(branding, 'logo-small.svg'), path.join(cloudImg, 'favicon.svg'));
    fs.copyFileSync(path.join(branding, 'logo-icon.svg'), path.join(cloudImg, 'logo.svg'));
    fs.copyFileSync(path.join(branding, 'logo-full.svg'), path.join(cloudImg, 'logo-full.svg'));
    for (const size of [180, 192, 512]) {
      const bytes = await render(browser, cloud, size);
      const name = size === 180 ? 'apple-touch-icon.png' : `icon-${size}.png`;
      fs.writeFileSync(path.join(cloudImg, name), bytes);
      fs.writeFileSync(path.join(pngDir, `thixinstall-cloud-${size}.png`), bytes);
    }
    fs.writeFileSync(path.join(cloudImg, 'icon-maskable-512.png'), await render(browser, cloud, 512));
    fs.writeFileSync(path.join(pngDir, 'thixinstall-full.png'),
      await (async () => {
        const context = await browser.newContext({ viewport: { width: 800, height: 200 } });
        const tab = await context.newPage();
        await tab.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${svg('logo-full.svg')}</body></html>`);
        const bytes = await tab.screenshot({ omitBackground: true, clip: { x: 0, y: 0, width: 800, height: 200 } });
        await context.close();
        return bytes;
      })());

    console.log('Icons written to', pngDir, appAssets, cloudImg);
  } finally {
    await browser.close();
  }
})().catch((error) => {
  console.error(error);
  process.exit(1);
});
