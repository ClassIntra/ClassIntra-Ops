"""ClassIntraOps 逐页截图（确定性键盘导航）。

为什么不用坐标点击：图标栏的 ListBox 项命中不稳定（焦点/缩放/遮挡都会让点击落空），
坐标点击会静默截到错误的页面。这里改为：
  置前窗口（AttachThreadInput 跨进程才稳） → 点一次导航项拿到焦点
  → Home 回第一项 → Down × n → PrintWindow 抓窗口内容
PrintWindow 能截被遮挡的窗口，所以不必把窗口摆到最前也能拿图。

用法:
    python nav_shot.py <输出目录> [页面名 ...]

示例:
    python nav_shot.py shots                     # 全部页面
    python nav_shot.py shots secrets peers       # 只抓这两页

注意：页面顺序必须与 MainWindow.axaml 中 NavList 的项顺序一致。
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

u32 = ctypes.windll.user32
u32.SetProcessDPIAware()

PY = r"C:\Users\iflytek\.workbuddy\binaries\python\versions\3.13.12\python.exe"
HERE = os.path.dirname(os.path.abspath(__file__))
GRABWIN = os.path.join(HERE, "grabwin.py")

# 与 MainWindow.axaml 的 NavList 顺序一致（secrets/peers/logs 在未定位 CI 时被门禁禁用）
PAGES = ["welcome", "overview", "secrets", "peers", "install", "logs", "settings"]

VK_HOME = 0x24
VK_DOWN = 0x28
KEYEVENTF_KEYUP = 0x0002
NAV_CLICK_X = 31   # 图标栏中心（窗口内坐标）
NAV_CLICK_Y = 152  # 任意一个导航项，只为把焦点交给 ListBox


def find_window(sub):
    found = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def cb(hwnd, _):
        if not u32.IsWindowVisible(hwnd):
            return True
        n = u32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        u32.GetWindowTextW(hwnd, buf, n + 1)
        if sub in buf.value:
            found.append(hwnd)
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else None


def force_foreground(hwnd):
    k32 = ctypes.windll.kernel32
    fg = u32.GetForegroundWindow()
    tid_fg = u32.GetWindowThreadProcessId(fg, None)
    tid_me = k32.GetCurrentThreadId()
    try:
        u32.AttachThreadInput(tid_me, tid_fg, True)
        u32.ShowWindow(hwnd, 9)
        u32.BringWindowToTop(hwnd)
        u32.SetForegroundWindow(hwnd)
        u32.SetFocus(hwnd)
    finally:
        u32.AttachThreadInput(tid_me, tid_fg, False)


def key(vk):
    u32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.05)
    u32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.25)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    out_dir = sys.argv[1]
    wanted = sys.argv[2:] or PAGES
    os.makedirs(out_dir, exist_ok=True)

    hwnd = find_window("ClassIntraOps")
    if not hwnd:
        print("未找到 ClassIntraOps 窗口（先启动 launcher）")
        return 1

    force_foreground(hwnd)
    time.sleep(1.2)
    print("foreground:", u32.GetForegroundWindow() == hwnd)

    # 把焦点交给图标栏：点一次导航项区域
    rect = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(rect))
    u32.SetCursorPos(rect.left + NAV_CLICK_X, rect.top + NAV_CLICK_Y)
    u32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.05)
    u32.mouse_event(0x0004, 0, 0, 0, 0)
    time.sleep(1.2)

    for name in wanted:
        if name not in PAGES:
            print(f"跳过未知页面: {name}")
            continue
        idx = PAGES.index(name)
        key(VK_HOME)
        for _ in range(idx):
            key(VK_DOWN)
        time.sleep(1.6)
        subprocess.run([PY, GRABWIN, os.path.join(out_dir, f"pg-{name}.png"), "ClassIntraOps"],
                       check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
