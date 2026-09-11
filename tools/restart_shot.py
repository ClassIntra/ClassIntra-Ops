"""Kill all ClassIntraOps instances, start a fresh one, wait, screenshot."""
import os
import subprocess
import sys
import time

os.system("taskkill /F /IM ClassIntraOps.exe >nul 2>&1")
time.sleep(2)

EXE = r"D:\NetWork\Integration\ClassIntraOps\launcher\bin\Debug\net10.0\ClassIntraOps.exe"
subprocess.Popen(["cmd", "/c", "start", "", EXE], close_fds=True)
time.sleep(9)

r = os.popen("tasklist | findstr /I ClassIntraOps").read()
print("processes:", r.strip().replace("\n", " | "))

graber = r"C:\Users\iflytek\.workbuddy\binaries\python\versions\3.13.12\python.exe"
subprocess.run([graber, r"D:\NetWork\Integration\ClassIntraOps\tools\grab.py",
                r"D:\NetWork\Integration\ClassIntraOps\selfcheck5.png"])
