<img src="src/Panel.Web/Static/favicon.svg" width="72" alt="">

# Vintage Story Panel

[![CI](https://github.com/ruslan120p/vintage-story-panel-net/actions/workflows/ci.yml/badge.svg)](https://github.com/ruslan120p/vintage-story-panel-net/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
![Platforms: Linux | Windows](https://img.shields.io/badge/platforms-Linux%20%7C%20Windows-lightgrey.svg)

**English** · [Русский](README.ru.md)

A web panel for running dedicated Vintage Story servers. Start and stop, console,
players, worlds, mods, backups, settings and updates — all in the browser, with no
command line on the server.

Runs on Linux and Windows. One panel manages several game servers on the same machine.
The interface is available in English and Russian.

> [!WARNING]
> **Early release — expect breaking changes.** Settings, stored data and the command line
> may change between versions. Take a backup of your worlds before updating the panel.

![Overview: servers, state, resources and system health](screenshots/Overview.png)

## Quick start

**Linux** — one command on the server:

```bash
curl -fsSL https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/get.sh | sudo sh
```

**Windows** — one command in PowerShell:

```powershell
irm https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/windows/get.ps1 | iex
```

The command downloads the latest release package for this machine and runs the installer.

The installer prints a one-time link to the [setup wizard](#setup-wizard): create the
administrator account there, and the panel is ready. Details — in [Installation](#installation).

## Features

**Overview.** The list of servers and the state of the selected one: is it running, is it
ready to accept players, how many are online, how loaded it is. Start, stop and restart
with one button. A summary of the machine itself: CPU and memory load, swap, free space on
the disks that hold the panel, servers and backups, and how much memory the panel uses. Creating a new server: the panel downloads the official game package
itself, verifies its checksum and prepares the directories.

**Players and console.** Who is online and how good their connection is. Chat and game
events. Ready-made console commands from a catalogue: kick, ban, whitelist, granting
roles. Administrators also get a free-form game console; every command is recorded in the
audit log.

**Log and events.** The server log in real time, with categories and a crash summary. The
panel's own log. A diagnostic bundle for support, with passwords and keys stripped out.
Performance charts. A feed of panel events.

**Worlds and mods.**
- Worlds: create, upload, download, copy, switch; an archive and a recycle bin, so that
  deleted worlds can be brought back.
- Mods: upload, enable and disable, roll back to the previous version, mod packs. The
  panel checks that every mod dependency is present and that the versions fit.
- ModDB: search for mods and install them in one click. The panel shows only releases
  compatible with the server's game version and checks compatibility again on download.

**Backups.** On demand and on a schedule, restore everything or just the world,
cleanup rules for old backups.

**Maintenance.**
- Scheduled restarts and backups, with a warning to players in chat.
- Watchdog: makes sure the server actually responds and restarts it when it hangs — no
  more often than a set limit.
- Updates for mods and for the game itself. Before updating the game the panel takes a
  snapshot of the server program, so the update can be rolled back.

**Settings.**
- `serverconfig.json` as a form split into sections: the panel shows what will change
  before saving and lets you revert a change. Everything else — in a JSON editor.
- The template for new worlds and the settings of the running world.
- Mod settings.
- Accounts and roles, two-factor sign-in.
- Notifications to Discord, Telegram or your own address (webhook).

### Roles

| Role | Can |
|---|---|
| Viewer | See the state, the log and the settings. Does not see players' IP addresses. |
| Operator | Everything a viewer can, plus control the server, players, worlds, mods and backups. |
| Administrator | Everything, plus accounts, creating and deleting servers, the free-form game console, panel settings. |

An action that cannot be undone (deleting a world, a server, a backup) asks you to type a
confirmation phrase.

### Screenshots

| | |
|---|---|
| ![Players and console: command catalogue, connections, player actions](screenshots/PlayersConsole.png) | ![Worlds and mods: the ModDB catalogue filtered by game version](screenshots/WorldsMods.png) |
| **Players & console** | **Worlds & mods** — ModDB |
| ![Log and events: the server log with categories and search](screenshots/LogEvents.png) | ![Backups: archives, restore, cleanup](screenshots/Backups.png) |
| **Log & events** | **Backups** |
| ![Maintenance: scheduled restarts and backups](screenshots/Maintenance.png) | ![Settings: serverconfig.json as a form](screenshots/Settings.png) |
| **Maintenance** | **Settings** |
| ![Setup wizard: creating the administrator](screenshots/Installation.png) | |
| **Setup wizard** | |

## Requirements

**Linux**
- A 64-bit x86-64 or ARM64 system with systemd (Debian, Ubuntu and the like).
- The `setpriv` utility (package `util-linux`, usually already installed). The game is
  started through it as a separate unprivileged user, never as root.
- Root access for installation.

**Windows**
- Windows 10, 11 or Windows Server, 64-bit.
- Administrator rights for installation.
- Servers on a local NTFS drive.

**Both:** internet access to download the game and mods. The installer sets up the .NET
runtime for the game itself; the panel does not need a separate .NET installation.

### Panel and game server resources

The panel and game servers run on the same machine. The game accounts for most
of the load; budget resources for all game servers running at the same time.

For the panel and agent without a running game, budget 1–2 modern CPU cores,
0.5–1 GB of RAM in addition to the OS requirements, and 2–3 GB of disk space for
installation, updates and logs. These are planning estimates, not measured minimums:
panel load measurements have not yet been recorded.

The [official Vintage Story guideline](https://www.vintagestory.at/selfhosting/)
is 1 GB of RAM per game server plus 300 MB per concurrent player, with 4 CPU threads
recommended. Mods and active world exploration increase the load.

Practical recommendations with headroom for **the whole machine: OS, panel and one game server**:

| Load | CPU | Total RAM | SSD space for the server and backups |
|---|---|---|---|
| 2–5 players, no heavy mods | 4 fast threads | 8 GB | from 40–60 GB |
| 5–10 players, moderate mods | 4–6 fast threads | 16 GB | from 80–100 GB |
| 10–20 players or a heavy mod pack | 6–8 fast threads | 24–32 GB | from 150 GB |

These are starting points, not guarantees for every mod pack. Disk space depends
on world growth and the number of retained backups. For several servers, add their
RAM requirements and leave headroom for the OS and panel; size the CPU for their
combined load. For example, a reasonable starting point for two servers with
5 players each is 6–8 fast threads, 16 GB of RAM and 150–200 GB of free SSD space;
budget 32 GB of RAM with heavy mods.

Each game server needs a different port. Internet access is needed to download
the game and mods; external players need a reachable game port (with router port
forwarding where necessary). The default panel port is 8080, and the first game
server's default port is 42420.

## Installation

Installation is one command: it takes the latest [release](https://github.com/ruslan120p/vintage-story-panel-net/releases)
package for this machine (`linux-x64`, `linux-arm64` or `win-x64`), verifies it against the
release `SHA256SUMS` and runs the installer from it. The `VS_PANEL_VERSION` variable selects
a specific release, for example `VS_PANEL_VERSION=0.3.0`. If you build the project yourself,
run the installer from the [published package](#from-a-build-directory).

### Linux

```bash
curl -fsSL https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/get.sh | sudo sh
```

It needs `curl` or `wget`, `tar` and `sha256sum` — Debian and Ubuntu have them out of the box.

The installer asks where to keep game servers (Enter — `/srv/vs-panel`). Then it creates
the services and the first server, opens the panel port to the local network and prints a
one-time link to the setup wizard — both on this machine and at its network addresses.

To skip the questions:
- `--servers-dir <directory>` — where to keep servers;
- `VS_PANEL_CREATE_SERVER=0` — do not create the first server, create it in the wizard.

Installer options go after `sh -s --`. Who can reach the panel is set here as well — see
[Panel access](#panel-access):

```bash
curl -fsSL …/deploy/get.sh | sudo sh -s -- --access internet    # open to the internet as well
curl -fsSL …/deploy/get.sh | sudo sh -s -- --access localhost   # this machine only, sign in over an SSH tunnel
curl -fsSL …/deploy/get.sh | sudo VS_PANEL_VERSION=0.3.0 sh     # a specific release
```

### Windows

In PowerShell:

```powershell
irm https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/windows/get.ps1 | iex
```

If PowerShell is not running as administrator, the command asks for the rights and
continues in a new window. Installer options are passed like this:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/windows/get.ps1))) --access internet
```

A specific release — `$env:VS_PANEL_VERSION = '0.3.0'` before the command.

On first installation a folder picker opens for game servers. "Cancel" keeps
`C:\ProgramData\vs-panel\servers`. A folder right at the root of a drive is best, for
example `D:\vs-servers`: a path without spaces, a new or empty folder.

The installer:
- installs two Windows services, `vs-panel-agent` and `vs-panel`, starting automatically
  and restarting on failure;
- creates the first server and opens a firewall rule for its port;
- opens the panel port to the local subnet, so the panel is reachable from other
  computers on the network;
- puts a "Vintage Story Panel" shortcut in the Start menu and on the desktop;
- opens the setup wizard in the browser.

Installer options (the same for `get.ps1` and `install.cmd`):

| Option | Effect |
|---|---|
| `--servers-dir D:\vs-servers` | where to keep servers, without the folder picker |
| `--no-server` | do not create the first server, create it in the wizard |
| `--no-browser` | do not open the browser, only print the link |
| `--skip-dotnet` | do not install the .NET runtime for the game |
| `--access lan\|internet\|localhost` | who can reach the panel, see [Panel access](#panel-access) |

> The services and game servers run as the system account (LocalSystem). The installer
> does not create users, does not ask for a password and does not change the security
> policy. Access to `C:\ProgramData\vs-panel` and to the servers folder stays with the
> system and administrators only.

### From a build directory

Without access to GitHub, or with your own build, run the installer from the package
directory: [publish it](docs/development.md#поставка) (`dotnet build` does not update
`artifacts`) or unpack a release archive, copy it to the machine and run `sudo sh install.sh`
in it on Linux or `install.cmd` on Windows. The options are the same.

### Setup wizard

The link from the installer works once. The wizard asks for:
1. the panel administrator account;
2. panel access: address, port, allowed host names, HTTPS-only cookies. What you chose
   during installation is filled in. If you change the address or the port, the firewall
   rule follows automatically;
3. the first server's parameters: name, port, player limit, world, play style — or skip
   it if you will create the server later.

A lost link can be issued again: `sudo vs-panel access` on Linux or `vs-panel access` in
an administrator command prompt on Windows. Once finished, the wizard is closed for good;
from then on you sign in as usual.

## Signing in

After installation the panel opens on the machine itself at **http://localhost:8080**,
and from other computers on the local network at the machine's address, for example
**http://192.168.1.10:8080**. The installer and `vs-panel access` print the addresses.

The interface language is switched in the panel; English is the default.

### Panel access

Who can reach the panel is set on install, update or repair. The options are the same for
`install.sh`, `install.cmd` and `vs-panel install|update|repair`:

| Option | Effect |
|---|---|
| `--access lan` | the panel listens on all addresses and answers to any name, the port is open to the local network (default) |
| `--access internet` | the same, but the port is open to everyone, the internet included |
| `--access localhost` | this machine only; from another computer — over an SSH tunnel |
| `--port <port>` | panel port |
| `--bind <address>` | the address the panel listens on, if you need one specific address |
| `--allowed-hosts <names>` | comma-separated names and addresses the panel may be reached by; `*` — any |
| `--secure-cookie true` | sign-in over HTTPS only; needs an HTTPS proxy in front of the panel |
| `--firewall lan\|internet\|off` | whom to open the port to; `off` — leave the firewall alone |
| `--trusted-proxies <list>` | reverse proxies whose `X-Forwarded-For` is trusted, see [HTTPS with Caddy](#https-with-caddy) |

Options can be combined: `--access internet --port 9000`. Instead of options you can use
the variables `VS_PANEL_ACCESS`, `VS_PANEL_PORT`, `VS_PANEL_BIND`,
`VS_PANEL_ALLOWED_HOSTS`, `VS_PANEL_SECURE_COOKIE`, `VS_PANEL_FIREWALL`,
`VS_PANEL_TRUSTED_PROXIES`.

To change access for an installed panel:

```bash
sudo vs-panel repair --access internet     # Linux
vs-panel repair --access internet          # Windows, administrator command prompt
```

Settings you do not pass stay as they are. An update without options keeps access as it was.

**Firewall.** The installer opens the panel port in ufw on Linux (if ufw is enabled) and in
Windows Firewall. For the local network on Linux these are the private ranges
`10.0.0.0/8`, `172.16.0.0/12` and `192.168.0.0/16`; on Windows — "local subnet". If the
system uses another firewall (firewalld, nftables), open the port there yourself and
install the panel with `--firewall off`. Access from the internet behind a router also
needs port forwarding to the machine.

**Game ports.** Every game server's port is open to everyone, TCP and UDP: players come
from anywhere. The rule is added when a server is created (by the installer or in the
panel), moved when `Port` changes in the server settings or a settings backup with another
port is restored, and removed when the server is deleted. A port used by another server of
the panel cannot be saved. As with the panel port, on Linux this is done only in ufw and
only when it is enabled; with `--firewall off` the panel leaves the firewall alone. If the
rule could not be added, the panel shows why: open the port yourself.

> **The panel speaks plain HTTP.** The password and the session travel in clear text. On
> your own local network that is usually acceptable; for the internet HTTPS is better, see
> [Publishing on the internet](#publishing-on-the-internet).

An **SSH tunnel** is the safest option: the panel listens on the machine only, nothing is
exposed.

```bash
ssh -L 8080:localhost:8080 server
# then open http://localhost:8080 in the browser
```

### Publishing on the internet

This is how the panel is set up on a VPS or a home server reachable from the internet. A
domain and HTTPS are optional: the panel works by IP address too. Below are three options,
from the simplest to the safest. Players do not care which one you pick: they connect to
the game port, not to the panel.

**Firewall first.** On an Ubuntu VPS ufw is usually disabled, and then everything is open.
Enable it **before installing**, allowing SSH first or you will lock yourself out:

```bash
sudo ufw allow OpenSSH
sudo ufw enable
```

The installer and the panel then open the panel port and the game ports themselves. If ufw
was enabled after installation, run `sudo vs-panel repair --access internet` (or with the
option you choose below) and open the game ports yourself: `sudo ufw allow 42420/tcp` and
`sudo ufw allow 42420/udp`.

**Many VPS providers have their own firewall** (security groups, "Firewall" in the provider's
control panel). The panel cannot see it: open the panel port there (`8080/tcp` by default),
the game ports (`42420` and up, TCP and UDP), and for HTTPS `80/tcp` and `443/tcp`.

**Players connect** in the game via "Multiplayer → Add server" to an address like
`203.0.113.10:42420` or `play.example.com:42420`. Overview shows it in **ADDRESS TO
CONNECT**. The web panel address does not work in the game.

#### Option 1. By IP address, no domain, no HTTPS

The simplest: the panel opens at `http://<server IP>:8080`.

```bash
curl -fsSL …/deploy/get.sh | sudo sh -s -- --access internet
# or for an installed panel:
sudo vs-panel repair --access internet
```

The password and the session travel in clear text and can be seen by anyone whose network
carries the traffic: public Wi-Fi, an ISP. If you choose this option:
- set a long unique password and enable two-factor sign-in (TOTP) in the account settings;
- do not sign in from other people's or public networks;
- give helpers no more than the role they need (Viewer, Operator).

You can change the panel port (`--port 9000`): it does not stop a targeted attack but cuts
down automated guessing.

#### Option 2. By domain, no HTTPS

Easier to remember, the same protection as option 1. You need a DNS **A** record for a
name (for example, `panel.example.com`) pointing at the server IP. No domain of your own —
a free name from a dynamic DNS service such as [DuckDNS](https://www.duckdns.org/) will do.

```bash
sudo vs-panel repair --access internet --allowed-hosts panel.example.com,203.0.113.10
```

`--allowed-hosts` is optional (default `*`, any name), but with it the panel answers only
to its own names and not to other names pointed at your IP.

#### Option 3. By domain with HTTPS (recommended)

The password and the session are encrypted; the certificate is free and renews itself. You
need the DNS record from option 2 and ports `80` and `443` open (in ufw and at the
provider). The panel port no longer needs to be open to the outside: only Caddy on the
same machine talks to it.

#### HTTPS with Caddy

[Caddy](https://caddyserver.com/) gets and renews a Let's Encrypt certificate on its own.
Debian 12 and Ubuntu 24.04 have it in the repository (`sudo apt install caddy`); for other
systems, Windows included, see the [Caddy install guide](https://caddyserver.com/docs/install).
The whole `/etc/caddy/Caddyfile`:

```
panel.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

```bash
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo systemctl reload caddy
```

The panel then listens on the machine only, trusts the domain name and sends its cookie
over HTTPS only:

```bash
sudo vs-panel repair --access localhost --allowed-hosts panel.example.com --secure-cookie true
```

Open `https://panel.example.com`. If no certificate was issued, check
`sudo journalctl -u caddy`: most often the DNS record has not updated yet or port 80 is closed.

**Visitor addresses.** Behind a proxy the panel takes the visitor's address from
`X-Forwarded-For` — for the audit log and for the limit on failed sign-ins. It believes the
header only when the connection itself comes from a trusted proxy; by default that is a
proxy on the same machine (`127.0.0.1,::1`). If the proxy runs on another machine — a
router, a separate nginx — name its address or network:

```bash
sudo vs-panel repair --trusted-proxies 127.0.0.1,::1,10.0.0.2
```

List only your own proxies, never the networks your visitors come from: anyone connecting
from a trusted address can put any address into the header.

### Settings file

All of this lives in `/etc/vs-panel.env` on Linux and `C:\ProgramData\vs-panel\vs-panel.env`
on Windows. You can edit the file by hand and then restart the panel (`vs-panel restart`).
A manual edit does not update the firewall rule: run `vs-panel repair`.

| Setting | Default | Meaning |
|---|---|---|
| `PANEL_BIND` | `0.0.0.0` | the address the panel listens on; `127.0.0.1` — this machine only |
| `PANEL_PORT` | `8080` | panel port |
| `PANEL_ALLOWED_HOSTS` | `*` | host names the panel may be reached by |
| `PANEL_SECURE_COOKIE` | `false` | sign-in over HTTPS only |
| `PANEL_FIREWALL` | `lan` | whom the panel port is open to: `lan`, `internet` or `off`; with `off` the panel leaves the game ports alone too |
| `PANEL_SESSION_HOURS` | `12` | how long a sign-in lasts |
| `PANEL_MAX_UPLOAD_MB` | `256` | upload limit for a mod or a file |
| `PANEL_MAX_WORLD_UPLOAD_MB` | `4096` | upload limit for a world |
| `PANEL_AUDIT_RETENTION_DAYS` | `90` | how long to keep the audit log |
| `PANEL_TRUSTED_PROXIES` | `127.0.0.1,::1` | reverse proxies whose `X-Forwarded-For` is trusted: addresses and networks such as `10.0.0.0/8` |

## Command line

On Linux — with `sudo`; on Windows — in an administrator command prompt, without `sudo`.
After installing on Windows open a new window: the `vs-panel` command appears there.

| Command | Effect |
|---|---|
| `vs-panel status` | state of the services and the panel address |
| `vs-panel access` | panel addresses on the machine and on the network; before the wizard — a new link to it |
| `vs-panel password` | a new password for the `admin` account, if it is forgotten |
| `vs-panel start`, `stop`, `restart` | start, stop, restart the panel |
| `vs-panel doctor` | check the installation without changing anything |
| `vs-panel update` | install the latest release; `--version <version>` — a specific one, `--from <directory>` — from a build directory |
| `vs-panel rollback` | return to the previous version |
| `vs-panel repair` | restore services and file permissions; with `--access` — change panel access |
| `vs-panel logs` | service log |
| `vs-panel servers` | list of game servers |
| `vs-panel uninstall` | remove the panel |
| `vs-panel help` | all commands |

### Updating

```bash
sudo vs-panel update                     # the latest release
sudo vs-panel update --version 0.3.0     # a specific release
sudo vs-panel update --from ./linux-x64  # your own package from a build directory
```

On Windows — the same commands in PowerShell as administrator, without `sudo`. If the
latest release or a newer version is already installed, `update` does nothing. The
installation command from [above](#installation) also updates an installed panel; on Windows
it is the way to reinstall the same version. Windows on ARM has no package yet.

Accounts, servers, worlds and settings are kept. The two previous versions are stored;
`vs-panel rollback` returns to the latest of them. After an interface update, reload the
open browser tab so that it picks up the icon and styles.

### Uninstalling

`vs-panel uninstall` removes the panel services and the firewall rule for its port: the
program, accounts and settings stay, and the panel can be installed again. With `--purge`
the program, the panel data and its settings are removed as well.

Game servers — their program, worlds and backups — are not touched when the panel is
removed. To delete a server completely, do it in the panel before removing the panel. On
Windows `--purge` does not remove the firewall rule for the game port or the .NET runtime
for the game.

## Where things are

| | Linux | Windows |
|---|---|---|
| Program | `/opt/vs-panel` | `C:\Program Files\vs-panel` |
| Panel settings | `/etc/vs-panel.env`, `/etc/vs-panel.json` | `C:\ProgramData\vs-panel\vs-panel.env`, `vs-panel.json` |
| Server registry | `/etc/vs-panel/instances.json` | `C:\ProgramData\vs-panel\instances.json` |
| Panel data | `/var/lib/vs-panel` | `C:\ProgramData\vs-panel\state` |
| Game servers | the chosen directory, `/srv/vs-panel` by default | the chosen folder, `C:\ProgramData\vs-panel\servers` by default |
| Service log | `journalctl -u vs-panel -u vs-panel-agent` | Event Viewer → Windows Logs → Application |

Each server has its own directory: `server` — the game program, `data` — worlds, mods and
settings, `backups` — backups. An administrator changes the folder for new servers in
Settings → Panel. Servers that already exist stay where they are.

## Troubleshooting

1. `vs-panel doctor` checks the services, files, permissions and panel reachability and
   tells you what is wrong.
2. `vs-panel status` — whether the services are running and at what address the panel is.
3. In the panel: Log & events → Diagnostics → download the diagnostic bundle. Passwords and
   keys are stripped from it, so it is safe to share.

If installation stopped with an error, save its output and the output of `vs-panel doctor`.

## How it works and security

The panel is two processes:
- **the web panel** (`vs-panel-web`) serves the interface. On Linux it runs as the
  unprivileged `vs-panel` user and has no access to game directories;
- **the agent** (`vs-panel-agent`) does everything that needs privileges: services, game
  files, the firewall. It runs as root on Linux and as LocalSystem on Windows.

The panel sends the agent only a server ID and an operation; the agent works out every
path itself from its own registry. It answers only the panel: on Linux it checks the
caller's user ID on its Unix socket (`SO_PEERCRED`), on Windows its named pipe is open to
the service account and administrators only.

- The game never runs as root: on Linux it is started as a separate `vsgame` user.
- An uploaded archive is judged as a whole — every path and link in it — before anything
  is extracted.
- Passwords are hashed with scrypt; two-factor sign-in (TOTP) is available.
- Changes made through the panel are recorded in the audit log; the free-form game console
  is for administrators only, and every line sent to it is recorded in full.

## Reporting problems

Open an [issue](https://github.com/ruslan120p/vintage-story-panel-net/issues) with:
- the operating system and the panel version (`vs-panel version`);
- what you did and what happened;
- the output of `vs-panel doctor` and, if possible, the diagnostic bundle (see
  [Troubleshooting](#troubleshooting)).

Please report security problems privately, through
[a security advisory](https://github.com/ruslan120p/vintage-story-panel-net/security/advisories/new),
not in a public issue.

Pull requests are welcome. For anything larger than a fix, open an issue first so we can
agree on the approach.

## Building from source

You need the [.NET SDK 10](https://dotnet.microsoft.com/download). The tests need no game
server and run on Linux and Windows:

```bash
dotnet build
dotnet test
```

To build an installation package (self-contained, no .NET needed on the target machine):

```bash
bash scripts/publish.sh linux-x64      # or linux-arm64
```

```powershell
.\scripts\publish.ps1 win-x64
```

The package lands in `artifacts/<target>` — that is the directory the installer runs
from. A release is published by the tag `v<version>`, matching `PanelVersion` in
`Directory.Build.props`. Style follows `.editorconfig`; check it with
`dotnet format whitespace --verify-no-changes`. How the code is organised, the rules the
tests enforce and how the installer works — in [docs/development.md](docs/development.md)
(in Russian).

## License

[MIT](LICENSE).

Vintage Story Panel is an independent project. It is not affiliated with, endorsed by or
supported by Anapoly Games, the developer of Vintage Story.
