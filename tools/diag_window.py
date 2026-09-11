"""Diagnose: start app, enumerate its top-level windows, capture output."""
import ctypes
import os
import subprocess
import sys
import time

EXE = r"D:\NetWork\Integration\ClassIntraOps\launcher\bin\Debug\net10.0\ClassIntraOps.exe"

os.system("taskkill /F /IM ClassIntraOps.exe >nul 2>&1")
time.sleep(2)

proc = subprocess.Popen([EXE], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                        creationflags=subprocess.CREATE_NO_WINDOW)
time.sleep(9)

if proc.poll() is not None:
    out, err = proc.communicate()
    print("EXITED code", proc.returncode)
    print("STDOUT:", out.decode("utf-8", "replace")[:2000])
    print("STDERR:", err.decode("utf-8", "replace")[:2000])
    sys.exit(0)

pid = proc.pid
print("pid", pid, "running")

# Enumerate top-level windows of this pid
u32 = ctypes.windll.user32
titles = []


@ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
def cb(hwnd, _):
    owner = ctypes.c_ulong()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
    if owner.value == pid:
        n = u32.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        u32.GetWindowTextW(hwnd, buf, n + 1)
        visible = u32.IsWindowVisible(hwnd)
        titles.append((hwnd, buf.value, bool(visible)))
    return True


u32.EnumWindows(cb, None)
print("windows:", titles)

# If a window exists, bring to front and screenshot
graber = r"C:\Users\iflytek\.workbuddy\binaries\python\versions\3.13.12\python.exe"
if titles:
    for hwnd, t, vis in titles:
        if vis and t:
            u32.SetForegroundWindow(hwnd)
            u32.BringWindowToTop(hwnd)
            break
    time.sleep(1)
    subprocess.run([graber, r"D:\NetWork\Integration\ClassIntraOps\tools\grab.py",
                    r"D:\NetWork\Integration\ClassIntraOps\selfcheck8.png"])
else:
    # No window: terminate and read output to find the exception
    proc.terminate()
    out, err = proc.communicate(timeout=5)
    print("no window. STDOUT:", out.decode("utf-8", "replace")[:3000])
    print("no window. STDERR:", err.decode("utf-8", "replace")[:3000])
