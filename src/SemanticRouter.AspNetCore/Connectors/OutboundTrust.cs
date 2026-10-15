using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The extra CA certificates of <see cref="NetworkOptions.CaBundle"/>. A server certificate is accepted when the
/// operating system trusts it, or when its chain ends at one of these roots and its name matches; nothing else.
/// </summary>
internal sealed class OutboundTrust
{
    // id-kp-serverAuth: the certificate must be meant for a TLS server, as the system check requires.
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private readonly X509Certificate2Collection? _roots;

    public OutboundTrust(X509Certificate2Collection? roots, string? source = null)
    {
        _roots = roots is { Count: > 0 } ? roots : null;
        Source = _roots is null ? null : source;
    }

    /// <summary>No bundle: only the operating system's store is trusted.</summary>
    public static OutboundTrust None { get; } = new(null);

    /// <summary>The extra roots, or <see langword="null"/> without a bundle.</summary>
    public X509Certificate2Collection? Roots => _roots;

    /// <summary>The bundle's full path, or <see langword="null"/>.</summary>
    public string? Source { get; }

    /// <summary>Loads the bundle named by <paramref name="options"/>; <see cref="None"/> when none is set.</summary>
    /// <exception cref="InvalidOperationException">The file cannot be read or holds no certificate.</exception>
    public static OutboundTrust From(NetworkOptions options, string? contentRoot)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.CaBundle))
        {
            return None;
        }

        string path = Path.GetFullPath(options.CaBundle, contentRoot ?? Environment.CurrentDirectory);
        var certificates = new X509Certificate2Collection();
        try
        {
            certificates.ImportFromPemFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            throw new InvalidOperationException($"SemanticRouter:Network:CaBundle: {path} could not be read: {ex.Message}", ex);
        }

        return certificates.Count > 0
            ? new OutboundTrust(certificates, path)
            : throw new InvalidOperationException($"SemanticRouter:Network:CaBundle: {path} holds no PEM certificate (-----BEGIN CERTIFICATE-----).");
    }

    /// <summary>Makes <paramref name="handler"/> trust the bundle too, unless it already has its own certificate check.</summary>
    public void Apply(HttpMessageHandler handler)
    {
        if (_roots is null)
        {
            return;
        }

        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.SslOptions.RemoteCertificateValidationCallback ??= (_, certificate, chain, errors) => Validate(certificate, chain, errors);
                break;
            case HttpClientHandler client:
                client.ServerCertificateCustomValidationCallback ??= (_, certificate, chain, errors) => Validate(certificate, chain, errors);
                break;
        }
    }

    /// <summary>
    /// Accepts what the system accepted. Otherwise only a chain problem is forgiven, and only when the chain builds to
    /// one of the bundle's roots; a wrong host name or a missing certificate never is.
    /// </summary>
    public bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || _roots is null || certificate is null)
        {
            return false;
        }

        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(_roots);
        custom.ChainPolicy.RevocationMode = chain?.ChainPolicy.RevocationMode ?? X509RevocationMode.NoCheck;
        custom.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthentication));
        if (chain is not null)
        {
            // The intermediates the server sent.
            custom.ChainPolicy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
            foreach (X509ChainElement element in chain.ChainElements)
            {
                custom.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        X509Certificate2 leaf = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        try
        {
            return custom.Build(leaf);
        }
        finally
        {
            if (!ReferenceEquals(leaf, certificate))
            {
                leaf.Dispose();
            }
        }
    }
}

/// <summary>Fails start-up when <see cref="NetworkOptions.CaBundle"/> cannot be loaded, instead of every HTTPS request.</summary>
internal sealed class NetworkOptionsValidator(IHostEnvironment? environment = null) : IValidateOptions<NetworkOptions>
{
    public ValidateOptionsResult Validate(string? name, NetworkOptions options)
    {
        try
        {
            OutboundTrust.From(options, environment?.ContentRootPath);
            return ValidateOptionsResult.Success;
        }
        catch (InvalidOperationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}
