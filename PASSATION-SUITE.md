# Akashic Records — passation pour la suite

> Document de reprise, rédigé le 2026-11-10 à la fin d'une longue session (portage Android).
> But : repartir dans un nouveau chat sans rien perdre. Il contient (1) l'état actuel,
> (2) les idées de l'utilisateur **telles qu'il les a formulées**, (3) l'avis donné pour chacune,
> (4) les questions encore ouvertes, (5) les infos pratiques pour travailler sur le projet.
>
> À lire aussi : `README.md`, `HANDOFF.md` (architecture PC), `MOBILITE-SYNC.md` (serveur / synchro),
> `REFONTE-UI.md`, et les règles obligatoires dans `copilot-instructions.md` / `.github/rules/`.

---

## 1. État actuel (branche `dev`, commit `ff9e892`)

`dev` contient tout le portage Android (fusion en avance rapide depuis `android-port`, poussée sur GitHub).

### App Android (`android/`, Kotlin + Compose + Room, base v2)
- **Navigation** : barre du bas Journaux / Musique / Calendrier / Collections / Plus (Organisation, Archives, Réglages).
- **Journaux** : journal personnel (calendrier + page, photos), Poésie (2ᵉ onglet), Recettes, Archive artistique (lecture seule).
  Sauvegarde automatique. Poèmes : mise en forme **propre au téléphone** (colonnes `Mobile*`, celle du PC jamais touchée),
  bouton « Reprendre celle du PC ».
- **Musique** : dossier `Music/Akashic` (sous-dossier = album), ambiances, file d'attente, lecture en arrière-plan,
  Similaires (iTunes + MusicBrainz), enchaînement automatique avec **2 titres préchargés**, téléchargements via
  yt-dlp + ffmpeg embarqués (mise à jour auto quotidienne), bouton ⤓ dans le mini-lecteur et dans le lecteur
  système (notification / écran verrouillé).
- **Calendrier** : événements (5 types, multi-jours, horaire, annuel), anniversaires, échéances de projets ;
  glisser pour changer de mois ; notifications (résumé du matin, rappels avec l'avance par type du PC,
  anniversaires en pop-up).
- **Widgets** (8, désactivables un par un dans Réglages) : Agenda, Journal, Poésie, Recettes, Musique,
  Collections (un film + un film d'animation), Organisation, Archives.
- **Icônes d'accueil colorées** (Musique, Poésie, Calendrier) via Réglages, + raccourcis d'appui long, + icône d'app.
- **Import des données du PC** : Réglages → choisir `akashic.db` ou un zip `data\` + `media\` (remplace les données du téléphone).

### App PC (`src/`, C# / WPF)
- Emojis en couleur (bibliothèque **Emoji.Wpf**) dans le calendrier, les toasts, journaux, collections, lecteur, téléchargements.
  Les champs de saisie restent standards (la `TextBox` d'Emoji.Wpf rendait mal au test).
- Schéma : colonnes `Mobile*` ajoutées à `Poem` (le PC les conserve, ne les utilise pas).
- ⚠️ **Pas encore recompilée dans le `bin\`** de l'utilisateur : compilée seulement dans un dossier de test.
  Il faut quitter Akashic puis `dotnet build AkashicRecords.sln`.

### Outillage
- `build-mobile.cmd` (double-clic) / `build-mobile.ps1` à la racine : compile l'APK, fait le dump des données du PC,
  copie le tout dans `D:\workspace\akashic-mobile\` et dans `Mon Drive\Akashic\` (Google Drive détecté automatiquement).
  Options : `-SkipApk`, `-SkipData`, `-NoDrive`, `-DriveDir`.

---

## 2. Les idées de l'utilisateur pour la suite (formulation d'origine)

> « Ok so now, let me tell you about my different ideas for what follows, and tell me your "thoughts". So many ways to go.
> There's what I need to buy for my local server that both apps are supposed to connect to and that would store all of
> my data + process different calls if that makes it easier on the apps (api calls).
> Firstly, there are bugs and inconsistencies in the web app that need fixing, like when I re open a page, it doesn't
> always go back to where I was.
> Secondly, I have a few things I'd like to add the first is something that allows the user, in the settings to
> disable/enable modules/ submodules of the application, the display adapts itself.
> Third thing is adding a way to look for musics on the internet instead on always relying on suggestions, maybe using
> the apple music api, from there on, those are mostly phone app focused devs:
> fourth thing is adding a reading module, with two parts, an easier one, something that allows me to read epubs on my
> phone, second part, harder, being a library where I can browse for books and download epubs freely, like on internet
> archive or other website.
> Fifth might be hard but it would be doing the same with movies or animes, idk if that's feasible though, maybe using
> websites like movielairs apis? I don't know.
> Sixtly would be re opening the pinterest issue, i wasn't able to get an api key finally, but I still would like to be
> able to browse references on my app, sadly.
> Finally adding a crossswords section would be fun, but that's last on the list for a reason. »

---

## 3. Avis donné, idée par idée

### 0. Serveur local (à acheter)
- **Matériel conseillé** : mini-PC Intel **N100** (Beelink, Minisforum…, ~150–250 €), **16 Go RAM**, **SSD 1 To**,
  plutôt qu'un Raspberry Pi. Raisons : yt-dlp / ffmpeg / appels d'API sans peine, accélération vidéo (QuickSync)
  si un lecteur de films (Jellyfin) arrive un jour ; la musique (11,6 Go) tient largement.
- **En plus** : un disque externe pour les sauvegardes ; **Tailscale** (gratuit) pour joindre le serveur hors de chez soi
  sans ouvrir le réseau à Internet.
- **Rôle** : `MOBILITE-SYNC.md` a déjà tranché (le serveur est la seule référence, Drive ne reçoit que des archives froides).
  Il prendrait les téléchargements, suggestions et recherches → apps plus légères.
- **Ordre** : pas de synchro tout de suite, mais concevoir chaque nouvelle fonction en pensant qu'elle passera un jour par lui.
  L'achat ne bloque rien.

### 1. Bugs : revenir là où on était à la réouverture d'une page
- Prioritaire, à faire en premier.
- Le PC enregistre déjà une partie de l'état (`JournauxActiveItem`, onglets…) mais pas partout ni de façon uniforme.
- **Approche** : reprendre section par section avec **une règle commune** de sauvegarde / restauration d'état,
  plutôt que des correctifs au cas par cas.
- ❓ À confirmer : « web app » désigne bien **l'app PC** (WPF) ? Demander une liste des cas qui ratent, même approximative.

### 2. Activer / désactiver modules et sous-modules dans les réglages
- Bonne idée, à faire juste après la 1 (fondations).
- **Téléphone** : facile (barre du bas, menu Plus et onglets déjà construits à partir de listes ; le catalogue de widgets existe).
- **PC** : un peu plus de travail sur la barre du haut et les onglets.
- **Proposition** : réglage **par appareil** (ex. Budget masqué sur le téléphone seulement). À confirmer.

### 3. Chercher de la musique sur Internet
- Meilleur rapport effort / plaisir.
- La recherche **iTunes Search** (gratuite, sans compte) est déjà utilisée pour les suggestions : ajouter un onglet
  « Rechercher » → extrait 30 s, lecture complète via yt-dlp, téléchargement, avec le pipeline existant.
- L'API **Apple Music** (MusicKit) demande un compte développeur à 99 $/an : inutile ici.

### 4a. Lecture d'epubs sur le téléphone
- Faisable proprement avec **Readium** (Kotlin toolkit, libre) : pagination, polices, reprise de lecture, surlignages.
- **Idée Akashic** : un passage surligné devient une **observation**, reliable aux projets artistiques comme celles des collections.
- Difficulté : moyenne.

### 4b. Bibliothèque pour trouver / télécharger des epubs
- Faisable avec des **sources légales et gratuites** : **Project Gutenberg** (API Gutendex), **Standard Ebooks**,
  **Wikisource**, **Ebooks libres et gratuits** (classiques français), **Internet Archive** (domaine public).
- La plupart exposent un catalogue **OPDS** → un seul navigateur OPDS ouvre plusieurs bibliothèques.
- Limites : pas de livres récents sous droits ; les prêts d'Internet Archive sont sous DRM ;
  **pas** de bibliothèques pirates (Anna's Archive, LibGen).

### 5. Films et animes — abandonné (décision de l'utilisateur, 2026-11-11)
- **Non** pour les sites de streaming sans droits (type celui cité) : illégal, et instable de toute façon.
- **Alternatives** :
  - avec le serveur : **Jellyfin** pour ses propres fichiers (appli + API ; un « À voir » présent sur le serveur se lancerait en un appui) ;
  - bouton **« Où le regarder ? »** sur chaque fiche (plateformes en France), via **TMDB** (gratuit ; une clé TMDB est déjà prévue dans la config) ;
  - films du domaine public d'Internet Archive.

### 6. Pinterest (pas de clé d'API obtenue)
- Aspirer Pinterest sans API : fragile et contraire à leurs conditions → non.
- **Voies proposées** :
  - **« Partager → Akashic »** : naviguer dans l'appli Pinterest (ou Instagram, un navigateur…) et partager l'image vers
    Akashic, qui l'enregistre dans les références ou un projet artistique. Simple et solide.
  - Sources avec une vraie API gratuite : **Are.na** (proche de Pinterest, pensé pour la création), **Unsplash**, **Wikimedia Commons**.

### 7. Mots croisés
- Faisable et sympa ; le plus dur est d'avoir de bonnes grilles en français, pas le code.
- Options : importer des grilles (.puz / .ipuz ; sources françaises gratuites rares) ou les générer
  (liste de mots + définitions du Wiktionnaire ; correct mais moins bon qu'une grille faite main).
- En dernier, comme prévu.

### Ordre conseillé
**1 → 2** (fondations) → **3** (rapide, gros plaisir) → **4a → 4b** → **6** → **7** → adapter au mobile les modules mal adaptés. (Le 5 est abandonné.)
Le serveur peut être acheté à tout moment.

---

## 4. Questions et points encore ouverts

- ❓ Le point 1 (« web app ») concerne-t-il bien l'app PC ?
- ❓ Modèle / marque du téléphone (comptent pour les widgets et raccourcis, qui varient selon le lanceur).
- ❓ Modules désactivables : par appareil ou global ?
- ⚠️ **Recompiler l'app PC** pour avoir les emojis en couleur (quitter Akashic d'abord).
- ⚠️ **Sécurité** : le remote git contient un **token GitHub en clair** (`git remote -v`, `.git/config`).
  À révoquer sur GitHub, puis `git remote set-url origin https://github.com/lgroussard/akashic-records.git`.
  Les clés **TMDB et RAWG** restent lisibles dans l'historique git (ancien `config.json.bak`) : à régénérer si le dépôt est public.
- Non testé sur l'émulateur : widgets Recettes / Organisation / Archives sur de vraies données, sélecteurs date/heure
  natifs du formulaire d'événement, résumé du matin à 8 h, import en `.zip` avec photos.
- La branche `android-port` est identique à `dev` : peut être supprimée.
- `D:\workspace\_archive\2026-11-10\` contient les anciens fichiers archivés (rien supprimé) : à trier par l'utilisateur.

---

## 5. Infos pratiques pour travailler sur le projet

- **Règles obligatoires** (`copilot-instructions.md`, `.github/rules/`) : aucune suppression hors `bin/` et `obj/` ;
  sauvegarde des données (`backup-data.ps1`) avant tout build qui touche `bin\` ; préférer déplacer que supprimer.
  Astuce : compiler le PC dans un dossier de test (`dotnet build … -o <dossier>`) évite de toucher `bin\`
  (où vivent `data\`, `config\` et 11,6 Go de `music\`).
- **Préférence utilisateur** : les tâches répétitives vont dans un **script à la racine du dépôt** (cf. `build-mobile.ps1`).
- **Gradle** échoue avec « Unable to establish loopback connection » depuis certains shells : déplacer TEMP
  (`$env:TEMP`, `$env:TMP` et `JAVA_TOOL_OPTIONS=-Djdk.net.unixdomain.tmpdir=… -Djava.io.tmpdir=…` vers
  `android\.gradle\tmp`). `build-mobile.ps1` le fait déjà.
- **Émulateur** : l'AVD `Medium_Phone` (Android 17) fonctionne ; `akashic` est cassé (image Android 34 absente).
  En Git Bash, `export MSYS_NO_PATHCONV=1` avant `adb push/shell` avec des chemins `/sdcard/…`.
- **Base Room** en version 2 : toute nouvelle colonne = nouvelle `Migration` dans `AkashicDatabase.kt`
  **et** l'équivalent `AddColumnIfMissing` dans `SchemaInitializer.cs` côté PC (les deux schémas doivent rester alignés
  pour l'import et la future synchro).
- **Méthode** : tester pour de vrai sur l'émulateur (captures d'écran, données importées du PC) avant d'annoncer qu'un
  point marche ; plusieurs bugs n'ont été trouvés qu'ainsi (migration manquante, boutons du widget musique, décembre vide).
