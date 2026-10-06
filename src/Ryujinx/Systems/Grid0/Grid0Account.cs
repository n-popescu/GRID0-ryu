using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.HLE.HOS.Services.Account.Acc.SwitchNet;

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
    }
}
