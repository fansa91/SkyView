using System.Runtime.CompilerServices;

// 让测试项目可以访问 internal 的纯函数（如 WeatherProviderBase.MapCondition）
[assembly: InternalsVisibleTo("SkyView.UnitTests")]
[assembly: InternalsVisibleTo("SkyView.ComponentTests")]
