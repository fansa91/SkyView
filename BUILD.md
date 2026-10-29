# SkyView 构建与运行说明

## 一、环境要求

| 组件 | 版本 | 说明 |
|------|------|------|
| 操作系统 | Windows 10 1809+ / Windows 11 | WinUI 3 目标平台 |
| .NET SDK | 10.0.402 | 已验证 |
| Windows SDK 引用包 | `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.57 | 通过 `<WindowsSdkPackageVersion>` 指定 |
| Windows App SDK | 1.8.260921001 | NuGet 自动还原，**自包含部署**，目标机无需预装运行时 |
| Python | 3.9+ | 仅 UI 布局检查脚本需要 |

## 二、解决方案结构

```
SkyView.sln
├── src/SkyView.Core/      # 非 UI 类库 (net10.0)：模型 / 数据源 / 服务 / ViewModel / 存储
├── src/SkyView/           # WinUI 3 应用 (net10.0-windows10.0.19041.0)
├── tests/
│   ├── SkyView.UnitTests/      # xUnit + Moq
│   ├── SkyView.ComponentTests/ # xUnit + Moq
│   └── SkyView.UITests/        # xUnit 冒烟测试
└── tools/
    ├── run_all.py              # 一键验证：构建 → 发布 → 测试 → UI 检查
    ├── check_ui_layout.py      # UI 布局只读检查（uiautomation）
    ├── check_ui_pywinauto.py   # UI 交互功能测试（pywinauto）
    └── capture_tiers.py        # 逐页截图取证（写 docs/shots/）
```

**为什么拆出 `SkyView.Core`**：业务逻辑（含 ViewModel）不引用 WinUI / Windows App SDK，
因此单元测试和组件测试可以在**没有图形会话、没有 WinAppSDK 运行时**的环境里正常跑完，
不会因为加载 `Microsoft.UI.Xaml.dll` 而让测试宿主直接崩溃。

## 三、关键构建配置

### 主项目 src/SkyView/SkyView.csproj

```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
<UseWinUI>true</UseWinUI>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- 自带 WinAppSDK 运行时 -->
<WindowsPackageType>None</WindowsPackageType>                  <!-- 未打包，走自动引导 -->
<SelfContained>true</SelfContained>                            <!-- 自带 .NET 运行时 -->
<WindowsSdkPackageVersion>10.0.26100.57</WindowsSdkPackageVersion>
<Platform>x64</Platform>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

- `WindowsPackageType=None`（未打包）→ `Windows.Storage.ApplicationData.Current` **不可用**，
  本地数据统一放 `%LOCALAPPDATA%\SkyView\`（见 `SkyView.Core/Services/AppPaths.cs`）。
- `WindowsAppSDKSelfContained=true` → publish 输出包含 WinAppSDK 运行时（约 10 个 `WindowsAppRuntime*` 文件），
  目标机**不需要**安装 Windows App SDK 运行时。
- `SelfContained=true` → publish 输出包含 .NET 运行时（`coreclr.dll` / `hostfxr.dll` /
  `System.Private.CoreLib.dll` 等）。**必须显式写**：从 .NET 6 起 `dotnet publish -r <rid>`
  不再隐含自包含，默认仍是 framework-dependent，目标机没装 .NET Desktop Runtime 就弹
  「You must install .NET Desktop Runtime」。两个 `SelfContained` 是两件事，别搞混：
  一个管 WinAppSDK，一个管 .NET 本体。
- `Platform=x64` + `RuntimeIdentifier=win-x64` 是自包含部署的必需项，缺了会报 `NETSDK1083`。

代价是体积：全自包含 publish 约 **219 MB / 443 个文件**（其中 .NET 运行时占大头）。
只想内部开发机跑可以临时关掉 `SelfContained`，分发版务必打开。

### 测试项目

测试项目**不需要** `UseWinUI`，只引用 `SkyView.Core`：

```xml
<TargetFramework>net10.0</TargetFramework>
<ProjectReference Include="..\..\src\SkyView.Core\SkyView.Core.csproj" />
```

## 四、构建 / 发布命令

> 日常验证直接用 **[五、一键验证脚本](#五一键验证脚本推荐)** 更省事，下面这些是它内部调用的原始命令。

```bash
# 构建主应用（产物：src/SkyView/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/）
dotnet build src/SkyView/SkyView.csproj -c Debug

# 发布（Release，产物在 .../win-x64/publish/，可直接分发运行）
dotnet publish src/SkyView/SkyView.csproj -c Release
```

> ⚠️ **不要用 `dotnet publish -o <自定义目录>`**。
> 使用自定义输出目录时，`dotnet publish` 不会复制应用自身的 `SkyView.pri`（XAML 资源索引），
> 运行时会立刻在 `InitializeComponent()` 抛 `XamlParseException`（进程退出码 `0xC000027B`）。
> 项目已在 csproj 里加了 `CopyPriToPublish` 目标做兜底，但**默认发布路径最稳**。

## 五、一键验证脚本（推荐）

`tools/run_all.py` 把「构建 → 发布 → 三层测试 → 产物完整性 → 两个 UI 检查」串成一条命令，
最后输出汇总表。日常改完代码跑这一个就够了。

```bash
# 全流程（约 1.5~2 分钟）
python tools/run_all.py

# 只重跑测试和产物检查，不重新发布
python tools/run_all.py --skip-publish

# 没有图形会话（远程/CI）时跳过 UI 检查
python tools/run_all.py --skip-ui

# 只跑两个 UI 检查
python tools/run_all.py --only-ui

# 换配置
python tools/run_all.py --configuration Debug
```

实测输出：

```
步骤                          结果          用时
----------------------------------------------------------------------
构建解决方案                  ✓ 通过       36.2s
发布自包含版本                ✓ 通过       26.3s
单元测试                      ✓ 通过        6.3s  78 用例通过
组件测试                      ✓ 通过        6.2s  12 用例通过
UI 冒烟测试                   ✓ 通过        5.7s  4 用例通过
发布产物完整性                ✓ 通过        0.0s  343 文件，关键文件齐全
UI 布局检查 (uiautomation)    ✓ 通过        9.7s  0 个问题
UI 功能检查 (pywinauto)       ✓ 通过       15.3s  Error 0 / Warning 0
----------------------------------------------------------------------

共 8 步：通过 8，失败 0，跳过 0，总用时 105.8s
全部通过。
```

几个设计点：

- **UI 检查自动带 `SKYVIEW_DEMO=1`**。没有 API Key 时真实数据源必然失败，
  逐小时/趋势图/生活指数不会渲染，断言无从下手；演示数据源提供稳定数据，让 UI 测试与网络解耦。
- **`发布产物完整性` 这一步专治 `SkyView.pri` 缺失**。它只读地检查
  `SkyView.exe` / `SkyView.pri` / `SkyView.Core.dll` / `appsettings.json` 是否都在，
  0 秒完成。这个坑的症状是「像缺运行包」，靠肉眼看目录很容易漏。
- **Python 解释器有要求**。脚本用 `sys.executable` 去跑 tools 下两个 UI 脚本，
  所以**跑本脚本的解释器**必须装好 `uiautomation` 与 `pywinauto`。
  本机 `C:\python\python.exe` 两个都有，建议用它：
  ```bash
  C:/python/python.exe tools/run_all.py
  ```
  预检发现缺模块时不会硬失败，而是把 UI 两步标记为「跳过」并列出缺什么，
  同时退出码为 1（有步骤被跳过 ≠ 全量通过）。
- 退出码 `0` 仅代表全部步骤真的通过；有任何失败或跳过都返回 `1`，可直接接 CI。

## 六、四个曾导致「界面启动不了」的真实原因（已修复）

按发现顺序记录，避免以后重复踩坑。

### 1. XAML 里使用了不存在的 `Symbol` 枚举值

`MainWindow.xaml` 的 `NavigationViewItem Icon="Place"` —— `Place` **不在 WinUI 3 的 `Symbol` 枚举里**。
XAML 解析器在 `MainWindow.InitializeComponent()` 直接抛异常，而 `OnLaunched` 里没有捕获，
于是变成未处理异常 → `0xC000027B`，进程秒退、窗口从未出现。

已改用合法图标（`Home` / `Map` / `Setting`）。**这是"启动不了"的真正根因。**

### 2. 发布目录缺少 `SkyView.pri`

`dotnet publish` 不复制 `<AssemblyName>.pri`（XAML 的 XBF 资源都在里面），
缺了它启动时报同样的 `XamlParseException`，但 stack 落在 `MainWindow..ctor()`。
已在 csproj 增加 `CopyPriToPublish` 目标，在 Publish 之后补复制。

### 3. `IMemoryCache` 未注册

`WeatherService` 依赖 `IMemoryCache`，但 DI 里没注册，`MainPage` 构造时抛异常。
已在 `App.xaml.cs` 补 `services.AddMemoryCache()`。

### 4. .NET 运行时没有自包含（就是那句「需要安装运行包」）

症状是一个 **556×222 的错误对话框**，标题是 `SkyView.exe`，正文：

> You must install .NET Desktop Runtime to run this application.

这个框极具误导性：它没有崩溃、没有 `crash.log`、进程还活着，只是窗口不是主界面。
排查时如果用 `EnumWindows` 只看「有没有可见窗口」，会误判成「应用起来了」；
如果用 pywinauto 等 `title="SkyView"`，则会一直超时，因为框的标题是 `SkyView.exe`。

根因：只设了 `WindowsAppSDKSelfContained=true`，漏了 `SelfContained=true`。
.NET 6 起 `dotnet publish -r win-x64` **不再**隐含自包含，publish 目录里 141 MB 全是
WinAppSDK 和依赖包，`coreclr.dll` / `hostfxr.dll` 一个都没有 → apphost 去找系统装的
.NET Desktop Runtime，找不到就弹这个框。

修法：csproj 加 `<SelfContained>true</SelfContained>`。之后 publish 是 219 MB / 443 个文件，
把 `DOTNET_ROOT` 删掉、`PATH` 里也不带 dotnet，仍然 2 秒内出主窗口。

**判别方法**：`publish` 目录里有没有 `coreclr.dll`。有 = 自包含；没有 = 依赖系统运行时。

### 排查手段：崩溃日志

`App` 构造函数里挂了 `UnhandledException`，崩溃栈写到：

```
%LOCALAPPDATA%\SkyView\crash.log
```

以后再遇到"双击没反应"，**先看这个文件**，比翻事件日志快得多。
注意：第 4 类问题（缺运行时）**不会**产生 crash.log，因为它压根没进托管代码。

## 七、测试结果（已验证）

| 测试项目 | 通过 | 失败 | 总计 |
|----------|------|------|------|
| SkyView.UnitTests | 122 | 0 | 122 |
| SkyView.ComponentTests | 12 | 0 | 12 |
| SkyView.UITests | 4 | 0 | 4 |
| **合计** | **138** | **0** | **138** |

```bash
dotnet test tests/SkyView.UnitTests/SkyView.UnitTests.csproj
dotnet test tests/SkyView.ComponentTests/SkyView.ComponentTests.csproj
dotnet test tests/SkyView.UITests/SkyView.UITests.csproj
```

（或直接 `python tools/run_all.py`，会连构建和 UI 检查一起跑。）

覆盖内容：
- 天气状况文本映射（中/英、模糊匹配、未知降级）
- `WeatherFormat` 单位与数字格式化、温度换算、AQI 分级、缓存时长文案
- `WeatherViewModel` 刷新成功/失败/加载态/命令可用性
- 空状态：首次进入不显示 `0°C`、成功后退场、失败时不误报空、刷新失败保留旧数据
- 离线缓存：缓存写入/读回、损坏文件回退、缓存恢复时打标记、刷新成功后清除标记
- 单位切换：`ApplyUnit` 切 °F 后温度与趋势区间同步换算
- `PreferencesStore` 往返/损坏回退/自动刷新间隔归一化
- `ProviderConfigItem` 的 Key 掩码、留空不改、清空语义
- `WeatherService` 多源按优先级降级、全部失败时的异常、缓存命中
- 各数据源的逐小时/空气质量/生活指数**经接口调用**的映射正确性
  （刻意通过 `IWeatherProvider` 而不是具体类型调用——接口默认实现曾导致派生类方法永不执行）
- 天气图标分类：中文/英文、强度词、优先级（雷 > 雪 > 暴雨 > 雨）、降水概率升级、兜底
- 发布产物完整性（`SkyView.exe` / `SkyView.pri` / `SkyView.Core.dll` / `appsettings.json`）

## 八、数据与配置位置

| 内容 | 路径 | 说明 |
|------|------|------|
| 加密的 API Key / 数据源设置 | `%LOCALAPPDATA%\SkyView\settings.dat` | **DPAPI 加密**（`ProtectedData`，CurrentUser 范围） |
| 城市列表 | `%LOCALAPPDATA%\SkyView\cities.json` | 纯文本，非敏感 |
| 崩溃日志 | `%LOCALAPPDATA%\SkyView\crash.log` | 未处理异常栈 |
| 数据源默认定义 | 应用目录 `appsettings.json` | 首次运行的默认清单 |

> 换机器或重装系统后 `settings.dat` 解不开（DPAPI 与用户账户绑定），属正常现象，重新填 Key 即可。

## 九、UI 布局检查（Python uiautomation）

`tools/check_ui_layout.py` 基于 PyPI 的 **uiautomation**（UIA3）做布局校验。

### 安装

```bash
pip install uiautomation
```

### 用法

```bash
# 推荐：连到已运行的进程（最准确）
python tools/check_ui_layout.py --pid 12345

# 启动并检查
python tools/check_ui_layout.py --launch

# JSON 报告
python tools/check_ui_layout.py --pid 12345 --json > layout-report.json
```

### 窗口定位策略

1. `--pid`：遍历顶级窗口匹配 `ProcessId`，带重试（应对窗口尚未创建）
2. `--launch`：先按启动进程 PID 定位，失败回退到 `Name="SkyView"` 并用子元素特征（城市输入框、刷新按钮）校验
3. uiautomation 的根节点枚举失效时，自动回退到 Win32 `EnumWindows`

### 实测输出（Release publish 版，PID 3956）

```
=== UI Automation 布局检查报告 (Python uiautomation) ===
应用：SkyView
窗口标题：SkyView
类名：WinUIDesktopWin32WindowClass
进程 ID：3956
边界：(234,234) 1000x720
可见：True，启用：True

--- 导航视图 ---
导航项数量：3
  导航项 '天气': {'left': 246, 'top': 311, 'width': 312, 'height': 36}
  导航项 '城市': {...}
  导航项 '设置': {...}

--- 主页面 ---
有城市输入框：True
有刷新按钮：True
有进度条：False          <- 非加载态时进度条隐藏，属正常
TextBlock 数量：24

--- 设置页面 ---
已导航检查：True
有 API Key 输入框：True
有保存按钮：True
有优先级 NumberBox：True
数据源条目数：3

--- 问题 (0) ---
  无问题
=== 检查完成 ===
```

退出码：有 `Error` 级问题时为 `1`，否则为 `0`。已接入 CI 判定。

### 说明

- 脚本会自动点击导航项进入「设置」页，校验 API Key 输入框 / 保存按钮 / 优先级 NumberBox / 数据源条目数。
- 进度条在非加载态为 `Collapsed`，报告为 `False` 不计入问题。

## 十、UI 功能测试（pywinauto）

`tools/check_ui_pywinauto.py` 用 **pywinauto（uia 后端）** 做**真实交互**测试 ——
和第九节的只读布局测量互补：这个会真的输入文字、点按钮、切页面。

### 结论：pywinauto 完全可用于 WinUI 3

WinUI 3 用标准 UIA（Microsoft UI Automation）暴露控件，pywinauto 的 `uia` 后端能完整驱动。
实测可用的操作：

| 操作 | 方法 | 实测结果 |
|------|------|---------|
| 连接进程 | `Application(backend="uia").connect(process=pid)` | OK |
| 取窗口 | `app.window(title="SkyView")` | OK（`WindowSpecification`） |
| 按 AutomationId 查找 | `spec.child_window(auto_id="CityInput")` | OK |
| 读输入框内容 | `get_value()` / `window_text()` | OK（读出 `北京`） |
| 写入输入框 | `set_edit_text("上海")` | OK（值真的变了） |
| 点击按钮 | `invoke()` | OK（刷新真的触发） |
| 切换导航页 | `select()` | OK（切到设置页成功） |
| 判断可用性 | `is_enabled()` | OK |
| 遍历全部控件 | `wrapper.descendants()` + `automation_id()` | OK（拿到 16 个 Id） |

### 安装

```bash
pip install pywinauto
```

### 用法

```bash
# 自动启动 + 完整功能流程（布局 + 改城市 + 点刷新 + 进设置页）
python tools/check_ui_pywinauto.py --launch

# 连到已运行的进程
python tools/check_ui_pywinauto.py --pid 12345

# 只要布局，不触发交互
python tools/check_ui_pywinauto.py --launch --skip-functional

# JSON 报告
python tools/check_ui_pywinauto.py --launch --json > report.json
```

### 它会做什么

1. 连接窗口，校验标题 / 类名 / 边界 / 可见 / 启用
2. 校验 3 个导航项（`HomeNavItem` / `CityNavItem` / `SettingsNavItem`）
3. 校验主页面 `CityInput`、`RefreshButton`
4. **功能流程**：把城市改成 `--probe-city`（默认 `上海`）→ 点刷新 → 轮询等待结果稳定 →
   读取 `ErrorText` / `TemperatureText` / `EmptyHintText` → **还原原城市**
5. 导航到设置页，校验 `ApiKeyBox` / `SaveSettingsButton` / `PriorityBox` / `ProviderList` 及条目数（应为 3）
6. 回到天气页，退出

判定规则：温度 / 错误提示 / 空状态引导三者至少有其一，否则报"界面无响应"。
未配置 API Key 时返回的业务错误记为 `Info` 而非 `Error`（属预期）。

### pywinauto 在 WinUI 3 上的 4 个坑（脚本已绕过）

1. `app.windows()[0]` 返回的是 **`UIAWrapper`**，**没有 `child_window()`** 方法 ——
   必须用 `app.window(title="SkyView")` 拿到 `WindowSpecification` 才能按 auto_id 查找。
   （最初按 `windows()[0].child_window(...)` 写，全部返回 MISS，就是这个原因。）
2. `app.window(process=pid)` 会抛 `KeyError`：`connect(process=...)` 已绑定进程，不能再传 `process`。
3. **AutomationId 必须显式写在 XAML 里**才有。`ListView.ItemTemplate` 内部的控件
   （如每行的启用 CheckBox）默认没有 Id，只能靠 `descendants()` 按控件类型统计。
4. 深层控件用 `child_window` 有时不稳，改用 `descendants()` + `automation_id()` 过滤更可靠。

### 三个工具怎么选

| | `check_ui_layout.py` (uiautomation) | `check_ui_pywinauto.py` (pywinauto) | `capture_tiers.py` |
|---|---|---|---|
| 定位 | 只读布局测量 | 交互式功能测试 | 逐页截图取证 |
| 输出 | 控件边界坐标、控件计数 | 交互结果、状态文本 | `docs/shots/*.png` |
| 改界面 | 不改 | 会改（并自动还原） | 会改（切单位、滚页面） |
| 适合 | CI 里的布局回归、视觉走查 | 验证"点了到底有没有反应" | 人工看效果、写文档配图 |
| 依赖 | `pip install uiautomation` | `pip install pywinauto` | 两个都要 + `pillow` |

> 三个脚本都会自动优先使用 **Release publish** 的 exe，找不到才退回 Debug 构建。
>
> 想一次把前两个都跑掉：`python tools/run_all.py --only-ui`（会自动带 `SKYVIEW_DEMO=1`）。

`capture_tiers.py` 会依次截：天气页顶屏 → 温度趋势 → 预报/生活指数 → 设置页 →
设置保存后 → 切换单位后，并顺手校验「切单位后温度文本必须变化」，不变化就返回 1。

滚动走 UIA 的 `ScrollPattern`（`Scroll(NoAmount, SmallIncrement)` 小步推进，
直到目标标题完整进入可视区）。**不要用注入滚轮**：

- 滚轮要求窗口在前台、鼠标真的落在窗口上，后台跑会静默无效，很难 debug
- 逐小时那条横向滚动区会把纵向滚轮吃掉，鼠标落在那儿就滚不动页面

页面 ScrollViewer 是**有** ScrollPattern 的（`class_name == "ScrollViewer"`
且 `CurrentVerticallyScrollable == True`），按 class_name 过滤就能和逐小时那条
横向滚动器区分开。注意 `CurrentCanScroll` 在这个场景恒为 False，别拿它判断。

还有个前置条件：脚本启动时必须调 `SetProcessDpiAwareness(2)`。
否则进程是 DPI-unaware 的，`GetWindowRect` 给的是逻辑像素，而 `SetCursorPos`
按物理像素工作，125% 缩放下两者差 25% —— **鼠标会落到别的窗口上，点击全打在空处**。
必须在任何窗口查询之前调用，模块导入时就要设好。

### 截图为什么用 PrintWindow 而不是 ImageGrab

`ImageGrab.grab(bbox=窗口矩形)` 是"按坐标去屏幕上抠一块"，窗口只要不在最前、
或者周围有一像素误差，就会把桌面、任务栏、别的窗口边角一起截进来。
改成让窗口自己往内存 DC 上画：

```python
user32.PrintWindow(hwnd, mdc, PW_RENDERFULLCONTENT)   # flag = 0x2
```

- `PW_RENDERFULLCONTENT` 不能省。WinAppSDK 走 DirectComposition 合成，
  不带这个 flag 走的是老的 `WM_PRINT` 路径，截出来是纯黑或纯白。
- 拿到 `GetDIBits` 的 BGRA 缓冲后，PIL 用 `Image.frombuffer(..., "raw", "BGRX", ...)`
  直接读成 RGB，顺手跳过 alpha。
- `biHeight` 传负数 = top-down，省一次垂直翻转。
- **所有返回句柄的 GDI 函数都要显式写 `restype`**。ctypes 默认按 `c_int` 处理，
  64 位下 `CreateCompatibleDC` 的句柄会被截断成垃圾值，报错还很难懂。
- 仍然保留了 `ImageGrab` 兜底：`PrintWindow` 返回 0 时打一行提示再退回屏幕抓取。

**它的效果**：换掉之后截图边缘不再有桌面碎片，6 张全部 1000×720、内容完整，
`docs/shots/01-天气页.png` 里左侧导航三项、当前天气卡片、逐小时图标行都干净。

**它已经抓到过东西**：逐小时列表的第 8 项被切在卡片右边界上
（`15:00` 只剩 12px 宽，`16:00` 之后因为裁剪完全没渲染）。功能上可以横向滚动，
但视觉上像排版破了。默认窗口宽度 1000px 时，卡片内宽 581px、单项 78px，只能放下 7.4 项。
这是**已知待定项**，三个候选方案见下：

1. 收窄单项 74 → 66px，默认宽度下能放下 8 项，代价是窄窗口更容易挤
2. 右侧加渐隐遮罩暗示"还有更多"，但卡片背景色跟主题走，
   渐隐需要写死颜色，深色主题下会露馅
3. 在 `HourlyCard.SizeChanged` 里按可用宽度算固定列数（`n = (宽 + 间距) // 单项宽`），
   再把单项宽度均分。宽度无关、永远切在项与项之间，代价是多约 10 行后置代码

## 十一、多数据源使用说明

1. 启动应用 → 左侧导航「设置」
2. 为要用的数据源填 API Key（和风天气 / OpenWeatherMap / 心知天气）
3. 勾选「启用」，设优先级（数字越小越优先）
4. 点「保存设置」→ Key 经 DPAPI 加密写入 `settings.dat`，数据源工厂立即重载
5. 回「天气」页刷新（或重启应用自动加载），失败会按优先级自动降级到下一个源

### 各数据源备注

| 数据源 | 说明 |
|--------|------|
| 和风天气 QWeather | 先用 GeoAPI 把城市名解析成 LocationID，再查实时/预报；需要账号专属 API Host |
| OpenWeatherMap | 实时用 `/weather`，预报用 `/forecast`（3 小时粒度，按天聚合取最高/最低） |
| 心知天气 Seniverse | 实时 `/weather/now.json`，预报 `/weather/daily.json`，参数以字符串返回需容错解析 |

## 十二、天气图标库与动画库

### 分层

| 位置 | 职责 |
|------|------|
| `SkyView.Core/Icons/WeatherIconKind.cs` | 12 种图标的语义枚举（晴/多云/阴/小雨/雨/大雨/雷阵雨/雪/雾霾/沙尘/大风/未知） |
| `SkyView.Core/Icons/WeatherIconSelector.cs` | 天气文字 → 枚举，有序规则表，可单元测试 |
| `SkyView/Controls/WeatherIcon.xaml(.cs)` | 矢量绘制 + 环境动效，`Kind` / `IsAnimated` 依赖属性 |
| `SkyView/Motion/MotionLibrary.cs` | 入场淡入上浮、图标弹出，尊重系统「减少动画」 |

分类逻辑放在 Core 而不是控件里，是为了能脱离 UI 测试：`WeatherIconSelectorTests`
有 44 个用例覆盖中文/英文/强度词/优先级/兜底。

### 为什么不用 Path 字符串画图标

`Path.Data` 是**运行时**解析的，写错一个数字编译期发现不了，加载时抛异常 →
`InitializeComponent()` 失败 → 进程秒退 `0xC000027B`。这个坑本项目踩过两次。

所以图标全部用 `Ellipse` / `Rectangle` / `Line` / `Polygon` 拼，坐标是纯数字，
零解析风险，而且每个图元都能单独挂动画。

### 图层组合

一套 24×24 画布，图层顺序即 z 序：
太阳 → 背景云 → 主云 → 降水（5 个雨滴 + 3 片雪花）→ 闪电 → 雾线 → 风线。

| 图标 | 使用的图层 |
|------|------------|
| 晴 | 太阳（光芒缓慢旋转） |
| 多云 | 太阳缩到右上 + 云缩到左下 |
| 阴 | 背景云缩 0.82 右上错开 + 主云 |
| 小雨 / 雨 | 主云 + 2 / 3 颗雨滴（对称取位） |
| 大雨 | 主云刷成板岩灰 + 5 颗雨滴 |
| 雷阵雨 | 同上 + 闪电（明暗闪烁） |
| 雪 | 主云 + 3 片雪花（慢速飘落 + 横向摆动） |
| 雾霾 | 4 条灰雾线（各自左右浮动） |
| 沙尘 | 同上，雾线刷成土黄 |
| 大风 | 3 条风线 + 右侧小卷曲 |
| 未知 | 一朵中性云 |

几个设计上的坑，都是截图看出来的：

- **云不能只用几片等大椭圆**，看起来像个荷包蛋。要「左小鼓包 + 右大鼓包 + 圆角平底」，
  三个图元底边对齐在同一条线上，拼出来的剪影才像云。
- **大雨的云不能用纯蓝**（试过 `SteelBlue`），会读成一块蓝色色块。用板岩灰 `#546E7A`，
  再靠雨滴数量和颜色表达强度。
- **雪花不能用很浅的淡蓝**（`#B3E5FC`），白底卡片上基本看不见。改用 `#64B5F6` 并把直径
  从 2.2 放大到 3.4。
- **雨滴等距排布**（6.4 / 9.6 / 12.8 / 16.0 / 19.2，中心 12.8），取「偶数位」或「首中尾」
  都是对称的；直接取前 N 个会一边重一边轻。

### 强度信号从哪来

上游 `MapCondition` 会把「小雨 / 中雨 / 大雨」一律压成「雨」，文字里的强度信息就没了。
`WeatherIconSelector.Select(condition, precipitationProbability)` 用降水概率补一刀：
普通的雨 + 概率 ≥ 70% → 大雨图标。至少不会出现「暴雨却挂小雨图标」。

### 动画

图标动效是 `UserControl.Resources` 里的 Storyboard，全部 `RepeatBehavior="Forever"`，
幅度都很小（位移 < 1px、太阳 24 秒一圈），目的是「活的」而不是「在跳舞」。

入场动效用 `MotionLibrary.PlayEntrance` / `PlayPopIn`（Storyboard + 组合变换），
卡片按 190ms 起步、每张递增 70ms 错峰出现。**只在数据首次出现时播一次**，
自动刷新不重播 —— 每 30 分钟整页弹一下很烦人。

两处必须注意：

- **列表里的图标 `IsAnimated="False"`**。一屏二十几个图标同时跑 Storyboard 不只是耗电，
  视觉上也是一锅粥。未使用的图层 `Collapsed`，不再参与布局。
- **尊重系统的「减少动画」**（`UISettings.AnimationsEnabled`）。关掉的用户不少是前庭功能敏感，
  硬播会让人不适。`MotionLibrary` 会直接跳过并把元素设为不透明，不是简单地把动画删掉。

### 无障碍

`WeatherIcon` 重写了 `OnCreateAutomationPeer()`。UserControl 默认没有 AutomationPeer，
不加这个在 UIA 树里根本看不到——屏幕阅读器读不到，自动化也断言不到。
现在它在树里以「天气图标：晴」的名字出现，pywinauto 能直接校验分类是否正确。

## 十三、已知警告

- `MVVMTK0045`：`[ObservableProperty]` 字段在 WinRT/AOT 场景的兼容性提示，不影响功能；
  彻底消除需改为 `partial` 属性写法。
