#!/usr/bin/env bash
# Puts the hub's upscale model in place (shared computing). Run once, and again when the model changes.
#
#   sudo bash setup-compute.sh /path/to/compute-seed
#
# compute-seed (made on the PC by build-compute-seed.ps1) holds models/<name>.onnx: one model per feature.
# The engine that runs it is built into the hub and the desktop app: nothing else to install.
#
# What it does:
#   - removes what the older engine used (the Real-ESRGAN program and its .param/.bin models)
#   - copies the seed's models into /var/lib/akashic-hub/compute/models
#   - restarts the hub
set -euo pipefail

SEED="${1:-}"
DATA=/var/lib/akashic-hub

if [ "$(id -u)" -ne 0 ]; then echo "A lancer avec sudo : sudo bash setup-compute.sh <dossier compute-seed>" >&2; exit 1; fi
if [ -z "$SEED" ] || [ ! -d "$SEED/models" ]; then echo "Dossier compute-seed attendu (avec models/)." >&2; exit 1; fi
if ! id akashic >/dev/null 2>&1; then echo "Utilisateur 'akashic' absent : installe d'abord le hub (install.sh)." >&2; exit 1; fi

echo "== Ancien moteur retire =="
rm -rf "$DATA/compute/tools"
rm -f "$DATA/compute/models/"*.param "$DATA/compute/models/"*.bin

echo "== Modele =="
mkdir -p "$DATA/compute/models"
cp "$SEED/models/"*.onnx "$DATA/compute/models/"
chown -R akashic:akashic "$DATA/compute"
ls "$DATA/compute/models" | sed 's/^/  /'

echo "== Redemarrage du hub =="
systemctl restart akashic-hub
sleep 2
systemctl is-active --quiet akashic-hub && echo "Hub relance." || { echo "Le hub ne repart pas : journalctl -u akashic-hub -n 50" >&2; exit 1; }
echo "Termine."
