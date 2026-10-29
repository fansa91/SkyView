namespace SkyView.Models;

/// <summary>「测试连接」结果。刻意不抛异常，把失败原因变成可读文本。</summary>
public class ProviderTestResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long ElapsedMilliseconds { get; init; }

    public static ProviderTestResult Ok(string message, long elapsedMs) => new()
    {
        Success = true,
        Message = message,
        ElapsedMilliseconds = elapsedMs,
    };

    public static ProviderTestResult Fail(string message, long elapsedMs) => new()
    {
        Success = false,
        Message = message,
        ElapsedMilliseconds = elapsedMs,
    };
}
