namespace Lacertae.Domain.Problems;

public sealed record Problem
{
    public Problem(
        string code,
        ProblemStage stage,
        string messageKey,
        bool isRetryable,
        string correlationId,
        IReadOnlyList<string> suggestedActionKeys,
        IReadOnlyDictionary<string, string>? safeContext = null)
    {
        Code = Require(code, nameof(code));
        Stage = stage;
        MessageKey = Require(messageKey, nameof(messageKey));
        IsRetryable = isRetryable;
        CorrelationId = Require(correlationId, nameof(correlationId));
        SuggestedActionKeys = suggestedActionKeys ?? throw new ArgumentNullException(nameof(suggestedActionKeys));
        SafeContext = safeContext ?? new Dictionary<string, string>();
    }

    public string Code { get; }
    public ProblemStage Stage { get; }
    public string MessageKey { get; }
    public bool IsRetryable { get; }
    public string CorrelationId { get; }
    public IReadOnlyList<string> SuggestedActionKeys { get; }
    public IReadOnlyDictionary<string, string> SafeContext { get; }

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Value cannot be blank.", parameterName)
            : value;
}
