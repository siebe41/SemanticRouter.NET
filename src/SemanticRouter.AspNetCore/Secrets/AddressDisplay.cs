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
}
