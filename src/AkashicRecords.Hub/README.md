# Akashic Hub — le serveur de synchronisation

Le hub est la source de vérité décrite dans `MOBILITE-SYNC.md` : le PC et le téléphone lui poussent
leurs modifications et lui tirent celles des autres. Il tourne sur le mini-PC, en service, sans écran.

Il est autonome : aucune référence vers l'app de bureau, rien à installer sur le mini-PC (pas de .NET).

**État** : le serveur est prêt. Les apps PC et téléphone ne lui parlent pas encore (pas 1, 2 et 4 du design).

---

## 1. Préparer les fichiers (sur le PC de développement)

Depuis la racine du dépôt :

```
.\build-hub.ps1
```

Le script lance les tests puis publie dans `D:\workspace\akashic-hub\` :

| Dossier | Pour |
|---|---|
| `win-x64\` (+ `akashic-hub-win-x64.zip`) | mini-PC sous Windows |
| `linux-x64\` (+ `akashic-hub-linux-x64.zip`) | mini-PC sous Linux |

Chaque dossier contient le hub, son script d'installation et ce fichier (`LISEZMOI.md`).

## 2. Installer sur le mini-PC

### Sous Windows 11

1. Copier le dossier `win-x64` sur le mini-PC (clé USB, partage réseau), n'importe où.
2. Ouvrir **PowerShell en administrateur**, aller dans ce dossier, puis :

   ```
   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
   ```

3. Le script affiche l'adresse du hub (`http://192.168.x.x:5199`) et un premier code d'appairage.

Ce que fait le script : copie le hub dans `C:\Program Files\AkashicHub`, garde les données à part dans
`C:\ProgramData\AkashicHub`, crée le service `AkashicHub` (démarre avec Windows, redémarre en cas de
panne, compte à droits réduits « Service local »), ouvre le port 5199 dans le pare-feu **pour les
réseaux privés seulement**, vérifie que le hub répond.

À régler une fois dans Windows, sinon le mini-PC s'endort et le téléphone ne trouve plus le hub :
*Paramètres > Système > Alimentation > Mise en veille : Jamais*. Et vérifier que le réseau de la
maison est bien classé « Privé » (le script prévient si ce n'est pas le cas).

Désinstaller : `powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1` (les données sont gardées).

### Sous Linux (Debian, Ubuntu… avec systemd)

1. Copier le dossier `linux-x64` sur le mini-PC.
2. Dans ce dossier :

   ```
   sudo bash install.sh
   ```

3. Le script affiche l'adresse du hub et un premier code d'appairage.

Ce que fait le script : copie le hub dans `/opt/akashic-hub`, garde les données dans
`/var/lib/akashic-hub`, crée l'utilisateur système `akashic` (sans connexion), installe le service
`akashic-hub` (démarre au boot, redémarre en cas de panne, accès disque limité à son dossier de données).

Si le pare-feu `ufw` est actif : `sudo ufw allow 5199/tcp`.
Journal du service : `journalctl -u akashic-hub -f`. Désinstaller : `sudo bash uninstall.sh` (les données sont gardées).

### Mettre à jour

Relancer `build-hub.ps1`, recopier le dossier, relancer le même script d'installation.
Les réglages (`hub.local.json`) et les données ne sont pas touchés.

## 3. Appairer un appareil

Le hub refuse tout appel de synchro sans jeton. Chaque appareil obtient le sien une fois, avec un
code à usage unique affiché **sur le mini-PC** :

| | Commande sur le mini-PC |
|---|---|
| Windows (PowerShell administrateur) | `& "C:\Program Files\AkashicHub\AkashicRecords.Hub.exe" pair` |
| Linux | `sudo -u akashic /opt/akashic-hub/AkashicRecords.Hub pair` |

Le code (8 chiffres) vaut 10 minutes et pour un seul appareil. L'appareil l'envoie avec son identifiant :

```
POST http://<adresse-du-hub>:5199/auth/pair
{ "code": "12345678", "device_id": "<GUID choisi par l'appareil>", "device_name": "Téléphone" }
→ { "device_id": "...", "token": "..." }
```

Le jeton est renvoyé une seule fois ; l'appareil le garde et l'envoie ensuite dans chaque appel :
`Authorization: Bearer <token>`. Cinq codes faux d'affilée grillent le code en attente.

Autres commandes (mêmes préfixes que ci-dessus) :

| Commande | Effet |
|---|---|
| `devices` | liste les appareils appairés et leur dernière visite |
| `revoke <device_id>` | coupe l'accès d'un appareil (téléphone perdu) |
| `config` | affiche les réglages réellement pris en compte |

Tant que les apps ne sont pas branchées, on peut vérifier le hub depuis un autre poste :
`http://<adresse-du-hub>:5199/health` dans un navigateur doit répondre `{"status":"ok",…}`.

## 4. Réglages

Par ordre de priorité croissante :

1. `hub.json` — valeurs livrées (remplacé à chaque mise à jour) ;
2. `hub.local.json` — réglages de cette machine, écrit par le script d'installation, jamais écrasé ;
3. variables d'environnement `AKASHIC_HUB_PORT`, `AKASHIC_HUB_LISTEN`, `AKASHIC_HUB_DATA`, `AKASHIC_HUB_ENTITY_TYPES` (liste séparée par des virgules) ;
4. ligne de commande `--Hub:Port=…`, `--Hub:Listen=…`, `--Hub:DataDir=…`.

| Clé (`Hub:`) | Défaut | Rôle |
|---|---|---|
| `Listen` | `0.0.0.0` | adresse d'écoute (toutes les interfaces : le téléphone doit l'atteindre) |
| `Port` | `5199` | port |
| `DataDir` | `hub-data` à côté de l'exécutable | dossier des données (les scripts d'installation le placent ailleurs) |
| `EntityTypes` | les types du domaine Akashic | types d'entités acceptés ; `"*"` accepte tout nom bien formé |
| `RetentionDays` | `30` | rétention des pierres tombales (annoncée, pas encore appliquée : aucune purge) |
| `MaxBlobMegabytes` | `1024` | taille maximale d'un média |
| `PairCodeMinutes` | `10` | durée de vie d'un code d'appairage |
| `Downloads` | `true` | téléchargements de musique faits par le hub (tableau partagé `/downloads`) |
| `YtDlp` | vide | yt-dlp à lancer ; vide = le hub garde sa propre copie dans `<DataDir>/bin`, récupérée sur GitHub au premier usage et mise à jour chaque jour (variable `AKASHIC_HUB_YTDLP`) |
| `Ffmpeg` | vide | ffmpeg (fichier ou dossier) ; vide = celui du PATH (installé par `install.sh` sous Linux ; variable `AKASHIC_HUB_FFMPEG`) |
| `DownloadArgs` | `-x --audio-format mp3 --embed-thumbnail --add-metadata` | arguments yt-dlp d'un téléchargement |

Après un changement : redémarrer le service (`Restart-Service AkashicHub` / `sudo systemctl restart akashic-hub`).

Dossier de données :

```
hub.db            journal (sync_log), état courant (entities), appareils (jetons hachés)
blobs\ab\abcd…    médias, un fichier par empreinte SHA-256
downloads.db      tableau des téléchargements (zones, titres demandés)
bin\yt-dlp        la copie de yt-dlp du hub
logs\hub-AAAA-MM-JJ.log   une ligne par requête (30 jours gardés)
```

**Sauvegarde** : tant que le job Drive n'existe pas, copier ce dossier (service arrêté) est la sauvegarde.

## 5. API

Tout sauf `/health` et `/auth/pair` exige `Authorization: Bearer <token>` (sinon `401`). JSON en `snake_case`.

| Endpoint | Effet |
|---|---|
| `GET /health` | `{ status, version, head_seq, retention_days }`, sans jeton |
| `POST /auth/pair` | `{ code, device_id?, device_name? }` → `{ device_id, token }` ; `403` si le code est faux, expiré ou déjà utilisé |
| `POST /sync/push` | `{ mutations: [{ mutation_id, entity_type, entity_id, base_rev, payload, deleted }] }`. Par mutation : `accepted` (nouvelle `rev`, `seq`), `duplicate` (même `mutation_id` déjà validé : même réponse, aucune écriture) ou `conflict` (`base_rev` périmée, `current` = état du hub). L'auteur est l'appareil du jeton. |
| `GET /sync/pull?since=<seq>&limit=n` | lignes du journal après le curseur, dans l'ordre de `seq`, tombales comprises ; `next_cursor` est le prochain `since` |
| `GET /snapshot?limit=n[&entity_type=…][&after_type=…&after_id=…]` | état courant complet, paginé, tombales comprises. Prendre `head_seq` de la **première** page, lire toutes les pages (`next_after_type` / `next_after_id`), puis reprendre `pull` à ce `head_seq`. |
| `POST /media[?hash=<sha256>]` | corps = octets bruts. `201` si nouveau, `200` si déjà connu ; `400 hash_mismatch` si le contenu ne correspond pas à l'empreinte annoncée ; `413` au-delà de la limite |
| `GET /media/{hash}` (et `HEAD`) | le média ; immuable, `Range` accepté (reprise, lecture audio) |
| `GET /downloads` | le tableau partagé : `{ enabled, tool, zones, jobs, folders }` (`folders` = dossiers de musique déjà au catalogue) |
| `POST /downloads/jobs` | `{ query, label, zone_id? }` ; `query` = un lien http(s) ou une recherche `ytsearch1:…`. Le hub attend 10 s (le temps de changer de zone), télécharge, garde le fichier et ajoute `MediaFile` + `MusicTrack` au catalogue (appareil `hub`) |
| `POST /downloads/jobs/{id}/zone`, `…/retry`, `DELETE /downloads/jobs/{id}`, `POST /downloads/clear-done` | ranger ailleurs, réessayer, retirer, vider les terminés |
| `POST /downloads/zones` | `{ name?, folder }` → la zone de ce dossier (réutilisée si elle existe) ; `POST /downloads/zones/{id}` `{ name }` renomme, `DELETE` retire. Une zone dont tous les titres sont téléchargés quitte le tableau d.elle-même. |

## 6. Tester

```
& "C:\Program Files\dotnet\dotnet.exe" run --project src\AkashicRecords.Hub.SmokeTest
```

Démarre le vrai hub sur un port libre avec un dossier jetable et fait jouer des appareils simulés,
chacun appairé avec son jeton : idempotence, conflit, tombales, convergence (40 tirages aléatoires),
horloges, redémarrage, appairage et refus sans jeton, snapshot, blobs, second type d'entité, journal
de requêtes. Code de sortie 0 si tout passe.

## 7. Choix pris par défaut (section 9 du design, tous révisables)

1. Fusion : dernier gagnant **par entité** (le client en conflit repousse sa version entière sur la révision courante).
2. Rétention : 30 jours (annoncée ; aucune purge codée).
3. Langage : ASP.NET minimal API, .NET 8.
4. Hébergement : mini-PC à la maison (testé sur le PC d'abord).

## 8. Ce qui manque encore, volontairement

- Brancher l'app PC et l'app téléphone sur le hub.
- HTTPS : le hub parle HTTP en clair. Sur le réseau de la maison le jeton protège l'accès mais circule en clair ; à régler en même temps que l'accès depuis l'extérieur (tunnel).
- Accès hors de la maison (tunnel type WireGuard/Tailscale).
- Purge des tombales et des blobs orphelins, archives vers Drive.
- Refus d'un curseur trop ancien (`pull` → « repars du snapshot ») : inutile tant qu'aucune purge n'existe.

## Écarts par rapport au design

- `sync_log` a une colonne en plus, `mutation_id` (unique) : c'est elle qui rend le renvoi d'un lot sans effet.
- `entity_id` est une chaîne émise par le client (un GUID), pas le `Id` entier auto-incrémenté des tables actuelles.
- Le hub ne valide pas le contenu des payloads ni l'intégrité des graphes (canevas) : il stocke du JSON par type.

## Calcul partagé (agrandissement d'images)

Le hub garde le programme d'agrandissement (Real-ESRGAN, version ncnn-vulkan) et ses modèles dans
`<DataDir>/compute`, et la liste des demandes dans `compute.db`. Les appareils appairés qui savent faire
tourner un modèle demandent du travail (`POST /compute/claim`), récupèrent le programme et le modèle auprès du
hub la première fois, font le calcul et renvoient le résultat, qui reste sur le hub. Le hub fait lui-même les
modèles listés dans `ComputeHubModels` (par défaut `high-fidelity-4x`), mais seulement quand aucun autre
appareil capable n'est là : un PC avec une carte graphique le fait en quelques secondes, le mini-PC en deux minutes.

Mise en place (une fois, puis pour ajouter des modèles) :

1. Sur le PC : `.\build-compute-seed.ps1` (prépare `D:\workspace\akashic-hub\compute-seed`).
2. Copier ce dossier sur le mini-PC, puis `sudo bash ~/compute-seed/setup-compute.sh ~/compute-seed`
   (pilote Vulkan, accès du compte `akashic` à la puce graphique, copie des fichiers, redémarrage du hub).

Ajouter un modèle = déposer sa paire `.param` + `.bin` dans `<DataDir>/compute/models`. Réglages :
`"Compute": false` coupe tout, `"ComputeHubModels": ""` empêche le hub de calculer lui-même.
