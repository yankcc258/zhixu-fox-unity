// 把 Tauri 版（zhixu-whale-maid-pet，feature/kiko-fox 分支）的 src/lines.js 导出成 Unity 用的 Resources/kiko_lines.json。
// usage: node Tools/export_lines.js <lines.js> Assets/Kiko/Resources/kiko_lines.json
// （git show origin/feature/kiko-fox:src/lines.js > /tmp/lines.js 先取出来）
const fs = require('fs');
const [src, out] = process.argv.slice(2);
const LINES = eval(fs.readFileSync(src, 'utf8') + ';LINES');
const groups = Object.entries(LINES).filter(([, v]) => Array.isArray(v)).map(([key, lines]) => ({ key, lines }));
fs.writeFileSync(out, JSON.stringify({ groups }, null, 1));
console.log(out, groups.length, 'groups', groups.reduce((a, g) => a + g.lines.length, 0), 'lines');
