#!/usr/bin/env bash
set -euo pipefail

data=/var/lib/cloudagent
backups=/var/backups/cloudagent
keep=7

dest="$backups/$(date +%F)"
mkdir -p "$dest"

while IFS= read -r -d '' db; do
    rel="${db#"$data"/}"
    mkdir -p "$dest/$(dirname "$rel")"
    rm -f "$dest/$rel"
    sqlite3 "$db" ".backup '$dest/$rel'"
done < <(find "$data" -name '*.db' -type f -print0)

if [ -f "$data/people.json" ]; then
    cp "$data/people.json" "$dest/people.json"
fi

find "$backups" -mindepth 1 -maxdepth 1 -type d | sort | head -n -"$keep" | xargs -r rm -rf
