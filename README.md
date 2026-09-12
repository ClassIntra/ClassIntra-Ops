# ClassIntraOps

ClassIntra（CI）的运维控制台。独立仓库，不依赖 CI 的任何模块。

**下载**：[Releases](https://github.com/ClassIntra/ClassIntra-Ops/releases) 提供 Windows x64 自包含单文件（如 `ClassIntraOps-v0.1-win-x64.exe`，约 99MB），下载即用，无需安装 .NET。

## 形态

双形态组合，按目标机器情况选用：

| 形态 | 适用场景 | 说明 |
|------|---------|------|
| **launcher**（Avalonia 桌面控制台，**主形态**） | 管理员本机、有桌面的 Windows / Linux | Fluent 风格原生窗口：总览 / 密钥 / 跨班 / 安装 / 日志 / 设置 六页，业务逻辑 C# 原生实现 |
| **ops-server**（Web 控制台，备用形态） | 无头 Linux 服务器、远程管理 | 零第三方依赖，`node server.js` 直跑；浏览器访问 `http://127.0.0.1:9099`；从「环境设置」页可一键拉起 |

## 快速开始

### Avalonia 桌面控制台

```bash
# 开发运行
cd launcher
dotnet run -c Release

# 发布单文件（全新机器拷贝这一个 exe 即可，无需安装 .NET）
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# 产物: bin/Release/net10.0/win-x64/publish/ClassIntraOps.exe
```

功能一览：

- **总览**：CPU / 内存 / 磁盘 / 运行时长卡片 + PM2 进程表（状态灯、CPU、内存、重启次数）与启停操作
- **密钥配置**：由 `server/.env.example` 推导分组表单，密钥打码不回显、留空不覆盖，保存自动备份，一键「应用配置重启」
- **跨班对端**：解析 `RELAY_SERVERS`，TCP 层探活判在线（不误判 relay 的 426 路径），支持临时探活任意地址
- **安装更新**：从 GitHub 拉源码 → 装依赖 → 构建前端 → PM2 启动，输出实时回显，失败即停不回滚
- **服务日志**：server-out.log / server-error.log 尾部 300 行
- **环境设置**：CI 仓库路径、工具检测（node/pnpm/git/pm2）、拉起 Web 控制台

### Web 控制台（无头环境）

```bash
node ops-server/server.js
# 打开 http://127.0.0.1:9099，Windows 可双击 ops.bat
```

## 功能

- **欢迎页与门禁**：CI 仓库未定位时，欢迎页是唯一落点，密钥 / 跨班 / 日志导航禁用——先让仓库就位，再谈配置；定位完成自动进入「配置密钥」
- **总览**：CPU / 内存 / 磁盘 KPI 仪表 + PM2 进程状态与启停 + 资源趋势图 + 应用配置重启
- **环境**：node / pnpm / git / pm2 检测、CI 仓库路径设置
- **密钥**：由 `server/.env.example` 推导表单 schema（含分组与注释），密钥默认打码、留空不覆盖，保存自动备份
- **跨班**：解析 `RELAY_SERVERS` 并对每个对端做 TCP 探活（不误判 relay 的 426 路径），附延迟微型柱与行内探活
- **安装**：从 GitHub 拉源码 → 装依赖 → 构建 → PM2 启动，全程实时回显，失败即停不回滚
- **日志**：`logs/server-out.log` / `server-error.log` 尾部
- **托盘常驻**：关闭窗口 = 隐藏进系统托盘后台运行（监控持续），左键恢复，右键菜单退出
- **崩溃兜底**：UI 线程未处理异常统一落盘 `logs/crash.log`，窗口不闪退；后台任务未观察异常不杀进程
- **无边框窗口**：客户区扩展 + 自绘标题栏（拖动 / 双击最大化 / 窗口控制）+ 右下缩放握把

## 设计决定（为什么这样做）

1. **Avalonia 原生实现业务逻辑**（C# 版）：pm2 jlist 解析、.env schema 推导与读写、TCP 探活，全部与 Node 版对齐。桌面形态不依赖 ops-server 常驻，没有鸡生蛋问题。
2. **零第三方依赖（Node 侧）**：只用 Node 内置模块。没有 `npm install`，就不受受限网络拉包抖动影响。
3. **schema 不手写**：运行时解析 `.env.example`（权威字段清单）与 `ecosystem.config.js` 的 env 块（正则提取，不求值），CI 加字段零漂移。
4. **改密钥必须走「应用配置重启」**：它执行 `pm2 restart ecosystem.config.js --update-env`。普通的 `pm2 restart <name>` 保留旧环境变量，`.env` 改了不生效——这是 `ecosystem.config.js` 只在 start 时读 `.env` 决定的。
5. **本进程绝不进 PM2**：控制台要重启 PM2，被 PM2 管等于自杀；且会加剧 PM2 双 daemon 抢 `\\.\pipe\rpc.sock` 的问题。开机自启用注册表 Run 或计划任务单独拉起。
6. **对端探活用 TCP 而非 HTTP**：CI 的 relay 只在 `/relay` 路径响应（返回 426 Upgrade Required），根路径会把 HTTP 请求挂到超时，误判成离线。TCP 握手不受应用路由影响。
7. **只绑 127.0.0.1**：本服务能读写全部密钥。确需局域网访问时设置 `OPS_HOST`，并务必同时设置 `OPS_TOKEN`。
8. **失败即停、保留现场**：安装向导任一步失败立即停止，不自动回滚，避免破坏已有部署。

## 配置（环境变量）

| 变量 | 默认 | 说明 |
|------|------|------|
| `OPS_PORT` | 9099 | 监听端口 |
| `OPS_HOST` | 127.0.0.1 | 监听地址，改前先读安全须知 |
| `OPS_TOKEN` | 空 | 启用后所有请求需带 `?token=` 或 `x-ops-token` 头 |
| `CI_ROOT` | 自动探测 | CI 仓库根目录 |

## 冒烟测试

```bash
cd ops-server
node test-smoke.js
# env 读写测试在临时目录进行，不会碰真实的 server/.env
```
