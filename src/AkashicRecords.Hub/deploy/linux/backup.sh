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

if ! mountpoint -q "$MOUNT"; then
    mount "$MOUNT" 2>/dev/null || true
fi
if ! mountpoint -q "$MOUNT"; then
    log "disque de sauvegarde absent : rien a faire cette nuit"
    exit 0
fi
if ! touch "$MOUNT/.akashic-test" 2>/dev/null; then
    log "disque monte en lecture seule (Windows ne l'a pas ejecte proprement ?) : sauvegarde impossible"
    exit 1
fi
rm -f "$MOUNT/.akashic-test"

mkdir -p "$DEST/base" "$DEST/blobs" "$DEST/fichiers"
day=$(date +%F)
start=$(date +%s)

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
