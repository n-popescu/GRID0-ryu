using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Bcat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet
{
    /// <summary>
    /// Fills a game's delivery cache with the BCAT files GRID0+ serves consoles.
    /// </summary>
    /// <remarks>
    /// A console's bcat sysmodule downloads them in the background; Splatoon 3's Splatfest packs
    /// reach it this way. The emulator has no BCAT client, so before a game starts this asks
    /// GRID0+'s emulator API for the title's files (GET /emulator/v1/bcat/&lt;title&gt;), downloads
    /// the ones that changed into <see cref="LocalDeliveryCache"/>, and removes what the server no
    /// longer has, the way a console's cache follows the server.
    /// </remarks>
    public static class Grid0Bcat
    {
        public static async Task SyncAsync(SwitchNetAccountClient client, ulong titleId, CancellationToken ct)
        {
            if (client == null || titleId == 0)
            {
                return;
            }

            string title = titleId.ToString("x16");
            (int status, string body) = await client.SendAsync(HttpMethod.Get, $"/emulator/v1/bcat/{title}", null, ct).ConfigureAwait(false);

            if (status == 404)
            {
                // A server without the BCAT API: leave whatever the cache holds.
                return;
            }

            if (status != 200)
            {
                Logger.Warning?.Print(LogClass.ServiceBcat, $"GRID0+ BCAT: list refused ({status})");

                return;
            }

            Dictionary<string, Dictionary<string, string>> wanted = [];

            using (JsonDocument document = JsonDocument.Parse(body))
            {
                foreach (JsonElement dir in document.RootElement.GetProperty("directories").EnumerateArray())
                {
                    string dirName = dir.GetProperty("name").GetString() ?? "";

                    if (!IsSafeName(dirName))
                    {
                        continue;
                    }

                    Dictionary<string, string> files = [];

                    foreach (JsonElement file in dir.GetProperty("files").EnumerateArray())
                    {
                        string fileName = file.GetProperty("name").GetString() ?? "";

                        if (IsSafeName(fileName))
                        {
                            files[fileName] = file.GetProperty("sha256").GetString() ?? "";
                        }
                    }

                    wanted[dirName] = files;
                }
            }

            string root = LocalDeliveryCache.TitlePath(titleId);
            int downloaded = 0;

            foreach ((string dirName, Dictionary<string, string> files) in wanted)
            {
                string dirPath = Path.Combine(root, dirName);
                Directory.CreateDirectory(dirPath);

                foreach ((string fileName, string sha256) in files)
                {
                    string path = Path.Combine(dirPath, fileName);

                    if (File.Exists(path) && Sha256(path) == sha256)
                    {
                        continue;
                    }

                    byte[] data = await client.GetBytesAsync($"/emulator/v1/bcat/{title}/{dirName}/{fileName}", ct).ConfigureAwait(false);

                    if (data == null || Convert.ToHexStringLower(SHA256.HashData(data)) != sha256)
                    {
                        Logger.Warning?.Print(LogClass.ServiceBcat, $"GRID0+ BCAT: {dirName}/{fileName} did not download intact");

                        continue;
                    }

                    // Written beside and moved into place, so a game never reads half a file.
                    string partial = path + ".partial";
                    await File.WriteAllBytesAsync(partial, data, ct).ConfigureAwait(false);
                    File.Move(partial, path, overwrite: true);
                    downloaded++;
                }

                foreach (string stale in Directory.GetFiles(dirPath).Where(f => !files.ContainsKey(Path.GetFileName(f))))
                {
                    File.Delete(stale);
                }
            }

            if (Directory.Exists(root))
            {
                foreach (string stale in Directory.GetDirectories(root).Where(d => !wanted.ContainsKey(Path.GetFileName(d))))
                {
                    Directory.Delete(stale, recursive: true);
                }

                if (wanted.Count == 0)
                {
                    Directory.Delete(root, recursive: true);
                }
            }

            if (wanted.Count > 0)
            {
                Logger.Info?.Print(LogClass.ServiceBcat,
                    $"GRID0+ BCAT: {title} has {wanted.Sum(d => d.Value.Count)} file(s) in {wanted.Count} folder(s), {downloaded} downloaded");
            }
        }

        private static bool IsSafeName(string name) =>
            name.Length is > 0 and <= 31 && name != "." && name != ".." &&
            name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

        private static string Sha256(string path)
        {
            using FileStream stream = File.OpenRead(path);

            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
    }
}
