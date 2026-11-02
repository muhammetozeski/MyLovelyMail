using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace MyLovelyMail.MainProject.Services.Tor;

public static partial class TorService
{
    const bool IsLogEnabled = true;

    public sealed record AccountExitLease(int Epoch, string ExitIp, DateTime VerifiedUtc);

    static readonly ConcurrentDictionary<string, AccountExitLease> accountExitIps =
        new(StringComparer.OrdinalIgnoreCase);

    static readonly SemaphoreSlim exitIpGate = new(1, 1);
    static readonly TimeSpan ExitLeaseLifetime = TimeSpan.FromMinutes(5);

    public static IReadOnlyDictionary<string, AccountExitLease> ActiveExitLeases => accountExitIps;

    /// <summary>Drops tracked circuit and exit-IP state when an account is removed or rotated.</summary>
    public static void ReleaseAccountCircuit(string isolationKey)
    {
        if (string.IsNullOrWhiteSpace(isolationKey)) return;
        circuitEpochs.TryRemove(isolationKey, out _);
        accountExitIps.TryRemove(isolationKey, out _);
        Log($"Released Tor circuit state for isolationKey='{isolationKey}'.", Run: IsLogEnabled);
    }

    static void InvalidateExitLease(string isolationKey)
    {
        if (!string.IsNullOrWhiteSpace(isolationKey))
            accountExitIps.TryRemove(isolationKey, out _);
    }

    /// <summary>
    /// Ensures that the circuit for <paramref name="isolationKey"/> has a verified exit IP that
    /// is not currently in use by any other account's circuit. If a collision is detected, rotates
    /// the account's circuit epoch and re-provisions until a distinct exit IP is obtained.
    /// </summary>
    public static async Task<string?> EnsureUniqueExitIpAsync(TorEndpoint endpoint, string isolationKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(isolationKey)) return null;

        int currentEpoch = CircuitEpochOf(isolationKey);
        if (accountExitIps.TryGetValue(isolationKey, out var existing) &&
            existing.Epoch == currentEpoch &&
            DateTime.UtcNow - existing.VerifiedUtc < ExitLeaseLifetime &&
            !IsExitIpUsedByAnotherAccount(isolationKey, existing.ExitIp))
        {
            return existing.ExitIp;
        }

        await exitIpGate.WaitAsync(cancellationToken);
        try
        {
            var sw = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                int epoch = CircuitEpochOf(isolationKey);
                if (accountExitIps.TryGetValue(isolationKey, out var lease) &&
                    lease.Epoch == epoch &&
                    DateTime.UtcNow - lease.VerifiedUtc < ExitLeaseLifetime &&
                    !IsExitIpUsedByAnotherAccount(isolationKey, lease.ExitIp))
                {
                    return lease.ExitIp;
                }

                string? exitIp = await ProbeExitIpAsync(endpoint, isolationKey, cancellationToken);
                if (string.IsNullOrWhiteSpace(exitIp))
                    return null;

                if (!IsExitIpUsedByAnotherAccount(isolationKey, exitIp))
                {
                    accountExitIps[isolationKey] = new AccountExitLease(epoch, exitIp, DateTime.UtcNow);
                    Log($"Tor circuit for '{isolationKey}' (epoch {epoch}) assigned unique exit IP {exitIp} in {sw.ElapsedMilliseconds} ms.", Run: IsLogEnabled);
                    return exitIp;
                }

                Log($"Tor exit IP {exitIp} for '{isolationKey}' collides with another active account; rotating circuit (attempt {attempt}/5).", LogLevel.Warning, Run: IsLogEnabled);
                RotateCircuit(isolationKey);
            }

            return accountExitIps.TryGetValue(isolationKey, out var finalLease) ? finalLease.ExitIp : null;
        }
        finally
        {
            exitIpGate.Release();
        }
    }

    static bool IsExitIpUsedByAnotherAccount(string isolationKey, string exitIp)
    {
        foreach (var pair in accountExitIps)
        {
            if (!string.Equals(pair.Key, isolationKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.Value.ExitIp, exitIp, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - pair.Value.VerifiedUtc < ExitLeaseLifetime)
            {
                return true;
            }
        }
        return false;
    }

    static async Task<string?> ProbeExitIpAsync(TorEndpoint endpoint, string isolationKey, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var creds = IsolationCredentials(isolationKey);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                },
                ConnectCallback = async (ctx, ct) =>
                {
                    var socks = new TorSocks5Client(endpoint.Host, endpoint.Port, creds);
                    return await socks.ConnectAsync(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct);
                }
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            string json = await http.GetStringAsync("https://check.torproject.org/api/ip", budget.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("IP", out var ipProp))
                return ipProp.GetString()?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Exit IP probe for '{isolationKey}' could not complete ({ex.Message}); proceeding on isolated SOCKS credentials.", LogLevel.Warning, Run: IsLogEnabled);
        }
        return null;
    }

    /// <summary>
    /// Creates an <see cref="HttpClient"/> whose connections and DNS resolution are 100% tunnelled
    /// through Tor on the isolated circuit of <paramref name="isolationKey"/>, with OCSP/CRL
    /// revocation fetches disabled so Windows CryptoAPI never leaks outside the tunnel.
    /// </summary>
    public static HttpClient CreateIsolatedHttpClient(string isolationKey, CookieContainer? cookies = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(isolationKey))
            throw new ArgumentException("An isolation key is required for Tor HTTP clients.", nameof(isolationKey));

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = cookies != null,
            CookieContainer = cookies ?? new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(4),
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            ConnectCallback = async (context, token) =>
            {
                var sw = Stopwatch.StartNew();
                var endpoint = await RequireEndpointAsync(token);
                await EnsureUniqueExitIpAsync(endpoint, isolationKey, token);
                var credentials = IsolationCredentials(isolationKey);
                var socks = new TorSocks5Client(endpoint.Host, endpoint.Port, credentials);
                var stream = await socks.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, token);
                Log($"Tor HTTP SOCKS5 connected: key='{isolationKey}', target={context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}, endpoint={endpoint}, epoch={CircuitEpochOf(isolationKey)}, elapsed={sw.ElapsedMilliseconds}ms", Run: IsLogEnabled);
                return stream;
            }
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(60)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; rv:128.0) Gecko/20100101 Firefox/128.0");
        return client;
    }
}
