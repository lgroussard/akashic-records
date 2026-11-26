#!/usr/bin/env bash
# Sets up the nightly copy of the hub to the external disk (label STOCKAGE), once.
#   sudo bash setup-backup.sh
# Then every night at 03:30 (or at the next start if the mini-PC was off), backup.sh copies the hub's databases
# and files to <disk>/akashic-sauvegarde. Unplugging the disk is harmless: the copy waits for it.
set -euo pipefail

LABEL=STOCKAGE
MOUNT=/mnt/akashic-sauvegarde
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then echo "A lancer avec sudo : sudo bash setup-backup.sh" >&2; exit 1; fi

echo "Outils : sqlite3, rsync"
apt-get update -qq && apt-get install -y -qq sqlite3 rsync >/dev/null

mkdir -p "$MOUNT"
# Mounted by its label at boot when present; 'nofail' so the mini-PC starts normally without it.
if ! grep -q "LABEL=$LABEL " /etc/fstab; then
    echo "LABEL=$LABEL $MOUNT ntfs3 defaults,nofail,x-systemd.device-timeout=10s 0 0" >> /etc/fstab
    systemctl daemon-reload
    echo "Disque $LABEL ajoute a /etc/fstab (monte sur $MOUNT)"
fi
umount /mnt/stockage 2>/dev/null || true   # the read-only look of the first inspection, if still there
mount "$MOUNT" 2>/dev/null || true
if mountpoint -q "$MOUNT"; then
    df -h "$MOUNT" | tail -1
else
    echo "Disque $LABEL introuvable pour l'instant : la sauvegarde l'attendra (branche-le sur le mini-PC)."
fi

install -m 755 "$SRC/backup.sh" /opt/akashic-hub/backup.sh
install -m 644 "$SRC/akashic-backup.service" /etc/systemd/system/akashic-backup.service
install -m 644 "$SRC/akashic-backup.timer" /etc/systemd/system/akashic-backup.timer
systemctl daemon-reload
systemctl enable --now akashic-backup.timer
echo "Sauvegarde programmee chaque nuit a 03:30 :"
systemctl list-timers akashic-backup.timer --no-pager | head -2

if mountpoint -q "$MOUNT"; then
    echo "Premiere sauvegarde maintenant (quelques minutes pour ~25 Go)..."
    systemctl start akashic-backup.service
    journalctl -u akashic-backup.service -n 3 --no-pager -o cat
    cat "$MOUNT/akashic-sauvegarde/DERNIERE-SAUVEGARDE.txt" 2>/dev/null || true
fi
