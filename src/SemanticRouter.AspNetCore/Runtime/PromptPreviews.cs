namespace SemanticRouter.AspNetCore;

/// <summary>The prompt text an activity entry keeps, as <see cref="StateOptions.StorePrompts"/> allows.</summary>
internal static class PromptPreviews
{
    /// <summary>The most characters of a prompt kept.</summary>
    public const int Length = 160;

    /// <summary>
    /// The start of <paramref name="prompt"/>, nothing with <see cref="PromptStorage.Off"/>. With
    /// <see cref="PromptStorage.Redacted"/>, credentials are masked in the whole prompt first, so a key cut by the
    /// truncation cannot leave its first characters behind.
    /// </summary>
    public static string Of(string prompt, PromptStorage storage)
    {
        if (storage == PromptStorage.Off)
        {
            return string.Empty;
        }

        string text = storage == PromptStorage.Redacted ? Redact(prompt) : prompt;
        return text.Length <= Length ? text : text[..Length] + "…";
    }

    /// <summary><paramref name="text"/> with credentials (the <c>LocalOnlyWhen: Secrets</c> patterns) replaced by <c>***</c>.</summary>
    public static string Redact(string text)
    {
        try
        {
            return ProfileSelection.SecretPattern().Replace(text, RouterConfiguration.MaskedSecret);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            // Too slow to check: keep nothing rather than something unchecked.
            return string.Empty;
        }
    }
}
