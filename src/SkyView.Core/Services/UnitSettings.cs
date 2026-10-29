using System;

namespace SkyView.Services;

/// <summary>
/// 当前温度单位。用静态状态是因为 IValueConverter 是无状态的单例，
/// 模板里的 <c>x:Bind</c> 无法把单位当作参数传进去。
/// 单位变更后需要重建列表集合，让 DataTemplate 里的绑定重新求值。
/// </summary>
public static class UnitSettings
{
    private static bool _useFahrenheit;

    public static bool UseFahrenheit
    {
        get => _useFahrenheit;
        set
        {
            if (_useFahrenheit == value)
                return;

            _useFahrenheit = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>单位变更通知（目前仅用于调试/扩展，界面靠重建集合刷新）。</summary>
    public static event EventHandler? Changed;

    public static double Convert(double celsius) =>
        _useFahrenheit ? celsius * 9d / 5d + 32d : celsius;

    public static string Symbol => _useFahrenheit ? "°F" : "°C";
}
