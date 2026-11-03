# Anonymous mail boundaries

`MailConnections` routes Tor accounts through the authenticated `TorSocks5Client`; each TCP
connection receives independent cryptographic SOCKS credentials. A no-authentication selection
is refused when credentials are supplied. Mail server names travel as SOCKS domain names. Saving
or loading a Tor-required account closes tracked direct sessions, and older account objects cannot
reopen them. Fast Mail requires Tor even before its account is persisted.

`TorMailTls.Harden` is shared by HTTP, IMAP, POP3 and SMTP. It disables TLS tickets, online
revocation and issuer certificate downloads, while retaining ordinary chain, server-name, expiry
and server-purpose validation. Anonymous automatic authentication excludes workstation-based
mechanisms. Direct MX delivery requires STARTTLS and validated certificates; DNS failures and
explicit null MX answers do not become implicit-host delivery attempts.

HTTP uses a new SOCKS login per socket, no pooling, no ambient credentials/trace headers and no
automatic redirects. Only HTTPS API calls are accepted. Cookies, when required by Guerrilla,
belong to one operation and are discarded with that client. Mail.tm requests a bearer token per
operation. Independent circuits do not imply different exit addresses; no third-party exit-IP
probe is attached to every connection.

`MailBodyRenderer.Html` rebuilds passive markup using MimeKit's tokenizer. Anonymous links are
copyable data attributes, with no navigable href. Remote CSS/URLs, foreign markup, active tags,
event attributes and unsafe embedded formats are removed. Raster CID images remain available.
The sandbox, document policy, disabled DNS prefetch and no-referrer policy provide additional
boundaries. Anonymous signature previews reuse this renderer. HTML export inserts its header
inside the document body so the policy remains in the head. Raw EML export preserves original
message bytes and is not a sanitized HTML export.

Anonymous outgoing metadata removes local timezone, computer identity, draft paths, transport
trace headers and attachment disposition timestamps. Attached original messages retain their
bytes so their existing cryptographic authentication is not invalidated. Logs omit anonymous
subjects at their call sites and redact address, IP and credential fields before output.

Scheduled anonymous checks have randomized order/spacing and interval variation. POP3 uses one
connection for these accounts; IMAP IDLE reconnects after a randomized bounded connection lifetime.
Fast Mail operations are serialized per account. Deletion cancels and waits for complete sync,
history-fetch and send/archive transactions before taking its recovery snapshot.

## Evidence and limits

`tests/AnonymityChecks` exercises protocol downgrade rejection, real TLS certificate rejection,
saved policy changes on an actual direct SMTP session, malicious markup representations,
ordinary-account controls, outgoing MIME, credential generation and Release API exclusion.
Run its executable/output under a `NewTemp` root; it refuses an unguarded data location.

The optional live provider mode provisions disposable accounts and checks independent inbox
sessions through Tor. The existing seven-step Fast Mail suite also attempts direct delivery.
A remote server's invalid certificate is a delivery failure, not a reason to allow insecure TLS.
These controls do not establish an absolute network anonymity guarantee or inspect identifying
text and metadata deliberately supplied inside arbitrary user attachments.

References: [Tor isolation](https://spec.torproject.org/path-spec/stream-isolation.html),
[issuer downloads](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.disablecertificatedownloads),
[MimeKit's incomplete legacy HTML filter](https://mimekit.net/docs/html/P_MimeKit_Text_HtmlToHtml_FilterHtml.htm).
