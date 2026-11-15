// Browser checks of ThixInstall.Cloud against a running stack: the setup assistant (or the sign-in), the start page of a visitor (tick apps, make a
// bundle, its page), a device through the API (enroll, inventory, an action sent from the browser and finished by the device), the pages of the
// administration, at desktop width and at 390 px, light and dark. Screenshots go to SHOTS (default: tests/e2e/shots); LOOK at them.
//
//   cd tests/e2e && npm install && node run.js http://localhost:4955
//
// The test accounts (local development only, see docs/CLOUD.md): ADMIN_USER / ADMIN_PASSWORD, USER_NAME / USER_PASSWORD.
'use strict';

const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright-core');

const base = (process.argv[2] || 'http://localhost:4955').replace(/\/$/, '');
const shots = process.env.SHOTS || path.join(__dirname, 'shots');
const admin = { user: process.env.ADMIN_USER || 'admin', password: process.env.ADMIN_PASSWORD || 'ThixInstall-Admin-2026', name: 'Matthias Admin' };
const person = { user: process.env.USER_NAME || 'anna', password: process.env.USER_PASSWORD || 'Anna-Test-2026', name: 'Anna Beispiel' };

let failures = 0;
function check(condition, message) {
  if (condition) { console.log('  ok   ' + message); } else { failures++; console.log('  FAIL ' + message); }
}

async function api(method, route, token, body) {
  const headers = { 'User-Agent': 'ThixInstall/e2e (windows)', 'X-ThixInstall-Protocol': '1' };
  if (token) headers.Authorization = 'Bearer ' + token;
  let payload;
  if (body !== undefined) { headers['Content-Type'] = 'application/json'; payload = JSON.stringify(body); }
  const response = await fetch(base + route, { method, headers, body: payload });
  const type = response.headers.get('content-type') || '';
  const data = type.includes('json') ? await response.json() : await response.text();
  return { status: response.status, data, headers: response.headers };
}

async function shot(page, name) {
  await page.waitForTimeout(300);
  await page.screenshot({ path: path.join(shots, name + '.png'), fullPage: true });
}

async function signIn(page, who) {
  await page.goto(base + '/Account/Login');
  await page.fill('#UserName', who.user);
  await page.fill('#Password', who.password);
  await Promise.all([page.waitForNavigation(), page.click('#login-form button[type=submit]')]);
}

/** Chooses a value of a select picker of the toolkit (the real input is hidden). */
async function pick(page, input, value) {
  await page.evaluate(([selector, v]) => { window.MatUi.picker.set(document.querySelector(selector).closest('.mat-picker'), v); }, [input, value]);
}

(async () => {
  fs.mkdirSync(shots, { recursive: true });
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 860 }, locale: 'de-DE' });
  const page = await context.newPage();
  const errors = [];
  const watch = (p) => {
    p.on('pageerror', (e) => errors.push(e.message));
    p.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(m.text()); });
  };
  watch(page);

  try {
    console.log('Setup / sign-in');
    await page.goto(base + '/');
    if (page.url().includes('/Account/Setup')) {
      await shot(page, 'cloud-setup');
      await page.fill('#UserName', admin.user);
      await page.fill('#DisplayName', admin.name);
      await page.fill('#Password', admin.password);
      await page.fill('#Repeat', admin.password);
      await Promise.all([page.waitForNavigation(), page.click('#setup-form button[type=submit]')]);
      check(new URL(page.url()).pathname === '/', 'the setup assistant creates the administrator and leads to the start page');
    } else {
      await signIn(page, admin);
    }

    // a second account (once)
    await page.goto(base + '/Admin/Users?q=' + person.user);
    if (!(await page.content()).includes('>' + person.name + '<')) {
      await page.goto(base + '/Admin/Users/Edit');
      await page.fill('#UserName', person.user);
      await page.fill('#DisplayName', person.name);
      await page.fill('#Password', person.password);
      await Promise.all([page.waitForNavigation(), page.click('form.mat-form button.mat-btn--primary')]);
    }

    console.log('The start page of a visitor');
    const visitor = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'de-DE', colorScheme: 'light' });
    const anon = await visitor.newPage();
    watch(anon);
    await anon.goto(base + '/?os=windows');
    check(await anon.locator('.ti-tile').count() > 20, 'the catalog shows the apps as tiles');
    check(await anon.locator('[data-bulk-bar]').isHidden(), 'the bar to create a bundle waits for a tick');
    await shot(anon, 'cloud-start');
    for (const name of ['Firefox', 'VLC media player', '7-Zip', 'Dateiendungen anzeigen']) {
      await anon.locator('.ti-tile', { hasText: name }).first().click();
    }
    check(await anon.locator('[data-bulk-count]').innerText() === '4', 'four ticks are counted');
    await anon.fill('#picker-search', 'zip');
    check(await anon.locator('.ti-tile:visible').count() < 5, 'the search narrows the tiles');
    await anon.fill('#picker-search', '');
    await shot(anon, 'cloud-start-picked');
    // the view as a visitor sees it (a full-page picture repeats the sticky bars)
    await anon.evaluate(() => { document.activeElement && document.activeElement.blur(); window.scrollTo(0, 0); });
    await anon.screenshot({ path: path.join(shots, 'cloud-start-view.png') });
    await Promise.all([anon.waitForNavigation(), anon.click('[data-bulk-bar] button[type=submit]')]);
    check(/\/b\/[0-9a-f]{32}$/.test(anon.url()), 'creating the bundle leads to its page');
    const bundleToken = anon.url().split('/').pop();
    check((await anon.content()).includes('Für Windows herunterladen'), 'the bundle page offers the Windows download');
    await shot(anon, 'cloud-bundle');
    const bundle = await api('GET', '/api/v1/bundles/' + bundleToken + '?arch=x64');
    check(bundle.status === 200 && bundle.data.packages.length === 3 && bundle.data.scripts.length === 1, 'the program finds the bundle by its token (3 apps, 1 script)');
    await anon.goto(base + '/?os=macos');
    check((await anon.content()).includes('Dock automatisch ausblenden'), 'macOS shows the Mac apps and scripts');
    await shot(anon, 'cloud-start-macos');

    console.log('A device through the API');
    const enroll = await api('POST', '/api/v1/devices/enroll', null, {
      userName: person.user, password: person.password,
      device: { name: 'BUERO-PC-07', os: 'Windows', osVersion: '10.0.26200', architecture: 'X64', agentVersion: 'local-20261116', hardwareId: 'E2E-BUERO-PC-07' },
    });
    check(enroll.status === 200 && enroll.data.device.syncEnabled, 'a device enrolls with name and password and has Sync on');
    const token = enroll.data.token;
    const catalog = await api('GET', '/api/v1/catalog?os=windows&arch=x64', token);
    const firefox = catalog.data.packages.find((p) => p.key === 'firefox');
    const vlc = catalog.data.packages.find((p) => p.key === 'vlc');
    const inventory = await api('PUT', '/api/v1/device/inventory', token, {
      apps: [
        { name: 'Mozilla Firefox (x64 de)', version: '120.0', publisher: 'Mozilla', packageId: firefox.id, source: 'registry' },
        { name: 'VLC media player', version: '3.0.20', publisher: 'VideoLAN', packageId: vlc.id, source: 'registry' },
        { name: 'Microsoft Visual C++ 2022 Redistributable', version: '14.40', publisher: 'Microsoft', source: 'registry' },
      ],
      collectedAt: new Date().toISOString(),
    });
    check(inventory.status === 204, 'the device reports its inventory');

    // the person sends an action from the browser; the device takes it
    // (a person whose password an administrator set chooses their own first: the browser part runs as the administrator, who sees every device)
    const anna = page;
    await anna.goto(base + '/Devices');
    await shot(anna, 'cloud-devices');
    const deviceId = enroll.data.device.id;
    await anna.goto(base + '/Devices/Detail?id=' + deviceId + '&tab=software');
    check((await anna.content()).includes('Mozilla Firefox (x64 de)'), 'the inventory is shown on the tab Software');
    await shot(anna, 'cloud-device-software');
    await anna.goto(base + '/Devices/Push?id=' + deviceId);
    await pick(anna, '#Kind', 'Install');
    await pick(anna, '#PackageId', String(vlc.id));
    await shot(anna, 'cloud-device-push');
    await Promise.all([anna.waitForNavigation(), anna.click('form.mat-form button.mat-btn--primary')]);
    check(anna.url().includes('/Actions/Detail'), 'sending an action leads to the action');
    const open = await api('GET', '/api/v1/device/actions', token);
    check(open.data.actions.length === 1 && open.data.actions[0].title.includes('VLC'), 'the device finds the action');
    const action = open.data.actions[0];
    await api('POST', '/api/v1/device/actions/' + action.id + '/start', token);
    const now = new Date().toISOString();
    await api('POST', '/api/v1/device/actions/' + action.id + '/finish', token, {
      succeeded: true, exitCode: 0, output: 'Download aus dem Cache: VLC 3.0.21 (40 MB), SHA-256 geprüft\nInstallation mit /S\nFertig.', installedVersion: '3.0.21', startedAt: now, finishedAt: now,
    });
    await anna.goto(base + '/Actions/Detail?id=' + action.id);
    check((await anna.content()).includes('Erfolgreich'), 'the finished action shows its state and output');
    await shot(anna, 'cloud-action');
    await anna.goto(base + '/Actions');
    await shot(anna, 'cloud-actions');

    console.log('Administration');
    await page.goto(base + '/Catalog/Packages');
    check(await page.locator('table.mat-table tbody tr').count() > 20, 'the catalog lists the apps');
    await shot(page, 'cloud-packages');
    await page.goto(base + '/Catalog/Packages/Edit?id=' + firefox.id + '&tab=windows');
    await shot(page, 'cloud-package-windows');
    await page.goto(base + '/Catalog/Packages/Edit?id=' + firefox.id + '&tab=versions');
    await shot(page, 'cloud-package-versions');
    await page.goto(base + '/Catalog/Scripts');
    await shot(page, 'cloud-scripts');

    // a profile with two items and its enrollment
    await page.goto(base + '/Profiles?q=B%C3%BCro');
    if (!(await page.content()).includes('>Büro<')) {
      await page.goto(base + '/Profiles/Edit');
      await page.fill('#Name', 'Büro');
      await page.fill('#Description', 'Browser, Office und PDF für alle Büro-Rechner');
      await Promise.all([page.waitForNavigation(), page.click('form.mat-form button.mat-btn--primary')]);
      for (const key of ['p' + firefox.id, 'p' + vlc.id]) {
        await pick(page, '#Item', key);
        await Promise.all([page.waitForNavigation(), page.click('form.mat-form .mat-btn-row button.mat-btn--primary')]);
      }
    } else {
      await page.click('table.mat-table tbody tr a');
      await page.goto(page.url() + '&tab=items');
    }
    check(await page.locator('.mat-itemlist .mat-item').count() >= 2, 'the profile holds its apps');
    await shot(page, 'cloud-profile-items');
    await page.goto(page.url().replace('tab=items', 'tab=enroll'));
    check((await page.content()).includes('--enroll-key'), 'the enrollment tab shows the silent installation');
    await shot(page, 'cloud-profile-enroll');

    await page.goto(base + '/Admin/Cache');
    await shot(page, 'cloud-cache');
    await page.goto(base + '/Admin/Settings?tab=desktop');
    await shot(page, 'cloud-settings-desktop');
    await page.goto(base + '/About');
    await shot(page, 'cloud-about');

    console.log('Dark and phone');
    const dark = await browser.newContext({ viewport: { width: 1280, height: 860 }, locale: 'de-DE', colorScheme: 'dark' });
    const night = await dark.newPage();
    watch(night);
    await night.goto(base + '/?os=windows');
    await night.locator('.ti-tile', { hasText: 'Firefox' }).first().click();
    await night.locator('.ti-tile', { hasText: 'Spotify' }).first().click();
    await night.evaluate(() => { document.activeElement && document.activeElement.blur(); window.scrollTo(0, 0); });
    await night.screenshot({ path: path.join(shots, 'cloud-start-dark.png') });
    await signIn(night, admin);
    await night.goto(base + '/Devices/Detail?id=' + deviceId);
    await shot(night, 'cloud-device-dark');

    const phone = await browser.newContext({ viewport: { width: 390, height: 844 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, locale: 'de-DE' });
    const mobile = await phone.newPage();
    watch(mobile);
    await mobile.goto(base + '/?os=windows');
    const overflow = await mobile.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    check(overflow <= 1, 'the start page fits a phone without sideways scrolling');
    await mobile.locator('.ti-tile', { hasText: 'Firefox' }).first().tap();
    await shot(mobile, 'cloud-phone-start');
    await mobile.goto(base + '/b/' + bundleToken);
    await shot(mobile, 'cloud-phone-bundle');
    await signIn(mobile, admin);
    await mobile.goto(base + '/Devices');
    await shot(mobile, 'cloud-phone-devices');

    check(errors.length === 0, 'no script errors' + (errors.length ? ': ' + errors.join(' | ') : ''));
  } catch (e) {
    failures++;
    console.log('  FAIL ' + (e && e.stack || e));
    await shot(page, 'failure');
  } finally {
    await browser.close();
  }

  console.log(failures === 0 ? 'All browser checks passed.' : failures + ' browser check(s) failed.');
  process.exit(failures === 0 ? 0 : 1);
})();
