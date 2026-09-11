'use strict';
/**
 * ClassIntraOps —— CI 运维控制台后端
 *
 * 设计约束：
 * 1) 零第三方依赖，只用 Node 内置模块，clone 后直接 node server.js
 * 2) 默认只绑 127.0.0.1，因为它能读写 .env 里的全部密钥
 * 3) 本进程绝不被 PM2 托管（它会重启 PM2，等于自杀）
 */

var http = require('http');
var fs = require('fs');
var path = require('path');
var os = require('os');

var proc = require('./lib/proc');
var envLib = require('./lib/env');
var pm2Lib = require('./lib/pm2');

// APP_ROOT 指向仓库根（ops-server 的上一级），CI 仓库探测要以它为基准
var ROOT = path.resolve(__dirname);
var APP_ROOT = path.resolve(ROOT, '..');
var PUBLIC_DIR = path.join(ROOT, 'public');
var CONFIG_FILE = path.join(APP_ROOT, 'config.json');

var PORT = Number(process.env.OPS_PORT || 9099);
var HOST = process.env.OPS_HOST || '127.0.0.1';
var TOKEN = process.env.OPS_TOKEN || '';
var CI_ROOT = process.env.CI_ROOT || '';

// ---------------------------------------------------------------- 配置持久化

function loadConfig() {
  var cfg = { ciRoot: '', repo: 'https://github.com/ClassIntra/ClassIntra.git', ref: 'main' };
  try {
    var saved = JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8'));
    if (saved && typeof saved === 'object') {
      Object.keys(saved).forEach(function (k) { cfg[k] = saved[k]; });
    }
  } catch (e) { /* 首次运行无配置 */ }
  if (CI_ROOT) cfg.ciRoot = CI_ROOT;
  return cfg;
}

function saveConfig(cfg) {
  try {
    fs.writeFileSync(CONFIG_FILE, JSON.stringify(cfg, null, 2), 'utf8');
    return true;
  } catch (e) {
    return false;
  }
}

var CONFIG = loadConfig();

/**
 * CI 根目录探测：显式配置 > 同级 ClassIntra > 当前目录。
 * 判定依据是 ecosystem.config.js 与 server 目录同时存在。
 */
function looksLikeCiRoot(dir) {
  try {
    return fs.existsSync(path.join(dir, 'ecosystem.config.js')) && fs.existsSync(path.join(dir, 'server'));
  } catch (e) {
    return false;
  }
}

function resolveCiRoot() {
  if (CONFIG.ciRoot && looksLikeCiRoot(CONFIG.ciRoot)) return CONFIG.ciRoot;

  var candidates = [
    path.resolve(APP_ROOT, 'ClassIntra'),
    path.resolve(APP_ROOT, '..', 'ClassIntra'),
    path.resolve(APP_ROOT, '..', 'ClassIntra-main'),
    path.resolve(process.cwd()),
    path.resolve(process.cwd(), '..')
  ];

  // 再沿仓库根向上找四级，覆盖 ops 仓库被放在更深目录的情况
  var dir = APP_ROOT;
  for (var i = 0; i < 4; i++) {
    dir = path.dirname(dir);
    if (!dir || dir === path.dirname(dir)) break;
    candidates.push(path.join(dir, 'ClassIntra'));
    candidates.push(path.join(dir, 'ClassIntra-main'));
  }

  for (var j = 0; j < candidates.length; j++) {
    if (looksLikeCiRoot(candidates[j])) return candidates[j];
  }
  return CONFIG.ciRoot || null;
}

// ---------------------------------------------------------------- HTTP 工具

function sendJson(res, code, obj) {
  var body = JSON.stringify(obj);
  res.writeHead(code, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'Cache-Control': 'no-store'
  });
  res.end(body);
}

function readBody(req) {
  return new Promise(function (resolve, reject) {
    var chunks = [];
    var size = 0;
    req.on('data', function (d) {
      size += d.length;
      if (size > 2 * 1024 * 1024) { reject(new Error('请求体过大')); req.destroy(); return; }
      chunks.push(d);
    });
    req.on('end', function () {
      var raw = Buffer.concat(chunks).toString('utf8');
      if (!raw) return resolve({});
      try { resolve(JSON.parse(raw)); } catch (e) { reject(new Error('JSON 解析失败')); }
    });
    req.on('error', reject);
  });
}

function listLocalIps() {
  var out = [];
  var ifaces = os.networkInterfaces();
  Object.keys(ifaces).forEach(function (name) {
    (ifaces[name] || []).forEach(function (i) {
      if (i.family === 'IPv4' && !i.internal) out.push(i.address);
    });
  });
  return out;
}

// ---------------------------------------------------------------- 路由

var routes = {};

routes['GET /api/health'] = function (req, res) {
  sendJson(res, 200, { ok: true, service: 'classintra-ops', pid: process.pid, uptime: process.uptime() });
};

routes['GET /api/config'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  sendJson(res, 200, {
    ok: true,
    ciRoot: ciRoot,
    ciRootValid: !!ciRoot,
    repo: CONFIG.repo,
    ref: CONFIG.ref,
    host: HOST,
    port: PORT,
    platform: process.platform,
    ips: listLocalIps(),
    tokenEnabled: !!TOKEN
  });
};

routes['POST /api/config'] = function (req, res) {
  readBody(req).then(function (body) {
    if (body.ciRoot) {
      var target = path.resolve(String(body.ciRoot));
      if (!looksLikeCiRoot(target)) {
        return sendJson(res, 400, { ok: false, error: '该目录下没有 ecosystem.config.js 或 server/，不像 CI 仓库: ' + target });
      }
      CONFIG.ciRoot = target;
    }
    if (body.repo) CONFIG.repo = String(body.repo);
    if (body.ref) CONFIG.ref = String(body.ref);
    saveConfig(CONFIG);
    sendJson(res, 200, { ok: true, config: { ciRoot: CONFIG.ciRoot, repo: CONFIG.repo, ref: CONFIG.ref } });
  }).catch(function (e) { sendJson(res, 400, { ok: false, error: e.message }); });
};

routes['GET /api/env/detect'] = function (req, res) {
  var jobs = [
    proc.detect('node', '--version'),
    proc.detect('pnpm', '--version'),
    proc.detect('npm', '--version'),
    proc.detect('git', '--version'),
    proc.detect('pm2', '--version')
  ];
  Promise.all(jobs).then(function (results) {
    var out = {};
    results.forEach(function (r) { out[r.name] = { available: r.available, version: r.version, raw: r.raw }; });
    sendJson(res, 200, { ok: true, tools: out, platform: process.platform });
  });
};

routes['GET /api/pm2/status'] = function (req, res) {
  pm2Lib.jlist().then(function (r) {
    sendJson(res, 200, Object.assign({ ciRoot: resolveCiRoot() }, r));
  });
};

routes['POST /api/pm2/action'] = function (req, res) {
  readBody(req).then(function (body) {
    return pm2Lib.action(body.name || '', body.action || '');
  }).then(function (r) {
    sendJson(res, 200, r);
  }).catch(function (e) { sendJson(res, 400, { ok: false, error: e.message }); });
};

routes['POST /api/pm2/restart-env'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  if (!ciRoot) return sendJson(res, 400, { ok: false, error: '未定位到 CI 仓库' });
  pm2Lib.restartWithEnv(ciRoot).then(function (r) { sendJson(res, 200, r); });
};

routes['POST /api/pm2/start-all'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  if (!ciRoot) return sendJson(res, 400, { ok: false, error: '未定位到 CI 仓库' });
  pm2Lib.startAll(ciRoot).then(function (r) { sendJson(res, 200, r); });
};

routes['GET /api/env/schema'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  if (!ciRoot) return sendJson(res, 400, { ok: false, error: '未定位到 CI 仓库' });
  try {
    var schema = envLib.readSchema(ciRoot);
    var current = envLib.readValues(ciRoot);
    sendJson(res, 200, { ok: true, ciRoot: ciRoot, schema: schema, current: current });
  } catch (e) {
    sendJson(res, 500, { ok: false, error: e.message });
  }
};

routes['POST /api/env/values'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  if (!ciRoot) return sendJson(res, 400, { ok: false, error: '未定位到 CI 仓库' });

  readBody(req).then(function (body) {
    var updates = body.values || {};
    var result = envLib.writeValues(ciRoot, updates);
    sendJson(res, 200, Object.assign({ ok: true, note: '改完需执行 pm2 restart ecosystem.config.js --update-env 才生效' }, result));
  }).catch(function (e) { sendJson(res, 400, { ok: false, error: e.message }); });
};

routes['GET /api/peers'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  if (!ciRoot) return sendJson(res, 400, { ok: false, error: '未定位到 CI 仓库' });

  var values = envLib.readValues(ciRoot);
  var raw = (values.values && values.values.RELAY_SERVERS) || '';
  var list = String(raw).split(',').map(function (s) { return s.trim(); }).filter(function (s) { return s !== ''; });

  pm2Lib.probePeers(list, 5000).then(function (r) {
    sendJson(res, 200, {
      ok: true,
      serverId: (values.values && values.values.RELAY_SERVER_ID) || '',
      configured: list,
      relayEnabled: (values.values && values.values.RELAY_SECRET) ? true : false,
      peers: r.peers,
      total: r.total,
      online: r.online
    });
  });
};

routes['POST /api/peers/probe'] = function (req, res) {
  readBody(req).then(function (body) {
    return pm2Lib.probePeers(body.targets || [], body.timeoutMs || 5000);
  }).then(function (r) { sendJson(res, 200, Object.assign({ ok: true }, r)); })
    .catch(function (e) { sendJson(res, 400, { ok: false, error: e.message }); });
};

routes['GET /api/system'] = function (req, res) {
  sendJson(res, 200, { ok: true, system: pm2Lib.systemInfo(resolveCiRoot() || process.cwd()) });
};

routes['GET /api/logs'] = function (req, res) {
  var ciRoot = resolveCiRoot();
  if (!ciRoot) return sendJson(res, 400, { ok: false, error: '未定位到 CI 仓库' });

  var q = new URL(req.url, 'http://ops.local').searchParams;
  var lines = Math.min(Number(q.get('lines')) || 200, 2000);
  var file = q.get('file') === 'error' ? 'server-error.log' : 'server-out.log';
  var logPath = path.join(ciRoot, 'logs', file);

  try {
    var text = fs.readFileSync(logPath, 'utf8');
    var all = text.split(/\r?\n/);
    sendJson(res, 200, {
      ok: true,
      file: file,
      path: logPath,
      lines: all.slice(Math.max(0, all.length - lines)),
      total: all.length
    });
  } catch (e) {
    sendJson(res, 200, { ok: false, file: file, path: logPath, error: '日志不可读: ' + e.message, lines: [] });
  }
};

/**
 * 安装向导：NDJSON 流，每完成一步推一行事件，失败即停、保留现场。
 */
routes['POST /api/install'] = function (req, res) {
  readBody(req).then(function (body) {
    var steps = Array.isArray(body.steps) ? body.steps : ['detect', 'clone', 'install', 'build', 'start'];
    var target = body.dir ? path.resolve(String(body.dir)) : (resolveCiRoot() || path.resolve(ROOT, '..', 'ClassIntra'));
    var repo = body.repo || CONFIG.repo;
    var ref = body.ref || CONFIG.ref;
    var pkgManager = body.pkgManager || 'pnpm';

    res.writeHead(200, {
      'Content-Type': 'application/x-ndjson; charset=utf-8',
      'Cache-Control': 'no-store',
      'X-Accel-Buffering': 'no'
    });

    function emit(obj) {
      res.write(JSON.stringify(obj) + '\n');
    }

    function runStep(name, label, command, cwd) {
      emit({ type: 'step', name: name, label: label, command: command, cwd: cwd, status: 'running' });
      return new Promise(function (resolve) {
        var handle = proc.stream(command, { cwd: cwd }, function (err, chunk) {
          if (chunk) emit({ type: 'output', step: name, chunk: chunk });
        });
        handle.done.then(function (r) {
          emit({ type: 'step', name: name, status: r.ok ? 'done' : 'failed', code: r.code, error: r.error });
          resolve(r);
        });
      });
    }

    (function chain() {
      var hasRepo = fs.existsSync(path.join(target, '.git'));

      var plan = [];
      if (steps.indexOf('clone') !== -1) {
        plan.push(hasRepo
          ? { name: 'clone', label: '更新源码', command: 'git fetch --all --tags && git checkout ' + ref + ' && git pull --ff-only', cwd: target }
          : { name: 'clone', label: '拉取源码', command: 'git clone --branch ' + ref + ' ' + repo + ' "' + target + '"', cwd: path.dirname(target) });
      }
      if (steps.indexOf('install') !== -1) {
        plan.push({ name: 'install', label: '安装依赖', command: pkgManager + ' install', cwd: target });
      }
      if (steps.indexOf('build') !== -1) {
        plan.push({ name: 'build', label: '构建前端', command: 'cd client && ' + pkgManager + ' run build', cwd: target });
      }
      if (steps.indexOf('start') !== -1) {
        plan.push({ name: 'start', label: '启动并注册', command: 'pm2 start ecosystem.config.js && pm2 save', cwd: target });
      }

      emit({ type: 'begin', target: target, repo: repo, ref: ref, steps: plan.map(function (s) { return s.name; }) });

      var i = 0;
      function next() {
        if (i >= plan.length) {
          emit({ type: 'finish', ok: true });
          res.end();
          return;
        }
        var s = plan[i++];
        runStep(s.name, s.label, s.command, s.cwd).then(function (r) {
          if (!r.ok) {
            emit({ type: 'finish', ok: false, failedAt: s.name, code: r.code, hint: '已保留现场，未自动回滚' });
            res.end();
            return;
          }
          next();
        });
      }

      if (!fs.existsSync(path.dirname(target)) && !hasRepo) {
        try { fs.mkdirSync(path.dirname(target), { recursive: true }); } catch (e) { /* 交给 git 报错 */ }
      }
      next();
    })();
  }).catch(function (e) {
    sendJson(res, 400, { ok: false, error: e.message });
  });
};

// ---------------------------------------------------------------- 静态资源

function serveStatic(req, res, pathname) {
  var rel = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
  var filePath = path.join(PUBLIC_DIR, rel);

  // 防目录穿越
  if (filePath.indexOf(PUBLIC_DIR) !== 0) {
    res.writeHead(403); res.end('Forbidden'); return;
  }

  fs.readFile(filePath, function (err, data) {
    if (err) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Not found: ' + rel);
      return;
    }
    var ext = path.extname(filePath).toLowerCase();
    var types = { '.html': 'text/html; charset=utf-8', '.js': 'application/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.svg': 'image/svg+xml' };
    res.writeHead(200, { 'Content-Type': types[ext] || 'application/octet-stream', 'Cache-Control': 'no-store' });
    res.end(data);
  });
}

// ---------------------------------------------------------------- 启动

var server = http.createServer(function (req, res) {
  var parsedUrl = new URL(req.url, 'http://ops.local');
  var pathname = parsedUrl.pathname;

  if (TOKEN) {
    var provided = parsedUrl.searchParams.get('token') || req.headers['x-ops-token'];
    if (provided !== TOKEN) {
      return sendJson(res, 401, { ok: false, error: '需要 token' });
    }
  }

  var key = req.method + ' ' + pathname;
  if (routes[key]) {
    return routes[key](req, res);
  }
  if (pathname.indexOf('/api/') === 0) {
    return sendJson(res, 404, { ok: false, error: '未知接口: ' + key });
  }
  serveStatic(req, res, pathname);
});

server.listen(PORT, HOST, function () {
  var ciRoot = resolveCiRoot();
  console.log('[ClassIntraOps] 控制台已启动: http://' + HOST + ':' + PORT);
  console.log('[ClassIntraOps] CI 仓库: ' + (ciRoot || '未定位到，请在页面中设置'));
  if (HOST !== '127.0.0.1' && HOST !== 'localhost' && HOST !== '::1') {
    console.warn('[ClassIntraOps] 警告: 当前监听 ' + HOST + '，本服务可读写全部密钥，建议仅本机访问');
  }
});

module.exports = server;
