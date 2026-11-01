#!/usr/bin/env bash
# Sets up the unprivileged sandbox user and rootless Docker on Ubuntu 24.04 or later, after provision.sh.
# Run as root from the copied deploy/sandbox folder. Safe to repeat. Builds nothing: run build-sandbox.sh next.
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash provision-sandbox.sh" >&2; exit 1; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sandbox_user=cloudagent-sandbox
sandbox_home=/var/lib/cloudagent-sandbox
service_user=cloudagent

id "$service_user" >/dev/null 2>&1 || { echo "Run provision.sh first: the $service_user user does not exist." >&2; exit 1; }

export DEBIAN_FRONTEND=noninteractive

wait_for_apt() {
    while fuser /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock >/dev/null 2>&1; do
        echo "Waiting for another package manager (often unattended-upgrades) to finish..."
        sleep 10
    done
}

wait_for_apt
apt-get -o DPkg::Lock::Timeout=600 update
apt-get -o DPkg::Lock::Timeout=600 install -y uidmap dbus-user-session slirp4netns acl curl ca-certificates sudo psmisc

if [ ! -f /etc/apt/sources.list.d/docker.list ]; then
    install -d -m 0755 /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
    chmod 0644 /etc/apt/keyrings/docker.asc
    # shellcheck disable=SC1091
    codename="$(. /etc/os-release && echo "$VERSION_CODENAME")"
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $codename stable" \
        > /etc/apt/sources.list.d/docker.list
    wait_for_apt
    apt-get -o DPkg::Lock::Timeout=600 update
fi
apt-get -o DPkg::Lock::Timeout=600 install -y docker-ce docker-ce-cli docker-ce-rootless-extras

systemctl disable --now docker.service docker.socket containerd.service >/dev/null 2>&1 || true

id "$sandbox_user" >/dev/null 2>&1 || useradd --create-home --home-dir "$sandbox_home" --shell /usr/sbin/nologin "$sandbox_user"
grep -q "^$sandbox_user:" /etc/subuid || usermod --add-subuids 100000-165535 "$sandbox_user"
grep -q "^$sandbox_user:" /etc/subgid || usermod --add-subgids 100000-165535 "$sandbox_user"

sandbox_uid="$(id -u "$sandbox_user")"
runtime_dir="/run/user/$sandbox_uid"

loginctl enable-linger "$sandbox_user"
systemctl start "user@$sandbox_uid.service"
for _ in $(seq 1 30); do
    [ -S "$runtime_dir/bus" ] && break
    sleep 1
done
[ -S "$runtime_dir/bus" ] || { echo "The user session for $sandbox_user did not start." >&2; exit 1; }

as_sandbox() {
    runuser -u "$sandbox_user" -- env \
        HOME="$sandbox_home" \
        XDG_RUNTIME_DIR="$runtime_dir" \
        DBUS_SESSION_BUS_ADDRESS="unix:path=$runtime_dir/bus" \
        PATH=/usr/bin:/bin:/usr/sbin:/sbin \
        "$@"
}

if [ ! -f "$sandbox_home/.config/systemd/user/docker.service" ]; then
    as_sandbox dockerd-rootless-setuptool.sh install
fi
as_sandbox systemctl --user enable --now docker.service

install -o root -g root -m 0755 "$here/cloudagent-sandbox-run" /usr/local/bin/cloudagent-sandbox-run

setfacl -m "u:$sandbox_user:--x" /etc/cloudagent
[ -f /etc/cloudagent/sandbox-images ] || printf '%s\n' "cloudagent-sandbox-dotnet:latest" > /etc/cloudagent/sandbox-images
[ -f /etc/cloudagent/sandbox-repos ] || printf '%s\n' "# name=absolute path of the directory mounted as /work" > /etc/cloudagent/sandbox-repos
chown root:"$sandbox_user" /etc/cloudagent/sandbox-images /etc/cloudagent/sandbox-repos
chmod 0640 /etc/cloudagent/sandbox-images /etc/cloudagent/sandbox-repos

sudoers=/etc/sudoers.d/cloudagent-sandbox
echo "$service_user ALL=($sandbox_user) NOPASSWD: /usr/local/bin/cloudagent-sandbox-run" > "$sudoers.tmp"
visudo -cf "$sudoers.tmp" >/dev/null
chmod 0440 "$sudoers.tmp"
mv "$sudoers.tmp" "$sudoers"

# sudo cannot switch users while the service has NoNewPrivileges set.
install -d -m 0755 /etc/systemd/system/cloudagent.service.d
printf '[Service]\nNoNewPrivileges=false\n' > /etc/systemd/system/cloudagent.service.d/sandbox.conf
systemctl daemon-reload

cat <<MSG

Sandbox user and rootless Docker are ready. Next:
  1. sudo bash build-sandbox.sh        (builds the image and starts the egress proxy)
  2. Add the repository directory to /etc/cloudagent/sandbox-repos as name=/absolute/path
  3. Make that directory writable by the container user (see docs/deploy.md)
  4. Set SANDBOX_RUNNER=/usr/local/bin/cloudagent-sandbox-run and SANDBOX_IMAGE=cloudagent-sandbox-dotnet:latest
     in /etc/cloudagent/cloudagent.env, then sudo systemctl restart cloudagent
MSG
