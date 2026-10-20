namespace SemanticRouter.AspNetCore;

/// <summary>
/// Replaces known secret values (the resolved API keys, secret headers, gateway keys and CLI environment values of the
/// active configuration) with <c>***</c> in text that leaves the router: error details sent to clients, upstream error
/// bodies relayed to them, activity entries and log messages. A backend that echoes a key back in an error message
/// must not turn the router into a way of reading it.
/// </summary>
internal sealed class SecretRedactor
{
    /// <summary>Values shorter than this are not redacted: they would match ordinary text.</summary>
    public const int MinimumLength = 6;

    private readonly string[] _secrets;

    /// <summary>Creates a redactor for <paramref name="secrets"/>.</summary>
    public SecretRedactor(IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        // Longest first, so a secret that contains another is replaced whole.
        _secrets = secrets
            .Where(s => s.Length >= MinimumLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    /// <summary>The secret values it replaces (long enough to match), for keeping them out of a child process's environment.</summary>
    internal IReadOnlyList<string> Values => _secrets;

    /// <summary>A redactor that changes nothing.</summary>
    public static SecretRedactor None { get; } = new([]);

    /// <summary>Returns <paramref name="text"/> with every known secret replaced by <c>***</c>.</summary>
    public string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (string secret in _secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
            {
                text = text.Replace(secret, RouterConfiguration.MaskedSecret, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>Like <see cref="Redact(string)"/>, passing <see langword="null"/> through.</summary>
    public string? RedactOrNull(string? text) => text is null ? null : Redact(text);
}
