'use strict';
/**
 * 冒烟测试：node test-smoke.js
 *
 * 只读接口直连真实 CI 环境；env 写入测试在临时目录进行，不碰真实 server/.env。
 */

var os = require('os');
var fs = require('fs');
var path = require('path');
var envLib = require('./lib/env');

var BASE = 'http://127.0.0.1:' + (process.env.OPS_PORT || 9099);
var pass = 0, fail = 0;

function check(name, ok, detail) {
  if (ok) { pass++; console.log('  PASS  ' + name + (detail ? '  ' + detail : '')); }
  else { fail++; console.log('  FAIL  ' + name + (detail ? '  ' + detail : '')); }
}

function get(p) {
  return fetch(BASE + p).then(function (r) { return r.json(); });
}

// ---------------------------------------------------------------- 单元：env 读写
function testEnvModule() {
  console.log('\n[1] env 模块（临时目录，不碰真实 .env）');
  var tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ciops-'));
  fs.mkdirSync(path.join(tmp, 'server'), { recursive: true });

  fs.writeFileSync(path.join(tmp, 'server', '.env.example'), [
    '# 服务端口',
    'PORT=9001',
    '',
    '# JWT 配置（必须设置）',
    '# 至少 32 位随机串',
    'JWT_SECRET=',
    '',
    '# 中继服务器配置',
    'RELAY_SERVERS=',
    'RELAY_SECRET='
  ].join('\n'), 'utf8');

  fs.writeFileSync(path.join(tmp, 'server', '.env'), [
    '# 服务端口',
    'PORT=9001',
    '',
    'JWT_SECRET=abcdefghijklmnopqrstuvwxyz012345',
    'RELAY_SERVERS=http://100.115.93.90:10011'
  ].join('\n'), 'utf8');

  var schema = envLib.readSchema(tmp);
  check('分组数 = 3', schema.groups.length === 3, '实际 ' + schema.groups.length);
  check('首组标题为「服务端口」', schema.groups[0].title === '服务端口', schema.groups[0].title);
  check('JWT_SECRET 判定为密钥', schema.groups[1].fields[0].secret === true);

  var values = envLib.readValues(tmp);
  check('读出现有值 JWT_SECRET', values.values.JWT_SECRET.indexOf('abc') === 0);
  check('密钥已打码', values.masked.JWT_SECRET.indexOf('2345') !== -1 && values.masked.JWT_SECRET.indexOf('abc') === -1,
    values.masked.JWT_SECRET);
  check('非密钥值不打码', values.masked.PORT === '9001');

  var w = envLib.writeValues(tmp, { PORT: '9002', NEW_KEY: 'hello world' });
  var after = envLib.readValues(tmp);
  check('写入后 PORT 更新', after.values.PORT === '9002', after.values.PORT);
  check('新键追加', after.values.NEW_KEY === 'hello world');
  check('未改动的键保留', after.values.JWT_SECRET.indexOf('abc') === 0);
  check('含空格的值已加引号', fs.readFileSync(path.join(tmp, 'server', '.env'), 'utf8').indexOf('NEW_KEY="hello world"') !== -1);
  check('输出为 LF 无 CRLF', fs.readFileSync(path.join(tmp, 'server', '.env'), 'utf8').indexOf('\r\n') === -1);
  check('生成了备份文件', fs.readdirSync(path.join(tmp, 'server')).some(function (f) { return f.indexOf('.bak-') !== -1; }));
  check('原注释保留', fs.readFileSync(path.join(tmp, 'server', '.env'), 'utf8').indexOf('# 服务端口') !== -1);

  fs.rmSync(tmp, { recursive: true, force: true });
}

// ---------------------------------------------------------------- 集成：HTTP 接口
function testApi() {
  console.log('\n[2] HTTP 接口（真实环境只读）');
  return get('/api/health')
    .then(function (h) { check('health 可达', h.ok === true, JSON.stringify(h.service)); })
    .then(function () { return get('/api/config'); })
    .then(function (c) {
      check('config 返回', c.ok === true);
      check('定位到 CI 仓库', !!c.ciRoot, c.ciRoot || '未定位');
      return c;
    })
    .then(function () { return get('/api/env/detect'); })
    .then(function (d) {
      check('环境检测返回', d.ok === true);
      check('node 可用', d.tools && d.tools.node && d.tools.node.available === true, d.tools.node && d.tools.node.version);
      if (d.tools && d.tools.git) console.log('        git: ' + (d.tools.git.available ? d.tools.git.version : '缺失'));
      if (d.tools && d.tools.pm2) console.log('        pm2: ' + (d.tools.pm2.available ? d.tools.pm2.version : '缺失'));
    })
    .then(function () { return get('/api/pm2/status'); })
    .then(function (p) {
      check('pm2 状态可读', p.ok === true, p.ok ? (p.processes || []).length + ' 个进程' : (p.error || ''));
      (p.processes || []).forEach(function (x) {
        console.log('        - ' + x.name + ' [' + x.status + '] cpu=' + x.cpu + '% mem=' + Math.round((x.memory || 0) / 1048576) + 'MB');
      });
    })
    .then(function () { return get('/api/env/schema'); })
    .then(function (s) {
      check('schema 可读', s.ok === true, s.ok ? s.schema.groups.length + ' 组' : s.error);
      check('检测到 .env', s.ok && s.current && s.current.exists === true);
      if (s.ok && s.schema.extraKeys && s.schema.extraKeys.length) {
        console.log('        ecosystem 独有键: ' + s.schema.extraKeys.map(function (k) { return k.key; }).join(', '));
      }
    })
    .then(function () { return get('/api/system'); })
    .then(function (y) {
      check('系统资源可读', y.ok === true, y.ok ? (y.system.cpu.usage * 100).toFixed(0) + '% CPU' : '');
    })
    .then(function () { return get('/api/peers'); })
    .then(function (r) {
      check('跨班状态可读', r.ok === true, r.ok ? (r.total + ' 个对端，在线 ' + r.online) : r.error);
      (r.peers || []).forEach(function (p) {
        console.log('        - ' + p.url + ' → ' + (p.ok ? 'HTTP ' + p.status + ' (' + p.ms + 'ms)' : p.error));
      });
    })
    .then(function () { return get('/api/logs?lines=5'); })
    .then(function (l) {
      check('日志可读', l.ok === true, l.ok ? (l.lines || []).length + ' 行' : l.error);
    })
    .then(function () { return get('/api/'); })
    .then(function (x) { check('未知接口返回 404', x.ok === false); });
}

require('./server.js');

setTimeout(function () {
  testEnvModule();
  testApi().then(function () {
    console.log('\n结果: ' + pass + ' 通过 / ' + fail + ' 失败\n');
    process.exit(fail === 0 ? 0 : 1);
  }).catch(function (e) {
    console.error('测试异常: ' + e.message);
    process.exit(1);
  });
}, 300);
