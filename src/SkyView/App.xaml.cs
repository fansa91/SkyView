using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using SkyView.Diagnostics;
using SkyView.Providers;
using SkyView.Services;
using SkyView.ViewModels;
using System;
using System.IO;
using System.Threading.Tasks;

namespace SkyView;

public sealed partial class App : Application
{
    /// <summary>全局服务定位器（WinUI 3 的 Frame 导航使用无参构造，无法直接构造注入）。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static IConfiguration Configuration { get; private set; } = null!;

    /// <summary>主窗口引用。页面需要订阅窗口的 Activated 事件来做过期刷新。</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 先挂崩溃日志，再初始化服务，避免启动期异常静默变成 0xC000027B
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
        UnhandledException += (_, e) =>
            CrashLog.Write("Application.UnhandledException", e.Exception);

        // 使用可执行文件所在目录，而不是当前工作目录：
        // 从任意目录启动（快捷方式、任务栏、文件管理器）都能找到 appsettings.json。
        var baseDir = AppContext.BaseDirectory;

        Configuration = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddMemoryCache();
        services.AddSingleton(Configuration);

        services.AddSingleton<ISecureConfigStore, SecureConfigStore>();
        services.AddSingleton<CityStore>();
        services.AddSingleton<IPreferencesStore, PreferencesStore>();
        services.AddSingleton<IWeatherCache, WeatherCache>();
        services.AddSingleton<WeatherProviderFactory>();
        services.AddSingleton<IWeatherService, WeatherService>();

        services.AddSingleton<WeatherViewModel>();
        services.AddSingleton<SettingsViewModel>();

        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        MainWindow = window;
        window.Activate();
    }
}
