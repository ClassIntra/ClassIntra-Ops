"""Full loop: kill -> build -> run -> wait for chart samples -> screenshot."""
import os
import subprocess
import time

DOTNET = r"C:\Program Files\dotnet\dotnet.exe"
PROJ = r"D:\NetWork\Integration\ClassIntraOps\launcher\ClassIntraOps.Launcher.csproj"
EXE = r"D:\NetWork\Integration\ClassIntraOps\launcher\bin\Debug\net10.0\ClassIntraOps.exe"
PY = r"C:\Users\iflytek\.workbuddy\binaries\python\versions\3.13.12\python.exe"
SHOT = r"D:\NetWork\Integration\ClassIntraOps\selfcheck6.png"

os.system("taskkill /F /IM ClassIntraOps.exe >nul 2>&1")
time.sleep(2)

r = subprocess.run([DOTNET, "build", PROJ, "-nologo", "-v", "q"],
                   capture_output=True, text=True)
tail = "\n".join(r.stdout.strip().splitlines()[-3:])
print("build:", tail)
if r.returncode != 0:
    print(r.stdout[-2000:])
    sys.exit(1)

subprocess.Popen(["cmd", "/c", "start", "", EXE], close_fds=True)
time.sleep(75)
subprocess.run([PY, r"D:\NetWork\Integration\ClassIntraOps\tools\grab.py", SHOT])
