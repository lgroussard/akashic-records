#!/usr/bin/env bash
# Sets up the hub's shared computing (image upscaling) on Linux. Run once, and again to add models.
#
#   sudo bash setup-compute.sh /path/to/compute-seed
#
# compute-seed (made on the PC by build-compute-seed.ps1) holds:
#   tools/realesrgan/linux-x64/realesrgan-ncnn-vulkan   what the hub runs itself
#   tools/realesrgan/win-x64/...                        what it hands to Windows PCs
#   models/<name>.param + .bin                          every model, for every device
#
# What it does:
#   - installs the Vulkan driver (mesa-vulkan-drivers) the program needs to use the graphics chip
#   - lets the service user "akashic" use the graphics chip (groups render and video)
#   - copies the seed into /var/lib/akashic-hub/compute (adds or replaces files, removes nothing)
#   - restarts the hub so that it runs as a member of those groups
set -euo pipefail

SEED="${1:-}"
DATA=/var/lib/akashic-hub

if [ "$(id -u)" -ne 0 ]; then echo "A lancer avec sudo : sudo bash setup-compute.sh <dossier compute-seed>" >&2; exit 1; fi
if [ -z "$SEED" ] || [ ! -d "$SEED/models" ]; then echo "Dossier compute-seed attendu (avec models/ et tools/)." >&2; exit 1; fi
if ! id akashic >/dev/null 2>&1; then echo "Utilisateur 'akashic' absent : installe d'abord le hub (install.sh)." >&2; exit 1; fi

echo "== Pilote Vulkan =="
apt-get update -qq
apt-get install -y -qq mesa-vulkan-drivers libvulkan1 libgomp1 >/dev/null

echo "== Acces a la puce graphique pour le hub =="
usermod -aG render,video akashic
if [ -e /dev/dri/renderD128 ]; then ls -l /dev/dri/renderD128; else echo "Attention : /dev/dri/renderD128 absent, le hub calculera sur le processeur (lent)." >&2; fi

echo "== Programme et modeles =="
mkdir -p "$DATA/compute"
cp -r "$SEED/." "$DATA/compute/"
find "$DATA/compute/tools" -type f -name 'realesrgan-ncnn-vulkan' -exec chmod 755 {} \;
chown -R akashic:akashic "$DATA/compute"
ls "$DATA/compute/models" | sed 's/\.\(param\|bin\)$//' | sort -u | sed 's/^/  modele : /'

echo "== Redemarrage du hub =="
systemctl restart akashic-hub
sleep 2
systemctl is-active --quiet akashic-hub && echo "Hub relance." || { echo "Le hub ne repart pas : journalctl -u akashic-hub -n 50" >&2; exit 1; }

# A quick check that the hub's user really sees the graphics chip.
if command -v vulkaninfo >/dev/null; then
    sudo -u akashic vulkaninfo --summary 2>/dev/null | grep -E "deviceName" | sed 's/^/  vu par le hub : /' || true
fi
echo "Termine."
