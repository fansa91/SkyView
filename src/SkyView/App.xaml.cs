using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using SkyView.Diagnostics;
using SkyView.Providers;
using SkyView.Services;
using SkyView.Shell;
using SkyView.ViewModels;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView;

public sealed partial class App : Application
{
    /// <summary>全局服务定位器（WinUI 3 的 Frame 导航使用无参构造，无法直接构造注入）。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public static IConfiguration Configuration { get; private set; } = null!;

    /// <summary>主窗口引用。页面需要订阅窗口的 Activated 事件来做过期刷新。</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>单实例锁，必须活到进程结束。</summary>
    private readonly Mutex? _instanceMutex;

    public App()
    {
        // 单实例检查放在最前面：第二次启动时在这里就把窗口叫到前台并退出，
        // 连服务容器和 XAML 资源都不用初始化。
        if (!SingleInstance.TryAcquire(out _instanceMutex))
        {
            SingleInstance.ActivateExisting();
            Environment.Exit(0);
            return;
        }

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

        // 定位服务依赖地理编码器（同一个工厂里的实例，共用 HttpClient 与结果缓存）
        services.AddSingleton(sp => new LocationService(sp.GetRequiredService<WeatherProviderFactory>().Geocoder));

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
