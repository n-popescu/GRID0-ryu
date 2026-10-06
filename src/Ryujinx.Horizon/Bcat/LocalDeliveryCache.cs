using Ryujinx.Common.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ryujinx.Horizon.Bcat
{
    /// <summary>
    /// A game's BCAT delivery cache as plain files: &lt;app data&gt;/bcat/&lt;title id&gt;/&lt;directory&gt;/&lt;file&gt;.
    /// </summary>
    /// <remarks>
    /// A console's bcat sysmodule downloads a game's delivery cache (Splatoon 3's Splatfest packs,
    /// for one) from the BCAT servers; GRID0+ answers them. The emulator has no BCAT client, and
    /// LibHac's delivery cache is a save nothing fills, so the game always found it empty. The
    /// GRID0+ sync (HLE's Grid0Bcat) writes the server's files here, and the delivery cache
    /// services serve a title from here whenever it has a folder.
    /// </remarks>
    public static class LocalDeliveryCache
    {
        public static string RootPath => Path.Combine(AppDataManager.BaseDirPath, "bcat");

        public static string TitlePath(ulong titleId) => Path.Combine(RootPath, titleId.ToString("x16"));

        /// <summary>Whether the title has a delivered cache: at least one directory.</summary>
        public static bool Has(ulong titleId)
        {
            string path = TitlePath(titleId);

            return titleId != 0 && Directory.Exists(path) && Directory.EnumerateDirectories(path).Any();
        }

        internal static string[] Directories(string titlePath) =>
            Directory.Exists(titlePath)
                ? Directory.GetDirectories(titlePath).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()
                : [];

        internal static string[] Files(string directoryPath) =>
            Directory.Exists(directoryPath)
                ? Directory.GetFiles(directoryPath).Order(StringComparer.Ordinal).ToArray()
                : [];

        /// <summary>A name safe to join onto a path: one component, no traversal.</summary>
        internal static bool IsSafeName(string name) =>
            name.Length is > 0 and <= 31 && name != "." && name != ".." &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

        /// <summary>A fixed-size, NUL-terminated name struct as a string.</summary>
        internal static string ToName<T>(ref T name) where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref name, 1));
            int end = bytes.IndexOf((byte)0);

            return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
        }

        internal static void FillName<T>(ref T name, string value) where T : unmanaged
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref name, 1));
            bytes.Clear();
            byte[] source = Encoding.ASCII.GetBytes(value);
            source.AsSpan(0, Math.Min(source.Length, bytes.Length - 1)).CopyTo(bytes);
        }

        /// <summary>The 16-byte digest the delivery cache lists a file with: its MD5.</summary>
        internal static void FillDigest<T>(ref T digest, string filePath) where T : unmanaged
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref digest, 1));
            bytes.Clear();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = MD5.HashData(stream);
            hash.AsSpan(0, Math.Min(hash.Length, bytes.Length)).CopyTo(bytes);
        }
    }
}
