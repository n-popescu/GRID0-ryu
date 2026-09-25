using Ryujinx.Common.Logging;
using Ryujinx.HLE.Loaders.Mods;
using System.Collections.Generic;
using System.IO;

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
    /// against real hardware, in switchnet's own <c>switch/README.md</c> ("Part 3: Splatoon 3
    /// pins its own certificates"), which is where the two patches below come from.
    /// </para>
    /// <para>
    /// Two independent fixes, both required:
    /// </para>
    /// <list type="number">
    /// <item>the certificate-pinning check itself, forced to always pass;</item>
    /// <item>the peer hostname comparison the game makes afterward, which otherwise still
    /// refuses a certificate whose SAN does not literally read a real Nintendo hostname.</item>
    /// </list>
    /// <para>
    /// The canonical source for this exact technique is <c>generate_patch.py</c> in
    /// <see href="https://github.com/kinnay/NPLN-Protocols">kinnay/NPLN-Protocols</see>,
    /// written for precisely this purpose ("if you want to capture traffic or write your own
    /// NPLN servers"). It has to be run against the operator's own legitimately dumped copy of
    /// the game to produce bytes for the operator's own build -- this project has no such dump
    /// to run it against. The offsets and instruction encodings below are instead facts recorded
    /// by <see href="https://github.com/NextendoNetwork/Ryujinx-Nextendo">NextendoNetwork/Ryujinx-Nextendo</see>,
    /// whose history records them as working for the three builds below. That fork's own code is
    /// PolyForm Shield licensed, not MIT like upstream Ryujinx, so only those facts -- which
    /// instruction, at which offset, becomes which -- are used here, never its code.
    /// </para>
    /// <para>
    /// <b>This is gated on Splatoon 3's exact build, not its version number or the emulator's
    /// firmware.</b> A title update changes the game's own binary and silently stops these
    /// patches matching -- the same failure mode <c>switch/README.md</c> warns about for the
    /// disk-based version of this patch. A build not listed in
    /// <see cref="_patchesByBuildId"/> gets nothing, exactly as an unmatched <c>exefs_patches</c>
    /// file would: if Splatoon 3 stops connecting after an update, regenerating this table from
    /// a fresh dump via <c>generate_patch.py</c> is the first thing to try, per that file's own
    /// "the last thing to change" guidance.
    /// </para>
    /// </remarks>
    internal static class PrivateServerSplatoon3Patches
    {
        public const ulong Splatoon3ApplicationId = 0x0100C2500FC20000;

        // Forces the pinned-certificate check to pass unconditionally. 4 bytes at 0x00157B20:
        // LDRB W10,[X21,#0x38] (AA E2 40 39) -> MOV W10,#1 (2A 00 80 52).
        private static readonly byte[] _certificatePinningBypass =
        [
            0x49, 0x50, 0x53, 0x33, 0x32,             // "IPS32"
            0x00, 0x15, 0x7B, 0x20, 0x00, 0x04,       // offset 0x00157B20, 4 bytes
            0x2A, 0x00, 0x80, 0x52,
            0x45, 0x45, 0x4F, 0x46,                   // "EEOF"
        ];

        // Fixes the peer-hostname comparison the game makes right after. Two records:
        // CBZ W0,+88 (C0 02 00 34) -> NOP (1F 20 03 D5) at 0x0014E1B0, and
        // MOV W20,W0 (F4 03 00 2A) -> MOV W20,WZR (F4 03 1F 2A) at 0x0014DD80.
        private static readonly byte[] _peerHostnameFix =
        [
            0x49, 0x50, 0x53, 0x33, 0x32,             // "IPS32"
            0x00, 0x14, 0xE1, 0xB0, 0x00, 0x04,       // offset 0x0014E1B0, 4 bytes
            0x1F, 0x20, 0x03, 0xD5,
            0x00, 0x14, 0xDD, 0x80, 0x00, 0x04,       // offset 0x0014DD80, 4 bytes
            0xF4, 0x03, 0x1F, 0x2A,
            0x45, 0x45, 0x4F, 0x46,                   // "EEOF"
        ];

        /// <summary>
        /// Splatoon 3 build id (uppercase hex, trailing zeros trimmed -- the same shape
        /// <see cref="ModLoader"/> already computes every build id in) to the patches that
        /// build needs.
        /// </summary>
        private static readonly Dictionary<string, byte[][]> _patchesByBuildId = new()
        {
            ["6830B3A12406CB4716FEC5ADDC35D3E2DC92D212"] = [_certificatePinningBypass, _peerHostnameFix],

            // 11.3.0. Both sites are at the SAME offsets as 11.2.0 above -- the binary's growth
            // by 4096 bytes falls after both -- so the same bytes apply unchanged.
            ["28C4287AEE36F7499DA60F3E68B54C70DA382D75"] = [_certificatePinningBypass, _peerHostnameFix],

            // A third, older build Ryujinx-Nextendo's history covers with only the
            // certificate-pinning bypass -- the peer-hostname fix was not yet known needed
            // for it, or its offsets were not yet found. Recorded as-is rather than guessed at.
            ["726D2B882DD9EF10F4A9D73EED088740630FB6C8"] = [_certificatePinningBypass],
        };

        /// <summary>
        /// Adds the patches known for <paramref name="buildId"/> to <paramref name="target"/>.
        /// Returns how many were added (0 for an unrecognised build).
        /// </summary>
        public static int Apply(string buildId, MemPatch target)
        {
            if (string.IsNullOrEmpty(buildId) || !_patchesByBuildId.TryGetValue(buildId, out byte[][] patches))
            {
                return 0;
            }

            foreach (byte[] bytes in patches)
            {
                using MemoryStream stream = new(bytes);
                using BinaryReader reader = new(stream);

                new IpsPatcher(reader).AddPatches(target);
            }

            Logger.Info?.Print(LogClass.ModLoader,
                $"Splatoon 3: applied {patches.Length} embedded private-server patch(es) for build {buildId}");

            return patches.Length;
        }
    }
}
