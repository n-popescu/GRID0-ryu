using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Integration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Graphics.GAL;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.UI;
using System;

namespace Ryujinx.HLE
{
    /// <summary>
    /// HLE configuration.
    /// </summary>
    public class HleConfiguration
    {
        /// <summary>
        /// The virtual file system used by the FS service.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal VirtualFileSystem VirtualFileSystem { get; private set; }

        /// <summary>
        /// The manager for handling a LibHac Horizon instance.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal LibHacHorizonManager LibHacHorizonManager { get; private set; }

        /// <summary>
        /// The account manager used by the account service.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal AccountManager AccountManager { get; private set; }

        /// <summary>
        /// The content manager used by the NCM service.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal ContentManager ContentManager { get; private set; }

        /// <summary>
        /// The persistent information between run for multi-application capabilities.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        public UserChannelPersistence UserChannelPersistence { get; private set; }

        /// <summary>
        /// The GPU renderer to use for all GPU operations.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal IRenderer GpuRenderer { get; private set; }

        /// <summary>
        /// The audio device driver to use for all audio operations.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal IHardwareDeviceDriver AudioDeviceDriver { get; private set; }

        /// <summary>
        /// The handler for various UI related operations needed outside of HLE.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal IHostUIHandler HostUIHandler { get; private set; }

        /// <summary>
        /// Control the memory configuration used by the emulation context.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly MemoryConfiguration MemoryConfiguration;

        /// <summary>
        /// The system language to use in the settings service.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly SystemLanguage SystemLanguage;

        /// <summary>
        /// The system region to use in the settings service.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly RegionCode Region;

        /// <summary>
        /// Control the initial state of the present interval in the SurfaceFlinger service (previously Vsync).
        /// </summary>
        internal readonly VSyncMode VSyncMode;

        /// <summary>
        /// Control the custom VSync interval, if enabled and active.
        /// </summary>
        internal readonly int CustomVSyncInterval;

        /// <summary>
        /// Control the initial state of the docked mode.
        /// </summary>
        internal readonly bool EnableDockedMode;

        /// <summary>
        /// Control if the Profiled Translation Cache (PTC) should be used.
        /// </summary>
        internal readonly bool EnablePtc;

        /// <summary>
        /// Control the arbitrary scalar applied to emulated CPU tick timing.
        /// </summary>
        public long TickScalar { get; set; }

        /// <summary>
        /// Control if the guest application should be told that there is a Internet connection available.
        /// </summary>
        public bool EnableInternetAccess { internal get; set; }

        /// <summary>
        /// Lets the hosts file on the virtual SD card override the DNS blacklist, so that
        /// Nintendo's hostnames can be pointed at a private replacement for them.
        /// </summary>
        /// <remarks>
        /// Narrow on purpose: it applies only to a host the hosts file actually names, so the
        /// blacklist still refuses every blocked host with no entry. See
        /// <see cref="HOS.Services.Sockets.Sfdnsres.Proxy.DnsBlacklist.TryAllow"/>.
        /// </remarks>
        public bool RedirectNintendoServers { internal get; set; }

        /// <summary>
        /// Path to a PEM bundle of certificate authorities the guest's TLS trusts in ADDITION to
        /// the host's, so that a privately-signed certificate for a Nintendo hostname is accepted.
        /// </summary>
        /// <remarks>
        /// This adds trust; it does not disable verification. A name mismatch or an expired
        /// certificate still fails. See
        /// <see cref="HOS.Services.Ssl.SslService.PrivateServerTrust"/>.
        /// </remarks>
        public string PrivateServerCaBundle { internal get; set; }

        /// <summary>
        /// A single address that answers for EVERY blocked Nintendo hostname that the hosts
        /// file does not more specifically cover.
        /// </summary>
        /// <remarks>
        /// <see cref="RedirectNintendoServers"/> and the hosts file are the precise mechanism:
        /// name the hostnames you want redirected, one line each. This is the blunt one: point
        /// every game-server-shaped hostname SwitchNet blocks at one place, without maintaining
        /// a hosts file at all. A hosts-file entry for a given hostname still wins over this when
        /// both are configured -- it is more specific.
        /// <para>
        /// This field alone is the switch, the same as the SwitchNet account fields below: empty
        /// means no redirect, independently of <see cref="RedirectNintendoServers"/>. An IP
        /// literal only -- a hostname would have to be resolved by the very resolver this
        /// configures, which is circular.
        /// </para>
        /// <para>
        /// Does not cover <c>nncs2</c>. Pia's NAT-check requires that host to answer from a
        /// SECOND, DIFFERENT address, or the console de-duplicates the probe and NAT detection
        /// never completes; see <see cref="PrivateServerNatCheckSecondaryAddress"/>.
        /// </para>
        /// </remarks>
        public string PrivateServerAddress { internal get; set; }

        /// <summary>
        /// The address the <c>nncs2</c> NAT-check host resolves to, instead of
        /// <see cref="PrivateServerAddress"/>.
        /// </summary>
        /// <remarks>
        /// Pia probes <c>nncs1</c> and <c>nncs2</c> as two separate servers and one NAT-check
        /// message type must be answered from a different address. Pointing both at the same
        /// address makes the console de-duplicate the probe, so NAT detection never completes and
        /// matchmaking finds matches that will not start. Leaving this empty while
        /// <see cref="PrivateServerAddress"/> is set answers <c>nncs2</c> from the primary address
        /// too, which has that same failure mode -- so this is not a "nice to have" the way most
        /// address fields are.
        /// </remarks>
        public string PrivateServerNatCheckSecondaryAddress { internal get; set; }

        /// <summary>
        /// Address of the SwitchNet server for the emulator's OWN account login:
        /// "192.168.1.50", "192.168.1.50:443" or a hostname.
        /// </summary>
        /// <remarks>
        /// Separate from the guest traffic RedirectNintendoServers redirects. That is
        /// the game reaching the server; this is the emulator fetching the identity
        /// token a game asks the account service for before it can reach any online
        /// game server. Empty means no login, and games get the id token Ryujinx
        /// generates locally -- correct in shape, signed with a throwaway key.
        /// </remarks>
        public string SwitchNetServer { internal get; set; }

        /// <summary>
        /// The username chosen at self-registration on the server's own
        /// "/register" page. The same credential a person types there, not
        /// anything console- or device-shaped -- SwitchNetAccountClient logs in
        /// with this directly, the same way a browser would.
        /// </summary>
        public string SwitchNetUsername { internal get; set; }

        /// <summary>
        /// That person's password. A credential for the operator's own private
        /// server; it is not a Nintendo password and cannot be used as one.
        /// </summary>
        public string SwitchNetPassword { internal get; set; }

        /// <summary>
        /// Control LibHac's integrity check level.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly IntegrityCheckLevel FsIntegrityCheckLevel;

        /// <summary>
        /// Control LibHac's global access logging level. Value must be between 0 and 3.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly int FsGlobalAccessLogMode;

        /// <summary>
        /// The system time offset to apply to the time service steady and local clocks.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly long SystemTimeOffset;

        /// <summary>
        /// The system timezone used by the time service.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        internal readonly string TimeZone;

        /// <summary>
        /// Type of the memory manager used on CPU emulation.
        /// </summary>
        public MemoryManagerMode MemoryManagerMode { internal get; set; }

        /// <summary>
        /// Control the initial state of the ignore missing services setting.
        /// If this is set to true, when a missing service is encountered, it will try to automatically handle it instead of throwing an exception.
        /// </summary>
        /// TODO: Update this again.
        public bool IgnoreMissingServices { internal get; set; }

        /// <summary>
        /// Aspect Ratio applied to the renderer window by the SurfaceFlinger service.
        /// </summary>
        public AspectRatio AspectRatio { get; set; }

        /// <summary>
        /// The audio volume level.
        /// </summary>
        public float AudioVolume { get; set; }

        /// <summary>
        /// Use Hypervisor over JIT if available.
        /// </summary>
        internal readonly bool UseHypervisor;

        /// <summary>
        /// Multiplayer LAN Interface ID (device GUID)
        /// </summary>
        public string MultiplayerLanInterfaceId { internal get; set; }

        /// <summary>
        /// Multiplayer Mode
        /// </summary>
        public MultiplayerMode MultiplayerMode { internal get; set; }

        /// <summary>
        /// Disable P2P mode
        /// </summary>
        public bool MultiplayerDisableP2p { internal get; set; }

        /// <summary>
        /// Multiplayer Passphrase
        /// </summary>
        public string MultiplayerLdnPassphrase { internal get; set; }

        /// <summary>
        /// LDN Server
        /// </summary>
        public string MultiplayerLdnServer { internal get; set; }

        /// <summary>
        /// LAN Play relay server, as host:port, optionally prefixed with user:password@
        /// </summary>
        public string MultiplayerLanPlayServer { internal get; set; }

        /// <summary>
        /// Virtual 10.13.x.x address to use on the LAN Play network, or empty for an automatic one
        /// </summary>
        public string MultiplayerLanPlayVirtualIp { internal get; set; }

        /// <summary>
        /// Carries local wireless (LDN) over the LAN Play relay using the ldn_mitm protocol, which is
        /// what makes games with no LAN mode of their own work over LAN Play
        /// </summary>
        public bool MultiplayerLanPlayLdnMitm { internal get; set; }

        /// <summary>
        /// An action called when HLE force a refresh of output after docked mode changed.
        /// </summary>
        public Action RefreshInputConfig { internal get; set; }

        /// <summary>
        /// Enables gdbstub to allow for debugging of the guest .
        /// </summary>
        public bool EnableGdbStub { internal get; set; }

        /// <summary>
        /// A TCP port to use to expose a gdbstub for a debugger to connect to.
        /// </summary>
        public ushort GdbStubPort { internal get; set; }

        /// <summary>
        /// Suspend execution when starting an application
        /// </summary>
        public bool DebuggerSuspendOnStart { internal get; set; }

        /// <summary>
        ///     The desired hacky workarounds.
        /// </summary>
        /// <remarks>This cannot be changed after <see cref="Switch"/> instantiation.</remarks>
        public EnabledDirtyHack[] Hacks { internal get; set; }

        public HleConfiguration(MemoryConfiguration memoryConfiguration,
                                SystemLanguage systemLanguage,
                                RegionCode region,
                                VSyncMode vSyncMode,
                                bool enableDockedMode,
                                bool enablePtc,
                                long tickScalar,
                                bool enableInternetAccess,
                                bool redirectNintendoServers,
                                string privateServerCaBundle,
                                string privateServerAddress,
                                string privateServerNatCheckSecondaryAddress,
                                string switchNetServer,
                                string switchNetUsername,
                                string switchNetPassword,
                                IntegrityCheckLevel fsIntegrityCheckLevel,
                                int fsGlobalAccessLogMode,
                                long systemTimeOffset,
                                string timeZone,
                                MemoryManagerMode memoryManagerMode,
                                bool ignoreMissingServices,
                                AspectRatio aspectRatio,
                                float audioVolume,
                                bool useHypervisor,
                                string multiplayerLanInterfaceId,
                                MultiplayerMode multiplayerMode,
                                bool multiplayerDisableP2p,
                                string multiplayerLdnPassphrase,
                                string multiplayerLdnServer,
                                string multiplayerLanPlayServer,
                                string multiplayerLanPlayVirtualIp,
                                bool multiplayerLanPlayLdnMitm,
                                bool enableGdbStub,
                                ushort gdbStubPort,
                                bool debuggerSuspendOnStart,
                                int customVSyncInterval,
                                EnabledDirtyHack[] dirtyHacks = null)
        {
            MemoryConfiguration = memoryConfiguration;
            SystemLanguage = systemLanguage;
            Region = region;
            VSyncMode = vSyncMode;
            CustomVSyncInterval = customVSyncInterval;
            EnableDockedMode = enableDockedMode;
            EnablePtc = enablePtc;
            TickScalar = tickScalar;
            EnableInternetAccess = enableInternetAccess;
            RedirectNintendoServers = redirectNintendoServers;
            PrivateServerCaBundle = privateServerCaBundle;
            PrivateServerAddress = privateServerAddress;
            PrivateServerNatCheckSecondaryAddress = privateServerNatCheckSecondaryAddress;
            SwitchNetServer = switchNetServer;
            SwitchNetUsername = switchNetUsername;
            SwitchNetPassword = switchNetPassword;
            FsIntegrityCheckLevel = fsIntegrityCheckLevel;
            FsGlobalAccessLogMode = fsGlobalAccessLogMode;
            SystemTimeOffset = systemTimeOffset;
            TimeZone = timeZone;
            MemoryManagerMode = memoryManagerMode;
            IgnoreMissingServices = ignoreMissingServices;
            AspectRatio = aspectRatio;
            AudioVolume = audioVolume;
            UseHypervisor = useHypervisor;
            MultiplayerLanInterfaceId = multiplayerLanInterfaceId;
            MultiplayerMode = multiplayerMode;
            MultiplayerDisableP2p = multiplayerDisableP2p;
            MultiplayerLdnPassphrase = multiplayerLdnPassphrase;
            MultiplayerLdnServer = multiplayerLdnServer;
            MultiplayerLanPlayServer = multiplayerLanPlayServer;
            MultiplayerLanPlayVirtualIp = multiplayerLanPlayVirtualIp;
            MultiplayerLanPlayLdnMitm = multiplayerLanPlayLdnMitm;
            EnableGdbStub = enableGdbStub;
            GdbStubPort = gdbStubPort;
            DebuggerSuspendOnStart = debuggerSuspendOnStart;
            Hacks = dirtyHacks ?? [];
        }

        /// <summary>
        /// Set the pre-configured services to use for this <see cref="HleConfiguration"/> instance.
        /// </summary>
        public HleConfiguration Configure(
            VirtualFileSystem virtualFileSystem,
            LibHacHorizonManager libHacHorizonManager,
            ContentManager contentManager,
            AccountManager accountManager,
            UserChannelPersistence userChannelPersistence,
            IRenderer gpuRenderer,
            IHardwareDeviceDriver audioDeviceDriver,
            IHostUIHandler hostUIHandler
        )
        {
            VirtualFileSystem = virtualFileSystem;
            LibHacHorizonManager = libHacHorizonManager;
            AccountManager = accountManager;
            ContentManager = contentManager;
            UserChannelPersistence = userChannelPersistence;
            GpuRenderer = gpuRenderer;
            AudioDeviceDriver = audioDeviceDriver;
            HostUIHandler = hostUIHandler;
            return this;
        }
    }
}
