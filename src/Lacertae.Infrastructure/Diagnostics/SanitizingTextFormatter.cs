using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Lacertae.Infrastructure.Diagnostics;

public sealed class SanitizingTextFormatter(LogSanitizer sanitizer) : ITextFormatter
{
    private static readonly MessageTemplateTextFormatter Formatter =
        new("[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {Message:lj}{NewLine}{Exception}");

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using StringWriter writer = new();
        Formatter.Format(logEvent, writer);
        output.Write(sanitizer.Sanitize(writer.ToString()));
    }
}
