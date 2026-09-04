using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Proxy;

namespace MyLovelyMail.MainProject.Services.Tor
{
    /// <summary>A SOCKS5 exchange that did not go the way the protocol says it must.</summary>
    public sealed class SocksProtocolException(string message) : Exception(message);

    /// <summary>The proxy answered the request, and its answer was a refusal.</summary>
    public sealed class SocksReplyException(byte replyCode, string message) : Exception(message)
    {
        public byte ReplyCode { get; } = replyCode;
    }

    /// <summary>
    /// The SOCKS5 wire protocol as Tor speaks it. Two things here are not what a general-purpose
    /// SOCKS client would do, and they are the reason this file exists rather than only leaning on
    /// MailKit's <see cref="Socks5Client"/>:
    /// <list type="bullet">
    /// <item>A host name is ALWAYS sent as address type 0x03 (DOMAINNAME). Resolving it locally
    /// first would put the mail server's name in a DNS query that leaves this machine in the
    /// clear — the account would still be tunnelled, and its provider would still be public.</item>
    /// <item>Tor's RESOLVE extension (command 0xF0) is implemented, which is the one answer
    /// available on a local socket to "is the thing on this port actually Tor?". A plain SOCKS5
    /// proxy has to reject an unknown command with 0x07; Tor resolves the name and returns an
    /// address.</item>
    /// </list>
    /// </summary>
    public static class TorSocks5
    {
        public const byte Version = 0x05;

        public const byte MethodNoAuth = 0x00;
        public const byte MethodUsernamePassword = 0x02;
        public const byte MethodNoneAcceptable = 0xFF;

        public const byte CommandConnect = 0x01;

        /// <summary>Tor's own SOCKS extension: resolve a name over the Tor network instead of opening a stream.</summary>
        public const byte CommandTorResolve = 0xF0;

        public const byte AddressTypeIPv4 = 0x01;
        public const byte AddressTypeDomain = 0x03;
        public const byte AddressTypeIPv6 = 0x04;

        public const byte ReplySucceeded = 0x00;
        public const byte ReplyGeneralFailure = 0x01;
        public const byte ReplyHostUnreachable = 0x04;
        public const byte ReplyCommandNotSupported = 0x07;

        /// <summary>Longest host name a SOCKS5 DOMAINNAME field can carry (its length is a single byte).</summary>
        public const int MaxDomainLength = 255;

        /// <summary>The username/password sub-negotiation of RFC 1929 has a version byte of its own.</summary>
        const byte UsernamePasswordVersion = 0x01;

        static string DescribeReply(byte reply) => reply switch
        {
            0x00 => "succeeded",
            0x01 => "general SOCKS server failure",
            0x02 => "connection not allowed by ruleset",
            0x03 => "network unreachable",
            0x04 => "host unreachable",
            0x05 => "connection refused",
            0x06 => "TTL expired",
            0x07 => "command not supported",
            0x08 => "address type not supported",
            _ => $"unknown reply code 0x{reply:X2}"
        };

        /// <summary>
        /// Greeting plus, when <paramref name="credentials"/> is given, the RFC 1929 login. Tor does
        /// not check the credentials: it uses them to keep streams on separate circuits (its
        /// IsolateSOCKSAuth default), which is what lets one account's traffic stay off another's.
        /// </summary>
        public static async Task HandshakeAsync(Stream stream, NetworkCredential? credentials, CancellationToken cancellationToken)
        {
            byte[] greeting = credentials == null
                ? [Version, 1, MethodNoAuth]
                : [Version, 2, MethodUsernamePassword, MethodNoAuth];

            await stream.WriteAsync(greeting, cancellationToken);
            await stream.FlushAsync(cancellationToken);

            byte[] selection = new byte[2];
            await stream.ReadExactlyAsync(selection, cancellationToken);

            if (selection[0] != Version)
                throw new SocksProtocolException($"The proxy answered with SOCKS version 0x{selection[0]:X2}, not SOCKS5.");

            switch (selection[1])
            {
                case MethodNoAuth:
                    return;

                case MethodUsernamePassword when credentials != null:
                    await AuthenticateAsync(stream, credentials, cancellationToken);
                    return;

                case MethodNoneAcceptable:
                    throw new SocksProtocolException("The proxy accepted none of the offered authentication methods.");

                default:
                    throw new SocksProtocolException($"The proxy selected authentication method 0x{selection[1]:X2}, which this client does not implement.");
            }
        }

        static async Task AuthenticateAsync(Stream stream, NetworkCredential credentials, CancellationToken cancellationToken)
        {
            byte[] user = Encoding.UTF8.GetBytes(credentials.UserName ?? string.Empty);
            byte[] password = Encoding.UTF8.GetBytes(credentials.Password ?? string.Empty);
            if (user.Length > 255 || password.Length > 255)
                throw new SocksProtocolException("SOCKS5 username/password authentication allows at most 255 bytes each.");

            var request = new List<byte>(3 + user.Length + password.Length) { UsernamePasswordVersion, (byte)user.Length };
            request.AddRange(user);
            request.Add((byte)password.Length);
            request.AddRange(password);

            await stream.WriteAsync(request.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            byte[] reply = new byte[2];
            await stream.ReadExactlyAsync(reply, cancellationToken);
            if (reply[1] != 0x00)
                throw new SocksProtocolException($"The proxy rejected the SOCKS5 username/password login (status 0x{reply[1]:X2}).");
        }

        /// <summary>
        /// Sends one SOCKS5 request for <paramref name="host"/> and returns the address the proxy
        /// bound. The host goes on the wire verbatim as DOMAINNAME whenever it is not already a
        /// literal IP — that, and nothing else, is what keeps the name out of a local DNS query.
        /// </summary>
        public static async Task<IPAddress?> SendRequestAsync(Stream stream, byte command, string host, int port,
            CancellationToken cancellationToken)
        {
            var request = new List<byte>(262) { Version, command, 0x00 };

            if (IPAddress.TryParse(host, out var literal))
            {
                request.Add(literal.AddressFamily == AddressFamily.InterNetworkV6 ? AddressTypeIPv6 : AddressTypeIPv4);
                request.AddRange(literal.GetAddressBytes());
            }
            else
            {
                byte[] name = Encoding.ASCII.GetBytes(host);
                if (name.Length is 0 or > MaxDomainLength)
                    throw new SocksProtocolException($"'{host}' does not fit in a SOCKS5 DOMAINNAME field.");
                request.Add(AddressTypeDomain);
                request.Add((byte)name.Length);
                request.AddRange(name);
            }

            request.Add((byte)(port >> 8));
            request.Add((byte)(port & 0xFF));

            await stream.WriteAsync(request.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);

            byte[] head = new byte[4];
            await stream.ReadExactlyAsync(head, cancellationToken);

            if (head[0] != Version)
                throw new SocksProtocolException($"The proxy answered with SOCKS version 0x{head[0]:X2}, not SOCKS5.");
            if (head[1] != ReplySucceeded)
                throw new SocksReplyException(head[1], $"The proxy refused: {DescribeReply(head[1])}.");

            return await ReadBoundAddressAsync(stream, head[3], cancellationToken);
        }

        /// <summary>Reads the BND.ADDR/BND.PORT tail of a reply and hands back the address when it is a literal one.</summary>
        static async Task<IPAddress?> ReadBoundAddressAsync(Stream stream, byte addressType, CancellationToken cancellationToken)
        {
            IPAddress? bound = null;
            switch (addressType)
            {
                case AddressTypeIPv4:
                {
                    byte[] address = new byte[4];
                    await stream.ReadExactlyAsync(address, cancellationToken);
                    bound = new IPAddress(address);
                    break;
                }
                case AddressTypeIPv6:
                {
                    byte[] address = new byte[16];
                    await stream.ReadExactlyAsync(address, cancellationToken);
                    bound = new IPAddress(address);
                    break;
                }
                case AddressTypeDomain:
                {
                    byte[] length = new byte[1];
                    await stream.ReadExactlyAsync(length, cancellationToken);
                    byte[] name = new byte[length[0]];
                    await stream.ReadExactlyAsync(name, cancellationToken);
                    break;
                }
                default:
                    throw new SocksProtocolException($"The proxy replied with address type 0x{addressType:X2}.");
            }

            byte[] boundPort = new byte[2];
            await stream.ReadExactlyAsync(boundPort, cancellationToken);
            return bound;
        }
    }

    /// <summary>
    /// A SOCKS5 proxy client for MailKit that speaks <see cref="TorSocks5"/>. MailKit ships one of
    /// its own and it is used first; this exists as the second way to reach the same Tor SOCKS
    /// port, so a fault in one client implementation cannot be the thing that takes an account
    /// offline. Both are equally tunnelled — neither ever opens a socket to the mail server.
    /// </summary>
    public sealed class TorSocks5Client(string proxyHost, int proxyPort, NetworkCredential? credentials = null) : IProxyClient
    {
        readonly NetworkCredential? credentials = credentials;

        public string ProxyHost { get; } = proxyHost;
        public int ProxyPort { get; } = proxyPort;
        public NetworkCredential ProxyCredentials => credentials!;
        public IPEndPoint? LocalEndPoint { get; set; }

        public Stream Connect(string host, int port, CancellationToken cancellationToken = default) =>
            Task.Run(() => ConnectAsync(host, port, cancellationToken), cancellationToken).GetAwaiter().GetResult();

        public Stream Connect(string host, int port, int timeout, CancellationToken cancellationToken = default) =>
            Task.Run(() => ConnectAsync(host, port, timeout, cancellationToken), cancellationToken).GetAwaiter().GetResult();

        /// <summary>
        /// Async, not a token-building wrapper around the other overload: a non-async method would
        /// dispose the linked source the moment it handed the task back, cancelling the very timer
        /// that is the timeout.
        /// </summary>
        public async Task<Stream> ConnectAsync(string host, int port, int timeout, CancellationToken cancellationToken = default)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            return await ConnectAsync(host, port, budget.Token);
        }

        public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            NetworkStream? stream = null;
            try
            {
                if (LocalEndPoint != null) socket.Bind(LocalEndPoint);
                await socket.ConnectAsync(ProxyHost, ProxyPort, cancellationToken);

                stream = new NetworkStream(socket, ownsSocket: true);
                await TorSocks5.HandshakeAsync(stream, credentials, cancellationToken);
                await TorSocks5.SendRequestAsync(stream, TorSocks5.CommandConnect, host, port, cancellationToken);
                return stream;
            }
            catch
            {
                // The half-open socket is this method's to close: MailKit only ever sees a stream
                // it can use, and a failed attempt leaves no descriptor behind for the retry.
                if (stream != null) await stream.DisposeAsync();
                else socket.Dispose();
                throw;
            }
        }
    }
}
