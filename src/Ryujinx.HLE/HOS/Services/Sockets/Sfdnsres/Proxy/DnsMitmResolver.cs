using ARMeilleure.Translation;
using Ryujinx.Common.Logging;
using Ryujinx.HLE.HOS.Services.Sockets.Nsd;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Net;
using System.Threading;

namespace Ryujinx.HLE.HOS.Services.Sockets.Sfdnsres.Proxy
{
    class DnsMitmResolver
    {
        private const string HostsFilePath = "/atmosphere/hosts/default.txt";

        private static DnsMitmResolver _instance;
        public static DnsMitmResolver Instance => _instance ??= new DnsMitmResolver();

        private readonly Dictionary<string, IPAddress> _mitmHostEntries = new();

        /// <summary>
        /// The address most recently handed out by a `getaddrinfo`-style lookup for a given
        /// port, regardless of which host it came from.
        /// </summary>
        /// <remarks>
        /// Exists for one specific failure mode: some guest network stacks issue exactly one
        /// address lookup for a host:port pair and then connect immediately after, but arrive at
        /// <c>connect()</c> with the address lost -- 0.0.0.0 / :: -- while the port survives
        /// intact. When that happens there is no way to recover the intended host from the
        /// connect call alone, but the port is still a strong hint: it is very likely the same
        /// target whose address this resolver just handed back. Recording it here lets
        /// <c>ManagedSocket.Connect</c> substitute a real address instead of failing outright.
        /// Keyed by port rather than by host because that is all a lost-address connect call has
        /// left to key on.
        /// </remarks>
        private readonly ConcurrentDictionary<int, IPAddress> _lastResolvedByPort = new();

        /// <summary>
        /// Records the address a `getaddrinfo`-style lookup just resolved for <paramref name="port"/>,
        /// for <see cref="TryGetLastResolved"/> to consult later.
        /// </summary>
        public void RememberResolution(int port, IPAddress address)
        {
            if (port != 0 && address != null)
            {
                _lastResolvedByPort[port] = address;
            }
        }

        /// <summary>
        /// Looks up the address most recently resolved for <paramref name="port"/>, if any.
        /// </summary>
        public bool TryGetLastResolved(int port, out IPAddress address) =>
            _lastResolvedByPort.TryGetValue(port, out address);

        private const int NplnJitSettleFloorMs = 2000;
        private const int NplnJitSettlePollWindowMs = 500;
        private const int NplnJitSettleCalmThreshold = 15;
        private const int NplnJitSettleDefaultCapMs = 60000;

        private static int _nplnJitSettleState;

        /// <summary>
        /// Splatoon 3 auto-connects to its NPLN lobby a short time after launch, which lands
        /// squarely inside the heaviest early-boot JIT compilation burst -- title code,
        /// background rejit and PPTC translation all running at once. grpc-core's connection
        /// setup can stall or fully deadlock when its own worker thread gets starved during that
        /// burst, even though the exact same setup succeeds fine once things calm down a couple
        /// of minutes later. This is a known, previously reported symptom: "start the game with
        /// guest internet access already on and the loading screen never finishes."
        ///
        /// Rather than guess a fixed delay -- too short does nothing, too long punishes every
        /// boot -- this polls <see cref="Translator.JitTranslationTicks"/> and returns once the
        /// per-window translation rate has actually dropped below a calm threshold, with a floor
        /// so it never returns instantly and a cap so a system that never goes quiet does not
        /// hang forever. Runs once per process, and only for the first lookup that looks like an
        /// NPLN host -- every other DNS lookup is unaffected.
        /// </summary>
        public static void MaybeAwaitJitSettling(string host)
        {
            if (host == null || !host.Contains("npln", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _nplnJitSettleState, 1, 0) != 0)
            {
                return;
            }

            int capMs = NplnJitSettleCapMs;

            if (capMs <= 0)
            {
                return;
            }

            Logger.Info?.Print(LogClass.ServiceBsd,
                "First NPLN host lookup -- waiting for the startup JIT burst to settle before resolving");

            Stopwatch stopwatch = Stopwatch.StartNew();

            Thread.Sleep(NplnJitSettleFloorMs);

            long lastTicks = Translator.JitTranslationTicks;

            while (stopwatch.ElapsedMilliseconds < capMs)
            {
                Thread.Sleep(NplnJitSettlePollWindowMs);

                long ticks = Translator.JitTranslationTicks;
                long delta = ticks - lastTicks;
                lastTicks = ticks;

                if (delta < NplnJitSettleCalmThreshold)
                {
                    break;
                }
            }

            Logger.Info?.Print(LogClass.ServiceBsd, $"JIT burst wait finished after {stopwatch.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// <c>RYUJINX_NPLN_JIT_SETTLE_CAP_MS</c> overrides the maximum wait; 0 disables the
        /// wait entirely. Absent or invalid falls back to <see cref="NplnJitSettleDefaultCapMs"/>.
        /// </summary>
        private static int NplnJitSettleCapMs
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("RYUJINX_NPLN_JIT_SETTLE_CAP_MS");

                if (env != null && int.TryParse(env, out int parsed) && parsed >= 0)
                {
                    return parsed;
                }

                return NplnJitSettleDefaultCapMs;
            }
        }

        public void ReloadEntries(ServiceCtx context)
        {
            string sdPath = FileSystem.VirtualFileSystem.GetSdCardPath();
            string filePath = FileSystem.VirtualFileSystem.GetFullPath(sdPath, HostsFilePath);

            _mitmHostEntries.Clear();

            if (File.Exists(filePath))
            {
                using FileStream fileStream = File.Open(filePath, FileMode.Open, FileAccess.Read);
                using StreamReader reader = new(fileStream);

                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();

                    if (line == null)
                    {
                        break;
                    }

                    // Ignore comments and empty lines
                    if (line.StartsWith('#') || line.Trim().Length == 0)
                    {
                        continue;
                    }

                    string[] entry = line.Split([' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                    // Hosts file example entry:
                    // 127.0.0.1  localhost loopback

                    // 0. Check the size of the array
                    if (entry.Length < 2)
                    {
                        Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Invalid entry in hosts file: {line}");

                        continue;
                    }

                    // 1. Parse the address
                    if (!IPAddress.TryParse(entry[0], out IPAddress address))
                    {
                        Logger.Warning?.PrintMsg(LogClass.ServiceBsd, $"Failed to parse IP address in hosts file: {entry[0]}");

                        continue;
                    }

                    // 2. Check for AMS hosts file extension: "%"
                    for (int i = 1; i < entry.Length; i++)
                    {
                        entry[i] = entry[i].Replace("%", IManager.NsdSettings.Environment);
                    }

                    // 3. Add hostname to entry dictionary (updating duplicate entries)
                    foreach (string hostname in entry[1..])
                    {
                        _mitmHostEntries[hostname] = address;
                    }
                }
            }
        }

        /// <summary>
        /// Looks a host up in the hosts file, without falling back to real DNS.
        /// </summary>
        /// <param name="host">The hostname the guest asked for</param>
        /// <param name="entry">The redirect, when the hosts file names this host</param>
        /// <returns>True if the hosts file names this host</returns>
        /// <remarks>
        /// Separate from <see cref="ResolveAddress"/> because callers need to know
        /// <em>whether</em> a host is redirected, not just where to. <see cref="IResolver"/>
        /// uses it to let a redirected host past the DNS blacklist: the blacklist exists to keep
        /// the guest away from Nintendo's servers, and a host the operator has explicitly pointed
        /// at their own machine is not one of those. A host with no entry stays blocked, so
        /// enabling redirection cannot quietly open a path to real Nintendo.
        /// </remarks>
        public bool TryResolveRedirect(string host, out IPHostEntry entry)
        {
            foreach (KeyValuePair<string, IPAddress> hostEntry in _mitmHostEntries)
            {
                // Check for AMS hosts file extension: "*"
                // NOTE: MatchesSimpleExpression also allows "?" as a wildcard
                if (FileSystemName.MatchesSimpleExpression(hostEntry.Key, host))
                {
                    entry = new IPHostEntry
                    {
                        AddressList = [hostEntry.Value],
                        // The hostname the GUEST asked for, not the pattern that matched it.
                        // With a wildcard entry the pattern is not a hostname at all, and
                        // returning it made the guest's TLS see a name mismatch against a
                        // certificate that was in fact correct.
                        HostName = host,
                        Aliases = [],
                    };

                    return true;
                }
            }

            entry = null;

            return false;
        }

        public IPHostEntry ResolveAddress(string host)
        {
            // An IP literal has nothing to resolve and nothing to rename. Splatoon 3 gets the
            // address of the server for a new session as an IP and passes it to getaddrinfo; a
            // real lookup answered with the reverse-DNS name of that address instead of the
            // one the guest asked for, and the gRPC connection to it never sent a request, so
            // creating a private room failed with 2321-4992.
            if (IPAddress.TryParse(host, out IPAddress literal))
            {
                return new IPHostEntry
                {
                    AddressList = [literal],
                    HostName = host,
                    Aliases = [],
                };
            }

            if (TryResolveRedirect(host, out IPHostEntry redirect))
            {
                Logger.Info?.PrintMsg(LogClass.ServiceBsd,
                    $"Redirecting '{host}' to: {redirect.AddressList[0]}");

                return redirect;
            }

            // No match has been found, resolve the host using regular dns
            return Dns.GetHostEntry(host);
        }
    }
}
