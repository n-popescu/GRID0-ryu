# Ryujinx Changelog

All updates to this Ryujinx branch will be documented in this file.

## [1.4.4](<https://github.com/n-popescu/GRID0-ryu/releases/tag/v1.4.4>) - 2026-10-07
### GRID0+:
 - **Splatoon 3 can start a match with fewer than eight players.** The GRID0+ toolbox's small-match
   patch is built in, so a match the server starts with 2, 4 or 6 players is no longer refused by
   the game. It exists for Splatoon 3 11.3.0 only.
 - **Splatoon 3's private-server patches are the toolbox's own `.ips` files**, so the emulator
   applies the same bytes a patched console does, and 11.3.0 also gets the toolbox's certificate-pin
   and verify-option patches. The two older builds keep exactly the patches they had.

## [1.4.3](<https://github.com/n-popescu/GRID0-ryu/releases/tag/v1.4.3>) - 2026-10-06
### GRID0+:
 - **Splatoon 3 no longer crashes at boot while a Splatfest is announced.** BCAT's
   RequestSyncDeliveryCacheWithDirectoryName (10101) and CancelSyncDeliveryCacheRequest (10200)
   are implemented.

## [1.4.2](<https://github.com/n-popescu/GRID0-ryu/releases/tag/v1.4.2>) - 2026-10-06
### GRID0+:
 - **BCAT from GRID0+.** A game's delivery cache, such as Splatoon 3's Splatfest packs, is fetched
   from the GRID0+ server at startup and again when a game boots, as a console's bcat sysmodule does.
### Network:
 - Each process gets real random entropy, so two emulators no longer generate the same ids.

## [1.4.1](<https://github.com/n-popescu/GRID0-ryu/releases/tag/v1.4.1>) - 2026-10-06
### GRID0+:
 - **Works out of the box.** The private-server settings default to the GRID0+ server, so only the
   login the GRID0+ Discord bot gives is needed; no environment variables. An upgraded configuration
   gets the defaults in its empty fields.
 - **GRID0+ menu**, as in citron's GRID0+ build: Friends (Ctrl+Shift+F) shows your friend code, your
   friends and what each is playing, and friend requests, and adds a friend by code; Account and server
   opens the network settings.
 - **Network settings** are one GRID0+ group: server, NAT check address, CA, login server, login and
   password.
 - **Friends in games.** The friend service answers with your GRID0+ friends and sends your presence,
   so Splatoon 3's friend list shows them and they can see and join you. Games get your GRID0+
   account id.
### Network:
 - Creating a Splatoon 3 private room no longer fails with 2321-4992: a lookup of an IP address is
   answered with that address.
 - A hostname read from the guest is cut at its first NUL.

## [1.4.0](<https://github.com/n-popescu/Ryubing-LanPlay/releases/tag/v1.4.0>) - 2026-09-25
### Network:
 - **Splatoon 3 no longer hangs on "connecting to the internet" entering the hall.** Its online client
   runs on gRPC, whose event loop polls its sockets together with a wakeup eventfd and is woken by a
   write to that eventfd. Both are requests to the same single-threaded `bsd:` service, and the poll
   blocked it, so the wake-up could never be served.
   - A poll that includes an eventfd now has its reply held back instead of blocking, and is re-run
     until something is ready or its timeout passes. It also reports a closed descriptor as POLLNVAL
     for that entry instead of failing the whole call. Every other poll, and so every NEX title, keeps
     its previous behaviour.
   - A poll on an eventfd that requests no events is treated as a request for input. gRPC polls its
     wakeup eventfd that way while connecting; it used to be refused with EINVAL, which made gRPC
     drop the descriptor and loop.
   - The IPC pointer buffer grows from 0x8000 to 0xF000, the most HIPC can express: gRPC's many open
     streams overflowed it.
 - **The SwitchNet Local CA is built in.** With **Private server address** set and no **Trusted CA
   certificate** configured, the emulated `ssl:` service trusts SwitchNet's own CA, the one
   switchnet-nro installs on a console. A configured bundle still takes precedence.
 - **SwitchNet Account now logs in with a username and password**, the same credential chosen at
   self-registration on the server's own `/register` page, instead of a server-generated device
   account id and its own separate password.
   - One request (`POST /login`) instead of the three-call device-auth chain (`dauth`, an anonymous
     BAAS token, then `/1.0.0/login`) — that whole chain existed to authenticate a device account
     this client never had a way to already hold. `/login` is SwitchNet's own endpoint for exactly
     that gap; the server resolves, or on first use mints, a device account behind the scenes.
   - Settings → Network → SwitchNet Account: the **Device Account ID** field is now **Username**.
     Headless: `--switchnet-device-account-id` is now `--switchnet-username`.
   - Configuration version 79. An existing configuration's device account id cannot be carried
     forward as a username — they are different credentials for a different endpoint — so it resets
     to empty, the same deliberate "login off" state a blank configuration already was.
 - **Splatoon 3's own certificate pinning is now bypassed automatically**, in memory, whenever
   **Private server address** is configured — no `atmosphere/exefs_patches` mod to install by hand.
   - Splatoon 3 does the TLS for its NPLN connections itself, over a raw socket with its own
     statically-linked BoringSSL, so `PrivateServerTrust` — which covers every *other* title, through
     the guest OS's own `ssl:` service — never gets a chance to run for it. Without this, the
     redirect and CA trust both work, and the game still sits on "connecting" forever.
   - `PrivateServerSplatoon3Patches.cs` carries the same two IPS patches (certificate-pin bypass,
     peer-hostname fix) `kinnay/NPLN-Protocols`' `generate_patch.py` produces, for the specific
     Splatoon 3 builds already verified against real hardware. Keyed to the game's exact build id,
     same as the disk-based version of this patch: a title update changes the executable and can
     silently stop it matching — see `docs/switchnet.md`'s Splatoon 3 section.

## [1.3.35](<https://github.com/n-popescu/Ryubing-LanPlay/releases/tag/v1.3.35>) - 2026-08-29
### Network:
 - Added **SwitchNet Account** to *Settings → Network*, below Private Nintendo Servers, for logging
   the emulator in to a SwitchNet server so games present that server's identity token instead of the
   one Ryujinx generates locally. [`docs/switchnet.md`](docs/switchnet.md) is the guide.
   - Three fields — **Server**, **Device Account ID** and **Device Account Password**. Create the
     account on the server with `switchnetctl account create`.
   - **The three fields are the switch.** There is deliberately no separate on/off checkbox beside
     them: filled-in credentials sitting next to an "off" is a state where two settings contradict
     each other and one of them wins quietly. All three empty is a working configuration, not an
     error.
   - This is independent of the redirect and CA settings above it. A private server that does not
     check identity needs those and no login at all; this matters only when the server verifies the
     token, which SwitchNet does. Both halves are usually wanted together.
   - The login runs the same three calls hardware does — device auth token, application token,
     then the account login — and the resulting token is cached and refreshed before it expires.
     It is fetched in `EnsureIdTokenCacheAsync`, the call hardware does this work in, so a game that
     follows the normal order never waits on it.
   - **A failed login is not fatal and does not fake success.** The game falls back to the locally
     generated token, which a SwitchNet game server will reject — with one warning in the log
     saying so, per distinct reason, rather than one per request.
   - Configuration version 77. Existing configurations get all three empty, so nothing acquires a
     login by upgrading.
   - Headless: `--switchnet-server`, `--switchnet-device-account-id` and `--switchnet-password`.

## 1.3.34 - withdrawn
Built from a commit that is no longer in this repository's history, so what it shipped does
not correspond to anything you can check out. Its contents are in 1.3.35 above, in a reworked
form. The release is left published rather than deleted, because deleting it would break the
download links of anyone who already has it.

## [1.3.33](<https://github.com/n-popescu/Ryubing-LanPlay/releases/tag/v1.3.33>) - 2026-08-28
### Network:
 - Added **Private Nintendo Servers** to *Settings → Network*, for pointing the guest at your own
   replacement for Nintendo's online servers. Two settings, both needed, and
   [`docs/private-servers.md`](docs/private-servers.md) is the guide.
   - **Redirect Nintendo's hostnames using the hosts file on the SD card.** Ryujinx already read
     `/atmosphere/hosts/default.txt` off the virtual SD card; what this changes is that a hostname on
     the built-in DNS block list may now be redirected by it. Without that, the hostnames a private
     server actually serves — NPLN game servers, `accounts.nintendo.com`, the NAT-check pair — were
     refused before the hosts file was ever consulted.
     - **The override is narrow on purpose:** it applies only to a hostname the hosts file names, so
       a blocked hostname with no entry stays blocked. Turning it on is a redirect to a server you
       specified, not a general unblocking, and it cannot open a path to Nintendo's own servers for
       anything you have not explicitly redirected.
     - Needs guest Internet access, and the checkbox is disabled without it.
   - **Trusted CA certificate.** A PEM bundle the guest's TLS trusts *in addition* to your system's,
     so a certificate your own server signed for a Nintendo hostname is accepted.
     - **This adds trust rather than disabling verification**, which is the difference between a
       redirect to a known server and a hole. A certificate that does not cover the hostname the game
       asked for is still rejected, as is an expired one, one from an unconfigured CA, and a
       self-signed one. The alternative — a callback that accepts everything — would have applied to
       every TLS connection the guest makes, including ones to real Nintendo.
     - Several PEM blocks in one file work, for two servers or a CA rotation, and an intermediate the
       server sends on the wire is used to build the chain.
   - Both apply to new lookups and new connections, so a running game keeps what it has. The CA
     bundle is cached on its path, so changing the file's contents without changing the path needs a
     restart.
   - Configuration version 76. Existing configurations get it off with no CA, since redirecting
     Nintendo's hostnames to somebody else's server is not a default anybody should acquire by
     upgrading.
   - Headless: `--redirect-nintendo-servers` and `--private-server-ca <path>`.
### Fixes:
 - Fixed a wildcard hosts-file entry reporting the pattern that matched as the resolved hostname
   rather than the hostname the guest asked for. With an entry like `*.baas.nintendo.com` the guest
   was handed `*.baas.nintendo.com` back, which its TLS then compared against the server's
   certificate and saw as a name mismatch — for a certificate that was in fact correct.
### Tests:
 - `DnsBlacklistTests` and `PrivateServerTrustTests`: 14 tests, including the two that matter most —
   a blocked host with no hosts-file entry staying blocked with the override on, and a hostname
   mismatch still being fatal with a private CA configured.
 - Every certificate a `PrivateServerTrustTests` case builds now shares one timestamp. Each helper
   read the clock for itself, so a second ticking over between making an issuer and making the
   certificate it signs left the child outliving its parent — which X509 refuses, failing the test
   about one run in three and aborting `dotnet test` somewhere different each time.

## [1.3.32](<https://github.com/n-popescu/Ryubing-LanPlay/releases/tag/v1.3.32>) - 2026-08-18
### Multiplayer:
 - Added **Use ldn_mitm for games without LAN Play support** to *Settings → Network*, shown when the
   multiplayer mode is LAN Play and on by default.
   - Controls whether local wireless (LDN) is carried over the relay with the ldn_mitm protocol, which
     is what makes games that have no LAN mode of their own work over LAN Play.
   - Turning it off narrows LAN Play to the plain IP traffic of games that do have a LAN mode, and
     stubs local wireless.
   - Read when a game initialises LDN, so it takes effect the next time the game enters its local
     multiplayer menu rather than mid-session.
   - Configuration version 75. Existing configurations are migrated with the option on, so LAN Play
     behaves exactly as before unless it is turned off.
### Tests:
 - Fixed the in-process LAN Play test relay taking the test host process down. Its forwarding loop
   caught `SocketException` but not `ObjectDisposedException`, so disposing the relay between a receive
   and a send threw on a background thread and ended the process, aborting `dotnet test` at a
   different test each run.

## [1.3.2](<https://git.ryujinx.app/ryubing/ryujinx/-/releases/1.3.2>) - 2025-06-09

## [1.3.1](<https://git.ryujinx.app/ryubing/ryujinx/-/releases/1.3.1>) - 2025-04-23

## [1.2.86](<https://github.com/Ryubing/Stable-Releases/releases/tag/1.2.86>) - 2025-03-13

## [1.2.82](<https://web.archive.org/web/20250312010534/https://github.com/Ryubing/Ryujinx/releases/tag/1.2.82>) - 2025-02-16

## [1.2.80-81](<https://web.archive.org/web/20250302064257/https://github.com/Ryubing/Ryujinx/releases/tag/1.2.81>) - 2025-01-22

## [1.2.78](<https://web.archive.org/web/20250301174537/https://github.com/Ryubing/Ryujinx/releases/tag/1.2.78>) - 2024-12-19

## [1.2.73-1.2.76](<https://web.archive.org/web/20250209202612/https://github.com/Ryubing/Ryujinx/releases/tag/1.2.76>) - 2024-11-19
A list of notable changes can be found on the release linked in the version number above.

Additionally, 1.2.74 & 75 were fixes for uploading Windows build artifacts.

1.2.76 fixes a rare crash on startup.

## [1.2.72](<https://git.ryujinx.app/ryubing/ryujinx/-/tags/1.2.72>) - 2024-11-03
PRs [#163](<https://web.archive.org/web/20241123015123/https://github.com/GreemDev/Ryujinx/pull/163>), [#164](<https://web.archive.org/web/20250307192526/https://github.com/Ryubing/Ryujinx/pull/164>), [#139](<https://web.archive.org/web/20250306123457/https://github.com/Ryubing/Ryujinx/pull/139>)
### HLE:
 - Add DebugMouse HID device.
   - Fixes "Clock Tower Rewind" crashing while loading.
### Audio:
 - Fix index bounds check in GetCoefficientAtIndex.
   - Fixes crashing in Super Mario Party Jamboree.
### misc:
 - Update macOS distribution .icns.

## [1.2.69](<https://git.ryujinx.app/ryubing/ryujinx/-/tags/1.2.69>) - 2024-11-01
### Infra:
  - Compile the native libraries into the Ryujinx executable.
  - Remove `libarmeilleure-jitsupport.dylib` from Windows & Linux releases (dylibs are macOS-only)
### Misc:
  - Remove custom themes in config.
    - This is a leftover from the GTK UI, as Avalonia does not have custom themes.
  - Replace "" with `string.Empty`.
  - Code cleanups & simplifications.

## [1.2.67](<https://git.ryujinx.app/ryubing/ryujinx/-/tags/1.2.67>) - 2024-11-01
PRs [#36](<https://web.archive.org/web/20250306215917/https://github.com/Ryubing/Ryujinx/pull/36>), [#135](<https://web.archive.org/web/20241122135125/https://github.com/GreemDev/Ryujinx/pull/135>)

### GUI:
  - Set UseFloatingWatermark to false when watermark is empty
    - Should prevent the text prompt box from having weird jumpy behavior.
### GPU:
  - Increase the amount of VRAM cache available for textures based on selected DRAM amount.
### Misc:
  - Fix homebrew loading.


## [1.2.64](https://git.ryujinx.app/ryubing/ryujinx/-/tags/1.2.64) - 2024-10-30
PRs [#92](https://web.archive.org/web/20241118052724/https://github.com/GreemDev/Ryujinx/pull/92), ~~[#96](https://github.com/GreemDev/Ryujinx/pull/96)~~, ~~[#97](https://github.com/GreemDev/Ryujinx/pull/97)~~,  [#101](https://web.archive.org/web/20250306223605/https://github.com/Ryubing/Ryujinx/pull/101), ~~[#103](https://github.com/GreemDev/Ryujinx/pull/103)~~
### GUI:
- Option to show classic-style title bar. Requires restart of emulator to take effect.
  - This is only relevant on Windows. Other Operating Systems default to this being on and not being changeable, because the custom (current) title bar only works on Windows in the first place.
### i18n:
- it_IT: 
  - Add missing Italian strings.
- pt_BR:
  - Add missing Brazilian Portuguese strings.
- fr_FR:
  - Fix some French strings.
### MISC:
- Higher-res logo.

## 1.2.59 - 2024-10-27

PRs ~~[#88](https://github.com/GreemDev/Ryujinx/pull/88), [#87](https://github.com/GreemDev/Ryujinx/pull/87)~~
### i18n:
- fr_FR:
  - Add missing translations for new features & fix a couple wrong ones.
  - Fix Ignore Missing Services / Ignore Applet tooltip.

## 1.2.57 - 2024-10-27
PRs ~~[#60](https://github.com/GreemDev/Ryujinx/pull/60)~~, [#42](https://web.archive.org/web/20241126203614/https://github.com/GreemDev/Ryujinx/pull/42)
### GUI:
- Automatically remove invalid DLC & updates as part of autoload.
- Added Thai translation for Ignore Applet hover tooltip.
### INPUT:
- When using multiple gamepads, when reconnecting they will no longer be mixed up between players.

## 1.2.50 - 2024-10-25
### GUI:
- Fix crash when using "delete all" button in mod manager.
### Updater:
- Remove Avalonia migration code.
### MISC:
- Replace references to IntPtr/UIntPtr to nint/nuint.

## 1.2.45 - 2024-10-25
### GUI:
- Added program icon to windows other than the main.
- Reference translations added in the last version.
- Shader compile counter is now translated.
### RPC:
- Added SONIC X SHADOW GENERATIONS asset image.
### MISC:
- Code cleanup.

## 1.2.44 - 2024-10-25
PR [#59](https://web.archive.org/web/20241125060420/https://github.com/GreemDev/Ryujinx/pull/59)
### GUI:
- Add descriptions for "ignoring applet" translated into other languages.

NOTE: The translation isn't referenced in the code yet, it will be in the next update. These are just the translations.

## Hotfix: 1.2.43 - 2024-10-24
### GUI:
- Do not enable Ignore Applet by default when upgrading config version.

## 1.2.42 - 2024-10-24
Sources:

Init function: [archive of github.com/MutantAura/Ryujinx/commit/9cef4ceba40d66492ff775af793ff70e6e7551a9](https://web.archive.org/web/20241122193401/https://github.com/MutantAura/Ryujinx/commit/9cef4ceba40d66492ff775af793ff70e6e7551a9)

Shader counter: ~~https://github.com/MutantAura/Ryujinx/commit/67b873645fd593e83d042a77bf7ab12e5ec97357~~ Original commit has been lost

Thanks MutantAura :D
### GUI:
- Implement shader compile counter (currently not translated, will change, need to pull changes.)
- Remove graphics backend / GPU name event logic in favor of a single init function.

## 1.2.41 - 2024-10-24
PR ~~[#54](https://github.com/GreemDev/Ryujinx/pull/54)~~

Thanks Whitescatz!
### i18n:
- th_TH (Thai): Added missing translations, reduce transliterated words, fix grammar.

## 1.2.40 - 2024-10-23
PR ~~[#40](https://github.com/GreemDev/Ryujinx/pull/40)~~

Thanks Вова С!
### GUI:
- Add option to ignore controller applet upon start.

*This option is under the hacks section for a reason; it ignores intended behavior. Use with caution.

## 1.2.39 - 2024-10-23
### MISC:
- Null-coalesce autoloaddirs on config load.
  - Should prevent crashing on config loads in some circumstances.

## 1.2.38 - 2024-10-23
PR [#51](https://web.archive.org/web/20241127022413/https://github.com/GreemDev/Ryujinx/pull/51)
### i18n:
- zh_CH (Simplified Chinese): Add some missing translations.

## 1.2.37 - 2024-10-23
PR [#37](https://web.archive.org/web/20241123010103/https://github.com/GreemDev/Ryujinx/pull/37)

Thanks Last Breath!
### GUI: 
- Set the default controller to the Pro Controller.

## 1.2.36 - 2024-10-21
PR ~~[#30](https://github.com/GreemDev/Ryujinx/pull/30)~~
### GUI:
- Fix repeated dialog popup notifying you of new updates when there aren't any, while having a bundled update inside an XCI and an external update file.

## 1.2.35 - 2024-10-21
PR [#32](https://web.archive.org/web/20241127010942/https://github.com/GreemDev/Ryujinx/pull/32)
### GUI:
- Replace "expand DRAM" option with a DRAM size dropdown.
  - Allows for using mods which require a ridiculous amount of memory to allocate from.

## 1.2.34 - 2024-10-21
PR [#29](https://web.archive.org/web/20241125093029/https://github.com/GreemDev/Ryujinx/pull/29)
### GUI:
- Fix duplicate controller names when 2 controllers of the same type are connected.
### INPUT:
- Fix invert x, y, and rotate when mapping physical left stick to logical right stick and vice versa.

## 1.2.32-1.2.33 - 2024-10-21
### i18n:   
- fr_FR: Added missing strings and general improvements. 
  - Improve French translation clarity & add missing translations by Nebroc351, helped by Fredy27 in the Discord.

## 1.2.31 - 2024-10-21
### GUI: 
- Revert maximized = fullscreen change.
  - Fixes fullscreen not hiding the Windows taskbar.

## 1.2.30 - 2024-10-19
### GUI: 
- Reload game list on locale change.
- Add keybinds to useful things (namely opening Amiibo scan window (Ctrl + A) and the scan button (Enter)).
- Reset RPC state when AppHost stops.

### MISC:
- XML & code cleanups.

## 1.2.29 - 2024-10-19
### GUI: 
- Remove references to ryujinx.org in the localization files.
- Switch from downloading amiibo.ryujinx.org to just referencing a file in the repo & images in the repo, under assets/amiibo.

This fork is now entirely independent of the existing Ryujinx infrastructure, and as such the Amiibo features will continue to work in my version when they break in the mainline version.

## 1.2.28 - 2024-10-17
### GUI: 
- Fix dialog popups doubling the window controls and laying text over the menu bar.

## 1.2.26 - 2024-10-17
### I18n: 
Added Low-power PPTC mode strings to the translation files.
### GUI:
- Remove OS-provided title bar and put the Ryujinx logo next to "File" in the menu bar.
  - What was in the title bar, Ryujinx version & current game information, is still visible by hovering the Ryujinx icon.
- Added icons to many actions in dropdown menus.
### RPC:
- Added Kirby and the Forgotten Land, Elder Scrolls V Skyrim, and Hyrule Warriors: Age of Calamity to the RPC assets.

## 1.2.25 - 2024-10-14
### CPU: 
- Add low-power PPTC mode.
  - Specifically, this setting causes the core count to get reduced by two-thirds, for lower-power but still fast loading if desired, and for unstable CPUs.

## 1.2.24 - 2024-10-14
### SDL: 
- Move Mouse & MouseDriver to Input project, instead of Headless.

## 1.2.22 - 2024-10-12
### GUI/RPC: 
- Added RDR, Luigi's Mansion 2 HD & 3 asset images.
### MISC:
- Minor code cleanups & improvements.
- Removed duplicate executable in the release bundle (leftovers from GTK & Avalonia dual releases).
- Removed Avalonia test release bundle, which was kept in Ryujinx for the OG Avalonia testers. That doesn't apply to this fork, so it's removed. 

## 1.2.21 - 2024-10-11
### GUI/RPC: 
- Add game version string when hovering large image asset.
- Add version information about this fork to the Ryujinx logo (big when in main menu, small when in game) when hovering.

## 1.2.20 - 2024-10-11
### MISC:
- Code cleanups & remove references to Ryujinx Patreon & Twitter.
### GUI:
- Add more Discord presence assets.

## 1.2.1-1.2.19 - 2024-10-08 - 2024-10-11
### GUI/INFRA/MISC:
- Remove GTK UI.
- Autoload DLC/Updates from dir ([#12](https://web.archive.org/web/20241127004005/https://github.com/GreemDev/Ryujinx/pull/12)).
- Changed executable icon to rainbow logo.
- Extract Data > Logo now also extracts the square thumbnail you see for the game in the UI. 
- The "use random UUID hack" checkbox in the Amiibo screen now remembers its last state when you reopen the window in a given session.
