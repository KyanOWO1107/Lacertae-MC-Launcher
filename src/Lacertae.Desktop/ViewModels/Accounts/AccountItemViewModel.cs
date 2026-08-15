using System.ComponentModel;
using Lacertae.Domain.Accounts;

namespace Lacertae.Desktop.ViewModels.Accounts;

public sealed class AccountItemViewModel : INotifyPropertyChanged
{
    private Account account;
    private string? avatarPath;
    private bool isDefault;
    private bool isVersionOverride;
    private bool isResolved;
    private bool isDeleting;

    public AccountItemViewModel(
        Account account,
        string? avatarPath,
        bool isDefault = false,
        bool isVersionOverride = false,
        bool isResolved = false)
    {
        this.account = account ?? throw new ArgumentNullException(nameof(account));
        this.avatarPath = avatarPath;
        this.isDefault = isDefault;
        this.isVersionOverride = isVersionOverride;
        this.isResolved = isResolved;
        isDeleting = account.Status == AccountStatus.Deleting;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Account Account => account;

    public string Id => account.Id;

    public string PlayerName => account.PlayerName;

    public AccountType Type => account.Type;

    public AccountStatus Status => account.Status;

    public string AccountTypeLabel => account.Type == AccountType.Microsoft ? "正版" : "离线";

    public string StatusLabel => account.Status switch
    {
        AccountStatus.Active => "可用",
        AccountStatus.ReauthenticationRequired => "需要重新登录",
        AccountStatus.Deleting => "删除中",
        _ => "未知状态",
    };

    public string? AvatarPath => avatarPath;

    public string AvatarFallbackLabel =>
        string.IsNullOrWhiteSpace(account.PlayerName)
            ? "?"
            : account.PlayerName[..1].ToUpperInvariant();

    public bool UsesPlaceholder => string.IsNullOrWhiteSpace(avatarPath);

    public bool IsDefault => isDefault;

    public bool IsVersionOverride => isVersionOverride;

    public bool IsResolved => isResolved;

    public bool IsDeleting => isDeleting || account.Status == AccountStatus.Deleting;

    public bool IsReauthenticationRequired => account.Status == AccountStatus.ReauthenticationRequired;

    public bool IsEnabled => !IsDeleting;

    public bool CanSetDefault => IsEnabled && account.Status == AccountStatus.Active;

    public bool CanSetVersionAccount => CanSetDefault;

    internal void Apply(
        Account next,
        string? nextAvatarPath,
        bool nextIsDefault,
        bool nextIsVersionOverride,
        bool nextIsResolved)
    {
        ArgumentNullException.ThrowIfNull(next);
        bool accountChanged = !Equals(account, next);
        bool avatarChanged = !string.Equals(avatarPath, nextAvatarPath, StringComparison.Ordinal);
        account = next;
        avatarPath = nextAvatarPath;
        isDefault = nextIsDefault;
        isVersionOverride = nextIsVersionOverride;
        isResolved = nextIsResolved;
        isDeleting = next.Status == AccountStatus.Deleting;

        if (accountChanged)
        {
            Publish(nameof(Account));
            Publish(nameof(PlayerName));
            Publish(nameof(AvatarFallbackLabel));
            Publish(nameof(Type));
            Publish(nameof(Status));
            Publish(nameof(AccountTypeLabel));
            Publish(nameof(StatusLabel));
            Publish(nameof(IsDeleting));
            Publish(nameof(IsReauthenticationRequired));
            Publish(nameof(IsEnabled));
            Publish(nameof(CanSetDefault));
            Publish(nameof(CanSetVersionAccount));
        }

        if (avatarChanged)
        {
            Publish(nameof(AvatarPath));
            Publish(nameof(UsesPlaceholder));
        }

        Publish(nameof(IsDefault));
        Publish(nameof(IsVersionOverride));
        Publish(nameof(IsResolved));
    }

    internal void MarkDeleting()
    {
        if (isDeleting)
        {
            return;
        }

        isDeleting = true;
        Publish(nameof(IsDeleting));
        Publish(nameof(IsEnabled));
        Publish(nameof(CanSetDefault));
        Publish(nameof(CanSetVersionAccount));
        Publish(nameof(StatusLabel));
    }

    private void Publish(string propertyName) => PropertyChanged?.Invoke(
        this,
        new PropertyChangedEventArgs(propertyName));
}
