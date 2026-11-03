using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using MimeKit;
using MimeKit.Text;
using MailKit.Security;
using MyLovelyMail.MainProject.DataModels.Mail;
using MyLovelyMail.MainProject.Services.Mail;
using MyLovelyMail.MainProject.Services.Mail.FastMail;
using MyLovelyMail.MainProject.Services.Tor;
using MyLovelyMail.MainProject.Storage;
using MyLovelyMail.MainProject.Stores;

// The executable and its disposable mail store must be built under a NewTemp directory.
if (args.Length is < 1 or > 2 || !Path.GetFullPath(AppContext.BaseDirectory).StartsWith(
        Path.GetFullPath(args[0]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Pass the NewTemp root containing the test executable and its generated data.");
Logger.ActivateLogging = false;
AppPaths.EnsureCreated();
if (args.Length == 2 && args[1] == "--recovery-fixture")
{
    const string master = "Synthetic vault master password";
    const string normalPassword = "Synthetic normal account password";
    const string fastPassword = "Synthetic Fast Mail password 40-character fixture!";
    Settings.CredentialVaultMode.Set(VaultMode.MasterPassword);
    CredentialVault.SetMasterPassword(master);
    var anchor = new MailAccountData { EmailAddress = "recovery-anchor@example.invalid" };
    var temporary = new MailAccountData { Protocol = IncomingProtocol.FastMail, FastMailProviderId = "mailtm", EmailAddress = "recovery-fixture@example.invalid" };
    AccountStore.Save(anchor);
    AccountStore.Save(temporary);
    CredentialVault.SetPassword(anchor.Id, normalPassword);
    CredentialVault.SetPassword(temporary.Id, fastPassword);
    using var message = new MimeMessage { MessageId = "fixture@example.invalid", Date = DateTimeOffset.UtcNow, Body = new TextPart("plain") { Text = "Private synthetic mail content for restore verification." } };
    using var bytes = new MemoryStream();
    message.WriteTo(bytes);
    MessageStore.SaveFolder(new MailFolderData { AccountId = temporary.Id, FullName = MessageStore.LocalFolderPrefix + "Drafts", IsLocal = true, Role = FolderRole.Drafts });
    MessageStore.SaveFullMessage(temporary.Id, MessageStore.LocalFolderPrefix + "Drafts", 1, bytes.ToArray());
    MessageStore.SaveFullMessage(temporary.Id, "INBOX", 2, bytes.ToArray());
    var script = FastMailRecovery.CreateRecoveryScript(temporary, new FastMailRecovery.RecoveryAccount(anchor.Id, anchor.EmailAddress));
    if (File.ReadAllText(script.Path).Contains(fastPassword) || File.ReadAllText(script.Path).Contains("Private synthetic mail content"))
        throw new Exception("Recovery data was not encrypted.");
    CredentialVault.RemovePassword(temporary.Id);
    string fixturePath = Path.Combine(AppContext.BaseDirectory, "recovery-fixture.json");
    File.WriteAllText(fixturePath, System.Text.Json.JsonSerializer.Serialize(new
    {
        ScriptPath = script.Path, script.Files, DataRoot = AppPaths.Root, AccountId = temporary.Id, AnchorId = anchor.Id,
        NormalPassword = normalPassword, FastPassword = fastPassword, MasterPassword = master
    }));
    Console.WriteLine(fixturePath);
    return;
}
#if DEBUG
if (args.Length == 2 && args[1] == "--live-end-to-end")
{
    Settings.CredentialVaultMode.Set(VaultMode.MasterPassword);
    CredentialVault.SetMasterPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    try
    {
        Console.WriteLine("LIVE: existing seven-step Fast Mail suite, sending only between its own disposable test mailboxes.");
        var result = System.Text.Json.JsonSerializer.SerializeToElement(
            await MyLovelyMail.MainProject.Diagnostics.DebugApi.RunFastMailEndToEndSuiteAsync(budget.Token));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "live-result.json"), result.GetRawText());
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (!result.GetProperty("allPassed").GetBoolean()) Environment.ExitCode = 1;
        Console.WriteLine($"Live suite: {result.GetProperty("passedCount")}/{result.GetProperty("totalSteps")} passed.");
    }
    finally { TorProcess.Stop(); }
    return;
}
#endif
int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

async Task RejectHandshake(byte[] response, NetworkCredential? credentials, string name)
{
    using var exchange = new ScriptedStream(response);
    try { await TorSocks5.HandshakeAsync(exchange, credentials, default); }
    catch (SocksProtocolException) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL: " + name);
}

var first = TorService.NewStreamCredentials();
var second = TorService.NewStreamCredentials();
Check(first.UserName != second.UserName && first.Password != second.Password, "Independent cryptographic SOCKS logins");
for (int sample = 0; sample < 50; sample++)
{
    string name = FastMailCredentials.GenerateUsername();
    if (name.Length is < 5 or > 10 || !name.All(char.IsAsciiLetterLower)
        || name.Zip(name.Skip(1), static (a, b) => "aeiou".Contains(a) != "aeiou".Contains(b)).Any(static valid => !valid))
        throw new Exception("The pronounceable cryptographic username format was violated.");
}
Check(true, "Fast Mail aliases have 5–10 alternating cryptographic vowel/consonant choices");
string generatedPassword = FastMailCredentials.GeneratePassword();
Check(generatedPassword.Length >= 40 && PasswordGenerator.CalculateEntropyBits(generatedPassword) >= 230
    && generatedPassword.Any(char.IsUpper) && generatedPassword.Any(char.IsLower) && generatedPassword.Any(char.IsDigit)
    && generatedPassword.Contains(' ') && generatedPassword.Any(static c => !char.IsLetterOrDigit(c) && c != ' '),
    "Default Fast Mail passwords retain every character category and meet the 230-bit minimum");
string digits = FastMailCredentials.GeneratePassword(new PasswordOptions { IncludeUppercase = false, IncludeLowercase = false, IncludeSpecialSymbols = false, IncludeDigits = true });
Check(digits.All(char.IsDigit) && PasswordGenerator.CalculateEntropyBits(digits) >= 230, "Reduced password alphabets adapt length and keep the minimum strength");
string clearPassword = FastMailCredentials.GeneratePassword(FastMailCredentials.DefaultPasswordOptions with { ExcludeAmbiguousCharacters = true });
Check(!clearPassword.Any(static c => "O0l1I|B8".Contains(c)), "Exclude-ambiguous password option is honored");
await RejectHandshake([5, 0], first, "Reject no-auth downgrade with isolation credentials");
await RejectHandshake([5, 2, 2, 0], first, "Reject invalid SOCKS password reply version");
await RejectHandshake([5, 2, 1, 1], first, "Reject failed SOCKS password login");
using (var exchange = new ScriptedStream([5, 2, 1, 0]))
{
    await TorSocks5.HandshakeAsync(exchange, first, default);
    Check(exchange.Written.Take(3).SequenceEqual(new byte[] { 5, 1, 2 }), "Offer only authenticated SOCKS when isolation is required");
}
using (var exchange = new ScriptedStream([5, 0]))
{
    await TorSocks5.HandshakeAsync(exchange, null, default);
    Check(exchange.Written.SequenceEqual(new byte[] { 5, 1, 0 }), "Explicit no-auth protocol path remains usable");
}
using (var exchange = new ScriptedStream([5, 0, 0, 1, 0, 0, 0, 0, 0, 0]))
{
    await TorSocks5.SendRequestAsync(exchange, 1, "mail.example.invalid", 993, default);
    Check(exchange.Written[3] == 3 && Encoding.ASCII.GetString(exchange.Written).Contains("mail.example.invalid"), "Mail hostname travels as SOCKS DOMAINNAME without local DNS");
}

foreach (var type in new[] { typeof(TorImapClient), typeof(TorPop3Client), typeof(TorSmtpClient) })
{
    using var client = (MailKit.MailService)Activator.CreateInstance(type)!;
    var method = type.GetMethod("GetSslClientAuthenticationOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
    RemoteCertificateValidationCallback validate = (_, _, _, errors) => errors == SslPolicyErrors.None;
    var options = (SslClientAuthenticationOptions)method.Invoke(client, ["mail.example.invalid", validate])!;
    Check(!options.AllowTlsResume && options.CertificateChainPolicy is { DisableCertificateDownloads: true, RevocationMode: X509RevocationMode.NoCheck }
        && options.RemoteCertificateValidationCallback == validate, type.Name + " blocks TLS ticket reuse and out-of-tunnel issuer downloads while keeping validation");
}
using (var ordinary = new MailKit.Net.Smtp.SmtpClient())
    Check(ordinary.CheckCertificateRevocation, "Ordinary accounts retain certificate revocation checks");
using (var smtp = new TorSmtpClient { LocalDomain = "example.invalid" })
using (var exchange = new ScriptedStream(Encoding.ASCII.GetBytes("220 fixture ESMTP\r\n250 fixture\r\n")))
{
    try { await smtp.ConnectAsync(exchange, "mail.example.invalid", 25, SecureSocketOptions.StartTls); throw new Exception("Missing STARTTLS accepted"); }
    catch (NotSupportedException)
    {
        string sent = Encoding.ASCII.GetString(exchange.Written);
        Check(sent.Contains("EHLO example.invalid") && !sent.Contains("AUTH", StringComparison.OrdinalIgnoreCase),
            "Anonymous SMTP refuses a missing STARTTLS extension before credentials or message content");
    }
}
using (var key = RSA.Create())
using (var certificate = new CertificateRequest("CN=mail.example.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)))
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var server = Task.Run(async () =>
    {
        using var accepted = await listener.AcceptTcpClientAsync(budget.Token);
        using var tls = new SslStream(accepted.GetStream());
        try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, budget.Token); }
        catch (System.Security.Authentication.AuthenticationException) { }
        catch (IOException) { }
    });
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, budget.Token);
    using var smtp = new TorSmtpClient { LocalDomain = "example.invalid" };
    try { await smtp.ConnectAsync(tcp.GetStream(), "mail.example.invalid", 465, SecureSocketOptions.SslOnConnect, budget.Token); throw new Exception("Untrusted certificate accepted"); }
    catch (SslHandshakeException) { Check(true, "Anonymous SMTP rejects an untrusted TLS certificate in a real handshake"); }
    await server;
}

var diagnosis = new ConnectDiagnosis(DiagnosisKind.Other, "fixture", "incoming");
Check(ReferenceEquals(diagnosis, await ConnectTriageService.ProbeAsync(diagnosis, ConnectTriageService.MailProtocol.Imap,
    new string('a', 56) + ".onion", account: new MailAccountData())), "Onion diagnostics cannot enter the direct DNS path");
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var server = Task.Run(async () =>
    {
        using var accepted = await listener.AcceptTcpClientAsync(budget.Token);
        using var stream = accepted.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true, NewLine = "\r\n" };
        await writer.WriteLineAsync("220 fixture ESMTP");
        if (!(await reader.ReadLineAsync(budget.Token))!.StartsWith("EHLO ")) throw new Exception("Expected EHLO");
        await writer.WriteLineAsync("250-fixture\r\n250 AUTH PLAIN");
        if (!(await reader.ReadLineAsync(budget.Token))!.StartsWith("AUTH PLAIN")) throw new Exception("Expected fixture authentication");
        await writer.WriteLineAsync("235 authenticated");
        return await stream.ReadAsync(new byte[1], budget.Token);
    });
    var changing = new MailAccountData { EmailAddress = "route-fixture@example.invalid", IncomingUsername = "fixture",
        SmtpHost = "127.0.0.1", SmtpPort = ((IPEndPoint)listener.LocalEndpoint).Port, SmtpSecurity = ConnectionSecurity.None };
    using var client = await MailConnections.OpenSmtpAsync(changing, budget.Token, "fixture");
    Check(client.IsAuthenticated, "Ordinary direct SMTP authentication remains usable");
    AccountStore.Save(new MailAccountData { Id = changing.Id, TorOnly = true });
    Check(await server == 0 && !client.IsConnected, "Saving Tor-only policy closes an already open direct mail session immediately");
    try { using var stale = await MailConnections.OpenSmtpAsync(changing, budget.Token, "fixture"); throw new Exception("A stale account snapshot bypassed Tor policy"); }
    catch (TorUnavailableException) { Check(true, "An older account object cannot reopen a direct session after a Tor policy change"); }
}

using (var http = TorService.CreateIsolatedHttpClient("test-isolation"))
{
    try { await http.GetAsync("http://mail.example.invalid/"); throw new Exception("HTTP was accepted"); }
    catch (InvalidOperationException e) when (e.Message.Contains("require HTTPS")) { Check(true, "Reject plaintext API calls before contacting a proxy"); }
    var outer = (DelegatingHandler)typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(http)!;
    var transport = (SocketsHttpHandler)outer.InnerHandler!;
    Check(!transport.AllowAutoRedirect && !transport.UseCookies && transport.Credentials == null && transport.ActivityHeadersPropagator == null,
        "HTTP refuses redirects, shared cookies, ambient credentials and trace headers");
    Check(transport.PooledConnectionLifetime == TimeSpan.Zero && transport.PooledConnectionIdleTimeout == TimeSpan.Zero
        && http.DefaultRequestHeaders.ConnectionClose == true, "HTTP does not reuse sockets between requests");
    Check(transport.SslOptions is { AllowTlsResume: false, CertificateChainPolicy.DisableCertificateDownloads: true }, "HTTP uses offline certificate validation without TLS tickets");
}

var normal = new MailAccountData { EmailAddress = "ordinary@example.invalid" };
var gatedAccount = new MailAccountData { Protocol = IncomingProtocol.FastMail, FastMailProviderId = "mailtm" };
AccountStore.Save(gatedAccount);
Task<FastMailAccountOperations.Deletion> deleting;
using (var operation = await FastMailAccountOperations.EnterAsync(gatedAccount, default))
{
    deleting = FastMailAccountOperations.BeginDeleteAsync(gatedAccount.Id, default);
    Check(operation.Token.IsCancellationRequested && !deleting.IsCompleted, "Deletion cancels active Fast Mail work and waits for its transaction gate");
}
using (var deletion = await deleting) deletion.Complete();
try { using var blocked = await FastMailAccountOperations.EnterAsync(gatedAccount, default); throw new Exception("Deleted account work resumed"); }
catch (OperationCanceledException) { Check(true, "Completed deletion rejects further Fast Mail network and archive work"); }
var anonymous = new MailAccountData { EmailAddress = "anonymous@example.invalid", TorOnly = true };
var fast = new MailAccountData { EmailAddress = "temporary@example.invalid", Protocol = IncomingProtocol.FastMail, TorOnly = false };
Check(fast.TorOnly, "Fast Mail enforces Tor before persistence and synchronization");
Settings.ExternalImages.Set(ExternalImagesPolicy.AllowAlways);

string Render(MailAccountData account, string body, MimePart? inline = null)
{
    var message = new MimeMessage { Subject = "fixture", Body = new TextPart("html") { Text = body } };
    if (inline != null) message.Body = new Multipart("related") { message.Body, inline };
    using var buffer = new MemoryStream();
    message.WriteTo(buffer);
    MessageStore.SaveFullMessage(account.Id, "INBOX", 1, buffer.ToArray());
    return MailBodyRenderer.Render(account, "INBOX", new MailMessageSummary { Uid = 1 }, allowRemoteImages: true, foldQuotedText: false)!.Html;
}

string[] probes =
[
    "<img src='https://tracker.invalid/open'>",
    "<img src='&#x68;ttps://tracker.invalid/open'>",
    "<img src='//tracker.invalid/open' srcset='https://tracker.invalid/open 2x'>",
    "<picture><source srcset='https://tracker.invalid/open'><img src=x></picture>",
    "<style>@import 'https://tracker.invalid/open';</style><p style='background:url(https://tracker.invalid/open)'>Text</p>",
    "<p style='background-image:u\\72l(https://tracker.invalid/open)'>Text</p>",
    "<svg><image href='https://tracker.invalid/open'/><a xlink:href='https://tracker.invalid/open'>x</a></svg>",
    "<video poster='https://tracker.invalid/open'><source src='https://tracker.invalid/open'></video>",
    "<meta http-equiv=refresh content='0;url=https://tracker.invalid/open'><link rel=preconnect href='https://tracker.invalid/open'>",
    "<form action='https://tracker.invalid/open'><input type=image src='https://tracker.invalid/open'></form>",
    "<a href='https://tracker.invalid/open' ping='https://tracker.invalid/ping' target='_self'>link</a>",
    "<a href='jav&#x61;script:alert(1)'>script</a><iframe srcdoc='<img src=https://tracker.invalid/open>'></iframe>",
    "<noscript><img src='https://tracker.invalid/open'></noscript><math><mtext><img src='https://tracker.invalid/open'></mtext></math>",
    "<img src='data:image/svg+xml;base64,PHN2Zy8+' onerror='alert(1)'>",
    "<img src='https://tracker.invalid/open' title='unterminated"
];
foreach (var account in new[] { anonymous, fast })
foreach (string probe in probes)
{
    string rendered = Render(account, probe);
    var tokenizer = new HtmlTokenizer(new StringReader(rendered));
    bool safe = rendered.Contains("default-src 'none'") && rendered.Contains("no-referrer");
    while (tokenizer.ReadNextToken(out var token))
    {
        if (token is not HtmlTagToken tag || tag.IsEndTag) continue;
        safe &= tag.Name is not ("script" or "iframe" or "svg" or "math" or "form" or "link" or "video" or "source" or "base");
        foreach (var attr in tag.Attributes)
        {
            safe &= !attr.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase);
            safe &= attr.Name is not ("href" or "srcset" or "ping" or "poster" or "background" or "action" or "srcdoc");
            if (attr.Name == "src") safe &= attr.Value?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true
                && attr.Value?.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase) == false;
        }
    }
    Check(safe, (account == fast ? "Fast Mail" : "Tor") + " passive HTML fixture " + Array.IndexOf(probes, probe));
}
Check(Render(normal, "<a href='https://example.invalid/'>normal</a><img src='https://example.invalid/image'>")
    .Contains("href=\"https://example.invalid/\""), "Ordinary accounts retain explicit external navigation");
Check(Render(normal, "<img src='https://example.invalid/image'>").Contains("src=\"https://example.invalid/image\""), "Ordinary accounts retain the show-images override");
Check(!Render(normal, "<style/>&lt;/style&gt;&lt;meta http-equiv='refresh' content='0;url=https://tracker.invalid/open'&gt;")
    .Contains("<meta http-equiv=\"refresh\"", StringComparison.OrdinalIgnoreCase), "Self-closing style cannot turn decoded text into active HTML");
Check(Render(normal, "<p style='background-image:url(https://example.invalid/image)'>Text</p>")
    .Contains("background-image:url(https://example.invalid/image)"), "Ordinary accounts retain opted-in inline backgrounds");
var inlinePart = new MimePart("image", "png")
{
    ContentId = "inline-test",
    Content = new MimeContent(new MemoryStream(Convert.FromBase64String("iVBORw0KGgo=")))
};
Check(Render(anonymous, "<b>Readable</b><img src='cid:inline-test'>", inlinePart).Contains("src=\"data:image/png;base64,"), "Anonymous mail retains embedded raster images and readable markup");

Settings.Signature.Set("Real identity signature");
Check(!ComposeService.BuildNew(anonymous).Body.Contains("Real identity"), "Anonymous accounts do not inherit the global signature");
Check(ComposeService.BuildNew(normal).Body.Contains("Real identity"), "Ordinary signature inheritance remains intact");
var retained = ComposeService.BuildNew(normal);
normal.TorOnly = true;
ComposeService.SaveDraft(retained);
Check(!retained.Body.Contains("Real identity"), "Existing drafts drop inherited signatures after the same account becomes anonymous");
normal.TorOnly = false;
Check(!SignatureService.PreviewDocument("<img src='https://tracker.invalid/open'><script>alert(1)</script>", blockRemoteContent: true)
    .Contains("tracker.invalid"), "Anonymous signature previews use the same offline renderer");
var draft = ComposeService.BuildNew(normal);
draft.Body += "\n\nOn 2026-11-01 13:45, Sender <sender@example.invalid> wrote:\n> Original body";
ComposeService.SwitchSendingAccount(draft, anonymous);
Check(!draft.Body.Contains("Real identity") && draft.Body.Contains(" UTC, Sender") && draft.Body.Contains("> Original body"), "Switching to an anonymous sender removes inherited identity and normalizes quote timezone");

using (var outgoing = new MimeMessage())
{
    outgoing.From.Add(MailboxAddress.Parse("old-identity@example.invalid"));
    outgoing.Sender = MailboxAddress.Parse("old-sender@example.invalid");
    outgoing.Headers.Add("X-Originating-IP", "192.0.2.1");
    outgoing.Headers.Add("X-LovelyDraft-Attachments", "C:\\Private\\document.txt");
    outgoing.Headers.Add("Received", "from private-host");
    outgoing.Headers.Add("Disposition-Notification-To", "old-identity@example.invalid");
    var attachment = new MimePart("application", "octet-stream") { ContentDisposition = new ContentDisposition("attachment")
        { CreationDate = DateTimeOffset.Now, ModificationDate = DateTimeOffset.Now, ReadDate = DateTimeOffset.Now, Size = 123 } };
    outgoing.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Unchanged body" }, attachment };
    string domain = DirectMxSmtpService.SanitizeMessageForAnonymity(anonymous, outgoing);
    Check(domain == "example.invalid" && outgoing.From.Mailboxes.Single().Address == anonymous.EmailAddress && outgoing.Sender == null,
        "Anonymous sender metadata comes from the selected sending account");
    Check(outgoing.Date.Offset == TimeSpan.Zero && outgoing.MessageId?.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase) == false, "Anonymous Date and Message-Id omit local timezone and computer name");
    Check(!outgoing.Headers.Any(h => h.Field.StartsWith("X-")) && outgoing.Headers["Received"] == null && outgoing.Headers["Disposition-Notification-To"] == null,
        "Anonymous send removes trace, draft-path and receipt headers");
    Check(attachment.ContentDisposition is { CreationDate: null, ModificationDate: null, ReadDate: null, Size: null }
        && outgoing.TextBody == "Unchanged body", "Anonymous MIME removes file timestamps while preserving message content");
}
using (var forwarded = new MimeMessage { MessageId = "original@example.invalid", Date = DateTimeOffset.UtcNow })
using (var outgoing = new MimeMessage())
{
    var originalBody = new Multipart("mixed") { new TextPart("plain") { Text = "Original authenticated content" } };
    originalBody.Boundary = "original-boundary";
    forwarded.Body = originalBody;
    byte[] Before() { using var bytes = new MemoryStream(); forwarded.WriteTo(bytes); return bytes.ToArray(); }
    byte[] original = Before();
    outgoing.Body = new Multipart("mixed") { new TextPart("plain") { Text = "Forward" }, new MessagePart { Message = forwarded } };
    DirectMxSmtpService.SanitizeMessageForAnonymity(anonymous, outgoing);
    Check(original.SequenceEqual(Before()), "Forwarded message attachments retain their exact original MIME bytes");
}

var parseMx = typeof(DirectMxSmtpService).GetMethod("ParseMxResponse", BindingFlags.NonPublic | BindingFlags.Static)!;
IReadOnlyList<string> Mx(string json) => (IReadOnlyList<string>)parseMx.Invoke(null, ["example.invalid", json])!;
Check(Mx("{\"Status\":0,\"Answer\":[{\"type\":15,\"data\":\"20 second.example.invalid.\"},{\"type\":15,\"data\":\"10 first.example.invalid.\"}]}")
    .SequenceEqual(new[] { "first.example.invalid", "second.example.invalid" }), "MX responses retain server preference ordering");
Check(Mx("{\"Status\":0}").SequenceEqual(new[] { "example.invalid" }), "A successful empty MX answer retains the RFC implicit-host fallback");
foreach (string invalid in new[] { "{\"Status\":3}", "{\"Status\":2}", "{\"Status\":0,\"Answer\":[{\"type\":15,\"data\":\"0 .\"}]}" })
{
    try { _ = Mx(invalid); throw new Exception("Unsafe DNS fallback accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException or IOException)
    { Check(true, "DNS error or explicit null MX cannot fall back to another mail host: " + invalid); }
}

#if !DEBUG
Check(typeof(TorService).Assembly.GetType("MyLovelyMail.MainProject.Diagnostics.DebugApi") == null, "Release assemblies exclude the local development API");
#endif

Logger.ActivateLogging = true;
string? log = Logger.Log("subject='private subject'; user='privateuser'; sid_token=secret-token address=private@example.invalid ip=192.0.2.22", PrintToConsole: false);
Check(log is not null && !log.Contains("private") && !log.Contains("secret-token") && !log.Contains("192.0.2.22"), "Log sinks redact subjects, usernames, session tokens, addresses and IPs");
Logger.ActivateLogging = false;
if (args.Length == 2 && args[1] == "--live-providers")
{
    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    Settings.CredentialVaultMode.Set(VaultMode.MasterPassword);
    CredentialVault.SetMasterPassword(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
    try
    {
        foreach (var provider in new IFastMailProvider[] { new GuerrillaMailProvider(), new MailTmProvider() })
        {
            Console.WriteLine("LIVE: " + provider.DisplayName + " provisioning and two independent inbox checks over Tor.");
            string key = Guid.NewGuid().ToString("N");
            var domains = await provider.GetDomainsAsync(key, budget.Token);
            var created = await provider.ProvisionAccountAsync(new FastMailProvisionRequest(key, null, domains[0], null, true), budget.Token);
            var account = new MailAccountData { Id = key, Protocol = IncomingProtocol.FastMail,
                FastMailProviderId = provider.Id, EmailAddress = created.EmailAddress, IncomingUsername = created.Username };
            AccountStore.Save(account);
            if (provider.SupportsPassword) CredentialVault.SetPassword(key, created.PasswordOrSecret);
            _ = await provider.ListMessagesAsync(account, budget.Token);
            _ = await provider.ListMessagesAsync(account, budget.Token);
            Check(true, provider.DisplayName + " fresh-session provisioning and inbox access");
        }
    }
    finally { TorProcess.Stop(); }
}
Console.WriteLine($"All {passed} checks passed. Disposable data: {AppPaths.Root}");

sealed class ScriptedStream(byte[] input) : Stream
{
    readonly MemoryStream incoming = new(input);
    readonly MemoryStream outgoing = new();
    public byte[] Written => outgoing.ToArray();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => incoming.Read(buffer, offset, count);
    public override void Write(byte[] buffer, int offset, int count) => outgoing.Write(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => incoming.ReadAsync(buffer, ct);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => outgoing.WriteAsync(buffer, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) { incoming.Dispose(); outgoing.Dispose(); } base.Dispose(disposing); }
}
