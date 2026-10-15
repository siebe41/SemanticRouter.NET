using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

public sealed class OutboundTrustTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("outbound-trust-tests").FullName;
    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _server;
    private readonly X509Certificate2 _stranger;

    public OutboundTrustTests()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Test Corporate Root", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        _ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        using ECDsa serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _server = Leaf("CN=localhost", serverKey, request => request.Create(_ca, now.AddHours(-1), now.AddDays(7), [1, 2, 3, 4]));

        using ECDsa strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _stranger = Leaf("CN=localhost", strangerKey, request => request.CreateSelfSigned(now.AddHours(-1), now.AddDays(7)));

        File.WriteAllText(BundlePath, _ca.ExportCertificatePem());
    }

    private string BundlePath => Path.Combine(_directory, "corporate-root.pem");

    public void Dispose()
    {
        _ca.Dispose();
        _server.Dispose();
        _stranger.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A TLS server certificate for localhost and 127.0.0.1, with its private key, usable by SslStream on every OS.</summary>
    private static X509Certificate2 Leaf(string subject, ECDsa key, Func<CertificateRequest, X509Certificate2> create)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 created = create(request);
        using X509Certificate2? withKey = created.HasPrivateKey ? null : created.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12((withKey ?? created).Export(X509ContentType.Pkcs12), password: null);
    }

    [Fact]
    public void Trusts_a_certificate_issued_by_the_bundle_besides_the_system_store()
    {
        OutboundTrust trust = OutboundTrust.From(new NetworkOptions { CaBundle = BundlePath }, contentRoot: null);

        trust.Roots.Should().ContainSingle();
        trust.Validate(_server, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        trust.Validate(_stranger, null, SslPolicyErrors.None).Should().BeTrue("what the system accepts stays accepted");
    }

    [Fact]
    public void Still_rejects_unrelated_certificates_and_wrong_host_names()
    {
        OutboundTrust trust = OutboundTrust.From(new NetworkOptions { CaBundle = BundlePath }, contentRoot: null);

        trust.Validate(_stranger, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
        trust.Validate(_server, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch).Should().BeFalse();
        trust.Validate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
        OutboundTrust.None.Validate(_server, null, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
    }

    [Fact]
    public void Resolves_a_relative_bundle_against_the_content_root_and_explains_a_bad_one()
    {
        OutboundTrust.From(new NetworkOptions { CaBundle = "corporate-root.pem" }, _directory).Source.Should().Be(BundlePath);
        OutboundTrust.From(new NetworkOptions(), _directory).Should().BeSameAs(OutboundTrust.None);

        File.WriteAllText(Path.Combine(_directory, "empty.pem"), "not a certificate");
        FluentActions.Invoking(() => OutboundTrust.From(new NetworkOptions { CaBundle = "empty.pem" }, _directory))
            .Should().Throw<InvalidOperationException>().WithMessage("*Network:CaBundle*holds no PEM certificate*");
        FluentActions.Invoking(() => OutboundTrust.From(new NetworkOptions { CaBundle = "missing.pem" }, _directory))
            .Should().Throw<InvalidOperationException>().WithMessage("*Network:CaBundle*missing.pem could not be read*");
    }

    [Fact]
    public void A_bundle_that_cannot_be_loaded_fails_validation()
    {
        using ServiceProvider provider = Services(Path.Combine(_directory, "missing.pem"));

        FluentActions.Invoking(() => provider.GetRequiredService<IOptions<NetworkOptions>>().Value)
            .Should().Throw<OptionsValidationException>().WithMessage("*Network:CaBundle*");
    }

    [Fact]
    public async Task The_routers_clients_reach_a_server_signed_by_the_bundle_and_other_clients_do_not()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task server = ServeAsync(listener, _server, stop.Token);
        var address = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/models");

        using (ServiceProvider provider = Services(BundlePath))
        {
            IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();
            using HttpClient connector = factory.CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + "corp");
            (await connector.GetStringAsync(address, stop.Token)).Should().Be("ok");

            // The host's own clients keep the system store only.
            using HttpClient other = factory.CreateClient("someone-else");
            await FluentActions.Awaiting(() => other.GetStringAsync(address, stop.Token)).Should().ThrowAsync<HttpRequestException>();
        }

        using (ServiceProvider provider = Services(caBundle: null))
        {
            using HttpClient connector = provider.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAICompatibleConnector.HttpClientNamePrefix + "corp");
            await FluentActions.Awaiting(() => connector.GetStringAsync(address, stop.Token)).Should().ThrowAsync<HttpRequestException>();
        }

        await stop.CancelAsync();
        listener.Stop();
        await server.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private static ServiceProvider Services(string? caBundle)
    {
        var settings = new Dictionary<string, string?>();
        if (caBundle is not null)
        {
            settings["SemanticRouter:Network:CaBundle"] = caBundle;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("someone-else");
        services.AddSemanticPromptRouting(configuration.GetSection(SemanticRouterOptions.SectionName));
        return services.BuildServiceProvider();
    }

    /// <summary>Answers every HTTPS request with "ok"; a client that refuses the certificate just drops the connection.</summary>
    private static async Task ServeAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellationToken);
                var request = new StringBuilder();
                var buffer = new byte[4096];
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = await tls.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException)
            {
                // The client refused the certificate.
            }
        }
    }
}
