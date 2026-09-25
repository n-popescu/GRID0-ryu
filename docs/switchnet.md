# SwitchNet support

Playing a game against a self-hosted [SwitchNet](https://github.com/n-popescu/switchnet)
server instead of Nintendo's.

Everything here is off by default and does nothing until you turn it on.

## What it does, and what it does not

There are **two** halves, and they solve different problems. Both are needed.

| | What it covers | Where it is set |
| --- | --- | --- |
| **Traffic redirect + certificate trust** | the *game's own* connections | see [`private-servers.md`](private-servers.md) |
| **Account login** | the *emulator* fetching an identity token to hand the game | Settings → Network → SwitchNet Account |

[`private-servers.md`](private-servers.md) covers the first half — pointing Nintendo's
hostnames at your server with the SD card's hosts file, and trusting the CA that
signed its certificate. Do that first; this page is only the second half.

The account login exists because a game asks the account service for a BAAS
identity token before it can reach any online game server. Ryujinx generates one
locally: the right shape, signed with an RSA key it makes up on the spot. That
is fine against a server that never checks the signature, and useless against
one that does — which SwitchNet does.

With a login configured, the emulator logs in the way a *person* does — the
username and password chosen at self-registration on the server's own
`/register` page — and hands the game the token **your server signed**:

- `POST /login` — your username and password, answered by a single response
  carrying the id token.

Not `/1.0.0/login`: that is the real, Nintendo-shaped device-account protocol
a console uses, keyed on a device account id the emulator has no way to
already hold. `/login` is SwitchNet's own, simpler endpoint for exactly this
case — the server resolves (or, the first time, silently creates) a device
account behind the scenes; you never see or need its id.

No Nintendo credential is presented, verified or forged anywhere in this. The
token involved is one your own server signed with its own key.

## Setting it up

### 1. Make an account on the server

Open `https://<your-server>/register` in a browser (or wherever your operator
told you it lives) and pick a username and password. That is the one time you
need to think about credentials — everything after this reuses them.

### 2. Redirect the guest's traffic

Follow [`private-servers.md`](private-servers.md): generate the hosts file with
`switchnetctl hosts`, put it on the emulated SD card and tick **Redirect Nintendo's
hostnames**. **Trusted CA certificate** can stay empty: the SwitchNet Local CA
(the one switchnet-nro installs on a console) is built in and used whenever a
private server is set. Point it at a file only if your server's certificate is
signed by a different CA.

### 3. Fill in the login

In *Settings → Network*, under **SwitchNet Account**:

- **Server** — where your server is, e.g. `192.168.1.50`, or `192.168.1.50:8443`
  if the edge is not on 443. A hostname works too.
- **Username** — from step 1.
- **Password** — from step 1.

Point this at the **same server** the hosts file does. They are separate
mechanisms and nothing forces them to agree; if they disagree, the game talks to
one server while the emulator logs into another, and the game server rejects a
token it never issued.

Leaving these empty is a valid configuration: the traffic redirect still works,
and the game gets the locally generated token as before. The three fields are
the switch — there is no separate on/off, because credentials sitting next to an
"off" is a state where two settings contradict each other and one wins quietly.

## When it does not work

Failures are logged once per distinct reason under `ServiceAcc`, with the actual
cause rather than a generic failure:

| Log line | What to do |
| --- | --- |
| `login was refused (401)` | the username or password is wrong |
| `could not reach … at <address>` | the server address is wrong, or the server is not running |
| `is not a host or host:port` | the address field is malformed |
| `did not finish within 10s` | the server is reachable but not answering |

After any of these the game falls back to the locally generated token, so it
will still start and then fail at the game server. The log line is the diagnosis.

## Splatoon 3 needs one more thing (handled automatically)

Splatoon 3 does not use the console's TLS stack. Its gRPC/HTTP2 game-server
client (NPLN) does TLS itself, over a raw socket, with its own embedded
BoringSSL and its own pinned certificates — so the emulator only ever sees
ciphertext and the certificate trust `private-servers.md` sets up cannot
reach it. Without a fix, the game just sits on "connecting" forever, TLS
handshake and all, once traffic is redirected to a private server.

Ryujinx-LanPlay applies the same certificate-pinning patch a real console
running SwitchNet needs **automatically**, in memory, whenever
**Private server address** (`private-servers.md`) is set — no
`exefs_patches` mod to install by hand. See
`PrivateServerSplatoon3Patches.cs` for the patch bytes and the build ids
they cover, and `kinnay/NPLN-Protocols`' `generate_patch.py` for the tool
that originally produces them. It *disables* certificate verification
rather than adding trust, and it is keyed to the game's exact build: a
title update changes the executable and can silently stop it matching. If
Splatoon 3 stops connecting after updating the game, that table is the
first thing to check.
