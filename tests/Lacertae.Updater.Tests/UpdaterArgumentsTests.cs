using Lacertae.Updater;

namespace Lacertae.Updater.Tests;

public sealed class UpdaterArgumentsTests
{
    [Fact]
    public void ParseAcceptsOnlyOneAbsolutePlanArgument()
    {
        string path = Path.Combine(Path.GetTempPath(), "lacertae-plan.json");

        UpdaterArguments parsed = UpdaterArguments.Parse(["--plan", path]);

        Assert.Equal(path, parsed.PlanPath);
        Assert.Throws<ArgumentException>(() => UpdaterArguments.Parse([]));
        Assert.Throws<ArgumentException>(() => UpdaterArguments.Parse(["--plan", path, "--verbose"]));
        Assert.Throws<ArgumentException>(() => UpdaterArguments.Parse(["--plan", "relative-plan.json"]));
    }
}
