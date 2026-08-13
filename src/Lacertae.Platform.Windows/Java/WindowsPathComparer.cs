using Lacertae.Application.Java;

namespace Lacertae.Platform.Windows.Java;

public sealed class WindowsPathComparer : IPathComparer
{
    public string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public bool Equals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
