using Microsoft.Extensions.Configuration;
using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyView.Providers;

/// <summary>
/// 按优先级构建可用数据源实例。构造时立即加载一次（修复"启动后列表为空，
/// 任何刷新都直接报所有数据源不可用"的问题），设置页保存后再 <see cref="Reload"/>。
/// </summary>
public sealed class WeatherProviderFactory
{
    private readonly IConfiguration _config;
    private readonly ISecureConfigStore _secureStore;
    private readonly HttpClient _http;
    private readonly GeocodingService _geocoder;
    private readonly object _gate = new();

    private List<IWeatherProvider> _instances = [];

    public WeatherProviderFactory(IConfiguration config, ISecureConfigStore secureStore)
    {
        _config = config;
        _secureStore = secureStore;

        // 所有数据源共享同一个 HttpClient，避免每次开关数据源都 new 一个（socket 耗尽）
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SkyView/1.0 (+https://github.com)");

        // 地理编码也复用同一个 HttpClient：AccuWeather / PirateWeather 都靠它把城市名换成坐标
        _geocoder = new GeocodingService(_http);

        Reload();
    }

    public void Reload()
    {
        var instances = new List<IWeatherProvider>();

        // 演示模式：用于没有 API Key 时展示完整界面，以及 UI 自动化测试的稳定数据源
        if (IsDemoModeEnabled())
        {
            instances.Add(new DemoProvider(_http, new ProviderConfig { Name = DemoProvider.DemoName, Enabled = true }));

            lock (_gate)
            {
                _instances = instances;
            }
            return;
        }

        foreach (var cfg in ReadConfigs().OrderBy(c => c.Priority))
        {
            // 设置页保存的值优先于 appsettings.json（加密存储）
            var saved = _secureStore.Get<ProviderConfig>($"provider:{cfg.Name}");
            if (saved is not null)
            {
                if (!string.IsNullOrWhiteSpace(saved.ApiKey))
                    cfg.ApiKey = saved.ApiKey;
                if (!string.IsNullOrWhiteSpace(saved.ApiHost))
                    cfg.ApiHost = saved.ApiHost;
                if (string.IsNullOrWhiteSpace(cfg.DisplayName))
                    cfg.DisplayName = saved.DisplayName;
                cfg.Enabled = saved.Enabled;
                cfg.Priority = saved.Priority;
            }

            if (!cfg.Enabled)
                continue;

            var provider = CreateProvider(cfg);
            if (provider is null)
                throw new NotSupportedException($"未知的天气数据源: {cfg.Name}");

            instances.Add(provider);
        }

        lock (_gate)
        {
            _instances = instances;
        }
    }

    /// <summary>
    /// 按给定配置造一个数据源实例（设置页的「测试连接」用它试未保存的输入）。
    /// 复用同一个 HttpClient，避免额外 socket。
    /// </summary>
    public IWeatherProvider? CreateProvider(ProviderConfig cfg) => cfg.Name switch
    {
        "QWeather" => new QWeatherProvider(_http, cfg),
        "OpenWeatherMap" => new OpenWeatherMapProvider(_http, cfg),
        "Seniverse" => new SeniverseProvider(_http, cfg),
        "AccuWeather" => new AccuWeatherProvider(_http, cfg, _geocoder),
        "PirateWeather" => new PirateWeatherProvider(_http, cfg, _geocoder),
        "OpenMeteo" => new OpenMeteoProvider(_http, cfg, _geocoder),
        "Wttr" => new WttrProvider(_http, cfg),
        _ => null,
    };

    /// <summary>供「定位当前城市」把坐标反查成城市名。</summary>
    public GeocodingService Geocoder => _geocoder;

    public IReadOnlyList<IWeatherProvider> GetProviders()
    {
        lock (_gate)
        {
            return _instances;
        }
    }

    /// <summary>
    /// 是否启用演示数据源。两种开启方式：
    /// <c>appsettings.json</c> 里 <c>App:DemoMode=true</c>，或环境变量 <c>SKYVIEW_DEMO=1</c>。
    /// 后者便于 UI 测试与临时演示，无需改配置文件。
    /// </summary>
    public bool IsDemoModeEnabled()
    {
        if (_config.GetValue<bool>("App:DemoMode"))
            return true;

        var env = Environment.GetEnvironmentVariable("SKYVIEW_DEMO");
        return !string.IsNullOrWhiteSpace(env) && env != "0"
            && !env.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>读取数据源定义；配置缺失时使用内置清单。</summary>
    public IReadOnlyList<ProviderConfig> ReadConfigs()
    {
        var fromConfig = _config.GetSection("WeatherProviders").Get<List<ProviderConfig>>();
        return fromConfig is { Count: > 0 } ? fromConfig : ProviderCatalog.CreateDefaults();
    }

    /// <summary>替换数据源列表（仅测试使用，避免测试通过反射改私有字段）。</summary>
    internal void ReplaceProviders(IEnumerable<IWeatherProvider> providers)
    {
        lock (_gate)
        {
            _instances = providers.ToList();
        }
    }
}
