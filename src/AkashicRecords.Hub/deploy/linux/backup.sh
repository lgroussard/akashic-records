#!/usr/bin/env bash
# Nightly copy of the hub to the external disk (run by akashic-backup.timer, as root).
#
#   <disk>/akashic-sauvegarde/
#       base/hub-AAAA-MM-JJ.db, downloads-AAAA-MM-JJ.db   the databases, the last 7 days kept
#       blobs/                                            every file of the catalogue (music, photos, books…)
#       fichiers/                                         the user's own folder (~lgroussard/fichiers)
#       DERNIERE-SAUVEGARDE.txt                           when and what, readable from any computer
#
# The databases are copied with SQLite's online backup (consistent while the hub runs). Files are only added
# or updated, never removed from the copy: a file deleted by mistake on the server is still on the disk.
# No disk plugged in: nothing happens, the next night tries again.
set -euo pipefail

MOUNT=/mnt/akashic-sauvegarde
DATA=/var/lib/akashic-hub
USER_FILES=/home/lgroussard/fichiers
DEST="$MOUNT/akashic-sauvegarde"
KEEP=7

log() { echo "$(date '+%F %T') $*"; }

# What the apps' "Serveur" screen shows (ServerStatus.cs): the last run's outcome, written after every run.
STATUS="$DATA/backup-status.json"
start=$(date +%s)
status() { # state message [bytes files]
    local last_ok
    last_ok=$(grep -o '"last_ok_at": *"[^"]*"' "$STATUS" 2>/dev/null | sed 's/.*: *"\(.*\)"/\1/' || true)
    [ "$1" = ok ] && last_ok=$(date -u +%FT%TZ)
    local total=null free=null
    if mountpoint -q "$MOUNT"; then
        read -r total free < <(df -B1 --output=size,avail "$MOUNT" | tail -1) || true
    fi
    cat > "$STATUS.part" <<JSON
{"state": "$1", "at": "$(date -u +%FT%TZ)", "last_ok_at": "${last_ok}", "message": "$2", "bytes": ${3:-null}, "files": ${4:-null}, "disk_total": ${total:-null}, "disk_free": ${free:-null}, "seconds": $(( $(date +%s) - start ))}
JSON
    chmod 644 "$STATUS.part" && mv -f "$STATUS.part" "$STATUS"
}
trap 'status failed "la copie s est arretee sur une erreur (journalctl -u akashic-backup)"' ERR

if ! mountpoint -q "$MOUNT"; then
    mount "$MOUNT" 2>/dev/null || true
fi
if ! mountpoint -q "$MOUNT"; then
    log "disque de sauvegarde absent : rien a faire cette nuit"
    status absent "disque de sauvegarde non branche"
    exit 0
fi
if ! touch "$MOUNT/.akashic-test" 2>/dev/null; then
    log "disque monte en lecture seule (Windows ne l'a pas ejecte proprement ?) : sauvegarde impossible"
    status failed "disque en lecture seule (mal ejecte de Windows ?)"
    exit 1
fi
rm -f "$MOUNT/.akashic-test"

mkdir -p "$DEST/base" "$DEST/blobs" "$DEST/fichiers"
day=$(date +%F)

for db in hub downloads; do
    if [ -f "$DATA/$db.db" ]; then
        sqlite3 "$DATA/$db.db" ".backup '$DEST/base/$db-$day.db.part'"
        mv -f "$DEST/base/$db-$day.db.part" "$DEST/base/$db-$day.db"
    fi
done
# Only the last $KEEP days of each database.
for db in hub downloads; do
    ls -1 "$DEST/base/$db-"*.db 2>/dev/null | sort | head -n -"$KEEP" | xargs -r rm -f
done

# Content-addressed blobs never change once written: a plain add-only copy.
rsync -rt --size-only "$DATA/blobs/" "$DEST/blobs/"
[ -d "$USER_FILES" ] && rsync -rt "$USER_FILES/" "$DEST/fichiers/"

blobs=$(find "$DEST/blobs" -type f | wc -l)
size=$(du -sh "$DEST" | cut -f1)
{
    echo "Derniere sauvegarde : $(date '+%A %d %B %Y a %H:%M')"
    echo "Duree : $(( $(date +%s) - start )) s"
    echo "Taille totale : $size"
    echo "Fichiers du catalogue : $blobs"
    echo "Bases gardees : $(ls -1 "$DEST/base" | tr '\n' ' ')"
} > "$DEST/DERNIERE-SAUVEGARDE.txt"
log "sauvegarde faite : $size, $blobs fichiers du catalogue"
status ok "" "$(du -sb "$DEST" | cut -f1)" "$blobs"
