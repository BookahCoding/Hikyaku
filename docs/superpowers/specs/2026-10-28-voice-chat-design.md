# Voice chat (chained, Desktop-style): design

Date: 2026-10-28. Release: 0.6.0 (with the Settings redesign already committed as 0.5.2).
Status: approved in conversation by the maintainer (mode, audience, approvals, how it works, technical plan); this spec
records it for review before the implementation plan.

## Goal

One button in the chat box starts a hands-free spoken conversation with the agent of the open chat, as close to a live call
as the gateway's chained voice allows: Hermes Desktop's default "chained" voice chat (listen, send when you pause, speak the
reply while it is still being written, listen again, talk over it to cut in). GPT-Live (OpenAI's full-duplex voice) is out
of scope.

## Decisions already made (maintainer, 2026-10-28)

- Mode: chained, like Hermes Desktop. No GPT-Live.
- Audience: both views. Simple shows only the button and plain status words; Advanced also shows the existing tool steps.
- Approvals and questions during a voice chat: the chat pauses and says "Hermes needs you, check the screen"; the answer is
  given on screen as now. Nothing is approved by voice. Secrets are never spoken.

## What the person sees and hears

- A headset button next to the microphone in the chat box. Clicking it shows a slim bar above the box: a level meter, a
  status word and a Stop button.
- Status words: "Listening…", "Sending…" (transcribing), "Thinking…", "Speaking…", "Waiting for you" (approval or question
  on screen). Problems: one plain line in the bar (see Error handling for when the chat ends).
- Listening: the turn ends after about 1.25 s of quiet once speech was heard (Desktop/Android timing; `voice.silence_duration`
  from the profile's config when it is set). What was said is sent as an ordinary message of the open chat, shown as the
  person's bubble.
- Speaking: the reply is voiced while it is written, sentence by sentence, in the profile's own voice. Code blocks and
  attached files (`MEDIA:` lines) are never read out.
- Talking over it: speaking while the reply is voiced stops the voice; speaking while Hermes is still thinking stops that
  turn. Either way what was said becomes the next message.
- Ending: a whole utterance that is only a stop phrase ("stop", "goodbye", "never mind", "cancel", … optionally addressed:
  "hey Hermes, stop"), or the profile's own `voice.stop_phrases` when set; Stop or Esc; three listens in a row that hear
  nobody; switching to another chat or closing the window.

## Wire contract (hermes-agent `hermes_cli/web_routers/audio.py`, BookahHermes `voice/*.kt` @ 5f00986b)

- Transcription: `POST /api/audio/transcribe?profile=` with a WAV data URL (exists in Hikyaku's `AudioApi`).
- Speech while writing: WebSocket `/api/audio/speak-stream?profile=`, opened with a fresh unnamed ticket from
  `POST /api/auth/ws-ticket` in the subprotocol list (an unnamed ticket opens any socket except the host terminal).
  Client sends `{"text": "..."}` as the reply grows, `{"done": true}` at its end, `{"stop": true}` to cut it. Server sends
  `{"type":"start","sample_rate":N,"channels":1}`, binary 16-bit little-endian PCM, then `{"type":"end"}`; or
  `{"type":"fallback"}` when it produced no audio, in which case the finished reply is voiced with `POST /api/audio/speak`
  (the 0.5.1 read-aloud path). A gateway without the socket (refused or 404) gets the same fallback.
- Warm-up: `POST /api/audio/tts-lease` (and `stt-lease` where present) with `{"lease": "<id>", "active": true}` at start and
  `false` at the end; best effort, failures ignored.
- Voice settings read from the profile's config (`GET api/config?profile=`): `voice.stop_phrases`, `voice.silence_duration`,
  `voice.barge_in`, `voice.barge_in_threshold_multiplier`, `voice.barge_in_grace_seconds`. Missing values use the defaults.
- A spoken turn is an ordinary `prompt.submit` (chained voice adds no marker; only GPT-Live does).

## Components

Core (no WPF, unit-tested with fakes):
- `EndOfSpeech`: frame-by-frame "has the speaker finished", with a background-noise floor (port of Android `Speech.kt`).
- `BargeIn`: frame-by-frame "is someone talking over the agent", phase-aware (thinking vs speaking), with the echo of the
  voice learnt and kept below the bar, a grace period after the voice starts, and the profile's sensitivity (port of
  Android `BargeIn.kt`).
- `VoiceStopPhrases`: the default list plus the profile's own, whole-utterance match.
- `SpeakableText`: the part of a reply written so far that can be voiced (code blocks held back, `MEDIA:` dropped; port of
  `speakableSoFar`), and the new part since the last send.
- `SpeechStreamClient`: the speak-stream socket (frames in and out, start/end/fallback).
- `VoiceChatSession`: the loop as a state machine (Off, Listening, Transcribing, Thinking, Speaking, WaitingForYou, Ended)
  driving the parts below through interfaces: microphone frames, transcriber, chat (send, reply text growing, turn ended,
  needs-you raised), speech output. Counts quiet listens, filters "Hermes hearing itself" (a transcript that only repeats
  what was just spoken is treated as silence), and reports one plain error line.

App (WPF and Windows audio):
- `AudioGraphVoiceIo`: one Windows AudioGraph per voice chat. Microphone through a device input node (speech/communications
  category, so Windows' own voice processing and echo cancellation apply where the PC offers them) into a frame output node
  (16 kHz mono 16-bit frames for the Core parts); speech through a frame input node fed with the socket's PCM, or the
  0.5.1 player for the fallback. AudioGraph is the engine that played every format on the maintainer's PC.
- `VoiceChatUi` (MainViewModel partial): the button, the bar, status words per view, Stop/Esc, ending on chat switch; wires
  the session to the existing send path and Needs-you.
- Experience: a new `ExperienceFeature.VoiceChat`, visible in Simple and Advanced (everyday chatting).

## Error handling

- No speech-to-text or voice on the gateway (404/unsupported), microphone refused or missing: one line in the bar, the chat
  ends, nothing is sent.
- A transcription or socket failure in one turn: said in the bar; two failed turns in a row end the chat (Android counts only
  failed turns, not quiet ones).
- The connection drops: the chat ends; the existing failed-reply card covers the message.
- Approvals/questions: the loop pauses (no listening, voice stops after the current sentence) and resumes when the request
  is answered or withdrawn.
- Nothing is resent automatically, as everywhere in Hikyaku.

## Testing

- Core: EndOfSpeech, BargeIn, stop phrases, speakable text and the socket client against recorded frame sequences and a fake
  socket; the session loop end to end with fakes (listen → send → speak → listen; barge-in in both phases; three quiet
  listens end it; a stop phrase ends it and is not sent; a needs-you pause and resume; fallback to whole-reply speech; errors).
- WPF: the button and bar in Simple and Advanced, status words, Stop/Esc and chat switch ending it.
- Mutation checks on the rules that protect the person: a stop phrase is never sent, approvals are never answered by voice,
  a misheard echo is not sent.
- Not provable here: real microphone, speakers and echo cancellation; the maintainer's live check after the 0.6.0 update.

## Out of scope

GPT-Live; wake word; client-direct provider calls (`/api/audio/voice-config`); streaming transcription
(`transcribe-stream`); answering approvals by voice; a voice-only window.
