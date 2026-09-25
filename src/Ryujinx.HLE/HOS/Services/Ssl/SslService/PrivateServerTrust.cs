using Ryujinx.Common.Logging;
using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ryujinx.HLE.HOS.Services.Ssl.SslService
{
    /// <summary>
    /// Extra certificate authorities the guest's TLS will accept, for a private replacement of
    /// Nintendo's servers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A private server serving <c>dauth-lp1.ndas.srv.nintendo.net</c> cannot have a certificate
    /// any public CA would issue, so it signs its own with a CA the operator controls. The guest's
    /// <see cref="SslStream"/> validates against the host's trust store and rejects it.
    /// </para>
    /// <para>
    /// This <em>adds</em> that CA. It does not disable verification, and the difference is the
    /// point: a name mismatch is still fatal, an expired certificate is still fatal, and a
    /// certificate chaining to any root other than the configured one is still fatal. So pointing
    /// the guest at a server whose certificate does not cover the hostname it asked for fails here
    /// rather than succeeding quietly — which is what makes this a redirect to a known server
    /// rather than a hole.
    /// </para>
    /// <para>
    /// The alternative — a callback that returns true unconditionally — is what most guides
    /// suggest and is much worse: it would apply to every TLS connection the guest makes,
    /// including ones to real Nintendo, for as long as the option is on.
    /// </para>
    /// </remarks>
    static class PrivateServerTrust
    {
        // Cached on the path, because a handshake happens on the guest's timeline: a game opening
        // a connection per request would otherwise re-read and re-parse the file every time. The
        // path is compared rather than watched, so changing the file needs a restart -- which is
        // the same granularity as the setting itself.
        private static readonly object _lock = new();
        private static string _loadedPath;
        private static X509Certificate2Collection _roots;

        /// <summary>
        /// Stands in for a bundle path to mean the SwitchNet Local CA built into this emulator.
        /// </summary>
        /// <remarks>
        /// It is the public certificate of the CA SwitchNet's <c>switchnetctl gen-certs</c> made
        /// for this deployment -- the same one switchnet-nro installs on a console -- so a
        /// SwitchNet user gets working TLS without finding and pointing at a file. It is only used
        /// when a private server is configured and no bundle is (see <see cref="Resolve"/>), and
        /// it adds trust in exactly that one CA: nothing chained to any other root is accepted
        /// because of it.
        /// </remarks>
        public const string BuiltInSwitchNetCa = "builtin:switchnet-local-ca";

        /// <summary>
        /// The bundle to trust for a guest: the configured one, else the built-in SwitchNet CA
        /// when a private server is configured, else none.
        /// </summary>
        public static string Resolve(string configuredBundle, string privateServerAddress)
        {
            if (!string.IsNullOrWhiteSpace(configuredBundle))
            {
                return configuredBundle;
            }

            return string.IsNullOrWhiteSpace(privateServerAddress) ? null : BuiltInSwitchNetCa;
        }

        /// <summary>
        /// Returns the configured roots, or null when there are none.
        /// </summary>
        public static X509Certificate2Collection GetRoots(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            lock (_lock)
            {
                if (_loadedPath == path)
                {
                    return _roots;
                }

                _loadedPath = path;
                _roots = Load(path);

                return _roots;
            }
        }

        private static X509Certificate2Collection Load(string path)
        {
            if (path == BuiltInSwitchNetCa)
            {
                return LoadBuiltIn();
            }

            if (!File.Exists(path))
            {
                // Warned once per path rather than thrown: a missing file means the guest falls
                // back to ordinary validation and the connection fails with a certificate error,
                // which is confusing on its own. This line is what explains it.
                Logger.Warning?.Print(LogClass.ServiceSsl,
                    $"Private server CA bundle not found: {path}. The guest will not trust a " +
                    "privately-signed certificate, so TLS to a private server will fail.");

                return null;
            }

            try
            {
                X509Certificate2Collection roots = new();
                roots.ImportFromPemFile(path);

                if (roots.Count == 0)
                {
                    Logger.Warning?.Print(LogClass.ServiceSsl,
                        $"Private server CA bundle {path} contains no certificates.");

                    return null;
                }

                foreach (X509Certificate2 root in roots)
                {
                    Logger.Info?.Print(LogClass.ServiceSsl,
                        $"Trusting private server CA: {root.Subject}");
                }

                return roots;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.ServiceSsl,
                    $"Could not read the private server CA bundle {path}: {exception.Message}");

                return null;
            }
        }

        private static X509Certificate2Collection LoadBuiltIn()
        {
            using Stream stream = typeof(PrivateServerTrust).Assembly.GetManifestResourceStream(
                "Ryujinx.HLE.HOS.Services.Ssl.SslService.Resources.SwitchNetLocalCA.pem");

            if (stream == null)
            {
                Logger.Warning?.Print(LogClass.ServiceSsl, "The built-in SwitchNet CA is missing from this build.");

                return null;
            }

            using StreamReader reader = new(stream);

            X509Certificate2Collection roots = new();
            roots.ImportFromPem(reader.ReadToEnd());

            foreach (X509Certificate2 root in roots)
            {
                Logger.Info?.Print(LogClass.ServiceSsl, $"Trusting built-in private server CA: {root.Subject}");
            }

            return roots;
        }

        /// <summary>
        /// Builds the validation callback for a connection, or null to use the default validation.
        /// </summary>
        public static RemoteCertificateValidationCallback BuildCallback(string caBundlePath)
        {
            X509Certificate2Collection roots = GetRoots(caBundlePath);

            if (roots == null)
            {
                return null;
            }

            return (_, certificate, chain, errors) => Validate(roots, certificate, chain, errors);
        }

        private static bool Validate(
            X509Certificate2Collection roots,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            // A name mismatch is NOT forgiven, and neither is a missing certificate. The extra CA
            // says "this issuer is legitimate"; it says nothing about whether the certificate
            // belongs to the host the guest asked for, and forgiving that would turn a redirect
            // into an accept-anything.
            if (errors != SslPolicyErrors.RemoteCertificateChainErrors)
            {
                Logger.Warning?.Print(LogClass.ServiceSsl,
                    $"Rejecting the server certificate: {errors}");

                return false;
            }

            if (certificate == null)
            {
                return false;
            }

            // Rebuilt rather than inspecting the chain the default validation produced: that chain
            // was built against the system store, so its status says nothing about whether this
            // certificate chains to one of OUR roots.
            using X509Chain rebuilt = new();
            rebuilt.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            rebuilt.ChainPolicy.CustomTrustStore.AddRange(roots);
            rebuilt.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

            // Intermediates the server sent, so a chain longer than leaf-plus-root can be built.
            if (chain != null)
            {
                foreach (X509ChainElement element in chain.ChainElements)
                {
                    rebuilt.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }

            using X509Certificate2 leaf = new(certificate);

            if (rebuilt.Build(leaf))
            {
                return true;
            }

            foreach (X509ChainStatus status in rebuilt.ChainStatus)
            {
                Logger.Warning?.Print(LogClass.ServiceSsl,
                    $"The server certificate does not chain to a trusted private CA: " +
                    $"{status.Status} ({status.StatusInformation.Trim()})");
            }

            return false;
        }
    }
}
