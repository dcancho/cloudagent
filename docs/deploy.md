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

Telegram: talk to `@BotFather`, send `/newbot`, and copy the token. To use the bot in a group, also
send `/setprivacy`, pick the bot and choose `Disable`, then remove the bot from the group and add it
again: Telegram applies the change only on joining. With privacy left on, the bot in a group only
receives messages that mention it or reply to it, so it answers those without knowing what the
group was saying before.

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
  the host log records the refusal with their id:

  ```bash
  sudo journalctl -u cloudagent | grep refused
  ```

  The account is `telegram:<id>`. The same line shows Discord ids of people not yet listed.
- Web chat: the tailnet login, for example `webchat:diego@example.com`.

Edit the file, then restart the service (`sudo systemctl restart cloudagent`) since it is read at
startup:

```bash
sudo -u cloudagent nano /var/lib/cloudagent/people.json
```

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
