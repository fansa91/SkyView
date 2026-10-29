"""SkyView 一键验证：构建 → 发布 → 三层测试 → 两个 UI 检查。

用法:
    python tools/run_all.py                  # 全流程
    python tools/run_all.py --skip-publish    # 跳过发布（只验证已有产物）
    python tools/run_all.py --skip-ui         # 跳过需要图形会话的 UI 检查（CI 无桌面时用）
    python tools/run_all.py --only-ui         # 只跑 UI 检查

UI 检查会自动带 SKYVIEW_DEMO=1 启动应用：
没有 API Key 时真实数据源必然失败，逐小时/趋势图/生活指数这些区块不会渲染，
断言就无从下手。演示数据源提供稳定、可预期的数据，让 UI 测试与网络解耦。

注意 Python 解释器：本脚本用 sys.executable 去跑 tools 下的两个 UI 脚本，
所以**跑本脚本的解释器**必须装好 uiautomation 与 pywinauto。
本机情况是 C:\\python\\python.exe 两个都有；WorkBuddy 自带的解释器可能只有 uiautomation。
启动时会打印解释器路径并做一次导入预检，缺哪个模块会直接说清楚。

退出码：0 = 全部通过；1 = 有步骤失败。
"""

from __future__ import annotations

import argparse
import importlib.util
import os
import re
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOLS = REPO_ROOT / "tools"
SLN = REPO_ROOT / "SkyView.sln"
APP_PROJECT = REPO_ROOT / "src/SkyView/SkyView.csproj"

TEST_PROJECTS = [
    ("单元测试", REPO_ROOT / "tests/SkyView.UnitTests/SkyView.UnitTests.csproj"),
    ("组件测试", REPO_ROOT / "tests/SkyView.ComponentTests/SkyView.ComponentTests.csproj"),
    ("UI 冒烟测试", REPO_ROOT / "tests/SkyView.UITests/SkyView.UITests.csproj"),
]

# 从 dotnet test 输出里抓通过数：中文「通过:    78」/ 英文「Passed:     78」
# 注意别先匹配到「已通过!」和「Passed!」这类小结行——后面紧跟的不是冒号，会自然跳过。
_TEST_PASSED = re.compile(r"(?:通过|Passed)\s*[:：]\s*(\d+)")
# pywinauto: 「--- 问题 (Error 0 / Warning 0 / Info 0) ---」
_UI_ISSUES = re.compile(r"问题 \(Error (\d+) / Warning (\d+)")
# uiautomation: 「--- 问题 (0) ---」
_LAYOUT_ISSUES = re.compile(r"问题 \((\d+)\)")


def _note_tests(output: str) -> str:
    match = _TEST_PASSED.search(output)
    return f"{match.group(1)} 用例通过" if match else ""


def _note_ui_issues(output: str) -> str:
    match = _UI_ISSUES.search(output)
    return f"Error {match.group(1)} / Warning {match.group(2)}" if match else ""


def _note_layout_issues(output: str) -> str:
    match = _LAYOUT_ISSUES.search(output)
    return f"{match.group(1)} 个问题" if match else ""


# 发布目录里必须有的文件。
# - 缺 SkyView.pri 会在 InitializeComponent() 抛 XamlParseException，进程以 0xC000027B 秒退，
#   症状很像「运行包没装」，实际是资源没拷过去。
# - 缺 coreclr.dll 说明 .NET 没自包含，目标机会弹「You must install .NET Desktop Runtime」，
#   同样像「缺运行包」，但这次是 csproj 少了 SelfContained=true。
PUBLISH_REQUIRED = [
    "SkyView.exe",
    "SkyView.pri",
    "SkyView.Core.dll",
    "appsettings.json",
    "coreclr.dll",      # .NET 运行时自包含
    "hostfxr.dll",
]


class Step:
    def __init__(self, name: str) -> None:
        self.name = name
        self.ok = False
        self.skipped = False
        self.seconds = 0.0
        self.detail = ""
        self.note = ""


# ---------------------------------------------------------------- 文本对齐
def _display_width(text: str) -> int:
    """中文/全角算 2 列，否则 len() 会让汇总表错位。"""
    width = 0
    for ch in text:
        width += 2 if ("\u1100" <= ch <= "\u115f"
                       or "\u2e80" <= ch <= "\ua4cf"
                       or "\uac00" <= ch <= "\ud7a3"
                       or "\uf900" <= ch <= "\ufaff"
                       or "\ufe30" <= ch <= "\ufe4f"
                       or "\uff00" <= ch <= "\uff60"
                       or "\uffe0" <= ch <= "\uffe6") else 1
    return width


def _pad(text: str, width: int) -> str:
    return text + " " * max(0, width - _display_width(text))


# ---------------------------------------------------------------- 环境
def _env() -> dict[str, str]:
    env = dict(os.environ)
    env.setdefault("DOTNET_ROOT", r"C:\dotnet")
    dotnet_dir = env["DOTNET_ROOT"]
    if dotnet_dir not in env.get("PATH", ""):
        env["PATH"] = dotnet_dir + os.pathsep + env.get("PATH", "")

    env["PYTHONUTF8"] = "1"
    env["PYTHONIOENCODING"] = "utf-8"
    # UI 检查统一走演示数据源
    env["SKYVIEW_DEMO"] = "1"
    return env


def _preflight_ui() -> list[str]:
    """返回缺失的 Python 模块名列表。"""
    missing = []
    for module in ("uiautomation", "pywinauto"):
        if importlib.util.find_spec(module) is None:
            missing.append(module)
    return missing


# ---------------------------------------------------------------- 执行
def _run(name: str, cmd: list[str], cwd: Path, env: dict[str, str],
         note_from_output=None) -> Step:
    step = Step(name)
    started = time.time()

    print(f"\n{'=' * 70}")
    print(f"▶ {name}")
    print(f"  {' '.join(cmd)}")
    print("=" * 70, flush=True)

    try:
        proc = subprocess.run(
            cmd, cwd=str(cwd), env=env,
            capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
    except FileNotFoundError as exc:
        step.seconds = time.time() - started
        step.detail = f"命令不存在: {exc}"
        print(f"✗ {step.detail}")
        return step

    step.seconds = time.time() - started
    output = (proc.stdout or "") + (proc.stderr or "")
    print(output.rstrip())

    step.ok = proc.returncode == 0
    if not step.ok:
        step.detail = f"退出码 {proc.returncode}"
    elif note_from_output is not None:
        step.note = note_from_output(output) or ""

    print(f"{'✓' if step.ok else '✗'} {name} 完成，用时 {step.seconds:.1f}s"
          f"{f'（{step.note}）' if step.note else ''}")
    return step


def _find_publish_dir(configuration: str) -> Path | None:
    """发布目录形如 bin/x64/Release/<tfm>/win-x64/publish，平台与 TFM 不写死。"""
    base = REPO_ROOT / "src/SkyView"
    candidates = [p for p in base.glob(f"bin/*/{configuration}/*/win-x64/publish") if p.is_dir()]
    if not candidates:
        return None
    return max(candidates, key=lambda p: p.stat().st_mtime)


def _check_publish_artifacts(configuration: str) -> Step:
    """只读检查发布目录：关键文件齐了，启动才不会一上来就崩。"""
    step = Step("发布产物完整性")
    publish_dir = _find_publish_dir(configuration)

    if publish_dir is None:
        step.skipped = True
        step.detail = f"未找到 {configuration} 的 publish 目录（先跑一次发布）"
        print(f"⊘ 跳过 {step.name}：{step.detail}")
        return step

    print(f"\n{'=' * 70}")
    print(f"▶ {step.name}")
    print(f"  目录 {publish_dir}")
    print("=" * 70, flush=True)

    missing = [name for name in PUBLISH_REQUIRED if not (publish_dir / name).is_file()]
    total_files = sum(1 for _ in publish_dir.rglob("*") if _.is_file())

    if missing:
        step.detail = f"缺少 {', '.join(missing)}"
        print(f"✗ 缺少: {', '.join(missing)}")
        print("  SkyView.pri 由 csproj 的 CopyPriToPublish 目标补拷；若仍缺失，"
              "检查该目标是否生效（dotnet publish 不会自己复制 PRI）。")
    else:
        step.ok = True
        step.note = f"{total_files} 文件，关键文件齐全"
        print(f"✓ 关键文件齐全（{total_files} 个文件）")
        for name in PUBLISH_REQUIRED:
            print(f"    · {name}")

    return step


def main() -> int:
    parser = argparse.ArgumentParser(description="SkyView 一键验证")
    parser.add_argument("--skip-publish", action="store_true", help="跳过 dotnet publish")
    parser.add_argument("--skip-ui", action="store_true", help="跳过需要图形会话的 UI 检查")
    parser.add_argument("--only-ui", action="store_true", help="只跑 UI 检查")
    parser.add_argument("--configuration", default="Release", help="构建配置，默认 Release")
    args = parser.parse_args()

    env = _env()
    steps: list[Step] = []
    overall_start = time.time()

    print("=" * 70)
    print("SkyView 一键验证")
    print("=" * 70)
    print(f"仓库     : {REPO_ROOT}")
    print(f"配置     : {args.configuration}")
    print(f"Python   : {sys.executable}")
    print(f"DOTNET_ROOT: {env['DOTNET_ROOT']}")

    # ---------- 构建 / 测试 ----------
    if not args.only_ui:
        steps.append(_run(
            "构建解决方案",
            ["dotnet", "build", str(SLN), "-c", args.configuration],
            REPO_ROOT, env,
        ))
        if not steps[-1].ok:
            _print_summary(steps, overall_start)
            return 1

        if not args.skip_publish:
            steps.append(_run(
                "发布自包含版本",
                ["dotnet", "publish", str(APP_PROJECT), "-c", args.configuration],
                REPO_ROOT, env,
            ))

        # 带上 -c 与解决方案构建保持一致：产物已存在时这一步是增量的，几乎不耗时；
        # 不加的话 dotnet test 会另建一套 Debug 产物，白等一轮编译。
        for label, project in TEST_PROJECTS:
            steps.append(_run(
                label,
                ["dotnet", "test", str(project), "-c", args.configuration],
                REPO_ROOT, env,
                note_from_output=_note_tests,
            ))

        steps.append(_check_publish_artifacts(args.configuration))

    # ---------- UI 检查 ----------
    if args.skip_ui:
        print("\n（已按 --skip-ui 跳过 UI 检查）")
    else:
        missing = _preflight_ui()
        if missing:
            hint = (
                f"当前 Python 缺少模块: {', '.join(missing)}。"
                "请换一个装好依赖的解释器运行本脚本（本机 C:\\python\\python.exe 已具备）。"
            )
            print(f"\n⚠ {hint}")
            steps.append(_skipped("UI 布局检查 (uiautomation)", hint))
            steps.append(_skipped("UI 功能检查 (pywinauto)", hint))
        else:
            steps.append(_run(
                "UI 布局检查 (uiautomation)",
                [sys.executable, str(TOOLS / "check_ui_layout.py"), "--launch", "--timeout", "20"],
                REPO_ROOT, env,
                note_from_output=_note_layout_issues,
            ))
            steps.append(_run(
                "UI 功能检查 (pywinauto)",
                [
                    sys.executable,
                    str(TOOLS / "check_ui_pywinauto.py"),
                    "--launch",
                    "--expect-data",
                    "--timeout", "20",
                ],
                REPO_ROOT, env,
                note_from_output=_note_ui_issues,
            ))

    _print_summary(steps, overall_start)

    if any(s.skipped for s in steps) and all(s.ok or s.skipped for s in steps):
        print("\n提示：有步骤被跳过，结果不代表全量通过。")
        return 1
    return 0 if all(s.ok for s in steps) else 1


def _skipped(name: str, reason: str) -> Step:
    step = Step(name)
    step.skipped = True
    step.detail = reason
    print(f"⊘ 跳过 {name}")
    return step


def _print_summary(steps: list[Step], overall_start: float) -> None:
    name_width, result_width, time_width = 30, 8, 10
    print(f"\n{'=' * 70}")
    print("验证汇总")
    print("=" * 70)
    print(f"{_pad('步骤', name_width)}{_pad('结果', result_width)}"
          f"{' ' * (time_width - _display_width('用时'))}用时")
    print("-" * 70)
    for step in steps:
        if step.skipped:
            flag = "⊘ 跳过"
        else:
            flag = "✓ 通过" if step.ok else "✗ 失败"
        spent = "—" if step.skipped else f"{step.seconds:.1f}s"
        print(f"{_pad(step.name, name_width)}{_pad(flag, result_width)}"
              f"{spent.rjust(time_width)}  {step.note}")
    print("-" * 70)

    failed = [s for s in steps if not s.ok and not s.skipped]
    skipped = [s for s in steps if s.skipped]
    total = time.time() - overall_start

    print(f"\n共 {len(steps)} 步：通过 {len(steps) - len(failed) - len(skipped)}，"
          f"失败 {len(failed)}，跳过 {len(skipped)}，总用时 {total:.1f}s")

    if failed:
        for step in failed:
            print(f"  ✗ {step.name}: {step.detail or '见上方输出'}")
    if skipped:
        for step in skipped:
            print(f"  ⊘ {step.name}: {step.detail}")

    if not failed and not skipped:
        print("全部通过。")


if __name__ == "__main__":
    sys.exit(main())
