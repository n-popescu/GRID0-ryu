using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Horizon.Bcat;
using Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Systems.Grid0
{
    /// <summary>The GRID0+ login from the settings, for code that runs without a game.</summary>
    internal static class Grid0Account
    {
        /// <summary>The shared client for the configured login, or null when there is none.</summary>
        public static SwitchNetAccountClient Client()
        {
            ConfigurationState.SystemSection system = ConfigurationState.Instance.System;

            return SwitchNetAccountSession.TryGetClient(
                system.SwitchNetServer.Value, system.SwitchNetUsername.Value, system.SwitchNetPassword.Value,
                out SwitchNetAccountClient client)
                ? client
                : null;
        }

        /// <summary>Splatoon 3, whose Splatfest packs come over BCAT.</summary>
        private const ulong Splatoon3 = 0x0100C2500FC20000;

        /// <summary>
        /// Brings every BCAT delivery cache up to date at startup, in the background: Splatoon 3's
        /// and any other title's already on disk. A console's bcat sysmodule syncs while the system
        /// runs, not only when a game starts; a game launched right after still syncs its own.
        /// </summary>
        public static void SyncBcatInBackground()
        {
            if (Client() is not { } client)
            {
                return;
            }

            Task.Run(async () =>
            {
                HashSet<ulong> titles = [Splatoon3];

                if (Directory.Exists(LocalDeliveryCache.RootPath))
                {
                    foreach (string dir in Directory.GetDirectories(LocalDeliveryCache.RootPath))
                    {
                        if (ulong.TryParse(Path.GetFileName(dir), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong id))
                        {
                            titles.Add(id);
                        }
                    }
                }

                foreach (ulong title in titles)
                {
                    try
                    {
                        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
                        await Grid0Bcat.SyncAsync(client, title, timeout.Token);
                    }
                    catch (Exception e)
                    {
                        Logger.Warning?.Print(LogClass.ServiceBcat, $"GRID0+ BCAT: {title:x16} not synced: {e.Message}");
                    }
                }
            });
        }
    }
}
