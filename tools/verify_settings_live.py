"""设置页「保存即生效」回归验证（自包含：备份/还原 preferences.json）。

针对两个用户反馈：
1. 温度单位切换无效 —— 主页大温度绑的是 Current.Temperature+转换器，
   单位切换只通知 TemperatureText，大字不重新求值（已修：改绑 TemperatureText）
2. 来源显示不随选择器切换而改变 —— 来源标注要等下一次取数才更新，
   保存后没有自动刷新（已修：Save_Click 结束后触发一次刷新）

验证点（全程不手动点刷新）：
1. 设置页翻转单位开关 → 保存 → 回主页：全窗口温度文本单位整体翻转
2. 设置页把「天气预报」源改成另一个免 Key 源 → 保存 → 回主页：
   趋势卡来源标注变成新指派的源
3. 进程无崩溃、截图存档

用法：python tools/verify_settings_live.py
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PUBLISH = REPO_ROOT / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish"
SHOTS = REPO_ROOT / "docs" / "shots"
PREFS = Path(os.environ.get("LOCALAPPDATA", "")) / "SkyView" / "preferences.json"
PREFS_BAK = PREFS.with_suffix(".json.verifybak")

# 下拉显示名 → provider.Name（来源标注里显示的是 Name）
DISPLAY_TO_NAME = {
    "和风天气": "QWeather",
    "OpenWeatherMap": "OpenWeatherMap",
    "心知天气": "Seniverse",
    "AccuWeather": "AccuWeather",
    "PirateWeather": "PirateWeather",
    "Open-Meteo": "OpenMeteo",
    "wttr.in": "Wttr",
}

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    ctypes.windll.user32.SetProcessDPIAware()

USER32 = ctypes.windll.user32
TEMP_RE = re.compile(r"-?\d+(?:\.\d+)?\s*°[CF]")


def _rect(hwnd: int) -> tuple[int, int, int, int]:
    r = wt.RECT()
    USER32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def _find_main(pid: int) -> int:
    best: list[tuple[int, int]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(h, _):
        o = wt.DWORD()
        USER32.GetWindowThreadProcessId(h, ctypes.byref(o))
        if o.value != pid or not USER32.IsWindowVisible(h):
            return True
        l, t, r, b = _rect(h)
        best.append(((r - l) * (b - t), h))
        return True

    USER32.EnumWindows(cb, 0)
    return max(best)[1] if best else 0


def _wait(pid: int, timeout: float = 30.0) -> int:
    dl = time.time() + timeout
    while time.time() < dl:
        h = _find_main(pid)
        if h:
            l, t, r, b = _rect(h)
            if (r - l) >= 400 and (b - t) >= 300:
                return h
        time.sleep(0.3)
    return 0


def _text_of(w) -> str:
    if w is None:
        return "<元素不存在>"
    for getter in ("window_text", "get_value"):
        try:
            value = getattr(w, getter)()
            if value:
                return str(value).strip()
        except Exception:
            continue
    try:
        return str(w.name).strip()
    except Exception:
        return ""


def _is_on(toggle) -> bool | None:
    try:
        return bool(toggle.is_selected())
    except Exception:
        pass
    try:
        return bool(toggle.get_toggle_state())
    except Exception:
        return None


def _flip(toggle) -> None:
    for action in ("toggle", "click_input"):
        try:
            getattr(toggle, action)()
            return
        except Exception:
            continue
    raise RuntimeError("既不能 toggle 也不能 click_input")


def _window_temps(win) -> list[str]:
    """窗口里所有温度文本（主页大温度、体感、逐小时、趋势区间）。"""
    out: list[str] = []
    try:
        for el in win.descendants():
            try:
                if el.element_info.control_type != "Text":
                    continue
                text = _text_of(el)
            except Exception:
                continue
            if text and TEMP_RE.search(text):
                out.append(text)
    except Exception:
        pass
    return out


def _capture(hwnd: int, name: str):
    PW_RENDERFULLCONTENT = 0x00000002
    GDI32 = ctypes.windll.gdi32

    class _BH(ctypes.Structure):
        _fields_ = [
            ("biSize", wt.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
            ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
            ("biSizeImage", wt.DWORD),
            ("biXPelsPerMeter", ctypes.c_long), ("biYPelsPerMeter", ctypes.c_long),
            ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
        ]

    l, t, r, b = _rect(hwnd)
    w, h = r - l, b - t
    if w <= 0 or h <= 0:
        return None
    USER32.GetWindowDC.restype = wt.HDC
    USER32.GetWindowDC.argtypes = [wt.HWND]
    USER32.ReleaseDC.argtypes = [wt.HWND, wt.HDC]
    USER32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
    GDI32.CreateCompatibleDC.restype = wt.HDC
    GDI32.CreateCompatibleDC.argtypes = [wt.HDC]
    GDI32.CreateCompatibleBitmap.restype = wt.HBITMAP
    GDI32.CreateCompatibleBitmap.argtypes = [wt.HDC, ctypes.c_int, ctypes.c_int]
    GDI32.SelectObject.restype = wt.HGDIOBJ
    GDI32.SelectObject.argtypes = [wt.HDC, wt.HGDIOBJ]
    GDI32.DeleteObject.argtypes = [wt.HGDIOBJ]
    GDI32.DeleteDC.argtypes = [wt.HDC]
    GDI32.GetDIBits.argtypes = [wt.HDC, wt.HBITMAP, wt.UINT, wt.UINT, ctypes.c_void_p, ctypes.c_void_p, wt.UINT]

    hdc = USER32.GetWindowDC(hwnd)
    mdc = GDI32.CreateCompatibleDC(hdc)
    bmp = GDI32.CreateCompatibleBitmap(hdc, w, h)
    old = GDI32.SelectObject(mdc, bmp)
    try:
        if not USER32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT):
            print("  ! 截图失败：PrintWindow 返回 0")
            return None
        hdr = _BH()
        hdr.biSize = ctypes.sizeof(_BH)
        hdr.biWidth = w
        hdr.biHeight = -h
        hdr.biPlanes = 1
        hdr.biBitCount = 32
        buf = ctypes.create_string_buffer(w * h * 4)
        if not GDI32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(hdr), 0):
            print("  ! 截图失败：GetDIBits 返回 0")
            return None
        from PIL import Image
        img = Image.frombuffer("RGB", (w, h), buf, "raw", "BGRX", 0, 1)
        out = SHOTS / name
        out.parent.mkdir(parents=True, exist_ok=True)
        img.save(out)
        return out
    finally:
        GDI32.SelectObject(mdc, old)
        GDI32.DeleteObject(bmp)
        GDI32.DeleteDC(mdc)
        USER32.ReleaseDC(hwnd, hdc)


def main() -> int:
    exe = PUBLISH / "SkyView.exe"
    if not exe.is_file():
        sys.exit(f"找不到 {exe}，先跑 dotnet publish")
    if not PREFS.is_file():
        sys.exit(f"找不到 {PREFS}")

    shutil.copy2(PREFS, PREFS_BAK)
    print(f"已备份 preferences.json -> {PREFS_BAK.name}")

    crash_path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SkyView", "crash.log")
    if os.path.exists(crash_path):
        os.remove(crash_path)

    env = dict(os.environ)
    env.pop("SKYVIEW_DEMO", None)
    proc = subprocess.Popen([str(exe)], env=env)
    try:
        from pywinauto import Application

        hwnd = _wait(proc.pid)
        if not hwnd:
            sys.exit("30s 内没等到主窗口")
        l, t, r, b = _rect(hwnd)
        USER32.MoveWindow(hwnd, l, t, 1000, 1000, True)
        USER32.SetForegroundWindow(hwnd)
        time.sleep(7.0)  # 启动自动刷新

        app = Application(backend="uia").connect(process=proc.pid)
        win = app.window(title="SkyView")

        def by_id(auto_id: str):
            try:
                return win.child_window(auto_id=auto_id)
            except Exception:
                return None

        temp_before = _text_of(by_id("TemperatureText"))
        fcst_before = _text_of(by_id("ForecastSourceText"))
        temps_before = _window_temps(win)
        print(f"[初始] 温度={temp_before!r}  预报来源={fcst_before!r}")
        print(f"[初始] 窗口温度文本 {len(temps_before)} 条：{temps_before[:6]}…")

        # ---------- 设置页 ----------
        win.child_window(auto_id="SettingsNavItem").select()
        time.sleep(1.6)

        unit_before = _is_on(by_id("UnitToggle"))
        _flip(by_id("UnitToggle"))
        time.sleep(0.4)
        unit_after = _is_on(by_id("UnitToggle"))
        print(f"[设置页] 单位开关 {unit_before} -> {unit_after}")

        combo = by_id("FeatureSource_Forecast").wrapper_object()
        try:
            combo_before = combo.selected_text()
        except Exception:
            combo_before = _text_of(combo)
        # 只在两个免 Key 源之间切换，保证指派后一定能取到数（付费 Key 已失效）
        target_display = "Open-Meteo" if combo_before == "wttr.in" else "wttr.in"
        combo.select(target_display)
        time.sleep(0.4)
        try:
            combo_now = combo.selected_text()
        except Exception:
            combo_now = _text_of(combo)
        print(f"[设置页] 预报源 {combo_before!r} -> {combo_now!r}")

        by_id("SaveSettingsButton").invoke()
        time.sleep(1.5)
        print(f"[设置页] 状态文本={_text_of(by_id('StatusText'))!r}")
        try:
            saved = json.loads(PREFS.read_text(encoding="utf-8"))
            print(f"[设置页] 保存后 preferences.json = {json.dumps(saved, ensure_ascii=False)}")
        except Exception as ex:
            print(f"[设置页] 保存后读取 preferences.json 失败: {ex}")

        # ---------- 回主页（不手动刷新，等保存时触发的自动刷新） ----------
        win.child_window(auto_id="HomeNavItem").select()

        expected_unit = "°F" if unit_after else "°C"
        stale_unit = "°C" if unit_after else "°F"
        expected_name = DISPLAY_TO_NAME[target_display]

        temp_after = ""
        fcst_after = ""
        temps_after: list[str] = []
        deadline = time.time() + 25.0
        while time.time() < deadline:
            time.sleep(1.5)
            temp_after = _text_of(by_id("TemperatureText"))
            fcst_after = _text_of(by_id("ForecastSourceText"))
            temps_after = _window_temps(win)
            if expected_unit in temp_after and expected_name in fcst_after:
                break

        print(f"[主页] 温度={temp_after!r}  预报来源={fcst_after!r}")
        print(f"[主页] 窗口温度文本 {len(temps_after)} 条：{temps_after[:6]}…")

        results: list[tuple[str, bool, str]] = []
        results.append((
            f"主页温度单位翻转为 {expected_unit}（无需手动刷新）",
            expected_unit in temp_after and stale_unit not in temp_after,
            f"{temp_before} → {temp_after}"))
        results.append((
            "全窗口温度文本单位整体翻转",
            bool(temps_after) and all(expected_unit in x for x in temps_after),
            f"{len(temps_after)} 条，含旧单位 {sum(1 for x in temps_after if stale_unit in x)} 条"))
        results.append((
            f"预报来源变为 {expected_name}（保存即生效，无需手动刷新）",
            expected_name in fcst_after,
            f"{fcst_before} → {fcst_after}"))

        shot = _capture(hwnd, "11-保存即生效.png")
        results.append(("主页截图", shot is not None, str(shot.name) if shot else "失败"))

        alive = proc.poll() is None
        results.append(("进程存活（无崩溃）", alive, f"alive={alive}"))

        crash = ""
        if os.path.exists(crash_path):
            crash = open(crash_path, encoding="utf-8", errors="replace").read()[-400:]
        results.append(("无崩溃日志", not crash, crash[:200] if crash else "无"))

        print()
        width = max(len(n) for n, _, _ in results)
        passed = 0
        for n, ok_flag, detail in results:
            print(f"  {'✓' if ok_flag else '✗'} {n.ljust(width)}  {detail}")
            passed += 1 if ok_flag else 0
        print(f"\n共 {len(results)} 项：通过 {passed}，失败 {len(results) - passed}")
        return 0 if passed == len(results) else 1
    finally:
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(timeout=8)
            except subprocess.TimeoutExpired:
                proc.kill()
        shutil.move(str(PREFS_BAK), str(PREFS))
        print("已关闭应用并还原 preferences.json")


if __name__ == "__main__":
    sys.exit(main())
