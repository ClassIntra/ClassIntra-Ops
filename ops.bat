@echo off
chcp 65001 >nul 2>&1
title ClassIntraOps
cd /d "%~dp0ops-server"

echo [ClassIntraOps] starting console at http://127.0.0.1:9099
echo [ClassIntraOps] close this window to stop the console

rem browser opens after 1s while node runs in the foreground
start "" cmd /c "timeout /t 1 >nul & start http://127.0.0.1:9099"
node server.js
