# Voice chat (chained) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One headset button in the chat box starts a hands-free, Desktop-style chained voice chat with the open chat's agent.

**Architecture:** Pure Core pieces (end of speech, barge-in, echo filter, stop phrases, speakable text, the speak-stream
socket, and an event-driven `VoiceChatSession` state machine behind an `IVoiceChatHost` port) carry all decisions and are
unit-tested with fakes. The App adds one Windows AudioGraph per voice chat (microphone frames in, PCM speech out) and a
`VoiceChatUi` partial of `MainViewModel` that implements the host on top of the existing send, stop, Needs-you and audio
paths.

**Tech Stack:** C# / .NET 9, WPF, WinRT AudioGraph (net9.0-windows10.0.19041.0), System.Net.WebSockets, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-28-voice-chat-design.md`

## Global Constraints

- Contract: hermes-agent `hermes_cli/web_routers/audio.py` (speak-stream, tts-lease, stt-lease, speak, transcribe) and BookahHermes `shared/core/.../voice/{Speech,BargeIn,SpeechStream}.kt` @ `5f00986b2a582e480592fd680131d501d57038ef`.
- Ports keep the Android constants exactly (listed in Tasks 1–2); levels are RMS of the 16-bit samples scaled ×4 and clipped to 1 ("1.0 is loud speech").
- `ExperienceFeature.VoiceChat` is visible in Simple and Advanced. Nothing is answered by voice; a stop phrase is never sent; nothing is resent automatically.
- Commits: explicit paths, message ends with only the Co-Authored-By trailer (copy it from `git log -1`). Gates after each task that touches App code (`gates.ps1` in the scratchpad); protected file hash and `.superpowers` manifest unchanged.
- C# strings with non-ASCII text are written with the Edit/Write tools, never through shell heredocs.

## Review Focus

1. Speakers instead of a headset: the agent's own voice must not keep cutting itself off (echo learning in BargeIn; `TtsEcho` filter) — test: a speaking phase with loud bleed and no person never trips.
2. A transcript that is empty or only noise must not be sent — test: empty transcript counts as a quiet listen.
3. Ending the chat mid-reply (Stop, Esc, chat switch) must stop the voice and release the microphone, but leave the text turn running — test in Task 5 and Task 7.
4. A gateway without speak-stream (older gateway, 404, refused socket) must still speak the finished reply via `/api/audio/speak` — test in Task 5 (fallback) and Task 4 (refused socket returns null).
5. An approval raised mid-reply must silence the voice and never be answered by what is said next — test in Task 5.

---

### Task 1: EndOfSpeech and voice levels

**Files:** Create `src/Hikyaku.Core/VoiceChat/EndOfSpeech.cs`; Test `tests/Hikyaku.Tests/Checkpoint47VoiceChatCoreTests.cs`.

**Interfaces — Produces:**
- `public sealed record VoiceActivity(float SpeechLevel = 0.025f, long SilenceMs = 1_250, long IdleMs = 12_000, long MaxMs = 60_000)`
- `public sealed class EndOfSpeech(VoiceActivity activity) { bool HeardSpeech; float Noise; bool OnFrame(float level, long elapsedMs); }`
- `public static class VoiceLevel { public static float Of(ReadOnlySpan<short> pcm); }` — RMS/32768 × 4, clipped to 1.

- [ ] Step 1: tests — (a) 300 ms of 0.005 then 1 s of 0.2 then 1.3 s of 0.005 in 20 ms frames ends at ≈ speech end + 1250 ms with `HeardSpeech`; (b) a 100 ms 0.2 blip inside the pause does not restart it unless it lasts ≥ 60 ms at speech level; (c) only quiet ends at `IdleMs` with `HeardSpeech == false`; (d) `VoiceLevel.Of` of a full-scale square wave is 1, of silence 0.
- [ ] Step 2: run, expect compile failure.
- [ ] Step 3: port `EndOfSpeech` from `Speech.kt` lines 1–95 verbatim (CALIBRATION_MS 300, SPEECH_OVER_NOISE 2.5, QUIET_OVER_NOISE 1.6, QUIET_FRACTION 0.8, SPEECH_RUN_MS 60, MURMUR_RUN_MS 200; noise follows quiet frames down ×0.3, up ×0.02; speech lifts it ×0.002).
- [ ] Step 4: run, expect pass. Step 5: commit.

### Task 2: BargeIn and the echo filter

**Files:** Create `src/Hikyaku.Core/VoiceChat/BargeIn.cs`, `src/Hikyaku.Core/VoiceChat/TtsEcho.cs`; Test as Task 1.

**Interfaces — Produces:**
- `public sealed record BargeInSettings(bool Enabled = true, double? Multiplier = null, long GraceMs = 500)`
- `public sealed class BargeIn(BargeInSettings settings, long silenceMs = 1_250, float roomFloor = 0) { enum Step { Waiting, Tripped, Hearing, Done }; Step OnFrame(float level, long nowMs, bool playing); bool Tripped; bool Spoke; float RoomFloor; const long PreRollMs = 800; }`
- `public static class TtsEcho { public static bool IsEcho(string transcript, string spoken); internal static double SequenceRatio<T>(IReadOnlyList<T> a, IReadOnlyList<T> b); }`

- [ ] Step 1: tests — (a) quiet room 0.01, then 0.3 for 400 ms while not playing → `Tripped`; (b) playing with bleed 0.15 rising and falling (no person) for 5 s never trips; (c) playing, then a person at 0.45 for 600 ms → trips; (d) after a trip, nobody past 900 ms → `Done` with `Spoke == false`; (e) `SequenceRatio("abcd","bcde")` = 0.75 (difflib); `IsEcho("the weather is sunny today", "The weather is sunny today in Athens.")` true; `IsEcho("yes", "...")` false; a 30-char window of a long reply is an echo.
- [ ] Step 2: run, expect failure. Step 3: port `BargeIn.kt` lines 50–310 and `isTtsEcho`/`sequenceRatio` verbatim with the companion constants (CALIBRATION_MS 400, SUSTAINED_MS 300, SUSTAINED_OVER_ECHO_MS 500, TALKING_MS 100, SUSTAINED_MAJORITY 0.8, MIN_TRIGGER_LEVEL 0.025, FLOOR_MULTIPLIER 3.5, PLAYBACK_MIN_LEVEL 0.05, BLEED_MULTIPLIER 1.8, MIN_BLEED_MARGIN 1.2, BLEED_PERCENTILE 0.8, TRIGGER_CEILING_LEVEL 0.25, ECHO_BAR_CEILING_LEVEL 0.4, BLEED_LEARN_HEADROOM 1.25, PLAYBACK_GAP_FOR_GRACE_MS 1000, FLOOR_SAMPLES 150, QUIET_FRACTION 0.8, QUIET_OVER_FLOOR 1.6, UTTERANCE_MAX_MS 30000, PRE_ROLL_MS 800, HOLD_SETTLE_MS 300, SPOKE_FRAMES 8, NOBODY_AFTER_MS 900, DEFAULT_MULTIPLIER 3.0; ECHO_SIMILARITY 0.6, MIN_ECHO_FRAGMENT 10).
- [ ] Step 4: pass. Step 5: commit.

### Task 3: Stop phrases, speakable text, voice settings

**Files:** Create `src/Hikyaku.Core/VoiceChat/VoiceText.cs`, `src/Hikyaku.Core/VoiceChat/VoiceChatSettings.cs`; Test as Task 1.

**Interfaces — Produces:**
- `public static class VoiceStopPhrases { bool IsStop(string transcript, IReadOnlyList<string>? profilePhrases, string? agentName); }` — default list = Android `STOP_PHRASES`; a profile list (non-null) replaces it; address prefixes "hey hermes", "hermes", "okay", "ok", "hey", plus "hey {agent}", "{agent}".
- `public static class SpeakableText { string SoFar(string reply, bool complete); string NewPart(string sent, string now); }` — port `speakableSoFar` (fences held back, `MEDIA:` dropped) and the "words from where the two part" rule.
- `public sealed record VoiceChatSettings(VoiceActivity Activity, BargeInSettings BargeIn, IReadOnlyList<string>? StopPhrases) { static VoiceChatSettings Defaults; static VoiceChatSettings From(ConfigValues? config); }` — reads `voice.silence_duration` (seconds), `voice.barge_in`, `voice.barge_in_threshold_multiplier`, `voice.barge_in_grace_seconds`, `voice.stop_phrases` (array; `[]` disables stop phrases).

- [ ] Step 1: tests — "Stop." / "hey Shisha, stop" (agent "Shisha") stop; "stop the container" does not; profile `["goodbye hermes"]` replaces "stop"; `[]` means nothing stops. `SoFar("Hi ```code` ...", false)` drops the open fence onward; `MEDIA:/x.png` dropped; `NewPart("Hello", "Hello there")` = " there". `From` with `{"voice":{"silence_duration":2,"barge_in":false,"stop_phrases":["bye"]}}` → SilenceMs 2000, Enabled false, ["bye"].
- [ ] Steps 2–5: fail, implement, pass, commit.

### Task 4: Speak-stream socket client and audio leases

**Files:** Create `src/Hikyaku.Core/VoiceChat/SpeechStreamClient.cs`; Modify `src/Hikyaku.Core/AudioApi.cs` (add `SetTtsLeaseAsync(string lease, bool active, string? profile, ct)` and `SetSttLeaseAsync(...)`, both swallowing errors); Test `tests/Hikyaku.Tests/Checkpoint47SpeechStreamTests.cs`.

**Interfaces — Produces:**
- `public abstract record SpeechStreamEvent; sealed record SpeechStart(int SampleRate, int Channels); sealed record SpeechAudio(byte[] Pcm); sealed record SpeechEnd; sealed record SpeechFallback;`
- `public interface ISpeechStream : IAsyncDisposable { IAsyncEnumerable<SpeechStreamEvent> Events(CancellationToken ct); Task AppendAsync(string text); Task FinishAsync(); Task StopAsync(); }`
- `public static class SpeechStreamClient { static Task<ISpeechStream?> OpenAsync(DashboardAuth auth, string? profile, CancellationToken ct); internal static SpeechStreamEvent? ParseText(string json); }` — mints `MintWsTicketAsync`, connects `ws(s)://…/api/audio/speak-stream?profile=` with the same subprotocols and Cloudflare Access headers as `HermesDashboardTransport`; returns null when the socket is refused.

- [ ] Step 1: tests — `ParseText` for start (rate default 24000), end, fallback, junk → null; against the existing in-test WebSocket fixture (`Checkpoint16RewindTests.Start` pattern): append/finish frames sent as `{"text":…}`/`{"done":true}`, binary frames surface as `SpeechAudio`, `end` closes the event stream; a refused upgrade returns null.
- [ ] Steps 2–5: fail, implement, pass, commit.

### Task 5: VoiceChatSession state machine

**Files:** Create `src/Hikyaku.Core/VoiceChat/VoiceChatSession.cs`; Test `tests/Hikyaku.Tests/Checkpoint47VoiceChatSessionTests.cs`.

**Interfaces — Produces:**
- `public enum VoiceChatPhase { Off, Listening, Transcribing, Thinking, Speaking, WaitingForYou, Ended }`
- `public interface IVoiceChatHost { void Transcribe(byte[] wav); void Send(string text); void CancelTurn(); void SpeakStart(); void SpeakAppend(string text); void SpeakFinish(); void SpeakStop(); void SayFixed(string line); }`
- `public sealed class VoiceChatSession(IVoiceChatHost host, VoiceChatSettings settings, string? agentName)` with `Phase`, `Problem`, `Level`, `event Action? Changed`, `Start()`, `End(string? why)`, and inputs (thread-safe, called from any thread): `OnFrame(ReadOnlySpan<short> pcm16k, long atMs)`, `OnTranscribed(string)`, `OnTranscribeFailed(string)`, `OnReplyText(string full)`, `OnTurnEnded(bool failed, string? why)`, `OnNeedsYou(bool waiting)`, `OnSpeechPlaying(bool playing)`, `OnSpeechDone()`.
- Rules: Listening uses EndOfSpeech; no speech → quiet count, 3 → End("Nobody spoke, so the voice chat ended."); a transcript that is empty, an echo of the last spoken reply, or (stop phrase → End, not sent); otherwise `Send` and Thinking with a fresh BargeIn (pre-roll ring of 800 ms). Reply text → `SpeakStart` once, `SpeakAppend(NewPart)`; turn ended → append the rest with `complete: true`, `SpeakFinish`; `OnSpeechDone` (or no speech at all) → Listening. Barge-in trip while Speaking → `SpeakStop`; the utterance (pre-roll + until Done) is transcribed; if real (not empty/echo) and the turn still runs → `CancelTurn` then `Send`. Needs-you → `SpeakStop`, `SayFixed("Hermes needs you. Please check the screen.")`, WaitingForYou, frames ignored; cleared → Thinking. Failures (transcription, failed turn) count; 2 in a row → End with the reason. `End` → `SpeakStop`, Ended (the text turn is left running).

- [ ] Step 1: tests with a recording fake host and a frame generator: full loop; three quiet listens end; stop phrase ends and is not sent; empty transcript not sent; echo not sent; fallback path (`SpeakFinish` then `OnSpeechDone`) returns to Listening; barge-in while speaking stops speech and sends the cut-in; barge-in while thinking with nobody (`Spoke == false`) cancels nothing; needs-you silences, ignores speech, resumes; two transcription failures end with the reason; `End` mid-reply calls `SpeakStop` and never `CancelTurn`.
- [ ] Steps 2–5: fail, implement, pass, commit.

### Task 6: AudioGraph voice I/O (App)

**Files:** Create `src/Hikyaku.App/AudioGraphVoiceIo.cs`.

**Interfaces — Produces:** `public sealed class AudioGraphVoiceIo : IAsyncDisposable { static Task<(AudioGraphVoiceIo? Io, string? Problem)> OpenAsync(); event Action<short[], long>? MicFrame; void BeginSpeech(int sampleRate, int channels); void QueuePcm(byte[] pcm16le); void EndSpeech(); void StopSpeech(); bool IsPlaying; event Action<bool>? PlayingChanged; event Action? SpeechDrained; }`
- One graph (`AudioRenderCategory.Speech`, quantum default): device input node `MediaCategory.Speech` → frame output node (mono float, read every `QuantumStarted`, converted to 16 kHz 16-bit by linear resampling, emitted in 20 ms frames with a monotonic ms clock); frame input node for speech PCM at the stream's rate (resampled to the graph rate), a queue drained per quantum, `SpeechDrained` raised once the queue empties after `EndSpeech`.
- [ ] Step 1: build; Step 2: throwaway probe in the scratchpad (not committed) plays a 1 s PCM tone through `QueuePcm` and counts `MicFrame` callbacks for 2 s; expect drained once and ≈ 100 frames. Step 3: commit.

### Task 7: VoiceChatUi (button, bar, host)

**Files:** Create `src/Hikyaku.App/VoiceChatUi.cs`; Modify `src/Hikyaku.Core/ExperienceFeatures.cs` (`VoiceChat` in the Simple set), `src/Hikyaku.App/MainWindow.xaml` (headset button beside the mic; the bar above the chat box), `src/Hikyaku.App/MainWindow.xaml.cs` (Esc ends it), `tests/Hikyaku.Tests/Checkpoint36ExperienceTests.cs` if the Simple list is asserted; Test `tests/Hikyaku.App.Tests/Checkpoint47VoiceChatWpfTests.cs`.

**Interfaces — Consumes:** Tasks 3–6. **Produces:** `ICommand ToggleVoiceChatCommand`, `VoiceChatBarVisibility`, `VoiceChatStatus` (Simple: "Listening…", "Sending…", "Thinking…", "Speaking…", "Waiting for you"; Advanced the same plus the problem line), `VoiceChatLevel`, `VoiceChatButtonVisibility`, `EndVoiceChat(string? why)`.
- Host: `Transcribe` → `AudioApi.TranscribeAsync(wav, "audio/wav", profile)`; `Send` → the normal send of the open chat; `CancelTurn` → `CancelCommand`; speech → `SpeechStreamClient` + `AudioGraphVoiceIo`, falling back to `AudioApi.SpeakAsync` + the 0.5.1 player on `SpeechFallback`/null socket; reply text and turn end from the open chat's last assistant `MessageVM` (`Text`, `IsLive`, `Failure`); Needs-you from `NeedsYouItems` count; leases on start and end; settings from `ConfigApi.GetValuesAsync` (defaults on failure); ends on chat switch and window close.
- [ ] Step 1: WPF tests with a fake session host seam (`VoiceChatUi` takes a factory): the button shows in Simple and Advanced; toggling shows the bar with "Listening…"; switching chat ends it; Esc ends it; in Simple the status never shows tool names or the raw problem text.
- [ ] Steps 2–5: fail, implement, gates (incl. UI acceptance screenshot of the bar `04m-voice-chat.png`), commit.

### Task 8: Release notes, version 0.6.0, mutation checks

**Files:** `Directory.Build.props` (0.6.0), `CHANGELOG.md`, `docs/release-notes/v0.6.0.txt` (plain text, includes the 0.5.2 Settings items), `docs/HANDOFF.md`.
- [ ] Mutation checks (copy to scratch, restore, `cmp`): stop phrase sent anyway; echo filter off; needs-you not pausing; End calling CancelTurn — each must fail a test.
- [ ] Full gates; commit; ask the maintainer before push and release.
