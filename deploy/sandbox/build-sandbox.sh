#!/usr/bin/env bash
# Builds the sandbox image and (re)starts the egress proxy, both inside the sandbox user's rootless Docker.
# Run as root from the copied deploy/sandbox folder after provision-sandbox.sh. Safe to repeat; run it again
# after editing egress/allowed-domains.txt.
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo bash build-sandbox.sh" >&2; exit 1; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sandbox_user=cloudagent-sandbox
sandbox_home=/var/lib/cloudagent-sandbox
image="${SANDBOX_IMAGE:-cloudagent-sandbox-dotnet:latest}"
internal=cloudagent-sandbox-internal
outside=cloudagent-sandbox-egress
proxy=cloudagent-egress

sandbox_uid="$(id -u "$sandbox_user")"
runtime_dir="/run/user/$sandbox_uid"

docker_as_sandbox() {
    runuser -u "$sandbox_user" -- env \
        HOME="$sandbox_home" \
        XDG_RUNTIME_DIR="$runtime_dir" \
        DOCKER_HOST="unix://$runtime_dir/docker.sock" \
        PATH=/usr/bin:/bin \
        docker "$@"
}

state="$sandbox_home/build"
rm -rf "$state"
install -d -o "$sandbox_user" -g "$sandbox_user" -m 0700 "$state" "$state/image" "$state/egress"
install -o "$sandbox_user" -g "$sandbox_user" -m 0644 "$here/Dockerfile" "$state/image/Dockerfile"
install -o "$sandbox_user" -g "$sandbox_user" -m 0644 "$here/egress/Dockerfile" "$here/egress/squid.conf" "$state/egress/"
install -o "$sandbox_user" -g "$sandbox_user" -m 0644 "$here/egress/allowed-domains.txt" "$state/allowed-domains.txt"

docker_as_sandbox build --tag "$image" "$state/image"
docker_as_sandbox build --tag cloudagent-egress:latest "$state/egress"

docker_as_sandbox network inspect "$outside" >/dev/null 2>&1 || docker_as_sandbox network create "$outside"
docker_as_sandbox network inspect "$internal" >/dev/null 2>&1 || docker_as_sandbox network create --internal "$internal"

docker_as_sandbox rm --force "$proxy" >/dev/null 2>&1 || true
docker_as_sandbox run --detach \
    --name "$proxy" \
    --restart unless-stopped \
    --network "$outside" \
    --cap-drop=ALL \
    --security-opt no-new-privileges \
    --read-only \
    --tmpfs /tmp \
    --tmpfs /var/cache/squid \
    --tmpfs /var/log/squid \
    --memory 128m \
    --pids-limit 64 \
    -v "$state/allowed-domains.txt:/etc/squid/allowed-domains.txt:ro" \
    cloudagent-egress:latest
docker_as_sandbox network connect "$internal" "$proxy"

echo "Image $image built; egress proxy $proxy is running on $internal."
