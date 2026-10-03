# Security policy

Vintage Story Panel runs a privileged agent (root on Linux, LocalSystem on Windows) next to
a web interface, so security reports are taken seriously.

## Reporting a vulnerability

Please report vulnerabilities privately through
[a GitHub security advisory](https://github.com/ruslan120p/vintage-story-panel-net/security/advisories/new),
not in a public issue, discussion or pull request.

Include what you can:
- the panel version (`vs-panel version`) and the operating system;
- what an attacker needs (network access to the panel, an account and its role, a local
  account on the machine) and what they gain;
- steps to reproduce, or a proof of concept.

The maintainer aims to answer within a week. Once a fix is released, the advisory is
published and the reporter credited, unless you prefer otherwise.

## Supported versions

Fixes go into the latest release only. Update with `vs-panel update`.

## Scope

In scope: the web panel, the agent and the protocol between them, the installer
(`vs-panel`, `install.sh`, `install.cmd`, `get.sh`, `get.ps1`), and the telemetry mod shipped
with the panel.

Out of scope: the Vintage Story game server and third-party mods; findings that need an
administrator account of the panel (an administrator can already use the game console and
manage servers); and attacks that need plain HTTP to be intercepted when the panel was
exposed to the internet without HTTPS against the README's advice.
