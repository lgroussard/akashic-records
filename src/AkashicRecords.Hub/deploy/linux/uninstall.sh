#!/usr/bin/env bash
# Removes the Akashic hub service and its program folder.
# The DATA folder (/var/lib/akashic-hub) and the "akashic" user are kept: remove them by hand
# if you really want to lose the journal, the paired devices and the media.
#
#   sudo bash uninstall.sh
set -euo pipefail

APP=/opt/akashic-hub

if [ "$(id -u)" -ne 0 ]; then echo "A lancer avec sudo : sudo bash uninstall.sh" >&2; exit 1; fi

if systemctl list-unit-files akashic-hub.service >/dev/null 2>&1; then
    systemctl disable --now akashic-hub 2>/dev/null || true
fi
for u in akashic-disk-index.timer akashic-disk-index.path; do systemctl disable --now $u 2>/dev/null || true; done
rm -f /etc/systemd/system/akashic-hub.service /etc/systemd/system/akashic-disk-index.{service,timer,path}
systemctl daemon-reload

# Only remove a folder that really is a hub install.
if [ -f "$APP/AkashicRecords.Hub" ]; then
    cd /
    rm -rf "$APP"
    echo "Dossier $APP supprime."
fi
echo "Service supprime. Donnees conservees dans /var/lib/akashic-hub"
