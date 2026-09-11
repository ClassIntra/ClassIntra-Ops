'use strict';
/**
 * PM2 封装 + 系统资源 + 对端探活（零第三方依赖）
 *
 * PM2 是跨平台的进程管理层：Windows 与 Linux 命令一致，
 * 所以启停逻辑不需要按平台分叉，只有「控制台自身开机自启」才需要分平台。
 */

var os = require('os');
var fs = require('fs');
var http = require('http');
var net = require('net');
var proc = require('./proc');

var ALLOWED_ACTIONS = ['start', 'stop', 'restart', 'reload', 'delete', 'save', 'flush'];
var NAME_RE = /^[A-Za-z0-9_.\-]+$/;

/**
 * pm2 jlist 的输出有时会混入非 JSON 的提示行，这里做容错截取。
 */
function extractJson(text) {
  var s = String(text || '');
  var start = s.indexOf('[');
  var end = s.lastIndexOf(']');
  if (start === -1 || end <= start) return null;
  try {
    return JSON.parse(s.slice(start, end + 1));
  } catch (e) {
    return null;
  }
}

function jlist() {
  return proc.run('pm2 jlist', { timeoutMs: 20000 }).then(function (r) {
    if (!r.ok) {
      return { ok: false, error: (r.stderr || r.stdout || '').trim().slice(0, 300) || 'pm2 不可用', processes: [] };
    }
    var list = extractJson(r.stdout);
    if (!list) return { ok: false, error: 'pm2 jlist 输出无法解析', processes: [] };

    var processes = list.map(function (p) {
      var pm2env = p.pm2_env || {};
      var monit = p.monit || {};
      return {
        name: p.name,
        pid: p.pid,
        status: pm2env.status,
        restarts: pm2env.restart_time,
        uptime: pm2env.pm_uptime ? Date.now() - pm2env.pm_uptime : null,
        cpu: monit.cpu,
        memory: monit.memory,
        nodeVersion: pm2env.node_version,
        script: pm2_env_safe(pm2env, 'pm_exec_path')
      };
    });
    return { ok: true, processes: processes };
  });
}

function pm2_env_safe(env, key) {
  try { return env[key] || null; } catch (e) { return null; }
}

/**
 * 对单个进程执行动作。name 走白名单校验，避免命令注入。
 */
function action(name, act) {
  if (ALLOWED_ACTIONS.indexOf(act) === -1) {
    return Promise.resolve({ ok: false, error: '不支持的动作: ' + act });
  }
  if (name && !NAME_RE.test(name)) {
    return Promise.resolve({ ok: false, error: '进程名不合法: ' + name });
  }

  var cmd = act === 'save' || act === 'flush' ? 'pm2 ' + act : 'pm2 ' + act + ' ' + name;
  return proc.run(cmd, { timeoutMs: 60000 }).then(function (r) {
    return { ok: r.ok, code: r.code, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
  });
}

/**
 * 用 ecosystem.config.js 重启并刷新环境变量。
 * 关键：pm2 restart <name> 会保留旧 env，改完 .env 必须用 --update-env 才生效。
 */
function restartWithEnv(ciRoot) {
  return proc.run('pm2 restart ecosystem.config.js --update-env', { cwd: ciRoot, timeoutMs: 120000 })
    .then(function (r) {
      return { ok: r.ok, code: r.code, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
    });
}

function startAll(ciRoot) {
  return proc.run('pm2 start ecosystem.config.js', { cwd: ciRoot, timeoutMs: 120000 })
    .then(function (r) {
      if (!r.ok) return { ok: false, code: r.code, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
      return proc.run('pm2 save', { cwd: ciRoot, timeoutMs: 30000 }).then(function () {
        return { ok: true, stdout: r.stdout.trim(), stderr: r.stderr.trim() };
      });
    });
}

/**
 * 系统资源快照。磁盘用 fs.statfs（Node 18.15+），避免调 wmic / df。
 */
function systemInfo(ciRoot) {
  var cpus = os.cpus() || [];
  var totalMem = os.totalmem();
  var freeMem = os.freemem();

  var load = cpus.map(function (c) {
    var total = Object.keys(c.times).reduce(function (a, k) { return a + c.times[k]; }, 0);
    return { idle: c.times.idle, total: total };
  });
  var idleSum = load.reduce(function (a, l) { return a + l.idle; }, 0);
  var totalSum = load.reduce(function (a, l) { return a + l.total; }, 0);
  var cpuUsage = totalSum > 0 ? (1 - idleSum / totalSum) : 0;

  var disk = null;
  try {
    var statfs = fs.statfsSync(ciRoot || process.cwd());
    disk = {
      total: statfs.blocks * statfs.bsize,
      free: statfs.bfree * statfs.bsize,
      available: typeof statfs.bavail === 'number' ? statfs.bavail * statfs.bsize : null
    };
  } catch (e) {
    disk = null;
  }

  return {
    platform: process.platform,
    arch: process.arch,
    hostname: os.hostname(),
    cpu: { cores: cpus.length, model: cpus.length > 0 ? cpus[0].model : null, usage: cpuUsage },
    memory: { total: totalMem, free: freeMem, used: totalMem - freeMem, usage: (totalMem - freeMem) / totalMem },
    disk: disk,
    uptime: os.uptime(),
    loadavg: proc.IS_WIN ? null : os.loadavg()
  };
}

/**
 * 归一化地址：RELAY_SERVERS 里存的是 ws:// 端点，探活要转成 http(s)，
 * 否则 http.request 直接报 ENOTFOUND；缺协议时补 http://。
 */
function normalizeUrl(rawUrl) {
  var target = String(rawUrl || '').trim();
  if (target === '') return null;
  if (/^wss?:\/\//i.test(target)) target = target.replace(/^ws/i, 'http');
  else if (!/^https?:\/\//i.test(target)) target = 'http://' + target;
  try {
    return { url: new URL(target), text: target };
  } catch (e) {
    return null;
  }
}

/**
 * TCP 层探活。
 *
 * 为什么不用 HTTP：CI 的 relay 只在 /relay 路径响应（返回 426 Upgrade Required），
 * 根路径不响应会把 HTTP 请求挂到超时，误判成离线。TCP 握手不受应用路由影响，
 * 对「对端在不在」这个问题既更快也更准。
 */
function tcpProbe(host, port, timeoutMs) {
  return new Promise(function (resolve) {
    var t0 = Date.now();
    var socket = new net.Socket();
    var settled = false;

    function done(ok, err) {
      if (settled) return;
      settled = true;
      socket.destroy();
      resolve({ ok: ok, error: err || null, ms: Date.now() - t0 });
    }

    socket.setTimeout(timeoutMs);
    socket.once('connect', function () { done(true, null); });
    socket.once('timeout', function () { done(false, '超时'); });
    socket.once('error', function (e) { done(false, String((e && e.code) || e.message || e)); });
    socket.connect(port, host);
  });
}

/**
 * 探测单个对端：先 TCP 握手定生死，再顺带取一次 HTTP 状态码作补充信息。
 * HTTP 拿不到不影响在线判定——426 与无响应都说明进程在监听。
 */
function probePeer(rawUrl, timeoutMs) {
  var parsed = normalizeUrl(rawUrl);
  if (!parsed) return Promise.resolve({ url: rawUrl, ok: false, error: '地址无法解析' });

  var host = parsed.url.hostname;
  var port = Number(parsed.url.port) || (parsed.url.protocol === 'https:' ? 443 : 80);
  var t0 = Date.now();

  return tcpProbe(host, port, timeoutMs || 5000).then(function (r) {
    var out = { url: rawUrl, resolved: parsed.text, host: host, port: port, ok: r.ok, ms: r.ms, layer: 'tcp' };
    if (!r.ok) {
      out.error = r.error;
      return out;
    }
    return httpStatusCode(parsed.url, parsed.text, 3000).then(function (h) {
      if (h.status !== null) out.status = h.status;
      return out;
    });
  });
}

function httpStatusCode(urlObj, targetText, timeoutMs) {
  return new Promise(function (resolve) {
    var req = http.request({
      protocol: urlObj.protocol,
      hostname: urlObj.hostname,
      port: urlObj.port || (urlObj.protocol === 'https:' ? 443 : 80),
      path: (urlObj.pathname || '/') + (urlObj.search || ''),
      method: 'GET',
      timeout: timeoutMs,
      headers: { 'User-Agent': 'ClassIntraOps/1.0', 'Accept': '*/*' }
    }, function (res) {
      resolve({ status: res.statusCode });
      res.resume();
    });
    req.on('timeout', function () { req.destroy(); resolve({ status: null }); });
    req.on('error', function () { resolve({ status: null }); });
    req.end();
  });
}

function probePeers(list, timeoutMs) {
  var targets = (list || []).filter(function (s) { return String(s || '').trim() !== ''; });
  if (targets.length === 0) return Promise.resolve({ peers: [], total: 0, online: 0 });
  return Promise.all(targets.map(function (t) { return probePeer(t, timeoutMs); }))
    .then(function (peers) {
      return {
        peers: peers,
        total: peers.length,
        online: peers.filter(function (p) { return p.ok; }).length
      };
    });
}

module.exports = {
  jlist: jlist,
  action: action,
  restartWithEnv: restartWithEnv,
  startAll: startAll,
  systemInfo: systemInfo,
  probePeer: probePeer,
  probePeers: probePeers
};
