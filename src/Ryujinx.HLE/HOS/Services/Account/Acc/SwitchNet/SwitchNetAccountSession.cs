using Ryujinx.Common.Logging;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet
{
    /// <summary>
    /// Holds the one SwitchNet login the whole emulator shares, and turns the
    /// configured settings into a client.
    /// </summary>
    /// <remarks>
    /// One login per process rather than one per account service instance: the id
    /// token belongs to the SwitchNet account, not to the object that happened to ask
    /// for it, and a game that opens several account sessions would otherwise log in
    /// several times over for the same answer.
    /// </remarks>
    static class SwitchNetAccountSession
    {
        /// <summary>The port a SwitchNet edge is reached on when the address names none.</summary>
        private const int DefaultPort = 443;

        /// <summary>
        /// How long <see cref="TryGetIdToken"/> will block the guest waiting for a
        /// login that has not happened yet.
        /// </summary>
        /// <remarks>
        /// LoadIdTokenCache is a synchronous IPC call, so a wait here is a wait the
        /// game sees. Real hardware does the fetch in EnsureIdTokenCacheAsync and this
        /// does too -- this bound only covers a game that skips straight to loading the
        /// cache, where a short stall beats handing back a token that will be rejected.
        /// </remarks>
        private static readonly TimeSpan _blockingWait = TimeSpan.FromSeconds(10);

        private static readonly object _lock = new();

        private static SwitchNetAccountClient _client;

        /// <summary>The settings the current client was built from, to notice a change.</summary>
        private static string _signature;

        /// <summary>Whether the last login attempt failed, so it is only logged once.</summary>
        private static string _lastFailure;

        /// <summary>
        /// Whether a success has ever been logged. Without this, ReportSuccess's
        /// "only log a recovery" rule means the very first successful login -- the one
        /// moment an operator most wants confirmation -- is silent, and silence is
        /// indistinguishable from SwitchNet never having been configured at all.
        /// </summary>
        private static bool _hasReportedSuccess;

        /// <summary>
        /// Reports whether SwitchNet login is configured, and returns a client for it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The three settings ARE the switch. There is no separate on/off beside them,
        /// because a filled-in server and credentials sitting next to an "off" is a
        /// state where the operator has said two contradictory things and one of them
        /// wins silently.
        /// </para>
        /// <para>
        /// Leaving them unset is a working configuration rather than an error.
        /// Redirecting the guest's own traffic -- Settings, Network, Private Nintendo
        /// Servers -- is a separate and independent thing: a private server that does
        /// not check identity needs that redirect and no login at all. This matters
        /// only when the server verifies the id token a game presents to it, which
        /// SwitchNet does.
        /// </para>
        /// </remarks>
        public static bool TryGetClient(HleConfiguration configuration, out SwitchNetAccountClient client)
        {
            client = null;

            if (configuration == null)
            {
                return false;
            }

            string server = configuration.SwitchNetServer?.Trim();
            string username = configuration.SwitchNetUsername?.Trim();
            string password = configuration.SwitchNetPassword;

            bool hasServer = !string.IsNullOrEmpty(server);
            bool hasUsername = !string.IsNullOrEmpty(username);
            bool hasPassword = !string.IsNullOrEmpty(password);

            if (!hasServer && !hasUsername && !hasPassword)
            {
                // All three empty is the deliberate off state -- nothing to warn about.
                return false;
            }

            if (!hasServer || !hasUsername || !hasPassword)
            {
                // One or two filled in is not a state anyone chooses on purpose: it is
                // the state right before finishing the settings page, or a field that
                // silently failed to save. Left as silent as "fully off" above, a
                // half-filled config and no SwitchNet account at all look identical in
                // the log -- which is exactly the question an operator staring at a
                // game stuck "connecting" cannot otherwise answer.
                string missing = (!hasServer ? "Server, " : "")
                    + (!hasUsername ? "Username, " : "")
                    + (!hasPassword ? "Password, " : "");
                missing = missing[..^2];

                WarnOnce($"SwitchNet Account is missing {missing} -- " +
                    "all three fields are needed, or leave all three empty to turn it off");

                return false;
            }

            if (!TryParseEndPoint(server, out EndPoint endPoint))
            {
                WarnOnce($"server address \"{server}\" is not a host or host:port");

                return false;
            }

            lock (_lock)
            {
                // The password is part of the signature so that correcting a typo takes
                // effect, but it is never logged or otherwise rendered.
                string signature = $"{endPoint}|{username}|{password.GetHashCode()}";

                if (_client == null || _signature != signature)
                {
                    _client?.Dispose();
                    _client = new SwitchNetAccountClient(new SwitchNetAccountOptions
                    {
                        ServerEndPoint = endPoint,
                        Username = username,
                        Password = password,
                    });
                    _signature = signature;
                    _lastFailure = null;
                    // A new configuration deserves its own confirmation when it works,
                    // not silence borrowed from whatever config succeeded before it.
                    _hasReportedSuccess = false;
                }

                client = _client;
            }

            return true;
        }

        /// <summary>
        /// Logs in if needed, without blocking the caller. For EnsureIdTokenCacheAsync,
        /// which is the call real hardware does this work in.
        /// </summary>
        public static async Task PrefetchAsync(HleConfiguration configuration, CancellationToken cancellationToken)
        {
            if (!TryGetClient(configuration, out SwitchNetAccountClient client))
            {
                return;
            }

            try
            {
                await client.GetIdTokenAsync(cancellationToken).ConfigureAwait(false);

                ReportSuccess();
            }
            catch (SwitchNetLoginException e)
            {
                WarnOnce(e.Message);
            }
            catch (OperationCanceledException)
            {
                // The guest gave up on the async context. Nothing to say.
            }
        }

        /// <summary>
        /// Returns the id token for the configured SwitchNet account, or false when
        /// there is none to be had.
        /// </summary>
        public static bool TryGetIdToken(HleConfiguration configuration, out string idToken)
        {
            idToken = null;

            if (!TryGetClient(configuration, out SwitchNetAccountClient client))
            {
                return false;
            }

            try
            {
                using CancellationTokenSource cts = new(_blockingWait);

                idToken = client.GetIdTokenAsync(cts.Token).GetAwaiter().GetResult();

                ReportSuccess();

                return true;
            }
            catch (SwitchNetLoginException e)
            {
                WarnOnce(e.Message);
            }
            catch (OperationCanceledException)
            {
                WarnOnce($"login did not finish within {_blockingWait.TotalSeconds:0}s");
            }

            return false;
        }

        /// <summary>
        /// Parses "host", "host:port", "1.2.3.4" or "[::1]:443" into an endpoint.
        /// </summary>
        /// <remarks>
        /// A <see cref="DnsEndPoint"/> even for a literal address, so a hostname and an
        /// IP take the same path and .NET does the resolving either way.
        /// </remarks>
        internal static bool TryParseEndPoint(string address, out EndPoint endPoint)
        {
            endPoint = null;

            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            address = address.Trim();

            // An operator who pastes a URL means the host inside it.
            if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri url))
                {
                    return false;
                }

                endPoint = new DnsEndPoint(url.Host, url.IsDefaultPort ? DefaultPort : url.Port);

                return true;
            }

            // A bracketed IPv6 literal, with or without a port.
            if (address.StartsWith('['))
            {
                int close = address.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }

                string host = address[1..close];
                string rest = address[(close + 1)..];

                if (rest.Length == 0)
                {
                    endPoint = new DnsEndPoint(host, DefaultPort);

                    return true;
                }
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out int bracketedPort) && IsPort(bracketedPort))
                {
                    endPoint = new DnsEndPoint(host, bracketedPort);

                    return true;
                }

                return false;
            }

            // A bare IPv6 literal has several colons and no port; anything else with one
            // colon is host:port.
            if (IPAddress.TryParse(address, out IPAddress literal))
            {
                endPoint = new DnsEndPoint(literal.ToString(), DefaultPort);

                return true;
            }

            int separator = address.LastIndexOf(':');
            if (separator < 0)
            {
                endPoint = new DnsEndPoint(address, DefaultPort);

                return true;
            }

            string namePart = address[..separator];
            if (namePart.Length == 0 || !int.TryParse(address[(separator + 1)..], out int port) || !IsPort(port))
            {
                return false;
            }

            endPoint = new DnsEndPoint(namePart, port);

            return true;
        }

        private static bool IsPort(int port) => port > 0 && port <= 65535;

        /// <summary>
        /// Logs a login failure once per distinct reason.
        /// </summary>
        /// <remarks>
        /// A game asks for the id token repeatedly, so an unconditional log would repeat
        /// the same line until it filled the log -- and the first occurrence, which is
        /// the one next to whatever the operator just changed, would scroll away.
        /// </remarks>
        private static void WarnOnce(string reason)
        {
            lock (_lock)
            {
                if (_lastFailure == reason)
                {
                    return;
                }

                _lastFailure = reason;
            }

            Logger.Warning?.Print(LogClass.ServiceAcc,
                $"SwitchNet login failed: {reason}. The game will fall back to a locally " +
                "generated id token, which a SwitchNet game server will reject.");
        }

        private static void ReportSuccess()
        {
            lock (_lock)
            {
                if (_lastFailure == null && _hasReportedSuccess)
                {
                    return;
                }

                _lastFailure = null;
                _hasReportedSuccess = true;
            }

            Logger.Info?.Print(LogClass.ServiceAcc, "SwitchNet login succeeded.");
        }
    }
}
