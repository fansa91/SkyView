#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SkyView UI 布局检查脚本（基于 Python uiautomation 第三方库）

依赖:
    pip install uiautomation

用法:
    python check_ui_layout.py --launch
    python check_ui_layout.py --pid 12345
    python check_ui_layout.py --hwnd 0x0020A1B2
    python check_ui_layout.py --json --launch > report.json

窗口定位策略（按优先级）：
    1. --hwnd：直接按窗口句柄定位（最直接）
    2. --pid / --launch：先遍历 UIA 根节点的子窗口匹配 ProcessId；
       若 UIA 根节点枚举不到窗口（某些会话/服务上下文会这样），
       自动回退到 Win32 EnumWindows + ControlFromHandle。
    3. 仅按名称时：先用 UIA 找 Name='SkyView'，失败再用 Win32 标题匹配，
       并通过子元素特征验证是不是真正的 SkyView 窗口。
"""

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

try:
    import uiautomation as auto
except ImportError:
    print("错误: 未安装 uiautomation 库，请先执行: pip install uiautomation", file=sys.stderr)
    sys.exit(1)

# 让中文在任意控制台编码下都能正常显示
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass


_REPO_ROOT = Path(__file__).resolve().parent.parent

# 优先用 Release 发布版（自带运行时，最接近用户实际运行状态），其次退回 Debug 构建
APP_PATH_CANDIDATES = [
    _REPO_ROOT
    / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/publish/SkyView.exe",
    _REPO_ROOT
    / "src/SkyView/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/SkyView.exe",
    _REPO_ROOT
    / "src/SkyView/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/SkyView.exe",
]


def _default_app_path() -> str:
    for candidate in APP_PATH_CANDIDATES:
        if candidate.is_file():
            return str(candidate)
    return str(APP_PATH_CANDIDATES[-1])


APP_PATH = _default_app_path()

DEFAULT_WINDOW_NAME = "SkyView"


class LayoutIssue:
    """布局问题记录"""

    def __init__(self, description: str, severity: str):
        self.description = description
        self.severity = severity  # "Error" | "Warning" | "Info"

    def to_dict(self) -> Dict[str, str]:
        return {"severity": self.severity, "description": self.description}


class RectInfo:
    """矩形边界信息"""

    def __init__(self, left: float, top: float, right: float, bottom: float):
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom
        self.width = right - left
        self.height = bottom - top

    def to_dict(self) -> Dict[str, float]:
        return {
            "left": self.left,
            "top": self.top,
            "right": self.right,
            "bottom": self.bottom,
            "width": self.width,
            "height": self.height,
        }

    def __str__(self) -> str:
        return (
            f"[{self.left:.0f},{self.top:.0f},{self.right:.0f},{self.bottom:.0f}] "
            f"W={self.width:.0f} H={self.height:.0f}"
        )


# --------------------------------------------------------------------------- #
# 窗口枚举
# --------------------------------------------------------------------------- #
def _enum_windows_win32() -> List[Dict[str, Any]]:
    """用 Win32 EnumWindows 枚举可见的顶级窗口（UIA 枚举不到时的兜底方案）"""
    try:
        import ctypes
        from ctypes import wintypes
    except Exception:
        return []

    user32 = ctypes.windll.user32
    results: List[Dict[str, Any]] = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def _callback(hwnd, _lparam):
        try:
            if not user32.IsWindowVisible(hwnd):
                return True

            length = user32.GetWindowTextLengthW(hwnd)
            title_buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, title_buf, length + 1)

            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))

            class_buf = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, class_buf, 256)

            results.append(
                {
                    "hwnd": int(hwnd),
                    "pid": int(pid.value),
                    "title": title_buf.value,
                    "class": class_buf.value,
                }
            )
        except Exception:
            pass
        return True

    try:
        user32.EnumWindows(_callback, 0)
    except Exception:
        return results
    return results


def _walk_top_level_windows() -> List[auto.WindowControl]:
    """遍历所有顶级窗口，返回控件列表"""
    windows: List[auto.WindowControl] = []
    try:
        root = auto.GetRootControl()
        child = root.GetFirstChildControl()
        while child:
            try:
                if child.ControlType == auto.ControlType.Window:
                    windows.append(child)  # type: ignore[arg-type]
            except Exception:
                pass
            child = child.GetNextSiblingControl()
    except Exception:
        pass
    return windows


def _control_from_hwnd(hwnd: int) -> Optional[auto.Control]:
    try:
        ctrl = auto.ControlFromHandle(hwnd)
        if ctrl and ctrl.Exists(0, 0):
            return ctrl
    except Exception:
        pass
    return None


def _find_window_by_pid(pid: int, timeout: int = 10,
                        poll_interval: float = 0.5) -> Optional[auto.Control]:
    """按进程 ID 查找窗口（带重试；UIA 枚举不到时用 Win32 兜底）"""
    deadline = time.time() + timeout
    diagnostics: List[str] = []

    while time.time() < deadline:
        if pid <= 0:
            break

        # 策略 1：UIA 根节点子窗口
        for w in _walk_top_level_windows():
            try:
                if w.ProcessId == pid:
                    return w
            except Exception:
                pass

        # 策略 2：Win32 枚举 + ControlFromHandle
        win32_windows = _enum_windows_win32()
        for info in win32_windows:
            if info["pid"] == pid and info["title"]:
                ctrl = _control_from_hwnd(info["hwnd"])
                if ctrl:
                    return ctrl

        diagnostics = [
            f"PID={i['pid']} Title='{i['title']}' Class='{i['class']}'" for i in win32_windows[:10]
        ]
        time.sleep(poll_interval)

    print(f"[DEBUG] 进程 {pid} 未找到匹配窗口。最近由 Win32 枚举到的窗口：", file=sys.stderr)
    if diagnostics:
        for line in diagnostics:
            print(f"[DEBUG]   {line}", file=sys.stderr)
    else:
        print("[DEBUG]   （Win32 也未枚举到任何可见窗口，可能当前会话没有可交互桌面）",
              file=sys.stderr)
    return None


def _find_window_by_name(name: str, timeout: int = 10,
                         poll_interval: float = 0.5) -> Optional[auto.Control]:
    """按名称查找窗口（UIA 优先，Win32 标题兜底）"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            w = auto.WindowControl(searchDepth=1, Name=name)
            if w.Exists(0, 0):
                return w
        except Exception:
            pass

        for info in _enum_windows_win32():
            if name.lower() in (info["title"] or "").lower():
                ctrl = _control_from_hwnd(info["hwnd"])
                if ctrl:
                    return ctrl

        time.sleep(poll_interval)
    return None


def _is_likely_skyview_window(window: auto.Control) -> bool:
    """判断窗口是否可能是 SkyView 主窗口（通过子元素特征验证）"""
    probes = [
        (auto.AutomationProperty.AutomationId, "CityInput"),
        (auto.AutomationProperty.AutomationId, "RefreshButton"),
        (auto.AutomationProperty.Name, "刷新天气"),
        (auto.AutomationProperty.Name, "城市"),
    ]
    for prop, value in probes:
        try:
            ctrl = window.FindFirst(
                searchCondition=auto.CreatePropertyCondition(prop, value),
                findOptions=auto.FindOptions.Descendants,
            )
            if ctrl and ctrl.Exists(0, 0):
                return True
        except Exception:
            continue
    return False


# --------------------------------------------------------------------------- #
# 检查器
# --------------------------------------------------------------------------- #
class UIAutomationLayoutInspector:
    """基于 Python uiautomation 的 UI 布局检查器"""

    def __init__(self, app_path: str, launch: bool = False, pid: Optional[int] = None,
                 hwnd: Optional[int] = None, timeout: int = 10):
        self.app_path = app_path
        self.launch = launch
        self.pid = pid
        self.hwnd = hwnd
        self.timeout = timeout
        self.window: Optional[auto.Control] = None
        self.process: Optional[subprocess.Popen] = None
        self.issues: List[LayoutIssue] = []

    def __enter__(self):
        self._connect()
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        if self.launch and self.process:
            try:
                self.process.terminate()
                self.process.wait(timeout=5)
            except Exception:
                pass

    # -- 连接 ---------------------------------------------------------------
    def _connect(self) -> None:
        if self.hwnd:
            self._connect_by_hwnd()
        elif self.launch:
            self._connect_by_launch()
        elif self.pid:
            self._connect_by_pid()
        else:
            self._connect_by_name()

        if self.window:
            try:
                self.window.SetFocus()
            except Exception:
                pass
            time.sleep(0.5)

    def _connect_by_hwnd(self) -> None:
        print(f"[INFO] 按窗口句柄连接: 0x{self.hwnd:08X}")
        self.window = _control_from_hwnd(self.hwnd)
        if not self.window:
            raise RuntimeError(f"无法从窗口句柄 0x{self.hwnd:08X} 获取 UI 控件")
        print(f"[INFO] 已定位到窗口: Name='{self.window.Name}' Class='{self.window.ClassName}'")

    def _connect_by_launch(self) -> None:
        print(f"[INFO] 启动应用程序: {self.app_path}")
        self.process = subprocess.Popen(self.app_path)
        launch_pid = self.process.pid
        print(f"[INFO] 启动进程 PID={launch_pid}")

        print(f"[INFO] 等待窗口出现（进程 PID={launch_pid}）...")
        self.window = _find_window_by_pid(launch_pid, timeout=self.timeout)
        if self.window:
            print(f"[INFO] 已通过 PID={launch_pid} 定位到窗口: Name='{self.window.Name}' "
                  f"Class='{self.window.ClassName}'")
            return

        print("[WARN] PID 定位失败，尝试按窗口标题定位...")
        self.window = _find_window_by_name(DEFAULT_WINDOW_NAME, timeout=self.timeout)
        if not self.window:
            raise RuntimeError("启动后未能找到 SkyView 主窗口（请检查应用是否崩溃，"
                               "崩溃日志位于 %LOCALAPPDATA%\\SkyView\\crash.log）")

        if _is_likely_skyview_window(self.window):
            print(f"[INFO] 已通过标题定位到窗口（特征验证通过）: "
                  f"PID={self.window.ProcessId} Class='{self.window.ClassName}'")
        else:
            self._add_issue(
                f"窗口定位可能不准确：标题匹配到 PID={self.window.ProcessId}，"
                f"未检测到 SkyView 特征子元素",
                "Warning",
            )

    def _connect_by_pid(self) -> None:
        print(f"[INFO] 连接到进程 PID={self.pid}")
        self.window = _find_window_by_pid(self.pid, timeout=self.timeout)
        if not self.window:
            raise RuntimeError(
                f"未找到进程 ID {self.pid} 的 SkyView 窗口。请确认进程正在运行且窗口已创建"
                f"（崩溃日志：%LOCALAPPDATA%\\SkyView\\crash.log）。"
            )
        print(f"[INFO] 已定位到窗口: Name='{self.window.Name}' "
              f"Class='{self.window.ClassName}' PID={self.window.ProcessId}")

    def _connect_by_name(self) -> None:
        print("[INFO] 按标题查找 SkyView 窗口...")
        self.window = _find_window_by_name(DEFAULT_WINDOW_NAME, timeout=self.timeout)
        if not self.window:
            raise RuntimeError("未找到运行中的 SkyView 窗口，请使用 --launch / --pid / --hwnd")
        print(f"[INFO] 已定位到窗口: Name='{self.window.Name}' "
              f"Class='{self.window.ClassName}' PID={self.window.ProcessId}")

    # -- 元素查询 -----------------------------------------------------------
    def _get_rect(self, element) -> Optional[RectInfo]:
        try:
            rect = element.BoundingRectangle
            return RectInfo(rect.left, rect.top, rect.right, rect.bottom)
        except Exception:
            return None

    def _find_first(self, **kwargs) -> Optional[auto.Control]:
        try:
            return self._search_recursive(self.window.GetFirstChildControl(), kwargs)
        except Exception:
            return None

    def _search_recursive(self, ctrl, match: dict, depth: int = 0,
                          max_depth: int = 14):
        if depth > max_depth or not ctrl:
            return None
        try:
            name = ctrl.Name or ""
            automation_id = ctrl.AutomationId or ""
            class_name = ctrl.ClassName or ""
            if (match.get("name") and name == match["name"]) or \
               (match.get("automation_id") and automation_id == match["automation_id"]) or \
               (match.get("class_name") and class_name == match["class_name"]):
                return ctrl
        except Exception:
            pass

        try:
            child = ctrl.GetFirstChildControl()
        except Exception:
            return None
        while child:
            result = self._search_recursive(child, match, depth + 1, max_depth)
            if result:
                return result
            try:
                child = child.GetNextSiblingControl()
            except Exception:
                break
        return None

    def _find_all(self, name: Optional[str] = None, class_name: Optional[str] = None,
                  max_depth: int = 14) -> List[auto.Control]:
        results: List[auto.Control] = []
        try:
            self._collect_recursive(self.window.GetFirstChildControl(), name, class_name,
                                    0, max_depth, results)
        except Exception:
            pass
        return results

    def _collect_recursive(self, ctrl, name: Optional[str], class_name: Optional[str],
                           depth: int, max_depth: int, results: List[auto.Control]) -> None:
        if depth > max_depth or not ctrl:
            return
        try:
            c_name = ctrl.Name or ""
            c_class = ctrl.ClassName or ""
            match = True
            if name and c_name != name:
                match = False
            if class_name and c_class != class_name:
                match = False
            if match:
                results.append(ctrl)
        except Exception:
            pass

        try:
            child = ctrl.GetFirstChildControl()
        except Exception:
            return
        while child:
            self._collect_recursive(child, name, class_name, depth + 1, max_depth, results)
            try:
                child = child.GetNextSiblingControl()
            except Exception:
                break

    def _add_issue(self, description: str, severity: str = "Warning") -> None:
        self.issues.append(LayoutIssue(description, severity))

    # -- 主检查 -------------------------------------------------------------
    def inspect(self) -> Dict[str, Any]:
        self.issues = []

        report: Dict[str, Any] = {
            "app_name": "SkyView",
            "window_title": "",
            "class_name": "",
            "process_id": None,
            "bounding_rectangle": {},
            "is_visible": False,
            "is_enabled": False,
            "checked_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "issues": [],
            "issue_count": 0,
            "error_count": 0,
            "is_valid": False,
        }

        if not self.window:
            self._add_issue("无法连接到 SkyView 主窗口", "Error")
            return self._finish(report)

        try:
            report["window_title"] = self.window.Name or ""
            report["class_name"] = self.window.ClassName or ""
            report["process_id"] = self.window.ProcessId
            report["is_visible"] = self.window.Exists(0, 0)
            report["is_enabled"] = self.window.IsEnabled
            rect = self._get_rect(self.window)
            report["bounding_rectangle"] = rect.to_dict() if rect else {}
        except Exception as exc:
            self._add_issue(f"读取窗口属性失败：{exc}", "Error")

        if not report["window_title"]:
            self._add_issue("主窗口标题为空", "Error")

        bounds = report["bounding_rectangle"]
        if bounds.get("width", 0) < 400:
            self._add_issue(f"主窗口宽度过小：{bounds.get('width', 0):.0f}", "Warning")
        if bounds.get("height", 0) < 300:
            self._add_issue(f"主窗口高度过小：{bounds.get('height', 0):.0f}", "Warning")

        # 1. 导航菜单
        nav_items_expected = ["天气", "城市", "设置"]
        nav_items_found: Dict[str, Any] = {}
        nav_names: List[str] = []

        for c in self._find_all(class_name="NavigationViewItem", max_depth=12):
            try:
                n = c.Name or ""
                if n:
                    nav_names.append(n)
                    rect = self._get_rect(c)
                    nav_items_found[n] = rect.to_dict() if rect else {}
            except Exception:
                pass

        # 有些 WinUI 版本导航项 ClassName 不是 NavigationViewItem，用名称兜底
        if not nav_names:
            for expected in nav_items_expected:
                ctrl = self._find_first(name=expected)
                if ctrl:
                    nav_names.append(expected)
                    rect = self._get_rect(ctrl)
                    nav_items_found[expected] = rect.to_dict() if rect else {}

        report["nav_item_count"] = len(nav_names)
        report["nav_items_found"] = nav_items_found

        for expected in nav_items_expected:
            if expected not in nav_names:
                self._add_issue(f"导航菜单缺少项：{expected}", "Warning")

        # 2. 主页面元素
        city_input = self._find_first(automation_id="CityInput") or self._find_first(name="城市")
        if city_input:
            report["has_city_input"] = True
            rect = self._get_rect(city_input)
            report["city_input_bounds"] = rect.to_dict() if rect else {}
            if not city_input.IsEnabled:
                self._add_issue("城市输入框未启用", "Warning")
        else:
            report["has_city_input"] = False
            self._add_issue("未找到城市输入框 (CityInput)", "Error")

        refresh_btn = self._find_first(automation_id="RefreshButton") or self._find_first(name="刷新天气")
        if refresh_btn:
            report["has_refresh_button"] = True
            rect = self._get_rect(refresh_btn)
            report["refresh_button_bounds"] = rect.to_dict() if rect else {}
            if not refresh_btn.IsEnabled:
                self._add_issue("刷新按钮当前不可用（可能正在加载）", "Warning")
        else:
            report["has_refresh_button"] = False
            self._add_issue("未找到刷新按钮", "Error")

        progress_bars = self._find_all(class_name="ProgressBar", max_depth=12)
        report["has_progress_bar"] = len(progress_bars) > 0
        if progress_bars:
            rect = self._get_rect(progress_bars[0])
            report["progress_bar_bounds"] = rect.to_dict() if rect else {}

        text_blocks = self._find_all(class_name="TextBlock", max_depth=12)
        report["text_block_count"] = len(text_blocks)
        if len(text_blocks) == 0:
            self._add_issue("主页面未找到任何 TextBlock 文本元素", "Warning")

        # 3. 设置页元素（需要先导航过去才有）
        settings_checked = self._inspect_settings_page(report)
        report["settings_page_checked"] = settings_checked

        # 4. 关键控件必须落在窗口可视区域内（超出说明布局溢出/被裁剪）
        for key, label, severity in (
            ("city_input_bounds", "城市输入框", "Error"),
            ("refresh_button_bounds", "刷新按钮", "Error"),
            ("api_key_input_bounds", "设置页 API Key 输入框", "Warning"),
            ("save_button_bounds", "设置页保存按钮", "Warning"),
        ):
            self._check_inside_window(report, key, label, severity)

        return self._finish(report)

    def _check_inside_window(self, report: Dict[str, Any], rect_key: str,
                             label: str, severity: str = "Error") -> None:
        rect = report.get(rect_key)
        win = report.get("bounding_rectangle") or {}
        if not rect or not win.get("width"):
            return

        tolerance = 1.0
        outside = (
            rect["right"] > win["right"] + tolerance
            or rect["bottom"] > win["bottom"] + tolerance
            or rect["left"] < win["left"] - tolerance
            or rect["top"] < win["top"] - tolerance
        )
        if outside:
            self._add_issue(
                f"{label}超出窗口边界：控件 {rect} / 窗口 {win}",
                severity,
            )

    def _navigate(self, nav_name: str, wait: float = 1.2) -> bool:
        """切换左侧导航项。成功切换返回 True。

        优先用 UIA 的 SelectionItem 模式，不依赖鼠标坐标；退而求其次才真点一下。

        注意 `Click(simulateMove=False)` 是个陷阱：它**不移动鼠标**，点击发生在
        当前光标位置。光标在别处时这一下就点空了，而且函数照样返回成功，
        表现为「导航了、但页面没切、控件全找不到」。这个 bug 只在光标位置不合适时出现，
        非常容易误判成界面问题。
        """
        try:
            item = self._find_first(name=nav_name)
            if not item:
                return False

            try:
                pattern = item.GetSelectionItemPattern()
                if pattern:
                    pattern.Select()
                    time.sleep(wait)
                    return True
            except Exception:
                pass

            item.Click(simulateMove=True)  # 会把鼠标先移到控件上
            time.sleep(wait)
            return True
        except Exception:
            return False

    def _wait_for(self, predicate, timeout: float = 8.0, interval: float = 0.25):
        """轮询等到 predicate 返回真值，超时返回 None。

        不要用固定的 time.sleep 等页面加载：界面一变重（比如这次加了几十个矢量图标），
        1.2 秒就不够了，表现为「控件明明在、却报未找到」。轮询能同时抗慢和抗快。
        """
        deadline = time.time() + timeout
        while time.time() < deadline:
            result = predicate()
            if result:
                return result
            time.sleep(interval)
        return None

    def _inspect_settings_page(self, report: Dict[str, Any]) -> bool:
        """导航到「设置」页，检查数据源表单控件，然后返回「天气」页。"""
        if not self._navigate("设置"):
            report["has_api_key_input"] = False
            report["has_save_button"] = False
            report["has_priority_number_box"] = False
            self._add_issue("无法导航到「设置」页，设置页布局未检查", "Warning")
            return False

        try:
            api_key = self._wait_for(
                lambda: self._find_first(automation_id="ApiKeyBox")
                        or self._find_first(name="API Key 输入框"))
            save_btn = self._wait_for(
                lambda: self._find_first(automation_id="SaveSettingsButton")
                        or self._find_first(name="保存设置"))
            # WinUI 3 里 NumberBox 的 UIA ClassName 并不总是 "NumberBox"，
            # 优先用 XAML 里设置的 AutomationId 定位
            priority_box = self._find_first(automation_id="PriorityBox")
            number_boxes = [priority_box] if priority_box else self._find_all(class_name="NumberBox",
                                                                            max_depth=12)

            report["has_api_key_input"] = bool(api_key)
            if api_key:
                rect = self._get_rect(api_key)
                report["api_key_input_bounds"] = rect.to_dict() if rect else {}

            report["has_save_button"] = bool(save_btn)
            if save_btn:
                rect = self._get_rect(save_btn)
                report["save_button_bounds"] = rect.to_dict() if rect else {}

            report["has_priority_number_box"] = len(number_boxes) > 0
            if number_boxes:
                rect = self._get_rect(number_boxes[0])
                report["number_box_bounds"] = rect.to_dict() if rect else {}

            provider_count = len(self._find_all(class_name="ListViewItem", max_depth=12))
            report["provider_item_count"] = provider_count

            if not api_key:
                self._add_issue("设置页未找到 API Key 输入框", "Error")
            if not save_btn:
                self._add_issue("设置页未找到保存按钮", "Error")
            if not number_boxes:
                self._add_issue("设置页未找到优先级 NumberBox", "Warning")
            if provider_count == 0:
                self._add_issue("设置页数据源列表为空", "Warning")
        finally:
            # 回到天气页，避免影响后续操作
            self._navigate("天气", wait=0.8)

        return True

    def _finish(self, report: Dict[str, Any]) -> Dict[str, Any]:
        report["issues"] = [i.to_dict() for i in self.issues]
        report["issue_count"] = len(self.issues)
        report["error_count"] = len([i for i in self.issues if i.severity == "Error"])
        report["is_valid"] = report["error_count"] == 0
        return report


def print_report(report: Dict[str, Any]) -> None:
    """以文本形式打印检查报告"""
    print("=== UI Automation 布局检查报告 (Python uiautomation) ===")
    print(f"应用：{report['app_name']}")
    print(f"窗口标题：{report['window_title']}")
    print(f"类名：{report['class_name']}")
    print(f"进程 ID：{report['process_id']}")
    br = report["bounding_rectangle"]
    print(f"边界：({br.get('left', 0):.0f},{br.get('top', 0):.0f}) "
          f"{br.get('width', 0):.0f}x{br.get('height', 0):.0f}")
    print(f"可见：{report['is_visible']}，启用：{report['is_enabled']}")
    print()

    print("--- 导航视图 ---")
    print(f"导航项数量：{report.get('nav_item_count', 0)}")
    for name, rect in report.get("nav_items_found", {}).items():
        print(f"  导航项 '{name}': {rect}")
    print()

    print("--- 主页面 ---")
    print(f"有城市输入框：{report.get('has_city_input', False)}")
    if report.get("city_input_bounds"):
        print(f"  边界：{report['city_input_bounds']}")
    print(f"有刷新按钮：{report.get('has_refresh_button', False)}")
    if report.get("refresh_button_bounds"):
        print(f"  边界：{report['refresh_button_bounds']}")
    print(f"有进度条：{report.get('has_progress_bar', False)}")
    print(f"TextBlock 数量：{report.get('text_block_count', 0)}")
    print()

    print("--- 设置页面 ---")
    print(f"已导航检查：{report.get('settings_page_checked', False)}")
    print(f"有 API Key 输入框：{report.get('has_api_key_input', False)}")
    print(f"有保存按钮：{report.get('has_save_button', False)}")
    print(f"有优先级 NumberBox：{report.get('has_priority_number_box', False)}")
    print(f"数据源条目数：{report.get('provider_item_count', 0)}")
    print()

    print(f"--- 问题 ({report['issue_count']}) ---")
    if report["issue_count"] == 0:
        print("  无问题")
    else:
        for issue in report["issues"]:
            prefix = {"Error": "[ERROR] ", "Warning": "[WARN]  "}.get(issue["severity"], "")
            print(f"  {prefix}{issue['description']}")
    print("=== 检查完成 ===")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="基于 Python uiautomation 的 SkyView UI 布局检查工具"
    )
    parser.add_argument("--launch", action="store_true", help="启动应用程序后再检查")
    parser.add_argument("--pid", type=int, default=None, help="连接到指定进程 ID")
    parser.add_argument("--hwnd", type=lambda v: int(v, 0), default=None,
                        help="连接到指定窗口句柄（支持 0x 前缀）")
    parser.add_argument("--timeout", type=int, default=10, help="等待窗口超时秒数 (默认 10)")
    parser.add_argument("--json", action="store_true", help="输出 JSON 格式报告")
    parser.add_argument("--app-path", type=str, default=APP_PATH, help="应用程序路径")

    args = parser.parse_args()

    try:
        with UIAutomationLayoutInspector(
            app_path=args.app_path,
            launch=args.launch,
            pid=args.pid,
            hwnd=args.hwnd,
            timeout=args.timeout,
        ) as inspector:
            report = inspector.inspect()

            if args.json:
                print(json.dumps(report, ensure_ascii=False, indent=2))
            else:
                print_report(report)

            return 1 if report["error_count"] > 0 else 0

    except Exception as exc:
        print(f"[FATAL] {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
