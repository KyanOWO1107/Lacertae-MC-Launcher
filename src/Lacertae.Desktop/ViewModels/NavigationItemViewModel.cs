namespace Lacertae.Desktop.ViewModels;

public static class LauncherRouteIds
{
    public const string Home = "home";
    public const string Accounts = "accounts";
    public const string Versions = "versions";
    public const string Downloads = "downloads";
    public const string Resources = "resources";
    public const string Tasks = "tasks";
    public const string Settings = "settings";
}

public sealed class NavigationItemViewModel
{
    public NavigationItemViewModel(string routeId, string label, string description)
    {
        if (string.IsNullOrWhiteSpace(routeId))
        {
            throw new ArgumentException("Route ID cannot be blank.", nameof(routeId));
        }

        Label = string.IsNullOrWhiteSpace(label)
            ? throw new ArgumentException("Navigation label cannot be blank.", nameof(label))
            : label;
        Description = string.IsNullOrWhiteSpace(description)
            ? throw new ArgumentException("Navigation description cannot be blank.", nameof(description))
            : description;
        RouteId = routeId;
    }

    public string RouteId { get; }

    public string Label { get; }

    public string Description { get; }

    public bool IsSelected { get; internal set; }
}
