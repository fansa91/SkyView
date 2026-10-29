using SkyView.Services;
using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;

namespace SkyView.Shell;

/// <summary>定位结果。<c>Success=false</c> 时 <c>Message</c> 是给用户看的原因。</summary>
public sealed record LocationResult(bool Success, string City, string Message, double Latitude = 0, double Longitude = 0);

/// <summary>
/// 用 Windows 定位服务获取当前城市。
/// <para>
/// 注意几个只在**未打包（unpackaged）**应用里才会遇到的约束：
/// </para>
/// <list type="bullet">
/// <item><c>RequestAccessAsync</c> 没有包清单里的 <c>location</c> 能力声明可依，
/// 它的结果完全取决于系统「定位服务」总开关和隐私设置，可能直接返回 Denied。</item>
/// <item>系统定位总开关关掉时，<c>GetGeopositionAsync</c> 会抛
/// <c>UnauthorizedAccessException</c>，而不是返回空值 —— 必须捕获并翻译成人话。</item>
/// <item>没有 GPS 的台式机上可能只有 Wi-Fi 定位，精度差、耗时长，
/// 因此设置了 10 秒超时，不能让界面一直转圈。</item>
/// </list>
/// </summary>
internal sealed class LocationService
{
    private readonly GeocodingService _geocoder;

    public LocationService(GeocodingService geocoder)
    {
        _geocoder = geocoder;
    }

    public async Task<LocationResult> LocateCityAsync()
    {
        GeolocationAccessStatus access;
        try
        {
            access = await Geolocator.RequestAccessAsync();
        }
        catch (Exception ex)
        {
            return new LocationResult(false, string.Empty,
                $"无法请求定位权限：{ex.Message}。可以到「设置 → 隐私和安全性 → 位置」里允许应用使用位置，或直接手动输入城市。");
        }

        if (access != GeolocationAccessStatus.Allowed)
        {
            var reason = access == GeolocationAccessStatus.Denied
                ? "定位权限被拒绝"
                : "系统没有授予定位权限";
            return new LocationResult(false, string.Empty,
                $"{reason}。请到「设置 → 隐私和安全性 → 位置」中开启，或直接手动输入城市。");
        }

        double latitude;
        double longitude;

        try
        {
            var locator = new Geolocator { DesiredAccuracyInMeters = 2000 };
            var position = await locator.GetGeopositionAsync(
                maximumAge: TimeSpan.FromMinutes(5),
                timeout: TimeSpan.FromSeconds(10));

            latitude = position.Coordinate.Point.Position.Latitude;
            longitude = position.Coordinate.Point.Position.Longitude;
        }
        catch (UnauthorizedAccessException)
        {
            return new LocationResult(false, string.Empty,
                "系统定位服务已关闭。请到「设置 → 隐私和安全性 → 位置」中打开定位服务。");
        }
        catch (Exception ex)
        {
            return new LocationResult(false, string.Empty,
                $"获取定位失败：{ex.Message}。请确认设备有可用的定位方式（Wi-Fi / GPS），或手动输入城市。");
        }

        if (Math.Abs(latitude) < 0.0001 && Math.Abs(longitude) < 0.0001)
        {
            return new LocationResult(false, string.Empty,
                "定位返回的坐标无效（0,0），可能定位服务尚未就绪。请重试或手动输入城市。");
        }

        var point = await _geocoder.ReverseAsync(latitude, longitude);
        if (point is null || string.IsNullOrWhiteSpace(point.City))
        {
            return new LocationResult(false, string.Empty,
                $"已定位到坐标（{latitude:0.##}, {longitude:0.##}），但没能解析出城市名。请手动输入城市。",
                latitude, longitude);
        }

        return new LocationResult(true, point.City,
            $"已定位到 {point.City}（{latitude:0.##}, {longitude:0.##}）",
            latitude, longitude);
    }
}
