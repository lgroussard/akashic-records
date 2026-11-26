#!/usr/bin/env bash
# Installs (or upgrades) the Akashic hub as a systemd service.
#
# Run from the published folder (linux-x64):
#   sudo bash install.sh            # port 5199
#   sudo bash install.sh 5200       # another port
#
# What it does:
#   - copies this folder to /opt/akashic-hub (an upgrade keeps hub.local.json)
#   - keeps the data apart, in /var/lib/akashic-hub (never touched by an upgrade or an uninstall)
#   - creates the system user "akashic" (no login, no shell) that runs the hub
#   - installs and starts the systemd unit akashic-hub (starts at boot, restarts on failure)
#   - checks /health and prints a first pairing code
set -euo pipefail

PORT="${1:-5199}"
APP=/opt/akashic-hub
DATA=/var/lib/akashic-hub
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ "$(id -u)" -ne 0 ]; then echo "A lancer avec sudo : sudo bash install.sh" >&2; exit 1; fi
if [ ! -f "$SRC/AkashicRecords.Hub" ]; then echo "AkashicRecords.Hub introuvable a cote de ce script (dossier linux-x64 attendu)." >&2; exit 1; fi
if ! command -v systemctl >/dev/null; then echo "systemd est requis (Debian, Ubuntu, Fedora...)." >&2; exit 1; fi

# Music downloads done by the hub: yt-dlp fetches it itself (into $DATA/bin), but converting to mp3 needs ffmpeg.
if ! command -v ffmpeg >/dev/null; then
    if command -v apt-get >/dev/null; then
        echo "Installation de ffmpeg (pour les telechargements de musique)..."
        apt-get update -qq && apt-get install -y -qq ffmpeg >/dev/null || echo "ffmpeg n'a pas pu etre installe : les telechargements echoueront." >&2
    else
        echo "ffmpeg absent : installe-le pour que le hub puisse telecharger de la musique." >&2
    fi
fi

if ! id akashic >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir "$DATA" --shell /usr/sbin/nologin akashic
    echo "Utilisateur systeme 'akashic' cree."
fi

if systemctl is-active --quiet akashic-hub; then
    echo "Arret du service existant..."
    systemctl stop akashic-hub
fi

echo "Copie du hub vers $APP"
mkdir -p "$APP" "$DATA"
if [ "$SRC" != "$APP" ]; then
    # hub.local.json is this machine's settings: never overwritten.
    find "$SRC" -mindepth 1 -maxdepth 1 ! -name hub.local.json -exec cp -r {} "$APP"/ \;
fi
chown -R root:root "$APP"
chmod -R u=rwX,go=rX "$APP"
chmod 755 "$APP/AkashicRecords.Hub"

if [ ! -f "$APP/hub.local.json" ]; then
    printf '{\n  "Hub": {\n    "DataDir": "%s",\n    "Port": %s\n  }\n}\n' "$DATA" "$PORT" > "$APP/hub.local.json"
    echo "Reglages ecrits dans $APP/hub.local.json"
else
    echo "Reglages existants conserves : $APP/hub.local.json"
    PORT="$(grep -o '"Port"[^0-9]*[0-9]*' "$APP/hub.local.json" | grep -o '[0-9]*$' || echo "$PORT")"
fi

chown -R akashic:akashic "$DATA"
chmod 750 "$DATA"

cp "$APP/akashic-hub.service" /etc/systemd/system/akashic-hub.service
systemctl daemon-reload
systemctl enable --now akashic-hub

ok=""
for _ in $(seq 1 20); do
    if command -v curl >/dev/null && curl -fsS -m 2 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then ok=1; break; fi
    if ! command -v curl >/dev/null && systemctl is-active --quiet akashic-hub; then ok=1; break; fi
    sleep 0.5
done
if [ -z "$ok" ]; then
    echo "Le service ne repond pas sur le port $PORT. Voir : journalctl -u akashic-hub -n 50" >&2
    exit 1
fi

echo
echo "Hub en service. Donnees : $DATA"
for ip in $(hostname -I 2>/dev/null); do
    case "$ip" in *:*) ;; *) echo "Adresse a saisir sur les appareils : http://$ip:$PORT" ;; esac
done
if command -v ufw >/dev/null && ufw status 2>/dev/null | grep -q "Status: active"; then
    echo "Pare-feu ufw actif : ouvre le port avec   sudo ufw allow $PORT/tcp"
fi
echo
echo "Premier code d'appairage :"
sudo -u akashic "$APP/AkashicRecords.Hub" pair
echo
echo "Un code par appareil. Pour en obtenir un autre plus tard :"
echo "  sudo -u akashic $APP/AkashicRecords.Hub pair"
