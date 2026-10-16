using System.Net;
using System.Net.Sockets;

namespace MyLovelyMail.MainProject.Services.Tor
{
    public static partial class TorService
    {
        #region Verification

        /// <summary>
        /// Asks the SOCKS port to resolve a name using Tor's RESOLVE command (0xF0). This is the
        /// one question whose answer separates Tor from any other SOCKS5 proxy: a general-purpose
        /// proxy must reject the unknown command with 0x07, while Tor resolves the name over a
        /// circuit and returns an address. A pass therefore proves three things at once — the port
        /// is Tor, Tor has bootstrapped, and it can reach the network.
        /// </summary>
        public static async Task<TorVerification> VerifyAsync(CancellationToken cancellationToken, TorEndpoint? endpoint = null,
            string probeHost = "check.torproject.org")
        {
            if (endpoint == null)
            {
                try
                {
                    // Discovery verifies on its own way through, so this branch is the UI's
                    // "check it now" button rather than anything the connection path calls.
                    endpoint = await RequireEndpointAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    return LastVerification = new TorVerification(false, ex.Message);
                }
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(VerifyTimeout);
            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                await socket.ConnectAsync(endpoint.Host, endpoint.Port, budget.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);

                await TorSocks5.HandshakeAsync(stream, IsolationCredentials("verify"), budget.Token);
                var address = await TorSocks5.SendRequestAsync(stream, TorSocks5.CommandTorResolve, probeHost, 0, budget.Token);

                return LastVerification = new TorVerification(true,
                    $"{endpoint} resolved {probeHost} over Tor" + (address == null ? "." : $" to {address}."),
                    ProvenNotTor: false, ResolvedAddress: address);
            }
            catch (SocksReplyException ex) when (ex.ReplyCode == TorSocks5.ReplyCommandNotSupported)
            {
                return LastVerification = new TorVerification(false,
                    $"{endpoint} is a SOCKS5 proxy but rejected Tor's RESOLVE command — it is not Tor.", ProvenNotTor: true);
            }
            catch (Exception ex)
            {
                return LastVerification = new TorVerification(false, $"{endpoint} did not answer the Tor check: {ex.Message}");
            }
        }

        #endregion
    }
}
