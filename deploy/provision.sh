#!/usr/bin/env bash
# One-time server setup for Ubuntu 24.04 or later. Run as root from the copied deploy folder. Safe to repeat.
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash provision.sh" >&2; exit 1; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
deploy_user="${DEPLOY_USER:-${SUDO_USER:-}}"
if [ -z "$deploy_user" ] || [ "$deploy_user" = root ]; then
    echo "Set DEPLOY_USER to the account that will run publish.ps1." >&2
    exit 1
fi
id "$deploy_user" >/dev/null

export DEBIAN_FRONTEND=noninteractive

wait_for_apt() {
    while fuser /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock >/dev/null 2>&1; do
        echo "Waiting for another package manager (often unattended-upgrades) to finish..."
        sleep 10
    done
}

wait_for_apt
apt-get -o DPkg::Lock::Timeout=600 update
icu="$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1)"
apt-get -o DPkg::Lock::Timeout=600 install -y sqlite3 ufw curl ca-certificates psmisc "$icu"

id cloudagent >/dev/null 2>&1 || useradd --system --home-dir /var/lib/cloudagent --shell /usr/sbin/nologin cloudagent

install -d -o cloudagent -g cloudagent -m 0750 /var/lib/cloudagent /var/lib/cloudagent/workspace /var/lib/cloudagent/memory /var/lib/cloudagent/memory/people
install -d -o cloudagent -g cloudagent -m 0750 /var/backups/cloudagent
install -d -o "$deploy_user" -g "$deploy_user" -m 0755 /opt/cloudagent
install -d -o root -g cloudagent -m 0750 /etc/cloudagent

[ -f /etc/cloudagent/cloudagent.env ] || install -o root -g cloudagent -m 0640 "$here/cloudagent.env.example" /etc/cloudagent/cloudagent.env
[ -f /var/lib/cloudagent/people.json ] || install -o cloudagent -g cloudagent -m 0640 "$here/people.example.json" /var/lib/cloudagent/people.json

install -o root -g root -m 0755 "$here/backup.sh" /usr/local/sbin/cloudagent-backup
install -o root -g root -m 0644 "$here/cloudagent.service" "$here/cloudagent-backup.service" "$here/cloudagent-backup.timer" /etc/systemd/system/

sudoers=/etc/sudoers.d/cloudagent-deploy
echo "$deploy_user ALL=(root) NOPASSWD: /usr/bin/systemctl restart cloudagent, /usr/bin/systemctl status cloudagent" > "$sudoers.tmp"
visudo -cf "$sudoers.tmp" >/dev/null
chmod 0440 "$sudoers.tmp"
mv "$sudoers.tmp" "$sudoers"

systemctl daemon-reload
systemctl enable cloudagent.service
systemctl enable --now cloudagent-backup.timer

ufw default deny incoming
ufw default allow outgoing
ufw allow OpenSSH
ufw --force enable

if ! command -v tailscale >/dev/null 2>&1; then
    wait_for_apt
    curl -fsSL https://tailscale.com/install.sh | sh
fi

cat <<'MSG'

Provisioned. Next:
  1. Fill in /etc/cloudagent/cloudagent.env and /var/lib/cloudagent/people.json
  2. sudo tailscale up
  3. Run publish.ps1 from your machine
The service is enabled but not started.
MSG
