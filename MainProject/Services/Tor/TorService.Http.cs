using System.Net;
using System.Net.Http;
using System.Net.Security;

namespace MyLovelyMail.MainProject.Services.Tor;

public static partial class TorService
{
    /// <summary>Releases local retry bookkeeping; isolation credentials are never retained.</summary>
    public static void ReleaseAccountCircuit(string isolationKey) => circuitEpochs.TryRemove(isolationKey, out _);

    /// <summary>
    /// Every TCP connection uses a new SOCKS login. DNS is resolved at Tor; cookies, redirects,
    /// pooling, automatic credentials, TLS tickets and OS certificate downloads are disabled.
    /// A provider requiring cookies may supply a container owned by a single operation.
    /// </summary>
    public static HttpClient CreateIsolatedHttpClient(string isolationKey, CookieContainer? cookies = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationKey);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = cookies != null,
            AllowAutoRedirect = false,
            Credentials = null,
            ActivityHeadersPropagator = null,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.Zero,
            PooledConnectionIdleTimeout = TimeSpan.Zero,
            SslOptions = TorMailTls.Harden(new SslClientAuthenticationOptions()),
            ConnectCallback = async (context, token) =>
            {
                var endpoint = await RequireEndpointAsync(token);
                var socks = new TorSocks5Client(endpoint.Host, endpoint.Port, NewStreamCredentials());
                return await socks.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token);
            }
        };
        if (cookies != null)
            handler.CookieContainer = cookies;

        var client = new HttpClient(new HttpsOnlyHandler(handler), disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");
        client.DefaultRequestHeaders.ConnectionClose = true;
        return client;
    }

    /// <summary>Rejects an insecure API URI before any connection or DNS lookup is attempted.</summary>
    sealed class HttpsOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { IsAbsoluteUri: true, Scheme: "https" })
                throw new InvalidOperationException("Tor mail HTTP requests require HTTPS.");
            return base.SendAsync(request, cancellationToken);
        }
    }
}
