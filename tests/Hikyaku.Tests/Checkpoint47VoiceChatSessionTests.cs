using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 47 — the voice chat loop, end to end with a recording host and synthetic microphone frames: listen, send when the
/// speaker pauses, speak the reply while it is written, listen again; stop phrases, quiet listens, the agent hearing itself,
/// talking over it, a Needs-you pause, failures, and ending without touching the text turn.
/// </summary>
public class Checkpoint47VoiceChatSessionTests
{
    private sealed class Host : IVoiceChatHost
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Sent = new();
        public int Transcribes;
        public void Transcribe(byte[] wav) { Transcribes++; Calls.Add("transcribe"); }
        public void Send(string text) { Sent.Add(text); Calls.Add("send:" + text); }
        public void CancelTurn() => Calls.Add("cancel");
        public void SpeakStart() => Calls.Add("speak-start");
        public void SpeakAppend(string text) => Calls.Add("append:" + text);
        public void SpeakFinish() => Calls.Add("finish");
        public void SpeakStop() => Calls.Add("stop");
        public void SayFixed(string line) => Calls.Add("say:" + line);
    }

    private sealed class Mic(VoiceChatSession session)
    {
        public long T;
        /// <summary>20 ms frames at 16 kHz whose level (as VoiceLevel measures it) is <paramref name="level"/>.</summary>
        public void Frames(float level, long ms)
        {
            var amplitude = (short)Math.Round(level / 4f * 32768f);
            var frame = Enumerable.Repeat(amplitude, 320).ToArray();
            for (var end = T + ms; T < end;)
            {
                T += 20;
                session.OnFrame(frame, T);
            }
        }

        public void Say() { Frames(0.005f, 300); Frames(0.2f, 1000); Frames(0.005f, 1400); }
    }

    private static (VoiceChatSession Session, Host Host, Mic Mic) Start(VoiceChatSettings? settings = null)
    {
        var host = new Host();
        var session = new VoiceChatSession(host, settings ?? VoiceChatSettings.Defaults, "Shisha");
        session.Start();
        return (session, host, new Mic(session));
    }

    [Fact]
    public void Listen_send_speak_while_written_then_listen_again()
    {
        var (s, host, mic) = Start();
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
        mic.Say();
        Assert.Equal(VoiceChatPhase.Transcribing, s.Phase);
        Assert.Equal(1, host.Transcribes);

        s.OnTranscribed("  What is the weather?  ");
        Assert.Equal(["What is the weather?"], host.Sent);
        Assert.Equal(VoiceChatPhase.Thinking, s.Phase);

        s.OnReplyText("It is sunny. ");
        s.OnSpeechPlaying(true);
        Assert.Equal(VoiceChatPhase.Speaking, s.Phase);
        s.OnReplyText("It is sunny. Warm too.");
        s.OnTurnEnded(failed: false, why: null);
        Assert.Equal(["transcribe", "send:What is the weather?", "speak-start", "append:It is sunny. ", "append:Warm too.", "finish"], host.Calls);
        s.OnSpeechDone();
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
    }

    [Fact]
    public void Three_listens_that_hear_nobody_end_the_chat_and_nothing_is_sent()
    {
        var (s, host, mic) = Start();
        mic.Frames(0.005f, 12_100);
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
        mic.Frames(0.005f, 12_100);
        mic.Frames(0.005f, 12_100);
        Assert.Equal(VoiceChatPhase.Ended, s.Phase);
        Assert.Contains("Nobody", s.Problem);
        Assert.Empty(host.Sent);
        Assert.Equal(0, host.Transcribes);
    }

    [Fact]
    public void A_stop_phrase_ends_the_chat_and_is_never_sent()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Hey Shisha, stop.");
        Assert.Equal(VoiceChatPhase.Ended, s.Phase);
        Assert.Null(s.Problem);
        Assert.Empty(host.Sent);
    }

    [Fact]
    public void An_empty_transcript_is_not_sent_and_counts_as_a_quiet_listen()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("   ");
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
        Assert.Empty(host.Sent);
        mic.Frames(0.005f, 12_100);
        mic.Say();
        s.OnTranscribed("");
        Assert.Equal(VoiceChatPhase.Ended, s.Phase);                      // three in a row
    }

    [Fact]
    public void The_agent_hearing_its_own_reply_is_not_sent()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Weather?");
        s.OnReplyText("It is sunny in Athens today with a light breeze.");
        s.OnTurnEnded(false, null);
        s.OnSpeechDone();
        mic.Say();
        s.OnTranscribed("it is sunny in athens today");
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
        Assert.Equal(["Weather?"], host.Sent);
    }

    [Fact]
    public void A_reply_with_nothing_speakable_goes_straight_back_to_listening()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Show the script");
        s.OnReplyText("```sh\necho hi\n```");
        s.OnTurnEnded(false, null);
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
        Assert.DoesNotContain("speak-start", host.Calls);
    }

    [Fact]
    public void Talking_over_the_voice_stops_it_and_sends_what_was_said_instead()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Tell me a long story");
        mic.Frames(0.01f, 600);                                            // the room while it thinks
        s.OnReplyText("Once upon a time there was a fox. ");
        s.OnSpeechPlaying(true);
        mic.Frames(0.03f, 1000);                                           // the voice, faintly back through the microphone
        mic.Frames(0.45f, 800);                                            // someone talks over it
        Assert.Contains("stop", host.Calls);
        mic.Frames(0.45f, 600);
        mic.Frames(0.01f, 1500);
        Assert.Equal(2, host.Transcribes);
        s.OnTranscribed("Actually, what about tomorrow?");
        var cancel = host.Calls.IndexOf("cancel");
        Assert.True(cancel >= 0 && cancel < host.Calls.IndexOf("send:Actually, what about tomorrow?"));
        Assert.Equal(VoiceChatPhase.Thinking, s.Phase);
    }

    [Fact]
    public void A_knock_while_it_thinks_cancels_nothing()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Think about it");
        mic.Frames(0.01f, 600);
        mic.Frames(0.5f, 400);                                             // trips, but nobody goes on talking
        mic.Frames(0.01f, 1200);
        Assert.DoesNotContain("cancel", host.Calls);
        Assert.Equal(1, host.Transcribes);
        Assert.Equal(VoiceChatPhase.Thinking, s.Phase);
    }

    [Fact]
    public void A_needs_you_request_silences_the_voice_ignores_speech_and_resumes()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Delete the old logs");
        s.OnReplyText("I will check first. ");
        s.OnSpeechPlaying(true);
        s.OnNeedsYou(true);
        Assert.Equal(VoiceChatPhase.WaitingForYou, s.Phase);
        Assert.Contains("stop", host.Calls);
        Assert.Contains(host.Calls, c => c.StartsWith("say:") && c.Contains("check the screen"));
        mic.Say();                                                         // "yes" is never taken as an answer
        mic.Frames(0.45f, 1000);
        Assert.Equal(1, host.Transcribes);
        Assert.Single(host.Sent);
        s.OnNeedsYou(false);
        Assert.Equal(VoiceChatPhase.Thinking, s.Phase);
        s.OnTurnEnded(false, null);
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
    }

    [Fact]
    public void A_microphone_that_sends_only_digital_silence_ends_the_chat_at_once_with_where_to_look()
    {
        // Live finding: on a PC where Windows blocks desktop apps from the microphone, every sample is exactly zero.
        var (s, host, mic) = Start();
        mic.Frames(0f, 1600);
        Assert.Equal(VoiceChatPhase.Ended, s.Phase);
        Assert.Contains("Microphone", s.Problem);
        Assert.Equal(0, host.Transcribes);

        var (quiet, _, quietMic) = Start();
        quietMic.Frames(0.002f, 1600);                                    // a real quiet room is never exactly zero
        Assert.Equal(VoiceChatPhase.Listening, quiet.Phase);
    }

    [Fact]
    public void Two_failures_in_a_row_end_the_chat_with_the_reason()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribeFailed("The gateway could not transcribe.");
        Assert.Equal(VoiceChatPhase.Listening, s.Phase);
        mic.Say();
        s.OnTranscribeFailed("The gateway could not transcribe.");
        Assert.Equal(VoiceChatPhase.Ended, s.Phase);
        Assert.Equal("The gateway could not transcribe.", s.Problem);
    }

    [Fact]
    public void Ending_mid_reply_stops_the_voice_but_leaves_the_text_turn_running()
    {
        var (s, host, mic) = Start();
        mic.Say();
        s.OnTranscribed("Write a poem");
        s.OnReplyText("Roses are red. ");
        s.End(null);
        Assert.Equal(VoiceChatPhase.Ended, s.Phase);
        Assert.Equal("stop", host.Calls.Last());
        Assert.DoesNotContain("cancel", host.Calls);
        s.OnReplyText("Roses are red. Violets are blue.");                // late events change nothing
        Assert.Equal("stop", host.Calls.Last());
    }
}
