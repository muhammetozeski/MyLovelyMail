using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;

namespace MyLovelyMail.MainProject.Services.Tor;

/// <summary>
/// MailKit clients for Tor-only accounts. The only difference from the stock clients is the TLS
/// handshake: no session resumption (a resumed session ticket would tie together two connections
/// that left through different circuits) and no online revocation fetch (made by the OS outside
/// the tunnel).
/// </summary>
static class TorMailTls
{
    public static SslClientAuthenticationOptions Harden(SslClientAuthenticationOptions options)
    {
        options.AllowTlsResume = false;
        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        options.CertificateChainPolicy ??= new X509ChainPolicy();
        options.CertificateChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        options.CertificateChainPolicy.DisableCertificateDownloads = true;
        if (options.CertificateChainPolicy.ApplicationPolicy.Count == 0)
            options.CertificateChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return options;
    }
}

/// <summary>IMAP client used on Tor-only accounts; see <see cref="TorMailTls"/>.</summary>
public sealed class TorImapClient : ImapClient
{
    protected override SslClientAuthenticationOptions GetSslClientAuthenticationOptions(string host, RemoteCertificateValidationCallback validationCallback) =>
        TorMailTls.Harden(base.GetSslClientAuthenticationOptions(host, validationCallback));
}

/// <summary>POP3 client used on Tor-only accounts; see <see cref="TorMailTls"/>.</summary>
public sealed class TorPop3Client : Pop3Client
{
    protected override SslClientAuthenticationOptions GetSslClientAuthenticationOptions(string host, RemoteCertificateValidationCallback validationCallback) =>
        TorMailTls.Harden(base.GetSslClientAuthenticationOptions(host, validationCallback));
}

/// <summary>SMTP client used on Tor-only accounts and Direct-to-MX delivery; see <see cref="TorMailTls"/>.</summary>
public sealed class TorSmtpClient : SmtpClient
{
    /// <summary>
    /// The EHLO name for an anonymous connection: the sender's own mail domain. Never left null,
    /// because MailKit then announces this computer's host name.
    /// </summary>
    public static string EhloDomainFor(string? emailAddress)
    {
        if (!string.IsNullOrWhiteSpace(emailAddress))
        {
            int at = emailAddress.LastIndexOf('@');
            if (at >= 0 && at + 1 < emailAddress.Length)
                return emailAddress[(at + 1)..].Trim().ToLowerInvariant();
        }
        return "localhost";
    }

    protected override SslClientAuthenticationOptions GetSslClientAuthenticationOptions(string host, RemoteCertificateValidationCallback validationCallback) =>
        TorMailTls.Harden(base.GetSslClientAuthenticationOptions(host, validationCallback));
}
