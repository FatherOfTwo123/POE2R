// patch-harden.mjs — Remove auto-flask/input, harden, rename overlay exe.
// Re-runnable: safe to apply again after pulling upstream changes.
// Usage:  node patch-harden.mjs
//         (run from the repo root)

import { readFileSync, writeFileSync, unlinkSync, existsSync, rmSync } from 'fs';
import { join, dirname } from 'path';
import { execSync } from 'child_process';

const root = dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1'));
const overlay = join(root, 'src', 'POE2Radar.Overlay');
const core = join(root, 'src', 'POE2Radar.Core');

function patch(filePath, old, replacement) {
  const abs = filePath.includes(':') || filePath.includes('/') ? filePath : join(overlay, filePath);
  let c = readFileSync(abs, 'utf-8');
  if (!c.includes(old)) {
    console.log(`  skip (already applied or upstream changed): ${abs}`);
    return false;
  }
  c = c.replace(old, replacement);
  writeFileSync(abs, c, 'utf-8');
  console.log(`  patched: ${abs}`);
  return true;
}

function patchMulti(filePath, replacements) {
  const abs = filePath.includes(':') || filePath.includes('/') ? filePath : join(overlay, filePath);
  let c = readFileSync(abs, 'utf-8');
  let any = false;
  for (const [old, rep] of replacements) {
    if (typeof old === 'string') {
      if (c.includes(old)) { c = c.replace(old, rep); any = true; }
    } else {
      if (old.test(c)) { c = c.replace(old, rep); any = true; }
    }
  }
  if (any) { writeFileSync(abs, c, 'utf-8'); console.log(`  patched: ${abs}`); }
  else console.log(`  skip (no matches): ${abs}`);
}

function deleteIfExists(p) {
  if (existsSync(p)) { unlinkSync(p); console.log(`  deleted: ${p}`); }
}

function deleteDirIfExists(p) {
  if (existsSync(p)) { rmSync(p, { recursive: true }); console.log(`  deleted dir: ${p}`); }
}

console.log('\n=== POE2Radar hardening patch ===\n');

// ── 1. Delete SendInputNative.cs and Input/ directory ──
console.log('[1/9] Removing input-sending code...');
deleteIfExists(join(overlay, 'Input', 'SendInputNative.cs'));
const inputDir = join(overlay, 'Input');
if (existsSync(inputDir)) {
  const files = [];
  import('fs').then(() => {}); // already imported
  const { readdirSync } = await import('fs');
  const remaining = readdirSync(inputDir, { recursive: true }).filter(f => typeof f === 'string');
  if (remaining.length === 0) deleteDirIfExists(inputDir);
  else console.log(`  Input/ dir still has files, not removing: ${remaining}`);
}

// ── 2. RadarApp.cs ──
console.log('\n[2/9] Patching RadarApp.cs...');
const radarApp = join(overlay, 'RadarApp.cs');
patchMulti(radarApp, [
  // Remove Input using
  ['using POE2Radar.Overlay.Input;\r\n', ''],
  ['using POE2Radar.Overlay.Input;\n', ''],
  // Remove auto-flask fields (try both \r\n and \n variants)
  [/(    \/\/ ── Auto-flask \(opt-in input\)\. Foreground \+ in-game gated; F8 master kill-switch\.\r?\n    \/\/    Flask keys are configurable in RadarSettings \(LifeKey\/ManaKey\)\. ──\r?\n    private bool _autoFlask = true;.*\/\/ auto-on; toggle with F8\r?\n    private DateTime _lifeFiredAt = DateTime\.MinValue, _manaFiredAt = DateTime\.MinValue;\r?\n    private DateTime _nextToggleAt = DateTime\.MinValue;\r?\n)/, ''],
  // Remove leftover _flaskNote field if still present
  [/(    private string _flaskNote = "";\r?\n)/, ''],
  // Remove F8 from hotkey help
  ['F8=auto-flask  ', ''],
  // Replace TickAutoFlask call with read-only vitals
  ['            TickAutoFlask(localPlayer);',
   '            // Read player vitals for display only (API + HUD).\n            if (_live.PlayerVitals(localPlayer) is { } v) { _hpPct = v.HpPct; _manaPct = v.ManaPct; }'],
  // Remove AutoFlask/FlaskNote from RadarState constructor
  ['_hpPct, _manaPct, _autoFlask, _flaskNote, _areaCode', '_hpPct, _manaPct, _areaCode'],
  // Remove FlaskNote from RenderContext
  ['            FlaskNote: _flaskNote,\r\n', ''],
  ['            FlaskNote: _flaskNote,\n', ''],
  // Remove F8 hotkey block
  ['        // F8 master kill-switch for auto-flask (debounced).\r\n' +
   '        if (Down(0x77) && DateTime.UtcNow >= _nextToggleAt)\r\n' +
   '        {\r\n' +
   '            _autoFlask = !_autoFlask;\r\n' +
   '            _nextToggleAt = DateTime.UtcNow.AddMilliseconds(300);\r\n' +
   '            Console.WriteLine($"\nAuto-flask: {(_autoFlask ? "ON" : "OFF")}");\r\n' +
   '        }\r\n',
   ''],
  ['        // F8 master kill-switch for auto-flask (debounced).\n' +
   '        if (Down(0x77) && DateTime.UtcNow >= _nextToggleAt)\n' +
   '        {\n' +
   '            _autoFlask = !_autoFlask;\n' +
   '            _nextToggleAt = DateTime.UtcNow.AddMilliseconds(300);\n' +
   `            Console.WriteLine($"\\nAuto-flask: {(_autoFlask ? "ON" : "OFF")}");\n` +
   '        }\n',
   ''],
]);

// Remove entire TickAutoFlask method (handles both \r\n and \n)
{
  let c = readFileSync(radarApp, 'utf-8');
  const tickMethod = /    \/\/\/ <summary>\r?\n\s+\/\/\/ Auto-flask: press the life\/mana flask key[\s\S]*?    private void TickAutoFlask\(nint localPlayer\)\r?\n\s+\{[\s\S]*?\r?\n\s+\}\r?\n\r?\n\s+\/\/\/ <summary>Poll overlay hotkeys: F8 auto-flask toggle/;
  const tickReplace = '    /// <summary>Poll overlay hotkeys: F9 quit, F12 dashboard, F6/F7 path targets.';
  if (tickMethod.test(c)) {
    c = c.replace(tickMethod, tickReplace);
    writeFileSync(radarApp, c, 'utf-8');
    console.log(`  patched (TickAutoFlask removal): ${radarApp}`);
  } else {
    console.log(`  skip (TickAutoFlask not found or already removed)`);
  }
}

// ── 3. RadarSettings.cs ──
console.log('\n[3/9] Patching RadarSettings.cs...');
const settings = join(overlay, 'Config', 'RadarSettings.cs');
{
  let c = readFileSync(settings, 'utf-8');
  const flaskBlock = /(\r?\n|\r\n)    \/\/ ── Auto-flask thresholds \+ per-flask cooldowns \(milliseconds\)\. ──\r?\n[\s\S]*?public int ManaKey \{ get; set; \} = 0x32;\r?\n\r?\n    \/\/ ── HTTP API\. ──/;
  if (flaskBlock.test(c)) {
    c = c.replace(flaskBlock, '\r\n    // ── HTTP API. ──');
    writeFileSync(settings, c, 'utf-8');
    console.log(`  patched: ${settings}`);
  } else {
    console.log(`  skip (flask settings not found or already removed)`);
  }
}

// ── 4. RenderContext.cs ──
console.log('\n[4/9] Patching RenderContext.cs...');
const renderCtx = join(overlay, 'Overlay', 'RenderContext.cs');
patchMulti(renderCtx, [
  ['    // Auto-flask status.\r\n    float HpPct,\r\n    float ManaPct,\r\n    string FlaskNote,\r\n    // Area / character HUD.',
   '    // Player vitals (display only — API + HUD).\r\n    float HpPct,\r\n    float ManaPct,\r\n    // Area / character HUD.'],
  ['    // Auto-flask status.\n    float HpPct,\n    float ManaPct,\n    string FlaskNote,\n    // Area / character HUD.',
   '    // Player vitals (display only — API + HUD).\n    float HpPct,\n    float ManaPct,\n    // Area / character HUD.'],
  ['    // ── Collapsible "POE2Radar" navigation-menu widget', '    // ── Collapsible navigation-menu widget'],
]);

// ── 5. ApiServer.cs ──
console.log('\n[5/9] Patching ApiServer.cs...');
const apiServer = join(overlay, 'Web', 'ApiServer.cs');
{
  let c = readFileSync(apiServer, 'utf-8');
  let changed = false;

  const replacements = [
    // /state output
    ['autoFlask = s.AutoFlask, flask = s.FlaskNote, ', ''],
    [/autoFlask = s\.AutoFlask, flask = s\.FlaskNote,\s*/g, ''],
    // ReadSettings flask fields
    [/\n        lifeThresholdPct = _settings\.LifeThresholdPct,\n        manaThresholdPct = _settings\.ManaThresholdPct,\n        lifeCooldownMs = _settings\.LifeCooldownMs,\n        manaCooldownMs = _settings\.ManaCooldownMs,\n        lifeKey = _settings\.LifeKey,\n        manaKey = _settings\.ManaKey,\n/g, ''],
    [/\r\n        lifeThresholdPct = _settings\.LifeThresholdPct,\r\n        manaThresholdPct = _settings\.ManaThresholdPct,\r\n        lifeCooldownMs = _settings\.LifeCooldownMs,\r\n        manaCooldownMs = _settings\.ManaCooldownMs,\r\n        lifeKey = _settings\.LifeKey,\r\n        manaKey = _settings\.ManaKey,\r\n/g, ''],
    // ApplySettings flask cases
    [/\n                case "lifeThresholdPct" when TryFloat.*?break;\n                case "manaThresholdPct" when TryFloat.*?break;\n                case "lifeCooldownMs" when TryInt.*?break;\n                case "manaCooldownMs" when TryInt.*?break;\n                case "lifeKey" when TryInt.*?break;\n                case "manaKey" when TryInt.*?break;\n/g, ''],
    [/\r\n                case "lifeThresholdPct" when TryFloat.*?break;\r\n                case "manaThresholdPct" when TryFloat.*?break;\r\n                case "lifeCooldownMs" when TryInt.*?break;\r\n                case "manaCooldownMs" when TryInt.*?break;\r\n                case "lifeKey" when TryInt.*?break;\r\n                case "manaKey" when TryInt.*?break;\r\n/g, ''],
    // RadarState record
    ['    bool AutoFlask,\n    string FlaskNote,\n    string AreaCode,', '    string AreaCode,'],
    ['    bool AutoFlask,\r\n    string FlaskNote,\r\n    string AreaCode,', '    string AreaCode,'],
    // RadarState Empty
    ['100, 100, false, "", "", "", 0)', '100, 100, "", "", 0)'],
    ['100, 100, false, "", "", "", 0)', '100, 100, "", "", 0)'],
    // API docs comments
    ['(+ read-only flask mirror)', ''],
    ['loopback-Host-gated; never exposes flask/automation writes', 'loopback-Host-gated'],
    // ReadSettings doc
    [/Covers radar\/visual options plus auto-flask\n\s+\/\/\/ tuning \(thresholds, cooldowns, keys\)\. All writes are loopback-Host-gated \(see Handle\), so a\n\s+\/\/\/ cross-origin site can't reach them\. The API port is read-only here \(changing it needs a\n\s+\/\/\/ restart\)\. This object also doubles as the GET payload\./,
     'Covers radar/visual options.\n    /// All writes are loopback-Host-gated (see Handle). The API port is read-only here.'],
    [/Covers radar\/visual options plus auto-flask\r\n\s+\/\/\/ tuning \(thresholds, cooldowns, keys\)\. All writes are loopback-Host-gated \(see Handle\), so a\r\n\s+\/\/\/ cross-origin site can't reach them\. The API port is read-only here \(changing it needs a\r\n\s+\/\/\/ restart\)\. This object also doubles as the GET payload\./,
     'Covers radar/visual options.\r\n    /// All writes are loopback-Host-gated (see Handle). The API port is read-only here.'],
    // Thread name
    ['"POE2Radar.Api"', '"PoeMapViewer.Api"'],
  ];

  for (const [pat, rep] of replacements) {
    if (typeof pat === 'string') {
      if (c.includes(pat)) { c = c.replaceAll(pat, rep); changed = true; }
    } else {
      if (pat.test(c)) { c = c.replace(pat, rep); changed = true; }
    }
  }
  if (changed) { writeFileSync(apiServer, c, 'utf-8'); console.log(`  patched: ${apiServer}`); }
  else console.log(`  skip (no matches): ${apiServer}`);
}

// ── 6. DashboardHtml.cs ──
console.log('\n[6/9] Patching DashboardHtml.cs...');
const dashboard = join(overlay, 'Web', 'DashboardHtml.cs');
{
  let c = readFileSync(dashboard, 'utf-8');
  let changed = false;

  // Remove kFlask status line
  const flaskStatusLine = '<div class="kv"><span>Auto-flask</span><span id="kFlask">\u2014</span></div>';
  if (c.includes(flaskStatusLine)) { c = c.replace(flaskStatusLine + '\n', ''); c = c.replace(flaskStatusLine + '\r\n', ''); changed = true; }

  // Remove Auto-Flask card — try multiple patterns since upstream HTML varies
  const flaskCardPatterns = [
    // Variant 1: <div class="card">\n<h3>Auto-Flask</h3>...\n</div>\n
    /\s*<div class="card">\s*\n\s*<h3>Auto-Flask<\/h3>[\s\S]*?<\/div>\s*\n(?=\s*<\/div>)/,
    /\s*<div class="card">\s*\r\n\s*<h3>Auto-Flask<\/h3>[\s\S]*?<\/div>\s*\r\n(?=\s*<\/div>)/,
    // Variant 2: card starts mid-line after </div> from calibration card
    /<\/div>\s*\n\s*<input class="numin" type="number"[^>]*data-set="lifeThresholdPct"><\/div>[\s\S]*?flaskState[^<]*<\/span><\/div><\/div>\s*\n\s*<\/div>/,
    /<\/div>\s*\r\n\s*<input class="numin" type="number"[^>]*data-set="lifeThresholdPct"><\/div>[\s\S]*?flaskState[^<]*<\/span><\/div><\/div>\s*\r\n\s*<\/div>/,
  ];
  for (const pat of flaskCardPatterns) {
    if (pat.test(c)) { c = c.replace(pat, '</div>\n'); changed = true; break; }
  }
  // Also try removing any residual flask input lines individually
  const flaskLinePatterns = [
    /.*data-set="lifeThresholdPct".*\n?/g,
    /.*data-set="manaThresholdPct".*\n?/g,
    /.*data-set="lifeKey".*\n?/g,
    /.*data-set="manaKey".*\n?/g,
    /.*data-set="lifeCooldownMs".*\n?/g,
    /.*data-set="manaCooldownMs".*\n?/g,
    /.*Life flask key.*\n?/g,
    /.*Mana flask key.*\n?/g,
    /.*Life threshold.*\n?/g,
    /.*Mana threshold.*\n?/g,
    /.*Life cooldown.*\n?/g,
    /.*Mana cooldown.*\n?/g,
    /.*flaskState.*\n?/g,
    /.*Auto-Flask.*\n?/g,
    /.*tap (life|mana) flask.*\n?/g,
  ];
  for (const pat of flaskLinePatterns) {
    if (pat.test(c)) { c = c.replace(pat, ''); changed = true; }
  }

  // JS: settings tab comment
  c = c.replace('+ flask via the loopback-gated /api/settings)', 'via the loopback-gated /api/settings)');

  // JS: keyin in loadSettings
  c = c.replace(/else if\(el\.classList\.contains\('keyin'\)\) el\.value=vkToChar\(s\[k\]\);\n/, '');
  c = c.replace(/else if\(el\.classList\.contains\('keyin'\)\) el\.value=vkToChar\(s\[k\]\);\r\n/, '');

  // JS: keyin in wireSettings
  c = c.replace(/else if\(el\.classList\.contains\('keyin'\)\) el\.onchange=\(\) => \{[\s\S]*?\}; \n/, '');
  c = c.replace(/else if\(el\.classList\.contains\('keyin'\)\) el\.onchange=\(\) => \{[\s\S]*?\}; \r\n/, '');

  // JS: charToVk/vkToChar helpers
  c = c.replace(/\/\/ Flask key inputs accept a single character[\s\S]*?const vkToChar = v => v \? String\.fromCharCode\(v\) : '';\n\n/, '');
  c = c.replace(/\/\/ Flask key inputs accept a single character[\s\S]*?const vkToChar = v => v \? String\.fromCharCode\(v\) : '';\r\n\r\n/, '');

  // JS: kFlask update
  c = c.replace(/  \$\('#kFlask'\)\.textContent=\(s\.autoFlask\?'on':'off'\)\+\(s\.flask\?' · '\+s\.flask:''\);\n/, '');
  c = c.replace(/  \$\('#kFlask'\)\.textContent=\(s\.autoFlask\?'on':'off'\)\+\(s\.flask\?' · '\+s\.flask:''\);\r\n/, '');

  // JS: flaskState update
  c = c.replace(/  const fs=\$\('#flaskState'\); if\(fs\) fs\.textContent=\(s\.autoFlask\?'ON':'OFF'\)\+\(s\.flask\?' · '\+s\.flask:''\);\n/, '');
  c = c.replace(/  const fs=\$\('#flaskState'\); if\(fs\) fs\.textContent=\(s\.autoFlask\?'ON':'OFF'\)\+\(s\.flask\?' · '\+s\.flask:''\);\r\n/, '');

  if (changed) { writeFileSync(dashboard, c, 'utf-8'); console.log(`  patched: ${dashboard}`); }
  else console.log(`  skip (no matches): ${dashboard}`);
}

// ── 7. Poe2Live.cs comments ──
console.log('\n[7/9] Patching Poe2Live.cs comments...');
const poe2Live = join(core, 'Game', 'Poe2Live.cs');
patchMulti(poe2Live, [
  ['(auto-flask + HP bars keep working)', '(HP bars keep working)'],
  ['safety-critical flask. Mana is deliberately NOT auto-guessed: the component holds other\n        // valid-looking VitalStructs between Health and Mana (verified live — an ordinal "2nd pool =\n        // Mana" guess lands on the wrong one), and driving the mana flask off the wrong pool is worse\n        // than not firing it. If Mana\'s offset drifts it just reads 0 (\u2192 mana% 100 \u2192 no misfire) until\n        // the table is updated. Health self-heals; mana degrades safely.',
   'safety-critical. Mana is deliberately NOT auto-guessed: the component holds other\n        // valid-looking VitalStructs between Health and Mana (verified live — an ordinal "2nd pool =\n        // Mana" guess lands on the wrong one). If Mana\'s offset drifts it just reads 0 (\u2192 mana% 100)\n        // until the table is updated. Health self-heals; mana degrades safely.'],
  ['life flask + HP bars keep working). Update', 'HP bars keep working). Update'],
  ['; mana flask needs the offset table updated.', ''],
  ['Drives the auto-flask thresholds. Returns null', 'Returns null'],
  ['that as "unknown" and NOT fire flasks, rather than assuming full/empty.', 'when the Life component / vitals can\'t be read plausibly (Max <= 0).'],
]);

// ── 8. Project identity ──
console.log('\n[8/9] Patching project identity...');
const csproj = join(overlay, 'POE2Radar.Overlay.csproj');
{
  let c = readFileSync(csproj, 'utf-8');
  let changed = false;

  // Already patched?
  if (c.includes('<AssemblyName>PoeMapViewer</AssemblyName>')) {
    console.log(`  skip (already patched): ${csproj}`);
  } else {
    // Insert after <TreatWarningsAsErrors> line, before </PropertyGroup>
    const marker = '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>';
    if (c.includes(marker)) {
      const insert = `\n    <NoWarn>IL2026,IL2087,IL2091</NoWarn>\n    <AssemblyName>PoeMapViewer</AssemblyName>\n    <RootNamespace>PoeMapViewer</RootNamespace>\n    <Product>PoE Map Viewer</Product>\n    <Description>External memory-reading map/radar overlay for Path of Exile 2</Description>\n    <Company>Local</Company>`;
      c = c.replace(marker, marker + insert);
      writeFileSync(csproj, c, 'utf-8');
      console.log(`  patched: ${csproj}`);
      changed = true;
    } else {
      console.log(`  skip (marker not found): ${csproj}`);
    }
  }
}

const program = join(overlay, 'Program.cs');
patchMulti(program, [
  ['Console.WriteLine("POE2Radar \u2014 map/radar overlay");\nConsole.WriteLine("=============================");',
   'Console.WriteLine("PoE Map Viewer \u2014 map/radar overlay");\nConsole.WriteLine("===================================");'],
]);

// ── 9. Build ──
console.log('\n[9/9] Building...');
try {
  const out = execSync('dotnet build src/POE2Radar.Overlay/POE2Radar.Overlay.csproj -c Debug', {
    cwd: root, encoding: 'utf-8', stdio: ['pipe','pipe','pipe']
  });
  console.log(out.split('\n').filter(l => l.includes('->') || l.includes('Erro') || l.includes('Aviso') || l.includes('Sucesso') || l.includes('êxito')).join('\n'));
  console.log('\n  BUILD OK');
} catch (e) {
  console.log(e.stdout?.split('\n').slice(-10).join('\n') || e.message);
  console.log('\n  BUILD FAILED');
}

console.log('\nDone. To publish a single-file exe:');
console.log('  node publish.mjs');
