namespace SemanticRouter.AspNetCore;

/// <summary>
/// An address as the router shows it in messages, the dashboard and logs: the scheme, host, port and path, without the
/// user name and password, query or fragment, any of which may carry a key
/// (<c>https://user:sk-…@host/v1?api-key=…</c>).
/// </summary>
internal static class AddressDisplay
{
    /// <summary>The address without its credentials, query and fragment; <see langword="null"/> for <see langword="null"/>.</summary>
    public static string? Show(Uri? address) => address switch
    {
        null => null,
        { IsAbsoluteUri: true } => address.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped),
        _ => address.OriginalString.Split('?', '#')[0],
    };

    private static readonly string[] SecretParameterWords =
        ["key", "token", "secret", "auth", "password", "pwd", "credential", "session", "signature", "sig"];

    /// <summary>
    /// The address with the credentials in it shown as <c>***</c>: the user information (<c>user:password@</c>) and the
    /// values of query parameters named like a key (<c>api-key</c>, <c>token</c>, <c>sig</c>…). Other parameters
    /// (<c>api-version</c>) stay readable. The same address when it holds no credentials.
    /// </summary>
    public static Uri? Mask(Uri? address)
    {
        if (address is not { IsAbsoluteUri: true } || (address.UserInfo.Length == 0 && address.Query.Length <= 1))
        {
            return address;
        }

        bool masked = false;
        var builder = new UriBuilder(address);
        if (address.UserInfo.Length > 0)
        {
            builder.UserName = RouterConfiguration.MaskedSecret;
            builder.Password = string.Empty;
            masked = true;
        }

        if (address.Query.Length > 1)
        {
            string[] parameters = address.Query[1..].Split('&');
            for (int i = 0; i < parameters.Length; i++)
            {
                int equals = parameters[i].IndexOf('=', StringComparison.Ordinal);
                string name = equals < 0 ? parameters[i] : parameters[i][..equals];
                if (equals >= 0 && SecretParameterWords.Any(w => Uri.UnescapeDataString(name).Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    parameters[i] = name + "=" + RouterConfiguration.MaskedSecret;
                    masked = true;
                }
            }

            builder.Query = string.Join('&', parameters);
        }

        return masked ? builder.Uri : address;
    }

    /// <summary>Whether <paramref name="address"/> still holds a <c>***</c> placeholder from <see cref="Mask"/>.</summary>
    public static bool HasMask(Uri? address) =>
        address is { IsAbsoluteUri: true }
        && (Uri.UnescapeDataString(address.UserInfo) == RouterConfiguration.MaskedSecret
            || Uri.UnescapeDataString(address.Query).Contains("=" + RouterConfiguration.MaskedSecret, StringComparison.Ordinal));

    /// <summary>
    /// <paramref name="real"/> when <paramref name="shown"/> is exactly its <see cref="Mask">masked</see> form (the
    /// dashboard sent the address back unchanged), otherwise <paramref name="shown"/>.
    /// </summary>
    public static Uri? Unmask(Uri? shown, Uri? real) =>
        shown is { IsAbsoluteUri: true } && real is not null && HasMask(shown)
        && string.Equals(Mask(real)?.AbsoluteUri, shown.AbsoluteUri, StringComparison.Ordinal)
            ? real
            : shown;
}
