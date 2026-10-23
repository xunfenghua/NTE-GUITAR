# NTE-GUITAR / NET-GUITAR — AI-Oriented Project Guide

**Repository snapshot:** 2026-10-23  
**Application version in the project file:** 3.4.0  
**Purpose:** A code-grounded map of the desktop application: every user-facing page and performance mode, the major workflows and data contracts, and the core algorithms that transform MIDI/Guitar Pro notation into game input or practice charts.

> This guide began as a code map for an earlier snapshot and is retained as a navigation aid. Runtime behavior is defined by the current C# and web source code. The project is distributed as Lite: model weights, build output, personal logs, media, and song examples are excluded. Design-history files are not part of the public source tree.

## 1. Product in one paragraph

NTE-GUITAR is a Windows desktop application for preparing and performing music in game-like keyboard, piano, and guitar interfaces. It imports MIDI and Guitar Pro files, lets users select tracks or extract a melody, maps notes to one of three instrument systems (36-key keyboard, independently calibrated game piano, or game guitar), saves reusable local scores, previews timelines, and emits timed keyboard/mouse actions to a bound game window. A separate semi-automatic guitar overlay turns generated guitar charts into a falling-note practice/rhythm display, supports human input and position guidance, and can automate position changes or play actions in selected modes.

## 2. Technology, process, and major boundaries

- **Desktop host:** C# / .NET 8 Windows Forms (`NTE-GUITAR.csproj`, `Program.cs`, `UI/MainForm.cs`). The project builds as a Windows GUI application and targets `net8.0-windows`.
- **Primary workbench UI:** HTML/CSS/ES modules under `wwwroot/`, hosted by WebView2 inside `MainForm`. UI-to-native calls use a JSON message bridge (`Bridge/WebMessageHandler.cs`); native-to-web events use `Bridge/WebEventPublisher.cs`.
- **Semi-auto overlay:** A second WebView2 surface owned by a WinForms top-level window. Its web assets live in `Web/SemiAutoOverlay/`; overlay session controllers live in `SemiAuto/WinForms/` and portable chart/mode logic in `SemiAuto/Chart/` and `SemiAuto/Modes/`.
- **MIDI parser:** Melanchall.DryWetMidi 8.0.3. Guitar Pro import uses AlphaTab 1.8.4. WebView2 uses Microsoft.Web.WebView2.
- **Input:** Win32 `SendInput`, scan-code keyboard input, mouse input, global hotkeys, and foreground-window checks. Core timing uses monotonic clocks (`Stopwatch`) and absolute microsecond event timestamps.
- **Optional companion server:** `AnnouncementServer/` is a separate ASP.NET Core project for announcements and related remote catalogue endpoints. The desktop client remains usable for local library/playback without that service.

### 2.1 Responsibility map

| Area | Main source locations | Responsibility |
|---|---|---|
| App startup/config/window | `Program.cs`, `UI/MainForm*.cs`, `Models/AppConfig.cs` | Startup modes, settings load/save, WebView2 host, overlay lifecycle, hotkeys, target window, window behavior |
| Web workbench | `wwwroot/index.html`, `wwwroot/js/*.js`, `wwwroot/css/*.css` | Navigation, library, conversion dialogs, piano, simulator, calibration, settings, playback controls |
| Web/native bridge | `Bridge/` | Command dispatch and event publication; the native side owns filesystem, input, parsing, and playback |
| MIDI parsing/conversion | `Midi/`, `Recognition/`, `Models/` | Raw-note representation, tempo conversion, track metadata, melody extraction, 36-key conversion |
| Guitar algorithms | `Guitar/` | Fret/string assignment, chord inference, speed/polyphony reduction, hybrid mode selection, guitar action compilation/scheduling |
| Piano algorithms | `Piano/` | Piano layout profiles, pitch mapping, compile-time validation and input-action generation |
| Semi-auto charts/modes | `SemiAuto/` | Reusable game-guitar charts, position planning, practice/rhythm timelines and overlay controllers |
| Storage/services | `Library/`, `Services/`, `Input/` | Song/config storage, caching, imports, playback lifecycle, device calibration, recommended scores |
| Tests/help | `Testing/`, `Help/` | Acceptance-oriented test harness and in-product explanations/tutorial |

## 3. User-facing interfaces

The primary navigation is declared in `wwwroot/index.html`. Pages are swapped in place by `wwwroot/js/app.js`; native code is not a separate page router.

### 3.1 Library (`page-library`)

The library lists locally saved converted songs. Filters separate **All**, **Keyboard score (36-key script)**, **Piano (game piano)**, and **Guitar score**. Main actions include import, seed/restore built-in scores, play selected song, generate a game-guitar chart, reconvert with new options, rename, and delete. A selected song can be loaded into the playback dock, and guitar-chart generation stores a reusable structured chart alongside the song.

Storage abstraction: `Library/SongLibrary.cs`, `Library/SongSerializer.cs`, and `Services/LibraryService.cs`. Song identity is independent of the original file path; converted notes and raw-note source data are serialized with metadata. The project also has a separate GTP shelf (`gtp-library`) and a recommended-score download cache.

### 3.2 Recommended Scores (`page-recommended-scores`)

This page browses the remote recommended MIDI catalogue, exposes difficulty and instrument filters (keyboard, game piano, guitar), refreshes the list, and opens a submission form. The client service is `Services/RecommendedScoreService.cs`; bridge commands begin with `recommended.*`. Downloaded MIDI is cached locally, then either added using a default full-score conversion flow or opened in the normal import workflow. Availability depends on the configured server/network; local converted songs remain local.

### 3.3 Guitar Pro conversion (`page-gtp`)

Imports `.gp`, `.gp3`, `.gp4`, `.gp5`, and `.gpx` through AlphaTab. The page displays tracks and supports choosing full score or melody, selecting target instrument (36-key keyboard, game piano, or guitar), selecting tracks, and transposing. Results go into an isolated local GTP list, with preview/play/delete actions. Unlike MIDI import, Guitar Pro notes can carry string/fret, voice, brush, dead-note, grace, tie, harmonic, bend, palm-mute, and score chord metadata. These richer fields are preserved in `GtpRichNote` where available. Service: `Services/GtpConversionService.cs`; parsing/model layer: `Gtp/GtpModels.cs`.

### 3.4 Performance (`page-performance`)

A controls page for performance configuration. It exposes the legacy 36-key keyboard mode and three guitar modes (fret solo, 24-chord accompaniment, full hybrid performance), along with transpose, guitar/chord settings, key preset editor, and SendInput compatibility test. The game piano has a dedicated page and profile workflow. Page copy and control IDs are in `wwwroot/index.html`; state and actions route through `settings.*`, `tools.*`, and `playback.*` bridge commands.

### 3.5 Game Piano (`page-game-piano`)

Independent game-piano interface. The user selects a piano score, chooses **36-key chromatic** or **21-key seven-degree scale** layout, adjusts speed, previews compile/mapping status, and starts/pauses/resumes/stops. It uses a distinct calibrated `PianoLayoutProfile`; it is not the legacy 36-key keyboard preset. A game window must be bound before starting input. The session uses a three-second countdown, then a foreground-checked scheduler. See `Piano/Compiler/PianoMidiCompiler.cs`, `Piano/Mapping/PianoPitchMapper.cs`, and `Piano/Services/PianoSessionService.cs`.

### 3.6 Guitar Simulator (`page-simulator`)

An interactive visualization/simulator of in-game guitar positions. It depicts pages/slots/strings and lets users trigger controls/notes, navigate pages using R/T semantics, and exercise guitar mapping without relying on song playback. The frontend implementation is in `wwwroot/js/guitar-simulator.js`; native calibration/input facilities supply actual coordinates and key events.

### 3.7 Game Calibration (`page-calibration`)

Binds the currently focused external game window, reports compatibility/calibration status, opens the advanced wheel calibration dialog, tests slot selection/strum, resets the wheel coordinates, and offers automatic guitar-slot binding/detection for the supported game workflow. Calibration coordinates are normalized against the game client area so they can be scaled across window dimensions. The auto-detection path is implemented in `Services/CalibrationService.cs` and related service/UI files; the 2026-10-20 design note says live-game visual validation is still required.

### 3.8 Tutorial (`page-tutorial`)

Displays ordered tutorial steps from `Help/TutorialContent.cs`. Version/first-run behavior is controlled by `Services/FirstRunService.cs`, config flags, and the `settings.getTutorial` bridge operation.

### 3.9 Settings (`page-settings`)

Contains global play settings and timing calibration: global play/pause hotkey (default F8), semi-auto page previous/next bindings, focus-loss auto-stop, always-on-top, themes, recommended window size, timing defaults, pre-roll, page key hold/inter-tap gap, minimum hold, release gap, wheel settle, and pulse/tap duration. It can export a diagnostics bundle. `SettingsService` validates supported hotkeys and keeps page keys distinct from the global hotkey. Timing defaults are config-driven and shared with playback compilers.

### 3.10 Global shell, playback dock, dialogs, and overlay

- **Window shell:** minimize/close, top-most toggle that should not steal game focus, fixed/free size behavior, recommended 1080×700 size, dark/light glass themes.
- **Playback dock:** play/pause/stop, seek, playback speed, auto-speed selection, computer MIDI audition, and four completion policies: single-song loop, sequential next song, random next song, or stop after completion. `wwwroot/js/playback.js` and `Services/PlaybackService.cs` coordinate state.
- **Import/reconvert modal:** file selection, track list, all/none/intelligent melody-track selection, per-track audition, target instrument, transpose, out-of-range fold/discard, melody extraction mode, extraction-vs-source comparison, audition and save.
- **Additional WinForms dialogs:** 36-key preset editor, compatibility test, wheel calibration, and playback-start advice/confirmation.
- **Semi-auto overlay:** independent floating window described in section 4.

## 4. Performance modes and execution modes

There are several mode taxonomies in this project; do not conflate them. `AppConfig.PlayMode` selects the **main guitar compiler** (0–3). `SemiAuto.Chart.PlayMode` selects the **overlay session** mode. `PositionExecutionMode` selects manual versus semi-auto position handling.

### 4.1 Main performance targets

| Main target/mode | Input transformation | Result |
|---|---|---|
| 36-key keyboard (`PlayMode=0`, also library instrument `Keyboard`) | MIDI pitch → MIDI 52–87 (E3–D#6) → index 0–35 → current key preset scan code | Batched keyboard press/release events |
| Game piano (library instrument `GamePiano`, independent session) | MIDI pitch → calibrated 12-key chromatic or 7-degree scale layout → note key plus optional modifier scan code | Piano profile actions and active-note visualization |
| Guitar fret solo (`PlayMode=1`) | selected source notes → solo sanitization → global octave selection → six-string/fret candidates → global position path | String keys plus page/slot/mouse actions |
| Guitar 24-chord accompaniment (`PlayMode=2`) | notes → chord evidence and temporal chord state → game chord/page/slot and articulation | Chord strums, blocks, arpeggios or plucks |
| Guitar full hybrid (`PlayMode=3`) | polyphony reduction + automatic speed + chord/shift analysis + per-onset fret/chord candidates + Viterbi | One globally planned mix of fret and chord actions |

Keyboard and game piano are not the same instrument implementation. The former stores index-based notes mapped through a user-editable 36-key preset. Piano uses its own verified physical key layout and can reject invalid mappings before playback.

### 4.2 Semi-auto overlay play modes

`SemiAuto/Chart/Models.cs` defines `Practice`, `RhythmNormal`, `RhythmHell`, `AutoShowcase`, and `Simulated`; the overlay selector currently exposes **音游模式** (`rhythm-normal`), **MIDI 跟谱** (`simulated`), **地狱模式** (`rhythm-hell`), and **锁屏可打** (`auto-showcase`). Practice is an additional controller-supported mode and may be opened via controller/API flows even though it is not one of the visible selector options in this snapshot.

| Overlay mode | Human input | Position automation | Main behavior |
|---|---|---|---|
| Practice | Required | Optional | Time-aligned chart targets; correct/wrong feedback; supports hold completion and manual/automatic position switching rules |
| Rhythm Normal | Required | Configurable | Falling seven-lane notes, hit timing grades, progress/score/combo feedback; lane keys default to `1 2 3 4 5 6 J` |
| Rhythm Hell | Required | Configurable | Rhythm mode with the harder session rules/tuning; shares chart, wall-clock, and hit-judgement foundations |
| Auto Showcase / lockscreen playable | Not required for scoring | Usually semi-auto execution | Automatically follows chart timing and position plan; overlay visualization/score is an automatic showcase measure, not a guarantee of the game's own score |
| Simulated / MIDI following | Required | No game input | Accepted hits play MIDI note/program through the MIDI output device; does not bind the game window or send game keyboard/mouse input |

Overlay has **Full Manual** and **Semi Auto** position execution. Full Manual displays the desired page/slot and leaves physical changes to the player. Semi Auto sends page and wheel actions using calibrated controls. Separately, a **switch mode** selector chooses automatic or manual guitar position switching. Keep those two configuration axes distinct.

Overlay UI includes a local/recommended guitar library picker, song progress/seek, speed (25–200%), play/pause/reset, current/next position, remaining time, falling-note canvas, seven-lane key display, lane remapping dialog, opacity, narrow/wide window layout, theme, drag handle, immersion options/hotkey, and P1–P5 starting-page confirmation when automatic positioning is required. Full immersion hides surrounding chrome while retaining the lane chart/keybar; optional auto-immersion enters on play and exits on pause/end. Overlay files are under `Web/SemiAutoOverlay/`; controllers are under `SemiAuto/WinForms/`.

### 4.3 Session behavior and safety

F8 is the default global play/pause key. Overlay visibility has an F9 hotkey. The main playback schedulers check the bound target window; with focus-loss stopping enabled, leaving the game foreground stops playback. Piano additionally verifies at countdown completion that the same bound window remains foreground. Stop, completion, failure, and disposal paths release held keys. Overlay input observation and game input sending are separate from the simulated MIDI output path.

## 5. Canonical data model and end-to-end pipeline

### 5.1 Pipeline

```text
MIDI/Guitar Pro source
  → parse tracks, note events, tempo map and instrument/notation metadata
  → normalize event times to absolute microseconds
  → user track selection / drum filtering / melody extraction
  → optional transpose, octave fold, polyphony reduction, speed scaling
  → target-specific mapping and global playability planning
  → timestamped keyboard/mouse/MIDI-output actions OR a reusable chart
  → absolute-time scheduler + target-window checks + release-all cleanup
```

### 5.2 Key records

- `Models/RawMidiNote.cs`: a source event with `StartUs`, `DurationUs`, MIDI pitch, velocity, channel, track/program metadata and optional guitar/voice provenance fields.
- `Models/GameNoteEvent.cs`: target 36-key normalized note (`NoteIndex` 0–35 plus time/duration).
- `Models/ConvertedSong.cs`: persistent song identity, display name, source hash/name, raw/selected source notes, converted keyboard notes/options, instrument/category, duration, and optional derived data.
- `Models/ConvertOptions.cs`: transpose, out-of-range policy, selected tracks, target instrument, recognition mode and related conversion flags.
- `Models/InputBatch.cs`, `Models/KeyAction.cs`: timestamped input actions grouped for efficient simultaneous dispatch.
- `Piano/Models/PianoProfile.cs` and `PianoCompileModels.cs`: calibrated piano bindings, layout kind, modifiers, validation issues, mapped notes and compile result.
- `SemiAuto/Chart/Models.cs`: immutable-ish chart records for notes, guitar positions, feasibility, position events, issues, and compile options.
- `Guitar/GuitarModels.cs`: guitar chord IDs, articulation, action resources, positions/events and compiler metadata.

### 5.3 Time semantics

The canonical music timeline is signed 64-bit **microseconds**. `MidiLoader` maps MIDI ticks through the file's tempo map; algorithms should not assume constant BPM. Playback events are absolute offsets from a monotonic scheduler origin. Speed transforms timestamps by `scaledUs = round(rawUs * 100 / speedPercent)`. Wall-clock time used in rhythm modes is converted back to original timeline units before grading so the judgment windows remain musically meaningful at altered speeds.

## 6. Core algorithms

### 6.1 MIDI parsing and cache

`Midi/MidiLoader.cs` reads the entire file with DryWetMIDI, calculates SHA-256, extracts all tracks/notes and track metadata, records the tempo map and maps start/end ticks to microseconds. Note On with velocity zero is treated as note-off according to MIDI convention. Notes retain channel, velocity, track, and program context. Results are sorted by onset and pitch. `Midi/MidiRawCache.cs` caches source-level raw-note parse output by content hash; conversion options are applied later and must not contaminate this cache.

### 6.2 Track choice and melody extraction

`Recognition/RecognitionService.cs` coordinates modes. The user can preserve a full score or select tracks manually. Current extraction families:

- **FullSong:** retain all input polyphony.
- **TrackPriority:** score a track using name tokens, GM instrument/program, pitch range, and density; favor vocal/lead/solo-like tracks and penalize bass/drums/chords/pads.
- **HighestNote:** group close onsets (about 18 ms), select the highest note, with heuristics to reject short ornaments/extreme isolated high notes and occasionally choose a plausible second-highest note.
- **ContinuousMelody:** make short temporal slices, score a bounded set of note candidates, and use Viterbi/dynamic programming over time. Transition bias is configurable; avoid assuming all real melodies are stepwise.
- **Monophonic:** score TrackId+Channel voices for monophony, range and GM program, select a likely voice, then collapse close polyphonic onsets to a top note.
- **AutoLeadFusion:** rank likely lead tracks by name/program/range/density/polyphony, keep a small candidate set, create onset candidates and explicit rests, then use a path search balancing local salience, continuity, same-track support and cross-track switching.

`PolyphonicMelodyExtractor` handles single-track polyphonic material distinctly from multi-track selection. Recognition is a heuristic over MIDI evidence; it cannot recover source-separation information that is absent from the file. Extraction preview reports retained notes/range and supports audition before saving.

### 6.3 36-key keyboard mapping and action compilation

`Midi/MidiConverter.cs` maps pitches to a 36-semitone playable span: MIDI 52 (E3) through 87 (D#6). Processing filters invalid/zero-velocity notes, optional channel 10 percussion (zero-based channel 9), unselected tracks and applies transpose. Out-of-range behavior is either discard or octave-fold by ±12 until in range. `NoteIndex = pitch - 52`. Same-onset duplicate indices are deduplicated. Persistent notes store logical indices, not physical key codes, so changing a preset remaps the whole library without reconversion.

`Input/ActionCompiler.cs` converts index/time/duration records to scan-code down/up actions using the active preset. Its pulse policy enforces minimum hold and retrigger gaps, shortens/advances releases for quick repeats, merges duplicate same-time physical key attacks, and can use dynamic sustain only when later events leave enough space. Same-timestamp key changes are grouped in an `InputBatch`; key-up sorts before key-down at identical times. `Input/InputScheduler.cs` sends batches against an absolute monotonic timeline and releases held keys on stop.

### 6.4 Game piano pitch mapping and scheduling

`Piano/PianoMidiCompiler.cs` validates the selected profile, optionally chooses a **global transposition** for the entire song, folds pitches into its playable range when configured, maps notes through `PianoPitchMapper`, applies tempo scaling, and compiles key/modifier events. The global shift search preserves the intervals of the piece; scoring rewards in-range notes and useful middle/natural-note positions, accounts for note duration/velocity, and penalizes larger transpositions. Manual nonzero transpose disables that automatic choice.

- **TwelveKeyChromatic:** select an exact base-key candidate or an exact pitch reachable by a validated Shift/Ctrl semitone modifier. Candidate ordering considers preferred modifier, direct key, pitch distance and layout row/position. Modifier requirements in a chord are resolved to avoid incompatible simultaneous modifiers where possible.
- **SevenKeyScale:** represent notes by degree across low/middle/high rows from a tonic and seven scale intervals. In-scale notes map exactly; out-of-scale notes adapt to the nearest degree and are marked as adapted. Layout bindings must be verified and have valid scan codes.
- **Action planning:** scale start/end times, maintain minimum key holds/retrigger intervals, cluster near-simultaneous chord onsets, cap polyphony to the verified profile limit while preserving top/bottom notes, resolve modifier conflicts, stagger incompatible modifier groups if needed, and generate explicit modifier pulses. Validation errors block playback. `PianoSessionService` compiles preview first and on start requires a bound foreground target, performs a 3-second countdown and then schedules actions.

### 6.5 Guitar fret solo mapping

`Guitar/FretConverter.cs` maps an equal-tempered pitch onto standard guitar strings (physical string 1–6, open MIDI pitches E4/B3/G3/D3/A2/E2 = 64/59/55/50/45/40) and frets 0–24. A fret maps to a game position as follows: fret 0 is P5/S1; frets 1–24 map to pages P1–P4 in six-fret groups and slots S1–S6. The algorithm does not clamp each impossible note to a nearby wrong pitch; impossible notes can become skipped states.

Already marked monophonic melody input is preserved through solo sanitization (except percussion filtering). Otherwise, notes within roughly 15 ms onset tolerance are reduced to the highest pitch, with same-pitch reattacks preserved. A whole-song octave shift is selected from candidates using weighted playability and a strong penalty for changing the register; this preserves intervals. For each note, candidate string/fret locations are scored by fret height, natural register/string fit, page/slot changes, hand movement and rapid-legato costs. Dynamic programming/Viterbi selects a globally low-cost path. Transitions that cannot physically finish page/slot preparation in the available interval are marked as timing conflicts and penalized; the compiler avoids silently pretending the old position is correct.

`Guitar/GuitarCompiler.cs` serializes page keys (R/T), mouse-wheel slot selection, string press/release, plucks and strums according to timing dependencies. Mechanical actions cannot all be sent as if they were a simultaneous chord: page switching and mouse settle must happen before the note attack.

### 6.6 Chord analysis and articulation

`Guitar/ChordAnalyzer.cs` and `ChordAnalyzerV2.cs` identify game-supported chords from onset/time windows and a game-specific ground-truth voicing table (`GameChordGroundTruth.cs`). The V2 analyzer uses exact attacks to avoid collapsing separate nearby plucks, gathers evidence across a temporal observation window, scores chord coverage/completeness/root and bass evidence, penalizes non-chord tones, and applies minimum stability plus hysteresis before changing chord state. Optional strict/bass-root settings alter penalties. Global semitone transposition can be searched to fit the game's chord vocabulary while penalizing unnecessary deviation from the original key.

Chord actions can be block strum, arpeggio, or plucked string notes. The code distinguishes game chord voicings from generic theoretical triads: emitted pitches should reflect actual game-string voicings. `StrumAnalyzer.cs` detects directional/articulation hints from note timing; `GuitarCompiler` and scheduler turn those into mouse or per-string actions. Same physical string retriggers are shortened to ensure release before the next attack.

### 6.7 Full hybrid guitar compiler (Mode 3)

`Guitar/HybridGuitarCompiler.cs` plans an entire performance as a sequence of onset frames, choosing between fret positions and supported chord actions instead of forcing one strategy for the whole song.

1. **Reduce source polyphony:** `MidiPolyphonyReducer` removes channel-10 percussion, extremely quiet/short notes, and certain unusually long pad notes; deduplicates equal pitches while keeping stronger/primary/longer evidence; limits frame voices while protecting marked primary melody, high register and bass/root coverage. Default onset grouping does not merge attacks that have distinct exact times unless configured.
2. **Automatic speed:** `MidiSpeedOptimizer` estimates the minimum physical time needed for same-key retrigger and large fret/page/slot transitions (including hold, release, and control/settle margins). The most demanding analyzed interval determines a recommended 25–100% speed. A manual speed overrides automatic speed; effective scale is `100 / speedPercent`.
3. **Chord/transpose/octave analysis:** analyze the scaled notes for game-chord matches and choose global semitone/octave adjustments; discard pitches outside the true playable guitar range rather than per-note clamping to false pitches.
4. **Frame candidates:** each exact onset frame yields fret candidates that preserve the top melody pitch and may attach other notes playable at a common position; supported chord candidates emit real game chord/articulation options. Primary melody protection requires the chord's actual sounding pitch to retain the lead when it is active.
5. **Global path selection:** Viterbi minimizes a path cost combining missed accompaniment, mode changes, page/slot changes, wheel settle time and physical transition feasibility. Switching guitar mode may reset the game's fret page to P1, so preparation time is computed from that reset. Long-note/lead-protection states can hold position. A transition without enough preparation time is not considered a valid low-cost route.
6. **Action emission:** selected decisions become keyboard page/mode/string controls and mouse wheel/strum actions with minimum holds and ordered dependencies. Repeated string attacks are shortened before the next onset.

The design objective is to preserve exact high melody pitches first, while simplifying accompaniment when the game's finite chord/fret system cannot represent every note. This is a constrained heuristic planner, not an audio synthesizer or a guarantee that the external game accepts every input.

### 6.8 Game guitar chart compiler for practice/overlay

`SemiAuto/Chart/GuitarChartCompiler.cs` creates a **chart**, not an input action stream. It retains source target notes, marks playability (`Playable`, `AtRisk`, `Unplayable`, etc.), maps notes to distinct physical strings/frets, groups simultaneous onsets (default 20 ms), and plans position events. Global position selection uses Viterbi with fret/page/slot movement penalties and a configurable physical preparation-time model (`PositionTimingModel`). If a simultaneous group has no common position, notes remain in the chart as unplayable with an issue rather than being silently deleted. It reports counts and a playability rate. The chart can be cached with source hash/effective-note fingerprint and exported to PDF.

This chart pipeline is distinct from `FretConverter` and the full hybrid compiler: it keeps diagnostic/unplayable note records for human practice and UI, rather than compiling only executable key actions.

### 6.9 Overlay practice and rhythm clocks

- `PracticeSessionController` holds time-aligned targets and judged IDs; scoring a note does not mutate or reorder the source chart. It groups same-time targets for chord prompts and accepts input near target times. `PracticeEngine` compares expected and actual game lane keys and calculates hold completion. Input observation is via `NativeInputObserver`.
- `RhythmSessionController` uses `Stopwatch` for elapsed time, applies speed ratio, converts wall-clock error back to original timeline time, and classifies hits through `HitJudgement`: Perfect/Good/Miss windows are configured in the controller (80 ms and 160 ms in this snapshot). `ClampWindowToAvoidDoubleHit` can cap a judgment window based on nearby targets.
- `PracticeTimeline` separates chart offset, wall-clock origin, pause/resume and playback speed. `PositionSwitchDifficulty` and `PositionTimingModel` estimate required position-change lead time and difficulty.
- `AutoShowcaseInputPlan` materializes the timed page/slot/string action plan for automatic showcase mode. `ImmersionSessionState` owns immersion mode state and toggle behavior.

### 6.10 Playback, calibration and input dispatch

`Services/PlaybackService.cs` owns selected song, start/pause/resume/stop/seek/speed, audition, compile dispatch and status events. It chooses keyboard scheduler or guitar scheduler based on instrument/mode. `InputScheduler` and `GuitarScheduler` execute precompiled timestamped batches. Keyboard sends use scan codes; guitar position selection uses calibrated normalized wheel coordinates and mouse/key actions. `KeyboardHoldGuard` and cleanup/finally paths protect against stuck keys. Mouse coordinates are transformed against the bound window/client rectangle. The UI/WebView bridge is not trusted to send arbitrary OS input directly; commands are decoded and validated in the native layer.

## 7. Persistence, caches and external services

### 7.1 Local app data

The app stores configuration and user data below `%LocalAppData%\\NTE-GUITAR\\` (older README references may use the former `MidiAutoPlayer` product folder). Main structures include:

- `config.json`: themes, selected preset, hotkeys, timing, calibration, piano profiles and overlay preferences.
- `presets/`: 36-key keyboard scan-code maps.
- `cache/raw/`: source MIDI parse cache keyed by SHA-256.
- `library/`: song index and per-song metadata/binary note data; guitar charts can be stored with the song.
- `gtp-library/`: isolated serialized Guitar Pro converted shelf.
- `recommended/`: downloaded recommended MIDI cache, separate from the user's converted library.

Song serialization uses explicit versionable metadata and resilient loading; saving uses temporary file/replace patterns where implemented. The chart store validates source identity/fingerprint to avoid reusing stale derivations after source changes.

### 7.2 Announcement/recommended score server

`AnnouncementServer/` is a standalone server project with an admin page and JSON-backed data. Client calls are optional network features. Recommended-score entries identify title/author/source/instrument/difficulty, file hash/size and download path; client-side hash verification is the integrity boundary before caching. Server deployment/update notes are in `AnnouncementServer/README.md` and packaging scripts.

## 8. Bridge command inventory (conceptual groups)

`Bridge/WebMessageHandler.cs` is the canonical list of WebView commands. The current handlers include:

- `announcement.*` — check, mark seen, open download.
- `recommended.*` — list/use/prepare manual import/prepare guitar chart/pick submission MIDI/submit.
- `library.*` — list, generate guitar chart, delete, rename, pick/parse MIDI, reconvert, preview extraction, save raw piano source, seed built-ins.
- `piano.*` — get state, set layout, update scale, compile preview, start/pause/resume/stop.
- `gtp.*` — pick/parse/convert/list/preview/play/delete.
- `playback.*` — load/play/toggle/pause/resume/stop/seek/standard or custom audition/set speed/auto speed/get or recover state.
- `calibration.*` — status, bind game window, auto bind/detect, confirm/cancel detection, test slot, save preset, reset defaults.
- `settings.*` — read/update, reset timing defaults, get algorithm help/tutorial, export diagnostics.
- `tools.*` — open preset editor, compatibility test, wheel calibration.
- `window.*` — minimize/close, size lock/restore, top-most state.
- `semiauto.*` — open/toggle, open with mode, set theme, compile chart.

Web code communicates through `wwwroot/js/bridge.js`. Keep command names/payload shapes synchronized between frontend and `WebMessageHandler`; push updates through `WebEventPublisher` and listen in the corresponding page module.

## 9. Command-line entry points and validation surface

`Program.cs` supports the default GUI and diagnostic/dev entry points:

- `--test` / `-t`: run the in-process acceptance test runner (`Testing/TestRunner.cs`).
- `--recommended-smoke`: run recommended-score integration smoke workflow.
- `--diagnose` / `-d`: run guitar diagnostics.
- `--build-icon`: internal icon helper.
- `--create-sample`: create a sample MIDI file.
- `--screenshot`: internal screenshot capture helper.

Tests are intentionally not run as part of this documentation task. Existing test sources include guitar, piano, semi-auto and recommended-score acceptance coverage. See `Testing/Acceptance-Test-Checklist.md` and `Testing/*Tests.cs` for actual assertion scope.

## 10. Important limits and interpretation rules for AI agents

1. **Preserve instrument distinctions.** `Keyboard` is the legacy 36-key script instrument. `GamePiano` is an independent calibrated instrument. `Guitar` has fret/chord/hybrid compile paths. Library category strings or UI labels can be ambiguous; inspect `LibraryInstrument` and `SongLibrary.ResolveInstrument`.
2. **Preserve raw note provenance.** `SelectedRawNotes` and recognition settings matter to guitar overlay/chart generation. A converted 36-key note list is insufficient to reconstruct a guitar performance.
3. **Do not equate a chart with compiled playback.** `GuitarChartCompiler` retains unplayable records for reporting; `FretConverter` / `HybridGuitarCompiler` select executable actions.
4. **Do not assume constant tempo.** Use `StartUs`/`DurationUs` produced from the MIDI tempo map. Do not re-interpret source ticks using one BPM.
5. **Do not flatten mechanical dependencies.** Guitar page changes, wheel selections, settle times and pluck/strum events have order and physical duration constraints.
6. **Do not assume the game accepted an action.** The app sends OS input and can check window focus, but external game recognition/network state is not fully observable. Auto-showcase grading is an app visualization unless game-side scoring is independently verified.
7. **Treat specifications as history.** Check source before asserting that every behavior in a dated spec is implemented. Example: the client heartbeat spec is marked pending review in the supplied snapshot; the Huanhuan automatic calibration spec notes real-game visual validation is outstanding.
8. **Time units are microseconds internally.** UI milliseconds and percentages are converted at boundaries. Watch integer overflow and rounding when editing time arithmetic.
9. **The source tree includes generated/build artifacts and `scratch/`.** The main project excludes scratch, `AnnouncementServer`, and `dist` from its compile item set. Use `NTE-GUITAR.csproj` and source directories as primary context, not stale files under `bin/`, `obj/`, or publish folders.

## 11. Source map for focused follow-up

| If the task concerns… | Start here |
|---|---|
| App lifecycle, WebView2, game binding, hotkeys, overlay window | `Program.cs`, `UI/MainForm.cs`, `UI/PlaybackHotkeyRouter.cs` |
| Main HTML page/controls | `wwwroot/index.html`, then the matching `wwwroot/js/<page>.js` |
| Web/native command contract | `Bridge/WebMessageHandler.cs`, `Bridge/WebEventPublisher.cs`, `Bridge/WebCommand.cs`, `wwwroot/js/bridge.js` |
| MIDI import/time/track info | `Midi/MidiLoader.cs`, `Models/RawMidiNote.cs`, `Services/LibraryService.cs` |
| Melody extraction | `Recognition/RecognitionService.cs`, `Recognition/*Mode.cs`, `Recognition/PolyphonicMelodyExtractor.cs` |
| 36-key conversion/input | `Midi/MidiConverter.cs`, `Input/ActionCompiler.cs`, `Input/InputScheduler.cs`, `Input/PresetManager.cs` |
| Piano mapping/session | `Piano/Mapping/PianoPitchMapper.cs`, `Piano/Compiler/PianoMidiCompiler.cs`, `Piano/Services/PianoSessionService.cs` |
| Guitar fret/chord/hybrid | `Guitar/FretConverter.cs`, `Guitar/ChordAnalyzerV2.cs`, `Guitar/HybridGuitarCompiler.cs`, `Guitar/GuitarCompiler.cs` |
| Guitar chart/export/PDF | `SemiAuto/Chart/GuitarChartCompiler.cs`, `GuitarChartStore.cs`, `ChartExportService.cs`, `GuitarChartPdfExporter.cs` |
| Overlay modes/input/rendering | `Web/SemiAutoOverlay/index.html`, `app.js`, `styles.css`; `SemiAuto/WinForms/`; `SemiAuto/Modes/` |
| Library identity/serialization | `Library/SongLibrary.cs`, `Library/SongSerializer.cs`, `Models/ConvertedSong.cs` |
| Settings/calibration/diagnostics | `Services/SettingsService.cs`, `CalibrationService.cs`, `DeviceCompatibilityService.cs`, `DiagnosticsExportService.cs` |
| Remote catalogue/announcement | `Services/RecommendedScoreService.cs`, `Services/AnnouncementCheckService.cs`, `AnnouncementServer/` |

## 12. Naming glossary

- **36-key / keyboard score:** Fixed 36-semitone logical note index range MIDI 52–87 mapped to a user-defined scan-code preset.
- **Game piano / Piano:** Separately calibrated physical layout, either chromatic 12-key semitone mapping or seven-degree scale mapping.
- **Fret mode:** Game guitar strings selected at an individual fret/page/slot position.
- **Chord mode:** Select one of the game's finite supported chord voicings, then strum, block, arpeggiate or pluck.
- **Hybrid mode / Mode 3:** Global planner that may alternate fret and chord actions per onset/frame.
- **Game guitar chart:** Human-readable/practice representation retaining playability issues and page/slot changes.
- **Semi-auto:** Overlay can execute position changes/actions while the player supplies note input, depending on selected overlay mode.
- **Auto-showcase / lockscreen playable:** App-driven demonstration path; its visualization score should not be interpreted as authoritative game scoring.
- **FullSong vs melody modes:** Source-note selection policies before instrument mapping; they are not performance instrument modes.
- **Position execution mode:** Whether the overlay sends the position change or only presents a prompt; distinct from melody extraction and guitar compiler mode.

---

## Appendix A. Numeric defaults and algorithm constants

Values below are source defaults for the current snapshot. User settings, compiler options, layout profiles, or callers may override them. When editing behavior, confirm the active call site's option object rather than assuming a default is universal.

### A.1 MIDI recognition and keyboard

- MIDI channels are zero-based in the raw model; channel index `9` is the conventional percussion channel 10.
- `HighestNoteMode` groups attacks within about 18 ms. `ContinuousMelodyMode` slices at 20 ms and keeps at most four note candidates per slice. `MonophonicMode` uses 20 ms coincidence grouping and only considers voices with at least 10 notes for its main voice ranking. `AutoLeadFusionMode` groups at 25 ms, keeps up to five lead tracks, uses up to three note candidates per group, and includes an explicit rest state.
- Keyboard valid MIDI range is inclusive `[52, 87]`; index is `pitch - 52`. Transpose is normally bounded by the UI/configuration to ±24 semitones. Octave folding repeatedly applies ±12; discard mode removes notes outside range.
- `ActionCompiler` defaults: tap 30 ms, minimum hold 20 ms, release gap 2 ms, retrigger interval 8 ms, sustain disabled by default in compiler options. App configuration also has dense pulse hold 25 ms. When dynamic sustain is enabled, the current code only lengthens notes when the next same-physical-key attack is at least 40 ms away or this is the final attack on that key; the configured cap is 1.5 s. A later global event can still shorten the hold to preserve release gaps.

### A.2 Piano mapping and event planning

- Default uncalibrated chromatic/scale layout scaffold has three rows. Scale rows have seven positions, and chromatic rows have twelve. Profiles begin unverified; a profile that has not passed validation must not be treated as executable.
- Piano compile options default to 100% speed, percussion-channel filtering enabled, and `AutoTransposeMaxCoverage` for an unplayable mapping unless the caller requests another policy. Piano's general default play range is MIDI 48–83, with profile bindings able to affect candidate coverage.
- Automatic chromatic octave transpose tries every integer shift in `[-18, +18]`; manual nonzero transpose bypasses the automatic search. The scoring weights each note by duration `clamp(DurationUs / 100 ms, 0.5, 6)` and velocity `clamp(Velocity / 100, 0.25, 1.27)`. In-range notes receive `12 × weight`, pitches in MIDI 55–79 add `1.5 × weight`, natural pitch classes add `2.5 × weight`, out-of-range pitches receive a distance-based penalty, and the global shift is penalized by `0.65 × abs(shift)`. Seven-key scale mode uses a separate search over the same shift range and strongly rewards in-scale pitches while penalizing out-of-scale notes.
- Piano time scaling is `round(timeUs × 100 / speedPercent, AwayFromZero)` with speed clamped to 25–200%. Current compiler constants: minimum hold 20 ms, same-key retrigger gap 8 ms, 15 ms onset cluster, 16 ms modifier-group stagger, 5 ms modifier lead-in, 10 ms modifier pulse, and 45 ms modifier-context reuse window. Chord duplicate mappings are deduplicated; verified polyphony limits preserve the highest note, usually the lowest note too, then fill remaining capacity from inner notes.

### A.3 Guitar fret planning and chord analysis

- Standard open-string pitches, physical strings 1 through 6: `[64, 59, 55, 50, 45, 40]`. Frets are 0–24. Fret 0 maps to P5/S1; frets 1–24 map by `page = floor((fret-1)/6)+1`, `slot = ((fret-1) mod 6)+1`.
- Fret solo onset grouping is 15 ms. Global octave-shift candidates include `{0, ±12, ±24}` plus useful shifts in `[-48, +48]` that create overlap with the playable range. Candidate scoring strongly favors preserving source register; notes outside the selected global playable range are not individually clamped. Position cost has no high-fret penalty through fret 12, then adds about 0.6 per fret for frets 13–18 and 2.0 per fret for frets 19–24. A physically late transition receives a large conflict penalty (250) rather than silently using a stale position. Fast phrases under 320 ms favor staying on the same page/slot.
- ChordAnalyzerV2 defaults: exact same-onset grouping (`AttackClusterUs = 0`), 320 ms harmonic evidence window, 240 ms minimum chord stability, 0.14 hysteresis, auto-transpose enabled. Semitone transpose search is `-6..+5`. Evidence weights by note duration and velocity, boosts pitches below MIDI 60, compares pitch classes with the game's C/G chord vocabulary, and slightly penalizes moving away from the original key. The score combines chord-tone coverage (0.62), completeness (0.26), bass/root/page priors, and non-chord penalties; strict and bass-root modes tune these terms.
- The 24 game chord voicings are explicit in `ChordAnalyzerV2.ChordStringPitches`; array order is physical string 6 (bass) through string 1 (treble). Do not substitute generic guitar chord fingering for this game-output table.

### A.4 Hybrid guitar and physical timing

- `MidiPolyphonyReducer` default voice cap is six per frame. It removes channel 9, velocity below 12, duration at most 5 ms, and pad programs 88–103 when duration exceeds 1.5 s. With zero onset snap tolerance, distinct attack timestamps remain separate. Equal-pitch duplicates keep primary melody first, then greater velocity and duration.
- `MidiSpeedOptimizer` default physical budget includes a 20 ms minimum key hold and 8 ms release gap (28 ms for a repeated same note), 50 ms wheel settle estimate, and 8 ms control inter-tap gap. Large pitch jumps (6 semitones or more) add page/slot preparation margin. Automatic recommendations are clamped to 25–100%; manual playback can be 25–200% and overrides the recommendation.
- Main app guitar config defaults include 25 ms page key hold, 25 ms inter-tap gap, 15 ms safety margin, 75 ms wheel settle, 25 ms mouse click hold, 1.5 s pre-roll, and 25 ms dense pulse. Some algorithm options have narrower/historical defaults; active `TimingProfile` values from `AppConfig.ToTimingProfile()` are the runtime source for main playback.
- Guitar page/slot actions are serialized around `PageKeyHold + PageInterTapGap + safety`; after a page change, wheel settle is required before a slot-dependent attack. Same-page slot changes still require mouse settle. Scheduler batches simultaneous compatible note actions but does not erase these mechanical dependencies.

### A.5 Practice chart and rhythm timing

- Chart compiler defaults: simultaneous tolerance 20 ms, page key hold 25 ms, inter-tap gap 12 ms, wheel settle 55 ms, safety margin 8 ms, page-change cost 8, slot-change cost 2, fret-distance cost 0.25, high-fret penalty 0.08 per fret above its baseline, infeasible transition penalty 10,000, Viterbi enabled.
- Rhythm grading in `RhythmSessionController` converts wall-time error into original chart-time error using speed ratio, then uses 80 ms Perfect and 160 ms Good windows; larger errors are Miss. These values classify local overlay feedback only.
- Practice input tolerance defaults to 80 ms. Simultaneous chart targets are grouped within 20 ms. Judging does not delete chart events; completion is tracked separately by note IDs.

These numeric details are summarized here so an AI reader has one English reference. Source code remains authoritative when an option is overridden or a behavior changes.
