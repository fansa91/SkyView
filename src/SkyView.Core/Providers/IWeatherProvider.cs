using SkyView.Models;
using System.Diagnostics;

namespace SkyView.Providers;

public interface IWeatherProvider
{
    string Name { get; }

    Task<WeatherCurrent> GetCurrentAsync(string city, CancellationToken ct = default);

    Task<IEnumerable<WeatherForecast>> GetForecastAsync(string city, int days, CancellationToken ct = default);

    /// <summary>
    /// 逐小时预报。数据源不支持时返回空集合。
    /// <para>
    /// 这里刻意**不**给默认实现：接口映射只在声明接口的类型上计算一次，
    /// 默认实现会导致派生类写的同名方法永远不被调用（实测踩过）。
    /// 便利实现放在 <see cref="WeatherProviderBase"/> 里作为 virtual 成员。
    /// </para>
    /// </summary>
    Task<IEnumerable<WeatherHourly>> GetHourlyAsync(string city, int hours, CancellationToken ct = default);

    /// <summary>空气质量。数据源不支持时返回 null。</summary>
    Task<AirQuality?> GetAirQualityAsync(string city, CancellationToken ct = default);

    /// <summary>生活指数。数据源不支持时返回空集合。</summary>
    Task<IEnumerable<WeatherLifeIndex>> GetLifeIndexAsync(string city, CancellationToken ct = default);

    /// <summary>
    /// 连通性测试。默认实现用一次实时天气请求验证：能拿到数据就说明 Key / Host / 网络都通。
    /// **约定不抛异常**，把失败原因变成可读文本返回，供「测试连接」按钮直接展示。
    /// </summary>
    async Task<ProviderTestResult> TestConnectionAsync(string city, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var current = await GetCurrentAsync(city, ct).ConfigureAwait(false);
            stopwatch.Stop();

            return ProviderTestResult.Ok(
                $"连接成功：{current.City} {current.Temperature:0.#}°C {current.Condition}（{current.Source}）",
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return ProviderTestResult.Fail("请求超时：请检查网络，或调大超时时间。", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return ProviderTestResult.Fail(ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }
}
