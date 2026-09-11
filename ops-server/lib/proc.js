'use strict';
/**
 * 子进程执行封装（零第三方依赖）
 *
 * 两个 Windows 特有坑在这里统一处理：
 * 1) pm2 / pnpm / git 在 Windows 上是 .cmd 而非 .exe，spawn 直调会失败，必须走 cmd /c
 * 2) 子进程 stdout 在 Windows 走系统 ANSI（GBK），不能无脑 utf8 解码
 */

var spawn = require('child_process').spawn;

var IS_WIN = process.platform === 'win32';

/**
 * 把 Buffer 解成字符串。先按 utf8 试，出现替换字符说明不是 utf8，
 * 再退到 gbk（Node 默认带 full-icu，TextDecoder 支持 gbk）。
 */
function decode(buf) {
  if (!buf || buf.length === 0) return '';
  var text = buf.toString('utf8');
  if (text.indexOf('\uFFFD') === -1) return text;
  try {
    var gbk = new TextDecoder('gbk').decode(buf);
    // gbk 几乎总能解出东西，只有确认不再含替换字符时才采信
    if (gbk.indexOf('\uFFFD') === -1) return gbk;
  } catch (e) {
    /* 该环境无 gbk 解码器，沿用 utf8 结果 */
  }
  return text;
}

function shellArgs(command) {
  return IS_WIN ? ['/c', command] : ['-c', command];
}

function shellBin() {
  return IS_WIN ? 'cmd' : 'sh';
}

/**
 * 执行一条命令并等它结束。
 * @returns Promise<{ok, code, stdout, stderr, ms, error}>
 */
function run(command, opts) {
  opts = opts || {};
  return new Promise(function (resolve) {
    var t0 = Date.now();
    var child;
    try {
      child = spawn(shellBin(), shellArgs(command), {
        cwd: opts.cwd || process.cwd(),
        env: opts.env || process.env,
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe']
      });
    } catch (e) {
      resolve({ ok: false, code: -1, stdout: '', stderr: String((e && e.message) || e), ms: 0, error: 'spawn-failed' });
      return;
    }

    var outChunks = [];
    var errChunks = [];
    var killed = false;

    var timer = null;
    if (opts.timeoutMs) {
      timer = setTimeout(function () {
        killed = true;
        try { child.kill(true); } catch (e) { /* 已退出 */ }
      }, opts.timeoutMs);
    }

    child.stdout.on('data', function (d) { outChunks.push(d); });
    child.stderr.on('data', function (d) { errChunks.push(d); });

    child.on('error', function (e) {
      if (timer) clearTimeout(timer);
      resolve({ ok: false, code: -1, stdout: '', stderr: String((e && e.message) || e), ms: Date.now() - t0, error: 'spawn-error' });
    });

    child.on('close', function (code) {
      if (timer) clearTimeout(timer);
      resolve({
        ok: code === 0,
        code: code,
        stdout: decode(Buffer.concat(outChunks)),
        stderr: decode(Buffer.concat(errChunks)),
        ms: Date.now() - t0,
        error: killed ? 'timeout' : null
      });
    });
  });
}

/**
 * 检测某个命令行工具是否可用，可用则返回版本号。
 * 版本探测统一用 --version，覆盖范围有限但够用；失败不抛异常。
 */
function detect(name, versionArg) {
  return run(name + ' ' + (versionArg || '--version'), { timeoutMs: 15000 }).then(function (r) {
    if (!r.ok) return { name: name, available: false, version: null, raw: (r.stderr || r.stdout || '').trim().slice(0, 200) };
    var text = (r.stdout || '').trim().split(/\r?\n/)[0];
    return { name: name, available: true, version: text, raw: text };
  });
}

/**
 * 流式执行：每有一批输出就回调一次，用于安装向导的实时回显（SSE）。
 * @returns 带 kill() 的句柄，以及一个 done Promise
 */
function stream(command, opts, onChunk) {
  opts = opts || {};
  var child = spawn(shellBin(), shellArgs(command), {
    cwd: opts.cwd || process.cwd(),
    env: opts.env || process.env,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe']
  });

  var done = new Promise(function (resolve) {
    var settled = false;
    function finish(code, err) {
      if (settled) return;
      settled = true;
      resolve({ ok: code === 0 && !err, code: typeof code === 'number' ? code : -1, error: err || null });
    }

    child.stdout.on('data', function (d) { onChunk(null, decode(d)); });
    child.stderr.on('data', function (d) { onChunk(null, decode(d)); });
    child.on('error', function (e) { finish(-1, String((e && e.message) || e)); });
    child.on('close', function (code) { finish(code, null); });
  });

  return {
    done: done,
    kill: function () { try { child.kill(true); } catch (e) { /* 已退出 */ } }
  };
}

module.exports = { run: run, detect: detect, stream: stream, decode: decode, IS_WIN: IS_WIN };
