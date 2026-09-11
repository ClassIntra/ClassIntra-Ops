'use strict';
/**
 * .env 相关：表单 schema 推导 + 保序保注释的读写
 *
 * schema 有两个来源，合并后去重：
 *   1) server/.env.example —— 权威字段清单，带分组注释，跨语言可解析
 *   2) ecosystem.config.js 的 env 块 —— 补齐 example 里漏写的键（如 NODE_ENV）
 * 不 require ecosystem.config.js：独立仓库不该依赖 CI 的模块解析方式。
 */

var fs = require('fs');
var path = require('path');

var SECRET_RE = /(KEY|SECRET|PASSWORD|TOKEN|PRIVATE|CREDENTIAL)/i;

function isSecretKey(key) {
  return SECRET_RE.test(key);
}

/**
 * 打码：短值全遮，长值保留后 4 位。GUI 能读密钥，所以默认不给明文。
 */
function maskValue(value) {
  if (value === undefined || value === null) return '';
  var s = String(value);
  if (s === '') return '';
  if (s.length <= 8) return s.replace(/[\s\S]/g, '*');
  return s.slice(0, -4).replace(/[\s\S]/g, '*') + s.slice(-4);
}

/**
 * 解析 .env / .env.example 文本为行模型，保留注释与空行顺序。
 * @returns {{lines: Array, map: Object}}
 */
function parse(text) {
  var lines = String(text || '').split(/\r?\n/);
  var model = [];
  var map = {};

  for (var i = 0; i < lines.length; i++) {
    var raw = lines[i];
    var trimmed = raw.trim();

    if (trimmed === '' || trimmed.charAt(0) === '#') {
      model.push({ type: trimmed === '' ? 'blank' : 'comment', raw: raw });
      continue;
    }

    var eq = trimmed.indexOf('=');
    if (eq === -1) {
      model.push({ type: 'raw', raw: raw });
      continue;
    }

    var key = trimmed.slice(0, eq).trim();
    var value = trimmed.slice(eq + 1).trim();
    // 去掉成对引号
    if (value.length >= 2 &&
        ((value.charAt(0) === '"' && value.charAt(value.length - 1) === '"') ||
         (value.charAt(0) === "'" && value.charAt(value.length - 1) === "'"))) {
      value = value.slice(1, value.length - 1);
    }

    model.push({ type: 'pair', key: key, value: value, raw: raw });
    map[key] = value;
  }

  return { lines: model, map: map };
}

/**
 * 由 .env.example 推导表单 schema。
 * 规则：空行分节；节内首个注释行作分组标题，其余注释作字段说明。
 */
function buildSchemaFromExample(text) {
  var parsed = parse(text);
  var groups = [];
  var current = null;
  var pendingComments = [];

  function flushGroup() {
    if (current && current.fields.length > 0) groups.push(current);
    current = null;
  }

  for (var i = 0; i < parsed.lines.length; i++) {
    var line = parsed.lines[i];

    if (line.type === 'blank') {
      flushGroup();
      pendingComments = [];
      continue;
    }

    if (line.type === 'comment') {
      pendingComments.push(line.raw.replace(/^\s*#\s?/, '').trim());
      continue;
    }

    if (line.type === 'pair') {
      if (!current) {
        current = {
          title: pendingComments.length > 0 ? pendingComments[0] : '其他',
          description: pendingComments.slice(1).join(' '),
          fields: []
        };
      } else if (pendingComments.length > 0 && current.fields.length === 0) {
        current.description = (current.description ? current.description + ' ' : '') + pendingComments.join(' ');
      }
      current.fields.push({
        key: line.key,
        defaultValue: line.value,
        secret: isSecretKey(line.key),
        hint: pendingComments.join(' ')
      });
      pendingComments = [];
      continue;
    }
  }
  flushGroup();

  return { groups: groups, map: parsed.map };
}

/**
 * 从 ecosystem.config.js 里正则抓取 env 块的键名。
 * 只做文本扫描，不做 JS 求值——避免执行任意代码。
 */
function extractEcosystemKeys(text) {
  var keys = [];
  var s = String(text || '');
  var start = s.search(/env\s*:\s*\{/);
  if (start === -1) return keys;

  var braceStart = s.indexOf('{', start);
  var depth = 0;
  var end = -1;
  for (var i = braceStart; i < s.length; i++) {
    if (s.charAt(i) === '{') depth++;
    else if (s.charAt(i) === '}') {
      depth--;
      if (depth === 0) { end = i; break; }
    }
  }
  if (end === -1) return keys;

  var block = s.slice(braceStart + 1, end);
  var re = /^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:/gm;
  var m;
  while ((m = re.exec(block)) !== null) keys.push(m[1]);
  return keys;
}

/**
 * 合并两个来源，产出前端直接可渲染的 schema。
 * @param {string} ciRoot CI 仓库根目录
 */
function readSchema(ciRoot) {
  var examplePath = path.join(ciRoot, 'server', '.env.example');
  var ecoPath = path.join(ciRoot, 'ecosystem.config.js');
  var result = { groups: [], extraKeys: [], sources: {} };

  var exampleText = '';
  try {
    exampleText = fs.readFileSync(examplePath, 'utf8');
    result.sources.example = 'server/.env.example';
  } catch (e) {
    result.sources.example = null;
  }

  if (exampleText) {
    var built = buildSchemaFromExample(exampleText);
    result.groups = built.groups;
    result.defaults = built.map;
  } else {
    result.defaults = {};
  }

  var known = {};
  result.groups.forEach(function (g) {
    g.fields.forEach(function (f) { known[f.key] = true; });
  });

  try {
    var ecoKeys = extractEcosystemKeys(fs.readFileSync(ecoPath, 'utf8'));
    result.sources.ecosystem = 'ecosystem.config.js';
    ecoKeys.forEach(function (k) {
      if (!known[k]) result.extraKeys.push({ key: k, secret: isSecretKey(k), source: 'ecosystem' });
    });
  } catch (e) {
    result.sources.ecosystem = null;
  }

  return result;
}

/**
 * 读取当前生效值（打码）。
 */
function readValues(ciRoot) {
  var envPath = path.join(ciRoot, 'server', '.env');
  var out = { exists: false, values: {}, masked: {}, path: envPath, mtime: null };
  try {
    var stat = fs.statSync(envPath);
    out.exists = true;
    out.mtime = stat.mtime.toISOString();
  } catch (e) {
    return out;
  }

  var parsed = parse(fs.readFileSync(envPath, 'utf8'));
  out.values = parsed.map;
  Object.keys(parsed.map).forEach(function (k) {
    out.masked[k] = isSecretKey(k) ? maskValue(parsed.map[k]) : parsed.map[k];
  });
  return out;
}

/**
 * 写入 .env：保留注释与键的原有位置，新键追加到末尾。
 * 强制 UTF-8 + LF 无 BOM——PowerShell 5.1 的 Set-Content 默认 GBK，这里绕开它。
 */
function writeValues(ciRoot, updates) {
  var envPath = path.join(ciRoot, 'server', '.env');
  var serverDir = path.dirname(envPath);

  var original = '';
  try {
    original = fs.readFileSync(envPath, 'utf8');
  } catch (e) {
    original = '';
  }

  if (original) {
    var stamp = new Date().toISOString().replace(/[:.]/g, '-');
    try { fs.writeFileSync(envPath + '.bak-' + stamp, original, 'utf8'); } catch (e) { /* 备份失败不阻断 */ }
  }

  var parsed = parse(original);
  var remaining = {};
  Object.keys(updates).forEach(function (k) { remaining[k] = String(updates[k]); });

  var outLines = [];
  for (var i = 0; i < parsed.lines.length; i++) {
    var line = parsed.lines[i];
    if (line.type === 'pair' && Object.prototype.hasOwnProperty.call(remaining, line.key)) {
      var value = remaining[line.key];
      // 含空格、引号、# 的值加引号，避免破坏解析
      if (/[\s"#]/.test(value)) value = '"' + value.replace(/"/g, '\\"') + '"';
      outLines.push(line.key + '=' + value);
      delete remaining[line.key];
    } else {
      outLines.push(line.raw);
    }
  }

  var appended = Object.keys(remaining);
  if (appended.length > 0) {
    outLines.push('');
    outLines.push('# 由 ClassIntraOps 追加 ' + new Date().toISOString());
    appended.forEach(function (k) {
      var value = remaining[k];
      if (/[\s"#]/.test(value)) value = '"' + value.replace(/"/g, '\\"') + '"';
      outLines.push(k + '=' + value);
    });
  }

  if (!original && !fs.existsSync(serverDir)) {
    fs.mkdirSync(serverDir, { recursive: true });
  }

  fs.writeFileSync(envPath, outLines.join('\n').replace(/\r\n/g, '\n') + '\n', 'utf8');

  return { ok: true, path: envPath, updated: Object.keys(updates), appended: appended, backup: original ? envPath + '.bak-*' : null };
}

module.exports = {
  parse: parse,
  readSchema: readSchema,
  readValues: readValues,
  writeValues: writeValues,
  maskValue: maskValue,
  isSecretKey: isSecretKey,
  buildSchemaFromExample: buildSchemaFromExample,
  extractEcosystemKeys: extractEcosystemKeys
};
