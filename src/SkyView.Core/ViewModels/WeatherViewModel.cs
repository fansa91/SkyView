using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using SkyView.Alerts;
using SkyView.Formatting;
using SkyView.Models;
using SkyView.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SkyView.ViewModels;

public partial class WeatherViewModel : ObservableObject
{
    private readonly IWeatherService _weatherService;
    private readonly IWeatherCache? _cache;
    private readonly IPreferencesStore? _preferences;
    private readonly int _forecastDays;
    private AppPreferences _prefs;

    public WeatherViewModel(
        IWeatherService weatherService,
        IConfiguration? configuration = null,
        IWeatherCache? cache = null,
        IPreferencesStore? preferences = null)
    {
        _weatherService = weatherService;
        _cache = cache;
        _preferences = preferences;
        _prefs = preferences?.Load() ?? new AppPreferences();

        UnitSettings.UseFahrenheit = _prefs.UseFahrenheit;
        _useFahrenheit = _prefs.UseFahrenheit;

        var defaultCity = configuration?.GetValue<string>("App:DefaultCity");
        City = string.IsNullOrWhiteSpace(defaultCity) ? "北京" : defaultCity!;

        var days = configuration?.GetValue("App:ForecastDays", 3) ?? 3;
        _forecastDays = days is > 0 and <= 15 ? days : 3;

        Current = new WeatherCurrent();
        ErrorMessage = string.Empty;

        // 离线优先：先把上次成功的数据显示出来，再让界面去后台刷新
        RestoreFromCache();
    }

    [ObservableProperty]
    public partial WeatherCurrent Current { get; set; }

    [ObservableProperty]
    public partial string City { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastUpdated { get; set; }

    /// <summary>未来几天预报（数据源不支持或失败时为空集合，不影响当前天气展示）。</summary>
    public ObservableCollection<WeatherForecast> Forecast { get; } = [];

    /// <summary>逐小时预报。</summary>
    public ObservableCollection<WeatherHourly> Hourly { get; } = [];

    /// <summary>恶劣天气预警（tier 3，本地从预报数据推导；无触发时为空，卡片隐藏）。</summary>
    public ObservableCollection<WeatherAlert> Alerts { get; } = [];

    /// <summary>生活指数。</summary>
    public ObservableCollection<WeatherLifeIndex> LifeIndex { get; } = [];

    /// <summary>是否已经成功加载过一次天气（首页用于只自动刷新一次）。</summary>
    public bool HasLoaded { get; private set; }

    private bool _hasCurrent;

    /// <summary>
    /// 是否已有真实的当前天气数据。
    /// <para>
    /// <see cref="Current"/> 在构造时是默认实例（温度 0），若直接绑定会把 <c>0°C</c> 当成真实数据展示，
    /// 用户会误以为拿到了天气。这里用一个显式标志控制卡片显隐：只有请求成功过（或从缓存恢复）才显示。
    /// 刷新失败时保留上一次的数据（不清空），避免界面闪成空白。
    /// </para>
    /// </summary>
    public bool HasCurrent
    {
        get => _hasCurrent;
        private set
        {
            if (_hasCurrent == value)
                return;

            _hasCurrent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmptyState));
        }
    }

    private bool _isFromCache;

    /// <summary>当前显示的是否为本地缓存（离线/首次启动尚未刷新成功）。</summary>
    public bool IsFromCache
    {
        get => _isFromCache;
        private set
        {
            if (_isFromCache == value)
                return;

            _isFromCache = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CacheHint));
        }
    }

    /// <summary>缓存提示文案，如「显示的是 12 分钟前的缓存数据」。</summary>
    public string CacheHint
    {
        get
        {
            if (!_isFromCache || LastUpdated is null)
                return string.Empty;

            var age = DateTimeOffset.Now - LastUpdated.Value;
            return $"当前显示的是缓存数据（{WeatherFormat.DescribeAge(age)}更新）";
        }
    }

    /// <summary>既没有数据也没有错误（首次进入、尚未拉到结果）时给一句引导文案。</summary>
    public bool IsEmptyState => !HasCurrent && !HasError;

    public bool HasForecast => Forecast.Count > 0;

    public bool HasAlerts => Alerts.Count > 0;

    /// <summary>预报区标题，按实际拿到的天数动态显示（「未来 15 天」/「未来 3 天」/「未来几天」）。</summary>
    public string ForecastTitleText => Forecast.Count > 0 ? $"未来 {Forecast.Count} 天" : "未来几天";

    public bool HasHourly => Hourly.Count > 0;

    public bool HasLifeIndex => LifeIndex.Count > 0 && _prefs.ShowLifeIndex;

    // ---------- 温度单位 ----------

    private bool _useFahrenheit;

    public bool UseFahrenheit
    {
        get => _useFahrenheit;
        private set
        {
            if (_useFahrenheit == value)
                return;

            _useFahrenheit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UnitSymbol));
            RaiseTemperatureDependents();
        }
    }

    public string UnitSymbol => _useFahrenheit ? WeatherFormat.Fahrenheit : WeatherFormat.Celsius;

    /// <summary>单位切换后，把所有温度相关文本重新通知一遍。</summary>
    private void RaiseTemperatureDependents()
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(FeelsLikeText));
        OnPropertyChanged(nameof(TrendRangeText));
        OnPropertyChanged(nameof(DewPointText)); // 露点是温度，随单位换算
    }

    // ---------- 派生展示文本 ----------

    public string TemperatureText => WeatherFormat.Temperature(Current.Temperature, _useFahrenheit);

    public string FeelsLikeText => WeatherFormat.Temperature(Current.FeelsLike, _useFahrenheit);

    // ---------- UV 指数（tier 1 三件套之一） ----------

    public bool HasUvIndex => Current.HasUvIndex;

    /// <summary>UV 徽标文本，如「UV 5 · 中等」。数据源未提供时为空串（界面据此隐藏）。</summary>
    public string UvIndexText => Current.HasUvIndex
        ? $"UV {(int)Math.Round(Current.UvIndex!.Value)} · {WeatherFormat.UvCategory(Current.UvIndex.Value)}"
        : string.Empty;

    /// <summary>UV 颜色档位（0 无数据 / 1-5 见 <see cref="WeatherFormat.UvLevel"/>）。</summary>
    public int UvLevel => Current.HasUvIndex ? WeatherFormat.UvLevel(Current.UvIndex!.Value) : 0;

    // ---------- 详细指标行（降水 / 云量 / 露点 / 阵风，tier 1 三件套之二） ----------

    public bool HasDetailMetrics => Current.HasDetailMetrics;

    public bool HasPrecipitationMetric => Current.HasPrecipitation;

    public string PrecipitationText => Current.HasPrecipitation
        ? $"{WeatherFormat.Number(Current.Precipitation!.Value, "0.##")} mm"
        : string.Empty;

    public bool HasCloudCoverMetric => Current.HasCloudCover;

    public string CloudCoverText => Current.HasCloudCover
        ? $"{WeatherFormat.Number(Current.CloudCover!.Value, "0")} %"
        : string.Empty;

    public bool HasDewPointMetric => Current.HasDewPoint;

    /// <summary>露点是温度，受 °C/°F 切换影响。</summary>
    public string DewPointText => Current.HasDewPoint
        ? WeatherFormat.Temperature(Current.DewPoint!.Value, _useFahrenheit)
        : string.Empty;

    public bool HasWindGustsMetric => Current.HasWindGusts;

    public string WindGustsText => Current.HasWindGusts
        ? $"{WeatherFormat.Number(Current.WindGusts!.Value, "0.#")} km/h"
        : string.Empty;

    // ---------- 短临降雨（tier 2：分钟级降雨，Open-Meteo minutely_15） ----------

    /// <summary>数据源是否提供分钟级降雨预判（wttr.in 等没有此能力，整行隐藏）。</summary>
    public bool HasMinutelyRain => Current.HasMinutelyRain;

    /// <summary>短临降雨文案，如「短临降雨：约 45 分钟后开始下雨，持续约 60 分钟」。</summary>
    public string MinutelyRainText => Current.MinutelyRain is null
        ? string.Empty
        : WeatherFormat.MinutelyRainSummary(Current.MinutelyRain);

    // ---------- 花粉（tier 3：Open-Meteo Air Quality API，主要覆盖欧洲） ----------

    /// <summary>数据源是否提供花粉数据（国内城市全部返回 null，整行隐藏）。</summary>
    public bool HasPollen => Current.HasPollen;

    /// <summary>花粉短文案，如「花粉：中（草 12.5 · 树 45 粒/m³）」。</summary>
    public string PollenText => Current.Pollen is { HasData: true } p
        ? $"花粉：{WeatherFormat.PollenCategory(p.Peak)}" +
          $"（草 {WeatherFormat.Number(p.Grass ?? 0, "0.#")} · 树 {WeatherFormat.Number(p.Tree ?? 0, "0.#")} 粒/m³）"
        : string.Empty;

    // ---------- 气压趋势（tier 1 三件套之三，本地对逐小时气压序列计算） ----------

    /// <summary>气压趋势短文案，如「▲ 急升」。逐小时无气压数据（非 Open-Meteo/wttr.in 源）时为空串。</summary>
    public string PressureTrendText => WeatherFormat.PressureTrend(Hourly.Select(h => h.Pressure).ToList());

    public bool HasPressureTrend => !string.IsNullOrEmpty(PressureTrendText);

    /// <summary>逐小时到达后气压趋势才可计算，通知一次。</summary>
    private void RaiseExtrasDependents()
    {
        OnPropertyChanged(nameof(HasForecast));
        OnPropertyChanged(nameof(ForecastTitleText));
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(HasHourly));
        OnPropertyChanged(nameof(HasLifeIndex));
        OnPropertyChanged(nameof(TrendRangeText));
        OnPropertyChanged(nameof(ForecastSourceText));
        OnPropertyChanged(nameof(HourlySourceText));
        OnPropertyChanged(nameof(PressureTrendText));
        OnPropertyChanged(nameof(HasPressureTrend));
    }

    public string AirQualityText => Current.HasAirQuality
        ? $"AQI {Current.Aqi} {(string.IsNullOrWhiteSpace(Current.AqiCategory) ? WeatherFormat.AqiCategory(Current.Aqi) : Current.AqiCategory)}"
        : string.Empty;

    public string AirQualityLevelText => Current.HasAirQuality
        ? (string.IsNullOrWhiteSpace(Current.AqiCategory) ? WeatherFormat.AqiCategory(Current.Aqi) : Current.AqiCategory)
        : string.Empty;

    public int AirQualityLevel => Current.HasAirQuality ? WeatherFormat.AqiLevel(Current.Aqi) : 0;

    public string WindText
    {
        get
        {
            if (!Current.HasWind && string.IsNullOrWhiteSpace(Current.WindDirection))
                return string.Empty;

            var direction = string.IsNullOrWhiteSpace(Current.WindDirection) ? "风向未知" : Current.WindDirection;
            return Current.HasWind ? $"{direction} {Current.WindSpeed:0.#} km/h" : direction;
        }
    }

    public string SunTimesText => Current.HasSunTimes
        ? $"日出 {Current.Sunrise} · 日落 {Current.Sunset}"
        : string.Empty;

    /// <summary>趋势图的温度区间摘要。图表是代码手绘的，这块文本同时作为无障碍描述和测试锚点。</summary>
    public string TrendRangeText
    {
        get
        {
            if (Forecast.Count == 0)
                return string.Empty;

            var min = Forecast.Min(f => f.TempLow);
            var max = Forecast.Max(f => f.TempHigh);
            return $"区间 {WeatherFormat.Temperature(min, _useFahrenheit)} ~ {WeatherFormat.Temperature(max, _useFahrenheit)}";
        }
    }

    /// <summary>预报区块的来源标注，如「来源：wttr.in」。按功能分配数据源后，各区块可能来自不同源。</summary>
    public string ForecastSourceText => SourceCaption(Forecast.FirstOrDefault()?.Source);

    /// <summary>逐小时区块的来源标注。</summary>
    public string HourlySourceText => SourceCaption(Hourly.FirstOrDefault()?.Source);

    private static string SourceCaption(string? source) =>
        string.IsNullOrWhiteSpace(source) ? string.Empty : $"来源：{source}";

    // ---------- 变更通知 ----------

    partial void OnCurrentChanged(WeatherCurrent value)
    {
        OnPropertyChanged(nameof(TemperatureText));
        OnPropertyChanged(nameof(FeelsLikeText));
        OnPropertyChanged(nameof(AirQualityText));
        OnPropertyChanged(nameof(AirQualityLevelText));
        OnPropertyChanged(nameof(AirQualityLevel));
        OnPropertyChanged(nameof(WindText));
        OnPropertyChanged(nameof(SunTimesText));
        // tier 1 新指标
        OnPropertyChanged(nameof(HasUvIndex));
        OnPropertyChanged(nameof(UvIndexText));
        OnPropertyChanged(nameof(UvLevel));
        OnPropertyChanged(nameof(HasDetailMetrics));
        OnPropertyChanged(nameof(HasPrecipitationMetric));
        OnPropertyChanged(nameof(PrecipitationText));
        OnPropertyChanged(nameof(HasCloudCoverMetric));
        OnPropertyChanged(nameof(CloudCoverText));
        OnPropertyChanged(nameof(HasDewPointMetric));
        OnPropertyChanged(nameof(DewPointText));
        OnPropertyChanged(nameof(HasWindGustsMetric));
        OnPropertyChanged(nameof(WindGustsText));
        // tier 2 短临降雨
        OnPropertyChanged(nameof(HasMinutelyRain));
        OnPropertyChanged(nameof(MinutelyRainText));
        // tier 3 花粉
        OnPropertyChanged(nameof(HasPollen));
        OnPropertyChanged(nameof(PollenText));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmptyState));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsNotLoading));

    partial void OnLastUpdatedChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(CacheHint));

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsNotLoading => !IsLoading;

    private bool CanRefresh() => !IsLoading;

    // ---------- 命令 ----------

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        ErrorMessage = string.Empty;
        IsLoading = true;

        try
        {
            Current = await _weatherService.GetCurrentWithFallbackAsync(City);
            LastUpdated = DateTimeOffset.Now;
            HasLoaded = true;
            HasCurrent = true;
            IsFromCache = false;

            await LoadExtrasAsync();
            SaveSnapshot();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 预报 / 逐小时 / 生活指数属于加分项：并行拉取，任一失败只影响自己那块，
    /// 不污染当前天气和错误提示。
    /// </summary>
    private async Task LoadExtrasAsync()
    {
        var forecastTask = SafeLoadAsync(() => _weatherService.GetForecastWithFallbackAsync(City, _forecastDays));
        var hourlyTask = SafeLoadAsync(() => _weatherService.GetHourlyWithFallbackAsync(City, _prefs.NormalizedHourlyCount));
        var lifeTask = SafeLoadAsync(() => _weatherService.GetLifeIndexWithFallbackAsync(City));

        await Task.WhenAll(forecastTask, hourlyTask, lifeTask).ConfigureAwait(true);

        ReplaceAll(Forecast, forecastTask.Result);
        ReplaceAll(Hourly, hourlyTask.Result);
        ReplaceAll(LifeIndex, lifeTask.Result);

        RebuildAlerts();
        RaiseExtrasDependents();
    }

    /// <summary>
    /// 从当前天气 + 预报重新推导恶劣天气预警（tier 3）。纯函数、无请求，
    /// 单位切换时也要重跑——Reason 里的高低温文本随 °C/°F 变化。
    /// </summary>
    private void RebuildAlerts()
    {
        ReplaceAll(Alerts, WeatherAlertEngine.Derive(Current, Forecast, _useFahrenheit));
        OnPropertyChanged(nameof(HasAlerts));
    }

    private static async Task<List<T>> SafeLoadAsync<T>(Func<Task<IEnumerable<T>>> loader)
    {
        try
        {
            var items = await loader().ConfigureAwait(false);
            return items?.ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
            target.Add(item);
    }

    // ---------- 单位与偏好 ----------

    /// <summary>
    /// 切换温度单位。会重建预报/逐小时的集合内容，
    /// 让 DataTemplate 里的绑定重新求值（否则列表项会保持旧单位的文本）。
    /// </summary>
    public void ApplyUnit(bool useFahrenheit, bool persist = true)
    {
        if (_prefs.UseFahrenheit != useFahrenheit)
        {
            _prefs.UseFahrenheit = useFahrenheit;
            if (persist)
                _preferences?.Save(_prefs);
        }

        UnitSettings.UseFahrenheit = useFahrenheit;
        UseFahrenheit = useFahrenheit;

        RebuildForUnitChange(Forecast);
        RebuildForUnitChange(Hourly);
        RebuildAlerts(); // Reason 里含高低温文本，单位切换后重推导
    }

    private static void RebuildForUnitChange<T>(ObservableCollection<T> source)
    {
        if (source.Count == 0)
            return;

        var snapshot = source.ToList();
        source.Clear();
        foreach (var item in snapshot)
            source.Add(item);
    }

    /// <summary>保存偏好并立即生效（设置页调用）。</summary>
    public void ApplyPreferences(AppPreferences preferences)
    {
        _prefs = preferences;
        _preferences?.Save(_prefs);

        UnitSettings.UseFahrenheit = _prefs.UseFahrenheit;
        UseFahrenheit = _prefs.UseFahrenheit; // setter 内部已 RaiseTemperatureDependents（含 DewPointText）

        RebuildForUnitChange(Forecast);
        RebuildForUnitChange(Hourly);
        RebuildAlerts();

        OnPropertyChanged(nameof(HasLifeIndex));
    }

    public AppPreferences Preferences => _prefs;

    // ---------- 缓存 ----------

    private void RestoreFromCache()
    {
        var snapshot = _cache?.Load();
        if (snapshot?.Current is null)
            return;

        City = string.IsNullOrWhiteSpace(snapshot.City) ? City : snapshot.City;
        Current = snapshot.Current;
        LastUpdated = snapshot.SavedAt;
        HasCurrent = true;
        IsFromCache = true;

        ReplaceAll(Forecast, snapshot.Forecast);
        ReplaceAll(Hourly, snapshot.Hourly);
        ReplaceAll(LifeIndex, snapshot.LifeIndex);

        RebuildAlerts();
        RaiseExtrasDependents();
    }

    private void SaveSnapshot()
    {
        if (_cache is null || !HasCurrent)
            return;

        _cache.Save(new WeatherSnapshot
        {
            City = City,
            Current = Current,
            Forecast = Forecast.ToList(),
            Hourly = Hourly.ToList(),
            LifeIndex = LifeIndex.ToList(),
            SavedAt = LastUpdated ?? DateTimeOffset.Now,
        });
    }
}
