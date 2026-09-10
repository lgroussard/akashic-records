# Refonte graphique Akashic Records — état & passation

> Document de reprise. Écrit honnêtement après une refonte partielle. But : qu'une autre IA (ou un humain) reprenne sans refaire les mêmes erreurs.

## 0. La leçon la plus importante (à lire en premier)

L'app est **WPF (desktop, plein écran)**. Un agent qui code sans **voir le rendu réel** produit du moche : un build à 0 erreur ne prouve RIEN sur l'apparence. La refonte a déraillé parce que les changements étaient « vérifiés » au build, pas à l'œil.

**Méthode obligatoire pour tout travail visuel :**
1. Modifier le XAML/style.
2. `dotnet build` (0 erreur).
3. Lancer l'app, **capturer l'écran de la fenêtre**, et **REGARDER la capture soi-même**.
4. Itérer jusqu'à ce que ça matche la maquette **avant** de le montrer à l'utilisateur.

Ne jamais dire « c'est conforme » sans avoir regardé une capture.

## 1. Source de vérité = les maquettes

`akashic-records/mockups/` (ouvrir `index.html`) :
- `_shared.css` — le langage visuel cible (palette, cartes, segmented control, pilules, panneau latéral).
- `01-journaux.html`, `02-collections.html`, `03-organisation.html`, `04-archives.html`, `05-musique.html`.

La maquette a été **validée par l'utilisateur**. L'app doit lui ressembler. Attention : les maquettes sont **plus simples** que les fonctionnalités existantes — il faut **reconstruire les vues sur la maquette**, pas recolorer l'ancienne structure (c'est l'erreur commise sur Journaux).

## 2. Design system en place (`src/AkashicRecords.App/App.xaml`)

Déjà créé et réutilisable (traduit de `_shared.css`) :
- **Brushes** : `AppBgBrush`, `Surface1/2/3Brush`, `SurfaceFaintBrush`, `AppBorderBrush`, `BorderStrongBrush`, `TextBrush`, `TextMutedBrush`, `TextFaintBrush`, `AccentBrush`, `Accent2Brush`, `AccentSoftBrush`, `AccentGradientBrush`, `TierS/A/B/C/DBrush`.
- **Styles** : `NavButtonStyle` (onglet segmented, radius 8), `ChipButtonStyle`, `MiniButtonStyle`, `PrimaryButtonStyle` (radius 8), `PrimaryPillButtonStyle` (CTA, radius 10, height 36), `SecondaryButtonStyle`, `TierPillButtonStyle`, `WorkChipButtonStyle`, `CardBorderStyle`, `DarkTextBoxStyle`, `DarkComboBoxStyle`, `FieldLabelStyle`, `PanelSectionHeaderStyle`.
- **Scrollbars fines** : `ThinHorizontalScrollBarStyle`, `ThinVerticalScrollBarStyle` (à scoper via `<Grid.Resources>` local, JAMAIS au niveau UserControl).
- **Canvas** : `CanvasGridBrush` (quadrillage 28px).

⚠️ `CardBorderStyle` actuel (`Surface1Brush` sur `AppBgBrush`) est **trop peu contrasté** : à l'écran les cartes ne se détachent pas du fond. À corriger (bordure `BorderStrongBrush` plus visible et/ou surface plus claire).

## 3. État par vue

| Vue | État | Détail |
|---|---|---|
| **Collections** | ✅ Fait & validé | Tier lists pleine hauteur, scroll horizontal par tier (+ molette + touchpad `WM_MOUSEHWHEEL`), modale d'ajout à pilules, fiche en panneau latéral, chip = couverture+titre+badges. Onglets segmented + CTA pilule OK. C'est la vue de référence pour le style. |
| **Journaux** | ⚠️ NE CORRESPOND PAS | Restylé (couleurs FR OK, français OK) mais **structure = ancienne, pas la maquette**. Problèmes vus sur capture : cartes invisibles, calendrier WPF natif ≠ maquette, contenu qui s'étale sur tout l'écran 1920 (immense vide), voids. À **reconstruire** sur la maquette. |
| **Organisation** | ❌ Pas commencé | Encore l'UI d'origine. |
| **Archives** | ❌ Pas commencé | Encore l'UI d'origine. |
| **Musique / widgets / MainWindow header** | ❌ Pas commencé | Header partiellement stylé via les styles partagés. |

## 4. Problèmes précis à régler sur Journaux (vus sur captures réelles)

1. **Cartes invisibles** → renforcer `CardBorderStyle` (fond + bordure nette).
2. **Calendrier** : c'est le `Calendar` WPF natif, il ne ressemble pas au calendrier custom de la maquette (grille 7 colonnes, jour du jour bordé accent, jours à entrée en `accent-soft`). Envisager un `Calendar` retemplaté OU un contrôle custom.
3. **Plein écran vs maquette compacte** : l'app fait 1920px de large, la maquette ~1100. Étirer le contenu edge-to-edge donne un rendu vide et sparse. **Piste** : contraindre le contenu Journaux à une largeur/hauteur max (~1280×720) et **centrer**, pour retrouver les proportions de la maquette.
4. **Journal personnel** : carte calendrier qui laisse un grand vide (calendrier petit en haut, bouton en bas) → la rendre compacte (hug content). Corps de page vide = immense void quand pas d'entrée.
5. **Recettes/Poésie/Archive artistique** : encore l'ancienne structure (sidebars « JOURNAL DE … » en majuscules à retirer, listes mono-ligne vs cartes deux-lignes, poésie ouvre sur une « étagère » de recueils absente de la maquette, archive artistique passe par une liste de projets au lieu du menu déroulant « Projet : … » de la maquette). Décisions produit à trancher avec l'utilisateur sur ces écarts (garder/supprimer l'étagère, liste→dropdown projets).

## 5. Contraintes & gotchas WPF (voir aussi `akashic-records/HANDOFF.md` §5)

- Images potentiellement supprimées → charger `BeginInit → CacheOption=OnLoad → UriSource → EndInit → Freeze()`.
- Contenu « page » borné (`MaxWidth`/`MaxHeight`), jamais `Height="*"` qui s'étire sur tout l'écran plein écran (cause directe des voids).
- Pas de `PlaceholderText` sur `TextBox` WPF (watermark = `TextBlock` superposé).
- Bouton à état sélectionné → template avec `Background="{TemplateBinding Background}"`.
- Fenêtres top-level → hide-on-close + `ForceClose()` (`ShutdownMode=OnExplicitShutdown`).
- Préserver **tous** les `x:Name` et handlers d'une vue lors d'un restyle (le code-behind s'y lie ; un build vert prouve que les liaisons existent, pas l'apparence).

## 6. Build / run

```powershell
# dotnet pas sur PATH :
$dotnet = "C:\Program Files\dotnet\dotnet.exe"
# Toujours tuer l'app avant de builder (verrou DLL) :
Get-Process -Name "AkashicRecords.App" -ErrorAction SilentlyContinue | Stop-Process -Force
& $dotnet build "d:\workspace\akashic-records\src\AkashicRecords.App\AkashicRecords.App.csproj" -t:Rebuild -clp:ErrorsOnly
& $dotnet run   --project "d:\workspace\akashic-records\src\AkashicRecords.App\AkashicRecords.App.csproj"
```

Fichiers clés : `App.xaml` (design system), `Views/CollectionsView.xaml(.cs)` (référence réussie), `Views/JournauxView.xaml(.cs)` (à reconstruire), `MainWindow.xaml` (header).

## 7. Agents personnalisés créés

`.github/agents/wpf-dev.agent.md` (implémenteur WPF, gotchas intégrés) et `.github/agents/wpf-ux-reviewer.agent.md` (revue fidélité maquette). Anvil est installé (Node LTS + `@fr-nan-ai/anvil` 0.8.8) mais le projet n'est **pas** initialisé Anvil (pas de `.anvil/`) — ne pas lancer `anvil init/generate` (écraserait la surface `.github/` existante). Détails en mémoire repo.

## 8. Recommandation de reprise

Faire vue par vue, dans l'ordre : Journaux (reconstruire sur maquette) → Organisation → Archives → Musique/header. Pour CHAQUE écran : coder → build → **capturer & regarder** → itérer → montrer. Ne pas élargir le périmètre tant qu'un écran n'est pas visuellement validé.
