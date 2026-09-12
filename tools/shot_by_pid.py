"""启动指定 exe 并只截它自己的窗口（按 PID 精确定位）。

为什么需要它：机器上可能同时跑着多个 ClassIntraOps 实例（例如你手开的旧版本），
按窗口标题匹配会截到别的实例。本工具用 GetWindowThreadProcessId 过滤，只认自己启动的进程。

用法:
    python shot_by_pid.py <exe路径> <输出png> [等待秒数]

示例（验证未定位 CI 的全新实例）:
    python shot_by_pid.py "%TEMP%/ops-gate/ClassIntraOps.exe" shots/welcome.png 12
"""
import os
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import grabwin  # noqa: E402  （同目录工具，复用 PrintWindow 抓图）


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2

    exe, out = sys.argv[1], sys.argv[2]
    wait = float(sys.argv[3]) if len(sys.argv) > 3 else 10.0

    proc = subprocess.Popen([exe], close_fds=True)
    print("pid:", proc.pid)
    time.sleep(wait)

    hwnd = grabwin.find_window_by_pid(proc.pid)
    if not hwnd:
        print("该进程没有可见窗口（是否已退出？）")
        return 1

    # 恢复并置前，避免最小化状态下抓到异常尺寸
    u32 = grabwin.u32
    u32.ShowWindow(hwnd, 9)  # SW_RESTORE
    time.sleep(1.2)

    w, h = grabwin.capture_window(hwnd, out)
    print(f"saved {out} {w}x{h}")
    print("pid 仍存活:", proc.poll() is None)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
