using Serilog;
using Serilog.Core;

namespace Lacertae.Infrastructure.Diagnostics;

public static class LoggingBootstrap
{
    public static Logger CreateFileLogger(string logDirectory, IEnumerable<string> privatePathPrefixes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentNullException.ThrowIfNull(privatePathPrefixes);

        Directory.CreateDirectory(logDirectory);
        string logPath = Path.Combine(logDirectory, "lacertae-.log");
        SanitizingTextFormatter formatter = new(new LogSanitizer(privatePathPrefixes));
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                formatter,
                logPath,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 14,
                shared: false)
            .CreateLogger();
    }
}
