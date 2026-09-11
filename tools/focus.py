"""Bring ClassIntraOps window to foreground, then caller screenshots."""
import ctypes

u32 = ctypes.windll.user32
u32.SetProcessDPIAware()

hwnd = u32.FindWindowW(None, "ClassIntraOps · CI 运维控制台")
if not hwnd:
    print("window not found")
    raise SystemExit(1)

SW_RESTORE = 9
if u32.IsIconic(hwnd):
    u32.ShowWindow(hwnd, SW_RESTORE)
u32.SetForegroundWindow(hwnd)
u32.BringWindowToTop(hwnd)
print("foreground ok", hwnd)
