# Deploying to a single VPS

Target: one Ubuntu 24.04 or later VPS (2 vCore, 4 GB RAM), the host running under systemd as the
unprivileged user `cloudagent`, reachable only through Tailscale and the outbound Discord and
Telegram connections. Nothing listens on a public port except SSH, and after step 6 not even that.

| What | Where |
| --- | --- |
| Binaries | `/opt/cloudagent/<version>`, with `/opt/cloudagent/current` pointing at the live one |
| Environment | `/etc/cloudagent/cloudagent.env` (root:cloudagent, 0640) |
| Data | `/var/lib/cloudagent`: `workspace/`, `memory/people/`, `people.json` |
| Backups | `/var/backups/cloudagent/<date>/`, the last 7 days |

The build is self-contained for linux-x64, so the server needs no .NET. SQLite ships as
`libe_sqlite3.so` inside the publish output. The server does need ICU, which `provision.sh` installs:
Discord.Net reads each server's locale, and without ICU it cannot load any server, so the bot only
sees direct messages.

## 1. Provision the VPS

Create the VPS with Ubuntu 24.04 or later and your SSH key, then log in as the default user (`ubuntu` on
OVHcloud). From the repository root on your machine:

```powershell
scp -r deploy ubuntu@<vps-ip>:~/deploy
ssh ubuntu@<vps-ip>
```

On the server:

```bash
cd ~/deploy
sudo bash provision.sh
```

The script is safe to run again. It creates the `cloudagent` user and the directories, installs
`sqlite3`, `ufw` and Tailscale, installs the systemd units and the daily backup timer, and denies all
incoming traffic except SSH. It also lets the login user run `sudo systemctl restart cloudagent`
without a password, which `publish.ps1` needs. The service is enabled but not started.

If you deploy as a user other than the one you are logged in as, run
`sudo DEPLOY_USER=<user> bash provision.sh` and pass `-User <user>` to `publish.ps1`.

## 2. Join the tailnet

```bash
sudo tailscale up
```

Open the URL it prints and approve the machine. Then from your machine check that
`ssh ubuntu@<tailnet-name>` works, where `<tailnet-name>` is the machine name shown in the Tailscale
admin console (or its MagicDNS name). Use it from here on.

In the admin console, under DNS, enable MagicDNS and HTTPS certificates; `tailscale serve` needs both.

## 3. Create the bots

Discord, in the [developer portal](https://discord.com/developers/applications): create an
application, add a bot, copy its token, and turn on the Message Content intent. Invite it to a
server you share with the people who will use it; direct messages work from any shared server.

Telegram: talk to `@BotFather`, send `/newbot`, and copy the token.

## 4. Fill in the configuration

On the server:

```bash
sudo nano /etc/cloudagent/cloudagent.env
```

Replace every `<placeholder>`. Delete the lines for anything you are not using: a placeholder token
makes that surface try to connect with it. `TAVILY_API_KEY` is optional. `CODE_REPOS` stays unset, so
the pilot cannot run commands on the server. Every variable is described in the Configuration table
of the [README](../README.md).

`WEBCHAT_ANONYMOUS=0` means the web chat only lets in people Tailscale identifies, and only if their
tailnet login is listed in `people.json`.

## 5. People

`/var/lib/cloudagent/people.json` is the allowlist. Anyone not listed is refused on Discord and
Telegram. See `deploy/people.example.json` for the shape and the README for the semantics.

Finding ids:

- Discord: in Discord, User Settings, Advanced, turn on Developer Mode. Right-click a user, Copy User
  ID. The account is `discord:<id>`.
- Telegram: have the person send any message to the bot. It answers that they are not authorized, and
  the host log records the refusal with their id. `cloudagent-admin people refused` lists them; the
  account is `telegram:<id>`. It also shows Discord ids of people not yet listed.
- Web chat: the tailnet login, for example `webchat:diego@example.com`.

Edit the file with the admin commands, then restart the service (`sudo systemctl restart
cloudagent`) since it is read at startup:

```bash
cloudagent-admin people add jose
cloudagent-admin people link jose discord:485305305144754176
cloudagent-admin people list
```

`provision.sh` installs `cloudagent-admin`. On a server provisioned before it existed, copy
`deploy/cloudagent-admin` to `/usr/local/bin` with mode 0755.

`cloudagent-admin status` shows the provider, surfaces, people, budget, and what the models cost
today. `cloudagent-admin usage --since 7d` breaks the model calls down by purpose, model, and
agent; set `MODEL_PRICES` in `/etc/cloudagent/cloudagent.env` to get dollars.

## 6. First deploy

From the repository root in PowerShell:

```powershell
.\deploy\publish.ps1 -Server <tailnet-name>
```

It publishes self-contained for linux-x64, copies the output to `/opt/cloudagent/<version>` over
`scp`, switches `current`, restarts the service and prints its status. Releases are named by
timestamp and commit; the last 5 are kept.

Check the service and the log:

```bash
systemctl status cloudagent
sudo journalctl -u cloudagent -f
```

The startup lines show the provider, the surfaces (`Surfaces:  webchat, telegram, discord`, no
`cli`) and the people loaded. Message the bot from an allowed account.

## 7. Expose the web chat inside the tailnet

```bash
sudo tailscale serve --bg 5080
tailscale serve status
```

The web chat is then at `https://<machine>.<tailnet>.ts.net/`, only for devices on the tailnet.
Do not use `tailscale funnel`: it publishes the service on the public internet.

## 8. Close SSH to the public internet

Only once step 2's `ssh ubuntu@<tailnet-name>` works:

```bash
sudo ufw allow in on tailscale0 to any port 22 proto tcp
sudo ufw delete allow OpenSSH
sudo ufw status verbose
```

Discord and Telegram only need outbound connections, so no other rule is needed. If you lock
yourself out, the OVHcloud KVM console from the control panel still reaches the machine.

## Sandbox for coding commands (optional)

`CODE_REPOS` stays unset until this is in place, because `run_command` would otherwise execute on the
server as `cloudagent`. With the sandbox, a conversation that runs commands gets one session container
under a separate user, `cloudagent-sandbox`, in rootless Docker. It starts with the conversation's first
command and serves the later ones, so restored packages and build output persist. There is one session
at a time (`SANDBOX_MAX_SESSIONS`); a second conversation is told the sandbox is in use. A session is
removed after 30 minutes without commands, after 4 hours, when its conversation is deleted, and when
the host stops; leftovers from a crashed host are removed at the next start. Its directory is kept.

```bash
cd ~/deploy/sandbox
sudo bash provision-sandbox.sh
sudo bash build-sandbox.sh
```

`provision-sandbox.sh` creates the user with subuid and subgid ranges and lingering, installs rootless
Docker for it, installs `/usr/local/bin/cloudagent-sandbox-run`, creates the two allowlists
`/etc/cloudagent/sandbox-images` and `/etc/cloudagent/sandbox-repos`, and lets `cloudagent` run only that
script as `cloudagent-sandbox`. Because `sudo` cannot switch users under `NoNewPrivileges`, it also
adds a drop-in that turns that setting off for the service. `build-sandbox.sh` builds the .NET image
and starts the egress proxy; run it again after editing `deploy/sandbox/egress/allowed-domains.txt`.

Then:

1. Add the repository to `/etc/cloudagent/sandbox-repos` as `name=/absolute/path`, the same name and path
   as in `CODE_REPOS`. This directory is the only thing mounted in the container, as `/work`.
2. Rootless Docker maps the container users to unprivileged ids of `cloudagent-sandbox`: uid 1000
   (`runner`) to the first id of its range in `/etc/subuid` plus 999, and uid 1001 (`coder`, used by
   the coding agent later) to that plus 1000. Give both ids, and the service user `cloudagent`, read and
   write access to the directory, as access and as default ACL, so the service can edit files the
   container creates:

   ```bash
   first=$(awk -F: '$1=="cloudagent-sandbox" {print $2}' /etc/subuid)
   sudo setfacl -R -m u:$((first+999)):rwX,u:$((first+1000)):rwX,u:cloudagent:rwX <path>
   sudo setfacl -R -d -m u:$((first+999)):rwX,u:$((first+1000)):rwX,u:cloudagent:rwX <path>
   ```
3. In `/etc/cloudagent/cloudagent.env` set `CODE_REPOS`, `SANDBOX_RUNNER=/usr/local/bin/cloudagent-sandbox-run`
   and `SANDBOX_IMAGE=cloudagent-sandbox-dotnet:latest`, then restart the service.

What it protects: a command runs with no capabilities, a read-only root filesystem, no new privileges,
limits on processes, memory and CPUs, and one writable mount. Its network is internal to Docker; the
only way out is a Squid proxy that allows the hosts in `allowed-domains.txt`, for now the NuGet hosts
only (the host does every git operation, and the coding agent's hosts come with the coding agent). The
wrapper accepts only a session id, a repository and an image from the allowlists, and a command; it
builds the docker command lines itself, never mounts the Docker socket, and never uses host networking
or `--privileged`. A container escape lands in `cloudagent-sandbox`, which owns nothing the host owns.
Every command runs under `timeout` inside the container, which stops the process itself and not only
the `docker exec` client, and a session container ends by itself after 4 hours. `/tmp` is a tmpfs
(`SANDBOX_TMP_SIZE`, 1g) that holds the package cache and build temp for the session's whole life.

What it does not protect:

- The mounted repository is writable by the commands, and an allowed host can still receive whatever
  they send it.
- `provision-sandbox.sh` turns `NoNewPrivileges` off for the whole `cloudagent` service so `sudo` can
  switch users. That weakens the host unit. A socket-activated wrapper would avoid it; it is planned,
  not done.
- The rootless Docker containers run outside the service's `MemoryMax`, so the sandbox's 2 GB comes on
  top of it on a 4 GB machine.
- Once the coding agent exists, its own shell can read its own login.

## Updating and rolling back

Deploy a new version by running `publish.ps1` again.

Rollback is switching `current` back and restarting:

```bash
ls /opt/cloudagent
ln -sfn /opt/cloudagent/<previous-version> /opt/cloudagent/current
sudo systemctl restart cloudagent
```

Data under `/var/lib/cloudagent` is untouched by deploys and rollbacks.

## Logs

```bash
sudo journalctl -u cloudagent -f
sudo journalctl -u cloudagent --since "1 hour ago"
```

The host also keeps a daily log and per-agent transcripts under
`/var/lib/cloudagent/workspace/.agent/`.

## Backups

`cloudagent-backup.timer` runs daily at about 03:30. It copies every `*.db` under
`/var/lib/cloudagent`, and `people.json`, with `sqlite3 ".backup"` into
`/var/backups/cloudagent/<date>/` and removes all but the last 7 days.

```bash
systemctl list-timers cloudagent-backup.timer
sudo systemctl start cloudagent-backup.service
ls -R /var/backups/cloudagent
```

Restore by stopping the service, copying a file from a dated folder back to the same relative path
under `/var/lib/cloudagent` as `cloudagent:cloudagent`, and starting the service.

These backups are on the same disk as the data. Also enable the automatic daily backup on the OVHcloud
VPS, which covers the whole machine.
