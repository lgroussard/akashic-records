# Akashic Records — Handoff

Desktop WPF app (C#/.NET 8) that acts as a personal "digital memory" overlay on Windows. Full spec/vision lives in `d:\workspace\akashic records.md` (French, ~2000 lines) — **read it before adding new domain models or features**; don't infer schemas from nav labels alone. The spec assumed Java/JavaFX/SQLite; this repo deliberately diverged to **C#/WPF/SQLite** instead.

This document describes everything built so far, in the order it was built, plus known gotchas and what's left.

---

## 1. Architecture

```
AkashicRecords.sln
├── src/AkashicRecords.Domain          (plain POCOs, no dependencies)
├── src/AkashicRecords.Infrastructure  (SQLite persistence, Windows integration, config)
└── src/AkashicRecords.App             (WPF UI)
Phase0/                                (throwaway early prototype — safe to delete)
```

- **Domain**: no WPF/SQLite references, just data classes and enums.
- **Infrastructure**: `Persistence/` (one repository class per entity, raw `Microsoft.Data.Sqlite`, no ORM), `Configuration/` (`AppConfig`/`ConfigService`), `WindowsIntegration/` (global hotkey, tray icon, screen-edge docking, startup manager).
- **App**: `MainWindow` (header bar + section switcher) and `Views/` (one `UserControl` per section — Organisation/Journaux/Collections/Archives are the four header nav buttons, but only **Journaux** and **Collections** have real content so far).

### Portability
The whole app is designed to run from a **portable folder** — copy the folder anywhere, run the exe, it self-initializes:
- Config: `<exe folder>\config\config.json` (not `%AppData%`)
- Database: `<exe folder>\data\akashic.db` (SQLite, schema auto-created on first run via `SchemaInitializer.EnsureCreated()`)
- Media: `<exe folder>\media\{covers,artwork,photos,recipes,poetry}\` (imported files copied in, original untouched)

`SchemaInitializer` creates all tables with `CREATE TABLE IF NOT EXISTS`, plus an `AddColumnIfMissing` helper (via `PRAGMA table_info`) for migrating existing databases when a column gets added to an already-shipped table — there's no real migration framework, just this one lightweight mechanism.

---

## 2. Original bug (RESOLVED)

The original handoff was about a screen-edge docking bug: the header bar (via `ScreenEdgeDock` / `SHAppBarMessage`) reserved a strip of screen space that didn't match where the header actually rendered, leaving dead space. Root cause: `MainWindow.FitToScreen()` was sizing the window off `SystemParameters.WorkArea`, which already excludes the app's own AppBar reservation — creating a feedback loop that grew worse each run. **Fixed** by sizing the window off the raw screen bounds (`SystemParameters.PrimaryScreenWidth/Height`, origin `0,0`) instead of `WorkArea`. Confirmed working.

---

## 3. Collections

Four independent tabs — **Films / Films d'animation / Animes / Livres** (spec explicitly says never merge these into one generic "works" list). Each tab is a self-contained tier list:

- **Tier bands** (S/A/B/C/D, always all shown even if empty) with works as clickable chips (cover image if set, else title text).
- Click a chip → **fiche** (detail overlay): tier picker (moves it between bands live), cover image (set/remove, stored via `MediaStorage.ImportCover`), **Evaluations** (user-defined axes, not a fixed list — e.g. "Visuel", "Humour" — pick existing or type a new one, score 1–5, delete individual entries), **Observations** (subject + free text, the personal-memory unit the spec calls the most important feature, addable/deletable).
- Escape closes the fiche first, then the whole section (bubbling `KeyDown` on `MainWindow`, not tunneling — lets nested overlays consume it first).

Domain: `Artwork`, `EvaluationAxis`, `ArtworkEvaluation`, `Observation`. Repositories: `ArtworkRepository`, `EvaluationAxisRepository`, `ArtworkEvaluationRepository`, `ObservationRepository`.

---

## 4. Journaux

Four tabs, each with a **deliberately different UI paradigm** per the spec ("chaque section peut avoir une philosophie différente") — none of them share a generic list/form component.

### 4a. Journal personnel
**Calendar + book**, not a card list (this was rebuilt once after initial feedback — a flat card list didn't match the "book you write in" mental model). Left: a `Calendar` control where days with an entry get a subtle blue tint (via `EntryDateBackgroundConverter`, a static-state `IValueConverter` — WPF doesn't auto-refresh day-button bindings, so `RefreshCalendarChips()` force-reassigns `CalendarDayButtonStyle` to trigger a redraw). Right: one entry "page" — title, free text, comma-separated tags, multiple photos. **Opens on today by default**; no db row is created until you actually type something (browsing dates doesn't litter empty entries — see `EnsureSelectedEntryExists()`). Previous/Next flip between *entries that actually exist*, chronologically; the calendar lets you jump to any date directly.

Domain: `JournalEntry`, `JournalPhoto`. Repositories: `JournalEntryRepository`, `JournalPhotoRepository`.

### 4b. Journal de recettes
**Sidebar (search + category chips + title list) + one recipe "page."** Also rebuilt once — first version was a flat card grid with a "tags" filter row that didn't read as a book. `Category` is a single user-defined field (distinct from free-form `Tags`) driving the filter chips. Search matches title/tags/**ingredients** too. Page: photo, title, category, ingredients, "Préparation", personal notes, tags, all auto-save on field blur; Previous/Next flips through the filtered set.

Domain: `Recipe` (has `Category` + `Tags` + `CoverImagePath`). Repository: `RecipeRepository`.

### 4c. Recueil de poésie
**Sidebar (search + recueil filter + poem list) + one poem "page,"** styled distinctly from the other two — the poem *text* is the dominant element (large serif italic), with a **Word-style formatting toolbar** above it (Left/Center/Right/Justify toggle buttons + margin −/+ buttons, persisted per-poem in `Poem.TextAlignment`/`Poem.Margin`). Poems can optionally belong to a `Recueil` (named grouping, via a `ComboBox` on the page) or stand alone.

**Important layout lesson**: the poem "page" is wrapped in a bounded `Border` (`MaxWidth="640" MaxHeight="560"`) — the first version let the poem `TextBox` use `Height="*"` inside a section that spans nearly the full screen (since the whole app window is full-screen), which stretched it into an invisible multi-hundred-pixel void and shoved the toolbar to the bottom of the monitor. Any new "page"-style view in this app **must** be bounded like this, not left to fill available space.

Domain: `Recueil`, `Poem`. Repositories: `RecueilRepository`, `PoemRepository`.

### 4d. Archive artistique
**Infinite pannable canvas**, one per `ArtisticProject` (simple title-only list to pick a project first). This is the spec's "most important/original feature" — linking personal Observations (from Collections) into visual, freeform artistic projects.

- Element types: **text notes** (editable inline), **images** (imported, resizable), **observation references** (pick any Observation from Collections; shown inline with full text — no popup, see gotcha below — and creates a real `Reference` row so the Observation↔Project graph link exists for future bidirectional lookups).
- All elements: **draggable**, **resizable** (corner handle), **deletable** (✕).
- **Schematic arrows**: "Draw arrow" toggle → click two elements → connector drawn, clipped to each box's *edge* (not center-to-center — see gotcha), with an arrowhead; click an arrow to delete it; deleting an element cascades to remove (and undo-restores) any arrows touching it.
- **Undo/redo** (Ctrl+Z/Ctrl+Y or buttons) via a small lambda-based command pattern (`ICanvasCommand`/`CanvasCommand`) — every add/move/resize/delete/edit/connector action pushes a `Do`/`Undo` pair onto a stack; not scoped to canvas-only, it's a clean reusable pattern if other views need undo later.
- **Panning**: click-and-drag on empty canvas background (checks `e.OriginalSource == ElementCanvas` to avoid hijacking element drags), plus Shift+wheel for horizontal scroll. Two-finger horizontal touchpad swipes needed a **native `WM_MOUSEHWHEEL` window-message hook** (`HwndSource.AddHook`) — WPF's `MouseWheel` event never surfaces horizontal deltas at all, there's no framework-level API for it. True diagonal two-finger panning could not be fixed further — Windows precision-touchpad drivers axis-lock a scroll gesture to whichever direction was dominant at the start, and that's decided below the app, not something WPF/Win32 message handling can override.
- Scrollbars are hidden (`Hidden`, not `Auto`/`Disabled`) — panning still works, they were just visual clutter once drag-panning existed.

Domain: `ArtisticProject`, `CanvasElement` (+ `CanvasElementType`, includes `Width`/`Height`), `CanvasConnector`, `Reference`. Repositories: `ArtisticProjectRepository`, `CanvasElementRepository`, `CanvasConnectorRepository`, `ReferenceRepository`.

---

## 5. Known gotchas (worth reading before touching WPF code here)

1. **`new BitmapImage(new Uri(path))` locks the file on disk** (default `OnDemand` caching) until GC'd. Any image that might later be deleted/replaced (covers, recipe photos, canvas images, journal photos, poem images) must be loaded via `BeginInit → CacheOption=OnLoad → UriSource → EndInit → Freeze()` (see `LoadImage` helper duplicated in `CollectionsView`/`JournauxView`). Otherwise `File.Delete` throws `IOException`, silently swallowed by the app's global crash handler (`App.xaml.cs`), making the delete feature look like it does nothing.
2. **Custom drag-to-move via `PreviewMouseLeftButtonDown` + `CaptureMouse`**: checking `e.OriginalSource is TextBox`/`is Button` directly is **not enough** — `OriginalSource` for a click inside a `TextBox`/`Button` is often an internal template part, not the control itself. Must walk up the visual tree (`VisualTreeHelper.GetParent`) checking for `TextBoxBase`/`ButtonBase` ancestors (see `ShouldSkipDrag` in `JournauxView`). Symptom if wrong: nested text boxes appear "not editable," nested buttons "don't respond to clicks," because the container's drag capture steals the mouse first.
3. **`FrameworkElement.ActualWidth`/`ActualHeight` are 0 until a layout pass runs**, which doesn't happen synchronously on adding to the visual tree. If you need an element's size immediately after creating it (e.g. computing connector-arrow endpoints right when a project loads), use the *explicitly-set* `Width`/`Height` properties instead — otherwise things render wrong on first load and "fix themselves" the moment any interaction triggers a layout pass, which is a very confusing bug to chase.
4. **Full-screen-window layout trap**: any "page"-style content (journal entry, poem, recipe) must be wrapped in a bounded container (fixed `MaxWidth`/`MaxHeight` `Border`), never left to size via `Height="*"` — this app's window spans the whole screen, so unbounded rows can silently stretch to 900+ pixels of invisible empty space.
5. **Dev workflow**: `dotnet` is not on PATH in the project's terminal — always call `& "C:\Program Files\dotnet\dotnet.exe"` explicitly. Before rebuilding, always kill any terminal running `dotnet run` for this app — the running `.exe` locks its own DLLs and the build fails with `MSB3026` file-lock errors otherwise.

---

## 6. Not yet built (remaining spec phases)

- **Organisation** (Projets + Transitions — progress tracking; spec explicitly says the Projet/Transition distinction needs clarifying with the user before designing).
- **Archives** (photo archive; family blog archive/scraper).
- **Musique** (mood-tagged library, floating player, separate from Collections).
- **Music downloader** (YouTube-link queue; must respect ToS, no DRM circumvention).
- **Widgets** (ambient desktop projections of existing data, e.g. tier-weighted "film of the day" — a *view* over existing data, not a new data source).
- `Phase0/` prototype folder still exists, unused — candidate for deletion whenever confirmed safe.
- No automated tests exist; all verification so far has been manual (build + run + click through).

- The header/reservation alignment fix (step 6 above) needs verification.

---

## 7. Session update (2026-08) — V1 built + refined

All four nav sections now have real content; several ambient/utility windows added. Everything below compiles and runs.

### 7.1 What now exists (by area)
- **Organisation** (`OrganisationView`, Projets/Transitions sub-tabs, master/detail):
  - `PersonalProject`(+`ProjectTask`): Status Active/Done/Archived, optional `Deadline`, task checklist → progress bar.
  - `Transition`(+`TransitionStep`): CurrentState→DesiredState, steps/progress, Notes, Resources, **`ReminderText`+`ShowOnDesktop`** (feeds the desktop reminder widget). `TransitionRepository.GetDesktopReminders()`.
- **Archives** (`ArchivesView`): **Photos** tab (`Photo`/`PhotoAlbum`; import→`media/archive` via `MediaStorage.ImportArchivePhoto`; thumbnail grid `DecodePixelWidth=200`; albums, tags, search, favorites; fullscreen viewer w/ arrow+Delete keys, slideshow `DispatcherTimer` stopped on `Unloaded`; delete is file-first + confirm). **Archive familiale** tab = placeholder (scraper deferred).
- **Musique** — floating `MusicPlayerWindow` (♪ header button). `MusicTrack`/`Mood`/`TrackMood`(+ unused `Playlist`/`PlaylistTrack`); `MusicTrack.Album`. `MusicLibraryScanner` scans `<exe>/music/*.mp3` (Title=filename, **Album = top-level subfolder**, backfilled on rescan). Playback via `System.Windows.Media.MediaPlayer` (no NuGet; duration from `NaturalDuration`). Player is **resizeable** (CanResizeWithGrip; `SizeToContent` toggles Manual/Height on expand/collapse); library shows **album accordions** (▶ per album plays it) OR a **flat filtered list** when an ambiance/search is active; **ambiances** with `+`-popup create, 🗑 delete (confirm), selected highlighted; library search box; queue. **Playlists were removed** (redundant w/ ambiances) — tables/repos remain but unused.
- **Collections** — existing tier lists + **new "À voir" watchlist tab** (`WatchlistItem`/`WatchlistItemRepository`; Film/FilmAnimation only; priority Should/Want/Need; optional cover). Separate from the watched tier lists; feeds Film du jour.
- **Ambient widgets** (tray-toggled): `FilmOfTheDayWidget` shows TWO daily picks (one Film + one FilmAnimation) from À voir — **animation shown by default, click poster flips to film**; weighted Need=3/Want=2/Should=1, same per day (persisted `FilmOfTheDayFilmId/AnimationId/Date`), **auto-refresh at midnight** (DispatcherTimer). `DesktopReminderWidget` shows `ShowOnDesktop` transitions. `FilmOfTheDayService.PickForDay(category,...)`.
- **Recherche globale** — header search box + `Popup`; `GlobalSearchService` (in-memory over repos); `ISearchNavigable` on all 4 views deep-links to the exact item; music hits open+play in the player.
- **Téléchargements** (tray) — queue-only + drives a **USER-installed yt-dlp** (dual-use; app does NOT bundle/circumvent). `ExternalDownloader` shells out; `AppConfig.DownloaderToolPath/DownloaderArguments/DownloaderAutoMode`; `setup-tools.ps1` (copied to output) installs yt-dlp+ffmpeg into `<exe>/tools/`; resolution: config path → `tools/yt-dlp.exe` → PATH; "Téléchargement auto" toggle + "Télécharger maintenant"; saves into `music/` then auto-rescans (`MusicPlayerWindow.ReloadLibrary()`); also open-in-browser / mark done / copy queued.

### 7.2 New architecture facts & gotchas (important)
- **`ShutdownMode="OnExplicitShutdown"`** (App.xaml): ONLY the tray Exit quits. Every top-level window (player, both widgets, downloader) MUST hide-on-close (`Closing`→`Cancel`+`Hide`) and expose `ForceClose()` called from `MainWindow.ExitApplication`. MainWindow owns them as lazily-created nullable fields. **Any new window must follow this pattern.**
- **Styled-button highlight gotcha**: button styles that hardcode the template `Border Background` (e.g. `PlayerSmallButton`, `ChipButtonStyle`) IGNORE `Button.Background`, so a "selected" background never shows. For stateful highlight use a template with `Background="{TemplateBinding Background}"` (see `PlayerChipButton`). This was the real cause of "selected ambiance not highlighted".
- **Button inside an `Expander` header** also toggles the expander — set `e.Handled=true` in its Click (used by the per-album ▶).
- **Resizeable custom-chrome window**: `WindowStyle=None`+`AllowsTransparency=True` needs `ResizeMode="CanResizeWithGrip"`; `SizeToContent` must be `Manual` to allow manual height (toggle to `Height` when collapsed to auto-shrink).
- **Dead code** to clean up later: `Artwork.WatchPriority` + `ArtworkRepository.GetWatchlistCandidates/UpdateWatchPriority` (replaced by `WatchlistItem`); `MainWindow._artworkRepository`; `Playlist`/`PlaylistTrack` repos.
- **PRAGMA foreign_keys still NOT enabled** — `ON DELETE` clauses are declarative; repos delete children manually. Consider enabling globally in a hardening pass.
- **Build/dev**: `dotnet` not on PATH → `& "C:\Program Files\dotnet\dotnet.exe"`; kill `AkashicRecords.App` before building (self-DLL lock → MSB3027); build log is UTF-16; one terminal is `cmd` (`&` fails — retry hits PowerShell).

### 7.3 Known pre-existing bug (not from this session)
- `CollectionsView.RemoveCoverImageButton_OnClick` → `MediaStorage.DeleteCover` throws `IOException` (cover file locked) — swallowed by the global handler, so "Remove cover" silently does nothing. Fix per gotcha #1 (load covers `OnLoad`+`Freeze`, and/or clear the `Image.Source` before deleting).

### 7.4 Still TODO
- Fix the cover-removal file-lock bug (7.3); remove dead code (7.2); give global search a proper review pass; family-blog archive scraper (needs the real site); optional: enable PRAGMA foreign_keys. No automated tests yet.

### 7.5 Fixed bug: track auto-advance cascading through the queue
Root cause: `PlayAt()` didn't reset `PositionSlider` before opening the next track. Old `Value` (e.g. 199s) stayed while `MediaOpened` lowered `Maximum` to the new (shorter) track's duration — WPF auto-clamps `Value` to the new `Maximum`, firing `ValueChanged`, which the handler mistook for a user seek and did `_mediaPlayer.Position = TimeSpan.FromSeconds(clampedValue)` — seeking the new track to near its end, instantly re-firing `MediaEnded` and cascading forward through several tracks. Fix: reset `PositionSlider.Value=0`/`Maximum=1` (guarded by `_updatingPositionFromTimer`) at the start of `PlayAt()`, before `_mediaPlayer.Open(...)`.
**General lesson**: when a Slider's `Maximum` can shrink below the current `Value`, WPF's auto-clamp fires `ValueChanged` as if the user moved it — any handler that reacts to `ValueChanged` by driving external state (like seeking a player) must reset `Value` first whenever `Maximum` is about to change, not just guard on who's "supposed" to be updating it.

