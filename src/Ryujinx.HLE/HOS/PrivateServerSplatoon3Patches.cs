using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Mods;
using System;
using System.IO;
using System.Reflection;

namespace Ryujinx.HLE.HOS
{
    /// <summary>
    /// The binary patches Splatoon 3 needs before it will talk to a private server, applied
    /// in memory rather than read from <c>exefs_patches</c> on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splatoon 3 carries its own statically linked gRPC/TLS stack and pins Nintendo's real
    /// certificate independently of the guest OS's <c>ssl:</c> service. Every other title
    /// that talks to a private server only needs <see cref="Services.Ssl.SslService.PrivateServerTrust"/>
    /// -- the emulated <c>ssl:</c> service trusting <c>PrivateServerCaBundle</c> is enough,
    /// because those titles actually ask that service to validate the peer. Splatoon 3 never
    /// does: its own compiled-in TLS stack checks the certificate itself, so the guest-OS-level
    /// trust callback is never consulted for its NPLN connections, and the TLS handshake never
    /// completes -- the game just sits on "connecting" forever. This is documented at length,
    /// against real hardware, in GRID0+'s own <c>switch/README.md</c> ("Part 3: Splatoon 3
    /// pins its own certificates"), which is where these patches come from.
    /// </para>
    /// <para>
    /// The patch bytes are the real <c>.ips</c> files shipped in the
    /// <see href="https://github.com/n-popescu/grid0plus-toolbox">grid0plus-toolbox</see>
    /// repository under <c>romfs/sd/atmosphere/exefs_patches/</c> -- the exact files a real
    /// console applies through Atmosphère. They are embedded in this assembly (see the
    /// <c>HOS\Patches\exefs_patches\**\*.ips</c> item in <c>Ryujinx.HLE.csproj</c>) and applied
    /// here in memory, so the emulator needs no SD-card <c>exefs_patches</c> directory. Only the
    /// Splatoon 3 game patches travel with the emulator: the toolbox's <c>bcat</c>, system
    /// <c>ssl:</c> and browser patches target modules this emulator high-level-emulates rather
    /// than running as a guest NSO, so they could never match a loaded build id and are left to
    /// the console toolbox. The <c>.ips</c> bytes carry no Nintendo code and neither create nor
    /// impersonate a Nintendo signature.
    /// </para>
    /// <para>
    /// <b>This is gated on Splatoon 3's exact build, not its version number or the emulator's
    /// firmware</b>, by the build id in each <c>.ips</c> file name (trailing zeros trimmed, the
    /// same shape <see cref="ModLoader"/> computes for every loaded NSO). A title update changes
    /// the game's own binary and silently stops these patches matching -- the same failure mode
    /// <c>switch/README.md</c> warns about for the disk-based version of this patch. A build
    /// with no matching file gets nothing, exactly as an unmatched <c>exefs_patches</c> file
    /// would: if Splatoon 3 stops connecting after an update, adding the new build's patches to
    /// the toolbox (and re-syncing them here) is the first thing to try.
    /// </para>
    /// </remarks>
    internal static class PrivateServerSplatoon3Patches
    {
        public const ulong Splatoon3ApplicationId = 0x0100C2500FC20000;

        // Manifest-name marker for the embedded toolbox patch tree. Each resource is named
        // ...Patches.exefs_patches.<category>.<buildId>.ips, so a patch for a build is found by
        // the "<buildId>.ips" suffix regardless of how MSBuild mangled the leading path.
        private const string PatchResourceMarker = ".Patches.exefs_patches.";

        private static readonly Assembly _assembly = typeof(PrivateServerSplatoon3Patches).Assembly;

        /// <summary>
        /// Adds every embedded patch whose file name matches <paramref name="buildId"/> to
        /// <paramref name="target"/>. Returns how many were added (0 for an unrecognised build).
        /// </summary>
        public static int Apply(string buildId, MemPatch target)
        {
            if (string.IsNullOrEmpty(buildId))
            {
                return 0;
            }

            string suffix = "." + buildId + ".ips";
            int applied = 0;

            foreach (string name in _assembly.GetManifestResourceNames())
            {
                if (!name.Contains(PatchResourceMarker, StringComparison.Ordinal) ||
                    !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using Stream stream = _assembly.GetManifestResourceStream(name);
                if (stream == null)
                {
                    continue;
                }

                using BinaryReader reader = new(stream);
                new IpsPatcher(reader).AddPatches(target);
                applied++;
            }

            if (applied > 0)
            {
                Logger.Info?.Print(LogClass.ModLoader,
                    $"Splatoon 3: applied {applied} embedded private-server patch(es) for build {buildId}");
            }

            return applied;
        }
    }
}
