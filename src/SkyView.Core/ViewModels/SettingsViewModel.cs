using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using SkyView.Models;
using SkyView.Providers;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SkyView.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    /// <summary>
    /// 自动保存的防抖间隔。连续输入（如敲 API Key）合并成一次落盘；
    /// UI 上 400ms 足够，测试里可调小。
    /// </summary>
    public TimeSpan SaveDebounce { get; set; } = TimeSpan.FromMilliseconds(400);

    private readonly ISecureConfigStore _store;
    private readonly WeatherProviderFactory _factory;
    private readonly IConfiguration? _config;
    private readonly IPreferencesStore? _preferencesStore;

    /// <summary>加载/初始化期间的属性变更不触发保存（只是把存量值读进界面）。</summary>
    private bool _suppressSave = true;

    /// <summary>持久化自身回写的属性（SavedApiKey / ApiKeyInput / StatusMessage）不再触发保存，防反馈环。</summary>
    private bool _persisting;

    private bool _dirty;
    private bool _dataSourcesTouched;
    private CancellationTokenSource? _saveCts;
    private Task _saveTask = Task.CompletedTask;

    public SettingsViewModel(
        ISecureConfigStore store,
        WeatherProviderFactory factory,
        IConfiguration? configuration = null,
        IPreferencesStore? preferencesStore = null)
    {
        _store = store;
        _factory = factory;
        _config = configuration;
        _preferencesStore = preferencesStore;

        PropertyChanged += OnAnyPropertyChanged;

        var prefs = preferencesStore?.Load() ?? new AppPreferences();
        UseFahrenheit = prefs.UseFahrenheit;
        AutoRefreshMinutes = prefs.NormalizedAutoRefreshMinutes;
        HourlyCount = prefs.NormalizedHourlyCount;
        ShowLifeIndex = prefs.ShowLifeIndex;

        // 功能数据源分配：没指派的功能保持空串 = 「自动」
        CurrentSource = prefs.SourceFor(WeatherFeature.Current) ?? string.Empty;
        ForecastSource = prefs.SourceFor(WeatherFeature.Forecast) ?? string.Empty;
        HourlySource = prefs.SourceFor(WeatherFeature.Hourly) ?? string.Empty;
        LifeIndexSource = prefs.SourceFor(WeatherFeature.LifeIndex) ?? string.Empty;

        TestCity = _config?.GetValue<string>("App:DefaultCity") is { Length: > 0 } city ? city : "北京";

        Providers = [];
        FeatureSourceOptions = [];
        StatusMessage = string.Empty;
        LoadProviders();

        _suppressSave = false;
    }

    /// <summary>
    /// 每次自动保存后触发。<paramref name="dataSourcesChanged"/> 为 true 表示改动
    /// 会影响取数链路（数据源启用/优先级/Key/功能分配），调用方应刷新天气；
    /// 纯显示偏好（单位、间隔、条数）只需重新应用，不必联网。
    /// </summary>
    public event Action<AppPreferences, bool>? Saved;

    [ObservableProperty]
    public partial ObservableCollection<ProviderConfigItem> Providers { get; set; }

    /// <summary>功能数据源下拉框的选项：「自动」+ 启用中的数据源。随启用状态实时重建。</summary>
    [ObservableProperty]
    public partial ObservableCollection<FeatureSourceOption> FeatureSourceOptions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    /// <summary>「测试连接」用的城市。</summary>
    [ObservableProperty]
    public partial string TestCity { get; set; }

    // ---------- 偏好设置 ----------

    [ObservableProperty]
    public partial bool UseFahrenheit { get; set; }

    /// <summary>自动刷新间隔（分钟）。0 = 关闭。</summary>
    [ObservableProperty]
    public partial double AutoRefreshMinutes { get; set; }

    [ObservableProperty]
    public partial double HourlyCount { get; set; }

    [ObservableProperty]
    public partial bool ShowLifeIndex { get; set; }

    // ---------- 功能数据源分配（空串 = 自动） ----------

    [ObservableProperty]
    public partial string CurrentSource { get; set; }

    [ObservableProperty]
    public partial string ForecastSource { get; set; }

    [ObservableProperty]
    public partial string HourlySource { get; set; }

    [ObservableProperty]
    public partial string LifeIndexSource { get; set; }

    /// <summary>把界面上的偏好收拢成一个对象。</summary>
    public AppPreferences BuildPreferences() => new()
    {
        UseFahrenheit = UseFahrenheit,
        AutoRefreshMinutes = (int)Math.Round(AutoRefreshMinutes),
        HourlyCount = (int)Math.Round(HourlyCount),
        ShowLifeIndex = ShowLifeIndex,
        FeatureSources = BuildFeatureSources(),
    };

    /// <summary>把四个下拉框的选择收成功能→源字典；「自动」（空串）不写入。</summary>
    private Dictionary<string, string> BuildFeatureSources()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SetIfAssigned(WeatherFeature.Current, CurrentSource);
        SetIfAssigned(WeatherFeature.Forecast, ForecastSource);
        SetIfAssigned(WeatherFeature.Hourly, HourlySource);
        SetIfAssigned(WeatherFeature.LifeIndex, LifeIndexSource);
        return map;

        void SetIfAssigned(string feature, string? source)
        {
            if (!string.IsNullOrWhiteSpace(source))
                map[feature] = source.Trim();
        }
    }

    private void LoadProviders()
    {
        Providers.Clear();

        var defaults = _config?.GetSection("WeatherProviders").Get<List<ProviderConfig>>();
        if (defaults is not { Count: > 0 })
            defaults = ProviderCatalog.CreateDefaults().ToList();

        foreach (var d in defaults.OrderBy(x => x.Priority))
        {
            // 设置页展示的必须是"工厂实际会用到"的那份值，否则会出现
            // appsettings 里 Enabled=false 但界面上显示为勾选的错位。
            var saved = _store.Get<ProviderConfig>($"provider:{d.Name}");
            var effective = saved ?? d;

            var item = new ProviderConfigItem
            {
                Name = d.Name,
                DisplayName = string.IsNullOrWhiteSpace(d.DisplayName) ? d.Name : d.DisplayName,
                ApiHost = string.IsNullOrWhiteSpace(effective.ApiHost) ? d.ApiHost : effective.ApiHost,
                SavedApiKey = effective.ApiKey,
                Enabled = effective.Enabled,
                Priority = effective.Priority,
                RequiresApiKey = d.RequiresApiKey,
                ApiKeyInput = string.Empty,
            };

            // 勾选/取消启用时，功能分配下拉框要跟着重建（选项只含启用中的源）
            item.PropertyChanged += OnProviderItemPropertyChanged;

            Providers.Add(item);
        }

        RebuildFeatureSourceOptions();
    }

    /// <summary>ViewModel 自身属性的变更 → 安排一次防抖保存。</summary>
    private void OnAnyPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSave || _persisting)
            return;

        switch (e.PropertyName)
        {
            case nameof(UseFahrenheit):
            case nameof(AutoRefreshMinutes):
            case nameof(HourlyCount):
            case nameof(ShowLifeIndex):
                MarkDirty(dataSources: false);
                break;

            case nameof(CurrentSource):
            case nameof(ForecastSource):
            case nameof(HourlySource):
            case nameof(LifeIndexSource):
                MarkDirty(dataSources: true);
                break;

            // TestCity 只用于「测试连接」，不落盘；StatusMessage 是保存结果本身
        }
    }

    /// <summary>
    /// 数据源条目的变更 → 重建功能分配选项 + 安排保存。
    /// 只有会影响取数的属性才计数；测试连接的过程状态（IsTesting/TestMessage）不算。
    /// </summary>
    private void OnProviderItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSave || _persisting)
            return;

        switch (e.PropertyName)
        {
            case nameof(ProviderConfigItem.Enabled):
                RebuildFeatureSourceOptions();
                MarkDirty(dataSources: true);
                break;

            case nameof(ProviderConfigItem.Priority):
            case nameof(ProviderConfigItem.ApiKeyInput):
            case nameof(ProviderConfigItem.ApiHost):
                MarkDirty(dataSources: true);
                break;
        }
    }

    /// <summary>
    /// 重建功能分配下拉框：「自动」+ 当前启用中的数据源。
    /// 已指派但被取消启用的源，其指派就地作废（回到「自动」）——
    /// 运行时本来也会因源不在启用列表而回退到自动链，这里让界面说实话。
    /// </summary>
    private void RebuildFeatureSourceOptions()
    {
        FeatureSourceOptions.Clear();
        FeatureSourceOptions.Add(new FeatureSourceOption { Name = string.Empty, DisplayName = "自动（按优先级）" });

        foreach (var p in Providers.Where(p => p.Enabled))
            FeatureSourceOptions.Add(new FeatureSourceOption { Name = p.Name, DisplayName = p.DisplayName });

        ResetIfDisabled(WeatherFeature.Current, () => CurrentSource, v => CurrentSource = v);
        ResetIfDisabled(WeatherFeature.Forecast, () => ForecastSource, v => ForecastSource = v);
        ResetIfDisabled(WeatherFeature.Hourly, () => HourlySource, v => HourlySource = v);
        ResetIfDisabled(WeatherFeature.LifeIndex, () => LifeIndexSource, v => LifeIndexSource = v);

        void ResetIfDisabled(string feature, Func<string> get, Action<string> set)
        {
            var assigned = get();
            if (string.IsNullOrEmpty(assigned))
                return;
            if (FeatureSourceOptions.Any(o => string.Equals(o.Name, assigned, StringComparison.OrdinalIgnoreCase)))
                return;
            set(string.Empty);
        }
    }

    // ---------- 实时保存 ----------

    /// <summary>标记有待保存的变更，并（重新）安排一次防抖保存。</summary>
    private void MarkDirty(bool dataSources)
    {
        _dirty = true;
        if (dataSources)
            _dataSourcesTouched = true;
        ScheduleSave();
    }

    /// <summary>
    /// 安排防抖保存。方法在 UI 线程上启动（属性 setter 来自控件绑定），
    /// <see cref="ConfigureAwait(bool)"/> 的 true 让落盘动作回到 UI 线程执行
    /// （Saved 事件的订阅方会动 ObservableCollection，不能在线程池上跑）。
    /// </summary>
    private void ScheduleSave()
    {
        _saveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _saveCts = cts;
        _saveTask = RunDebouncedSaveAsync(cts);
    }

    private async Task RunDebouncedSaveAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SaveDebounce, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
                Persist();
        }
        catch (OperationCanceledException)
        {
            // 防抖期间又有新变更：这一次作废，等最新那次调度
        }
    }

    /// <summary>立即落盘（离开页面前或测试用；没有待保存变更时是 no-op）。</summary>
    public void PersistNow()
    {
        if (!_dirty)
            return;

        _saveCts?.Cancel();
        _saveCts = null;
        Persist();
    }

    /// <summary>等待待处理的防抖保存完成（测试用；没有待保存任务时立即完成）。</summary>
    public Task SaveTask => _saveTask;

    /// <summary>
    /// 把界面当前状态写入存储：数据源配置进 DPAPI（settings.dat），
    /// 偏好进 preferences.json，然后重载数据源工厂并通知订阅方。
    /// </summary>
    private void Persist()
    {
        var warnings = new List<string>();

        _persisting = true;
        try
        {
            foreach (var p in Providers)
            {
                var apiKey = p.ResolveApiKey();

                _store.Set($"provider:{p.Name}", new ProviderConfig
                {
                    Name = p.Name,
                    DisplayName = p.DisplayName,
                    ApiHost = p.ApiHost?.Trim() ?? string.Empty,
                    ApiKey = apiKey,
                    Enabled = p.Enabled,
                    Priority = (int)Math.Round(p.Priority),
                });

                // 保存后同步到界面状态，避免再次保存时误以为"还是旧 Key"
                p.SavedApiKey = apiKey;
                p.ApiKeyInput = string.Empty;

                // 免 Key 数据源（Open-Meteo / wttr.in）不参与"启用了却没填 Key"的告警
                if (p.Enabled && p.RequiresApiKey && string.IsNullOrWhiteSpace(apiKey))
                    warnings.Add(p.DisplayName);
            }

            var prefs = BuildPreferences();
            _preferencesStore?.Save(prefs);
            _factory.Reload();

            StatusMessage = warnings.Count > 0
                ? $"已自动保存（{DateTime.Now:HH:mm:ss}）。但 {string.Join("、", warnings)} 已启用却没有 API Key，刷新仍会失败。"
                : $"已自动保存（{DateTime.Now:HH:mm:ss}），已生效。";
        }
        finally
        {
            _persisting = false;
        }

        var dataSourcesChanged = _dataSourcesTouched;
        _dirty = false;
        _dataSourcesTouched = false;

        Saved?.Invoke(BuildPreferences(), dataSourcesChanged);
    }

    /// <summary>
    /// 测试单个数据源的连通性。用界面上**当前输入**（包括还没保存的 Key）去试，
    /// 这样用户填完就能立刻知道对不对，不用先保存再回天气页看错误。
    /// </summary>
    [RelayCommand]
    public async Task TestConnectionAsync(ProviderConfigItem? item)
    {
        if (item is null || item.IsTesting)
            return;

        var config = item.ToConfig();
        if (item.RequiresApiKey && string.IsNullOrWhiteSpace(config.ApiKey))
        {
            item.TestSucceeded = false;
            item.TestMessage = "✗ 请先填写 API Key 再测试。";
            return;
        }

        item.IsTesting = true;
        item.TestSucceeded = null;
        item.TestMessage = "正在测试…";

        try
        {
            var provider = _factory.CreateProvider(config);
            if (provider is null)
            {
                item.TestSucceeded = false;
                item.TestMessage = $"✗ 未知的数据源：{config.Name}";
                return;
            }

            var city = string.IsNullOrWhiteSpace(TestCity) ? "北京" : TestCity.Trim();
            var result = await provider.TestConnectionAsync(city).ConfigureAwait(true);

            item.TestSucceeded = result.Success;
            item.TestMessage = result.Success
                ? $"✓ {result.Message}（{result.ElapsedMilliseconds} ms）"
                : $"✗ 失败：{result.Message}";
        }
        catch (Exception ex)
        {
            item.TestSucceeded = false;
            item.TestMessage = $"✗ 失败：{ex.Message}";
        }
        finally
        {
            item.IsTesting = false;
        }
    }
}
