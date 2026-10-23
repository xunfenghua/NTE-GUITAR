# Algorithms and data flow

This guide explains the algorithm boundaries in the source and helps locate implementations. It is not a line-by-line formal proof of every parameter; current C#/JS/Python code is the source of truth for exact behavior and defaults.

## 1. MIDI parsing and the unified timeline

`Midi/MidiLoader.cs` uses DryWetMIDI to read tracks, notes, channels, tempo-map events, and source metadata. MIDI positions begin in ticks; the tempo map converts ticks across tempo changes into elapsed time, then conversion normalizes events to a microsecond timeline. Note start and duration are preserved for editing, speed changes, compilation, preview, and MIDI writing. Track selection, drum filtering, and deduplication are applied at conversion according to options. Playback aligns absolute deadlines against a monotonic clock instead of accumulating one `Thread.Sleep` delay after another.

Implementation: `Midi/MidiLoader.cs`, `Midi/MidiConverter.cs`, `Models/RawMidiNote.cs`, `Core/PlaybackSpeedEngine.cs`, `Input/InputScheduler.cs`.

## 2. Melody recognition and track selection

`Recognition/` contains track-priority and melody-candidate strategies based on note density, pitch range, continuity, and onset neighborhoods. Polyphonic notes are grouped by onset, then ranked toward the highest voice, a continuous line, or a fused candidate. The import UI allows audition and selection, so extraction is a recommendation rather than a replacement for user control.

Strategies include monophonic reduction, highest-note selection, continuous melody, and lead fusion. They accept `RawMidiNote` collections and return ordered candidates.

Implementation: `Recognition/TrackPriorityMode.cs`, `Recognition/MonophonicMode.cs`, `Recognition/HighestNoteMode.cs`, `Recognition/ContinuousMelodyMode.cs`, `Recognition/AutoLeadFusionMode.cs`, `Recognition/PolyphonicMelodyExtractor.cs`.

## 3. 36-key script mapping

The target range is E3–D#6 (MIDI 52–87), mapped to 36 zero-based key indices. Transposition happens before mapping. Out-of-range notes are dropped or octave-folded according to options. Folding searches octave-equivalent pitches in range and deduplicates collisions. The library stores logical note indices rather than scan codes; `KeyPreset` maps indices to the current physical layout so remapping does not require reconversion. Notes at the same onset remain grouped as chords.

Implementation: `Midi/MidiConverter.cs`, `Midi/PhraseOctaveFolder.cs`, `Models/GameNoteEvent.cs`, `Models/KeyPreset.cs`, `Input/PresetManager.cs`.

## 4. Game-piano mapping and reachability

Game piano uses its own calibration profile, separate from the 36-key script keyboard. The mapper projects pitch onto either a 36-key chromatic or 21-key diatonic layout and emits physical key/modifier combinations. Compilation produces a reachability report; notes that cannot be represented by the selected profile are reported instead of silently mapped to an arbitrary key. Piano sessions own countdown, pre-start window checks, pause/resume/stop, and active-note events.

Implementation: `Piano/Mapping/PianoPitchMapper.cs`, `Piano/Compiler/PianoMidiCompiler.cs`, `Piano/Models/PianoProfile.cs`, `Piano/Services/PianoSessionService.cs`.

## 5. Global planning for guitar frets

For each pitch, the guitar mapper generates string/fret candidates. These represent playable positions, not a clamp to the nearest pitch. For a sequence, planning weighs candidate cost and transitions between positions, reducing frequent page changes and large jumps that a local greedy choice can create. Out-of-range or invalid candidates receive a skip reason. The action plan adds page/slot operations, control timing, and strums.

Implementation: `Guitar/FretConverter.cs`, `Guitar/GamepadActionPlanner.cs`, `Guitar/CircularPageNavigator.cs`, `Guitar/GuitarCompiler.cs`.

## 6. Chord recognition, accompaniment, and strumming

Chord analysis estimates chord candidates from pitch sets clustered in time, then uses evidence and state across onsets for more coherent accompaniment. Inversions, missing notes, melody notes, and timing offsets make single-frame guesses unreliable, so the implementation includes evidence aggregation, event filtering, state transitions, and comparison with game chord ground truth. The arranger turns chord regions and melodic/accompaniment events into game chord, page/slot, pluck, or strum actions. Strum direction and string groups are planned as timestamped action batches.

Implementation: `Guitar/ChordAnalyzer.cs`, `Guitar/ChordAnalyzerV2.cs`, `Guitar/GameChordGroundTruth.cs`, `Guitar/StrumAnalyzer.cs`, `Guitar/ArrangedAccompanimentCompiler.cs`, `Guitar/AccompanimentEventFilter.cs`, `Guitar/AttackClusterPolicy.cs`.

## 7. Polyphony reduction, speed recommendation, and hybrid performance

Game input limits simultaneous notes, usable slots, and retrigger frequency. The guitar pipeline analyzes same-onset polyphony and density, then reduces notes the target cannot execute concurrently. The speed optimizer estimates whether dense passages satisfy minimum press/release spacing and emits a suggested speed. Automatic speed is a compilation recommendation; UI/service selection determines whether a manual choice overrides it.

Full hybrid performance jointly chooses between fret and fixed-chord candidates over the piece. Candidate scores account for pitch error, melody preservation, control-action cost, switching, and timing feasibility. Cross-onset costs reduce local greedy choices. Long-note attacks and high melody notes have protection policies. If a fixed chord cannot express the melody accurately, planning falls back to a fret note; unplayable notes are skipped with a reason.

Implementation: `Guitar/MidiPolyphonyReducer.cs`, `Guitar/MidiSpeedOptimizer.cs`, `Guitar/HybridGuitarCompiler.cs`, `Guitar/ArrangedAccompanimentCompiler.cs`, `Guitar/PlaybackStartAdvisor.cs`.

## 8. Scheduling, retriggers, and safe release

Compiled output is a sequence of timestamped actions. The scheduler aligns absolute deadlines with a monotonic high-resolution clock to avoid cumulative drift. Same-onset chords are batched; retriggering the same physical key requires a release before the next press and respects minimum hold and release spacing. Page/slot/strum controls may use separate physical timing guards so faster playback cannot compress required game-control intervals. Completion, stop, focus loss, exceptions, and disposal all attempt to release held keys/strings.

Internal microsecond timestamps do not mean Windows or the target game responds at microsecond precision. Devices, focus, OS scheduling, and game input polling affect actual timing.

Implementation: `Input/ActionCompiler.cs`, `Input/InputScheduler.cs`, `Input/KeyboardSender.cs`, `Input/KeyboardHoldGuard.cs`, `Guitar/GuitarScheduler.cs`, `Guitar/GuitarControlTimingGuard.cs`, `Input/VirtualGamepadController.cs`.

## 9. Guitar Pro and notation conversion

AlphaTab parses Guitar Pro files and track information; the conversion entry point normalizes notes into the shared source-note model and uses the same instrument compilers. The user selects full score or melody, tracks, transposition, and instrument target. This avoids duplicating game schedulers for every input format.

Implementation: `Services/GtpConversionService.cs`, `Gtp/GtpModels.cs`, and the AlphaTab package reference in `NTE-GUITAR.csproj`.

## 10. Semi-auto overlay and hit judgement

Chart data records target pages/slots, note times, and input lanes. Practice, rhythm, harder rhythm, showcase, and MIDI-following modes choose separately whether player hits are required, positions are changed automatically, and game input is sent. Rhythm judgement evaluates the timing difference between actual input and target onset while maintaining combo, score, and progress. Practice mode also tracks hold completion and wrong-input feedback.

Full-manual mode displays the next target; semi-auto mode sends page/slot actions from a position plan. Simulated MIDI-following uses a MIDI output path rather than pretending that audible output is game input.

Implementation: `SemiAuto/Chart/`, `SemiAuto/Modes/`, `SemiAuto/WinForms/`, `Web/SemiAutoOverlay/`.

## 11. Editing, library, and write consistency

The library isolates songs by UUID and separates metadata, normalized song data, and optional source-note cache. Source-file parsing is cached by content hash. A song remains readable after its original path disappears. Serialization uses safe writes and index repair to reduce damage from partially written files. The piano-roll editor sends explicit note data to the native MIDI writer; the DOM is not used as persistent storage.

Implementation: `Library/SongLibrary.cs`, `Library/SongSerializer.cs`, `Midi/MidiRawCache.cs`, `Midi/MidiWriter.cs`, `Services/LibraryService.cs`.

## 12. Optional audio-to-score

The client, Python worker, and model snapshots are separate. The downloader reads a pinned revision manifest and verifies each file's SHA-256; runtime/model preparation is explicitly user-triggered. The worker uses a request-ID JSON Lines protocol for prepare, transcribe, cancel, and cleanup. If CUDA is unavailable it asks for explicit CPU confirmation first. Audio and job files stay under local AppData.

SheetSage2 and MERT code/weights are licensed CC BY-NC 4.0; commercial use or redistribution must comply with upstream terms. GitHub Releases do not contain model files.

Implementation: `AudioTranscription/README.md`, `AudioTranscription/bootstrap.py`, `AudioTranscription/worker.py`, `AudioTranscription/downloads.json`, `Services/AudioTranscriptionService.cs`.

## Acceptance runner

```powershell
dotnet run --project .\NTE-GUITAR.csproj -c Release -- --test
```

`Testing/TestRunner.cs` composes conversion, piano/guitar compilation, timing, serialization, and recommendation-score cases according to the current build. The exact test set changes with the source; historical test counts are not a stable API.
