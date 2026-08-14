using Lacertae.Application.Startup;
using Lacertae.Domain.Common;
using Lacertae.Domain.Problems;
using Lacertae.Domain.Results;
using Lacertae.Domain.Storage;
using Serilog.Core;

namespace Lacertae.Infrastructure.Startup;

public sealed class FileLoggingInitializer : IStartupLoggingInitializer
{
    private Logger? logger;

    public Result<Unit> Initialize(DataRoot dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        if (logger is not null)
        {
            return Result.Success();
        }

        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            logger = Diagnostics.LoggingBootstrap.CreateFileLogger(
                dataRoot.LogsPath,
                [userProfile, dataRoot.RoamingPath, dataRoot.LocalPath]);
            logger.Information("Lacertae startup logging initialized.");
            return Result.Success();
        }
        catch (IOException)
        {
            return Result.Failure(Problem("STARTUP_LOGGING_FAILED"));
        }
        catch (UnauthorizedAccessException)
        {
            return Result.Failure(Problem("STARTUP_LOGGING_FAILED"));
        }
    }

    public void Dispose()
    {
        logger?.Dispose();
        logger = null;
    }

    private static Problem Problem(string code) => new(
        code,
        ProblemStage.Configuration,
        "problem.startup.logging_failed",
        false,
        Guid.NewGuid().ToString("N"),
        ["action.startup.review_logs"],
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["safePath"] = "logs/lacertae-*.log",
        });
}
