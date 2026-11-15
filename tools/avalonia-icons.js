// Writes src/ThixInstall/Themes/Icons.axaml: the line icons of the desktop app as Avalonia geometries.
//
//   node tools/avalonia-icons.js
//
// The icons are MatDesign's (../MatDesign/src/MatUi/wwwroot/img/matui-icons.svg, 24 x 24, drawn with a 2 px line),
// so the desktop app and ThixInstall.Cloud use the same pictures. A few that MatUi does not have are drawn here in the
// same style (CUSTOM). rect, circle, line, polyline and polygon are turned into path data; the app strokes the
// geometry (Controls/IconView) and never fills it, except where noted.
'use strict';

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const sprite = fs.readFileSync(path.resolve(root, '..', 'MatDesign', 'src', 'MatUi', 'wwwroot', 'img', 'matui-icons.svg'), 'utf8');
const target = path.join(root, 'src', 'ThixInstall', 'Themes', 'Icons.axaml');

// Resource key -> MatUi symbol.
const FROM_MATUI = {
  History: 'clock', Image: 'image', Star: 'star', Settings: 'settings', Search: 'search', Link: 'link', Text: 'file-text',
  Copy: 'copy', Delete: 'trash', Cloud: 'cloud', Sync: 'refresh', Sparkles: 'sparkles', Info: 'info', Check: 'check',
  Close: 'x', ChevronDown: 'chevron-down', Pause: 'pause', Play: 'play', Download: 'download', Upload: 'upload',
  Private: 'eye-off', External: 'external', Login: 'login', Logout: 'logout', Sort: 'sort', Filter: 'filter',
  Device: 'monitor', Lock: 'lock', Warning: 'alert', User: 'user', Up: 'arrow-up', Down: 'arrow-down', Error: 'x-circle',
  Success: 'check-circle', Minus: 'minus', Plus: 'plus', Sun: 'sun', Moon: 'moon', Globe: 'globe', Layers: 'layers',
  Sidebar: 'sidebar', Box: 'box', Apps: 'apps', Grid: 'grid', Terminal: 'terminal', Code: 'code', List: 'list',
  ListChecks: 'list-checks', Shield: 'shield', Rocket: 'rocket', Stop: 'stop', Store: 'store', Activity: 'activity',
  Wrench: 'wrench', Key: 'key', Server: 'server', Folder: 'folder', Edit: 'edit', More: 'more-horizontal', Tag: 'tag',
};

// Drawn for ThixInstall in the same grid and line (Lucide style).
const CUSTOM = {
  Power: "<path d='M18.36 6.64a9 9 0 1 1-12.73 0'/><line x1='12' y1='2' x2='12' y2='12'/>",
  Maximize: "<rect x='5' y='5' width='14' height='14' rx='2'/>",
  Restore: "<rect x='4' y='8' width='12' height='12' rx='2'/><path d='M8 8V6a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-2'/>",
  Window: "<rect x='3' y='4' width='18' height='16' rx='2'/><line x1='3' y1='9' x2='21' y2='9'/>",
};

const num = (value) => Number(value);
const fmt = (value) => (Math.round(value * 1000) / 1000).toString();
const attrs = (tag) => {
  const result = {};
  for (const match of tag.matchAll(/([a-zA-Z0-9-]+)=['"]([^'"]*)['"]/g)) result[match[1]] = match[2];
  return result;
};

function rectPath(a) {
  const x = num(a.x || 0), y = num(a.y || 0), w = num(a.width), h = num(a.height);
  const r = Math.min(num(a.rx || a.ry || 0), w / 2, h / 2);
  if (r <= 0) return `M${fmt(x)},${fmt(y)} H${fmt(x + w)} V${fmt(y + h)} H${fmt(x)} Z`;
  return `M${fmt(x + r)},${fmt(y)} H${fmt(x + w - r)} A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x + w)},${fmt(y + r)} ` +
    `V${fmt(y + h - r)} A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x + w - r)},${fmt(y + h)} H${fmt(x + r)} ` +
    `A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x)},${fmt(y + h - r)} V${fmt(y + r)} A${fmt(r)},${fmt(r)} 0 0 1 ${fmt(x + r)},${fmt(y)} Z`;
}

function circlePath(a) {
  const cx = num(a.cx), cy = num(a.cy), r = num(a.r);
  return `M${fmt(cx - r)},${fmt(cy)} A${fmt(r)},${fmt(r)} 0 1 0 ${fmt(cx + r)},${fmt(cy)} A${fmt(r)},${fmt(r)} 0 1 0 ${fmt(cx - r)},${fmt(cy)} Z`;
}

function pointsPath(points, close) {
  const values = points.trim().split(/[\s,]+/).map(num);
  let d = '';
  for (let i = 0; i < values.length; i += 2) d += `${i === 0 ? 'M' : ' L'}${fmt(values[i])},${fmt(values[i + 1])}`;
  return close ? d + ' Z' : d;
}

// SVG path data allows "l.06.06" and "1-2.8": every number is written out with a space here. A path element starts at
// the origin, so its first "m" is absolute (it is joined to the other parts of the icon).
function normalizePath(d) {
  const tokens = d.match(/[a-zA-Z]|[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?/g) || [];
  if (tokens[0] === 'm') {
    tokens[0] = 'M';
    // Pairs after a relative moveto are relative linetos: keep them relative.
    if (tokens.length > 3 && !/^[a-zA-Z]$/.test(tokens[3])) tokens.splice(3, 0, 'l');
  }
  return tokens.map((token) => (/^[a-zA-Z]$/.test(token) ? token : fmt(Number(token)))).join(' ').replace(/([a-zA-Z]) /g, '$1');
}

function toPathData(markup) {
  const parts = [];
  for (const match of markup.matchAll(/<(path|rect|circle|line|polyline|polygon)\b([^>]*)\/?>/g)) {
    const a = attrs(match[2]);
    switch (match[1]) {
      case 'path': parts.push(normalizePath(a.d)); break;
      case 'rect': parts.push(rectPath(a)); break;
      case 'circle': parts.push(circlePath(a)); break;
      case 'line': parts.push(`M${a.x1},${a.y1} L${a.x2},${a.y2}`); break;
      case 'polyline': parts.push(pointsPath(a.points, false)); break;
      case 'polygon': parts.push(pointsPath(a.points, true)); break;
    }
  }
  return parts.join(' ');
}

const lines = [];
for (const [key, name] of Object.entries(FROM_MATUI)) {
  const symbol = sprite.match(new RegExp(`<symbol id="mat-i-${name}"[^>]*>([\\s\\S]*?)</symbol>`));
  if (!symbol) throw new Error(`MatUi has no icon ${name}`);
  lines.push(`  <StreamGeometry x:Key="Icon.${key}">${toPathData(symbol[1])}</StreamGeometry>`);
}
for (const [key, markup] of Object.entries(CUSTOM)) {
  lines.push(`  <StreamGeometry x:Key="Icon.${key}">${toPathData(markup)}</StreamGeometry>`);
}

const xml = `<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Generated by tools/avalonia-icons.js from MatDesign's icon sprite (24 x 24 grid, 2 px line): do not edit by hand. -->
${lines.join('\n')}
</ResourceDictionary>
`;
fs.mkdirSync(path.dirname(target), { recursive: true });
fs.writeFileSync(target, xml.replace(/\n/g, '\r\n'));
console.log(`${lines.length} icons written to ${target}`);
