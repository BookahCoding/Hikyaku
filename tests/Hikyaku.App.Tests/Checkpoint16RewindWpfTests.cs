using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// Stage 16a â€” Edit / Regenerate through the real MainViewModel: command â†’ confirmation â†’ UI cut â†’ rewind request â†’
/// stream, with refusal restore, cancel, scope switch and busy gating. Scripted transport only; no live gateway.
/// </summary>
[Collection("StaMath")]
public class Checkpoint16RewindWpfTests
{
    private sealed class Script : IGatewayTransport
    {
        public readonly List<ChatRequest> Requests = new();
        public Func<ChatRequest, IEnumerable<ChatEvent>> Respond = _ => [new StreamCompleted()];
        public TaskCompletionSource? Hold;

        public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            foreach (var e in Respond(request)) yield return e;
        }

        public Task<IReadOnlyList<ProfileInfo>> ListProfilesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProfileInfo>>([]);
    }

    private sealed record Fixture(MainViewModel Vm, Script Transport, ConversationVM Convo, List<RewindConfirmation> Asked)
    {
        public MessageVM A => Convo.History[0];
        public MessageVM B => Convo.History[2];
    }

    private static Fixture Make(bool confirm = true)
    {
        var transport = new Script();
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync(new[] { new ProfileKey("demo", "default") }).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => transport, () => "test",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        var asked = new List<RewindConfirmation>();
        vm.ConfirmRewind = c => { asked.Add(c); return confirm; };
        vm.NewChat();
        var convo = vm.SelectedConversation!;
        convo.SessionId = "s1";
        void Add(string role, string text, long? row = null)
        {
            var m = new MessageVM(role, text) { RowId = row };
            convo.History.Add(m);
            vm.Messages.Add(m);
        }
        Add("You", "A", 10); Add("Assistant", "a"); Add("You", "B", 20); Add("Assistant", "b");
        return new Fixture(vm, transport, convo, asked);
    }

    private static void Settle(MainViewModel vm) => Pump(TimeSpan.FromMilliseconds(150));

    [Fact]
    public void Edit_puts_the_message_in_the_composer_and_a_confirmed_send_rewinds_at_its_row()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Respond = _ => [new UserRowBound(99), new TokenDelta("new answer"), new StreamCompleted()];
            f.Vm.EditMessageCommand!.Execute(f.B);
            Assert.True(f.Vm.IsEditing);
            Assert.Equal("B", f.Vm.InputText);
            Assert.Equal(Visibility.Visible, f.Vm.EditBannerVisibility);

            f.Vm.InputText = "B edited";
            f.Vm.SendCommand.Execute(null);
            Settle(f.Vm);

            var req = Assert.Single(f.Transport.Requests);
            Assert.Equal(20, req.RewindBeforeRowId);
            Assert.Equal("s1", req.SessionId);
            Assert.Equal("B edited", req.Messages.Last().Content);
            var ask = Assert.Single(f.Asked);
            Assert.True(ask.IsEdit);
            Assert.Equal(2, ask.MessagesRemoved);                 // B and its answer go
            Assert.Equal(new[] { "A", "a", "B edited", "new answer" }, f.Vm.Messages.Select(m => m.Text));
            Assert.Equal(99, f.Convo.History[2].RowId);           // the new prompt is itself editable
            Assert.False(f.Vm.IsEditing);
            Assert.Equal("", f.Vm.InputText);
            Assert.Equal(f.Convo.History.Count, f.Vm.Messages.Count);
        });
    }

    [Fact]
    public void Regenerate_resends_the_original_text_and_drops_everything_after_it()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Respond = _ => [new TokenDelta("again"), new StreamCompleted()];
            f.A.SentText = "A (full skill body)";
            f.Vm.RegenerateMessageCommand!.Execute(f.A);
            Settle(f.Vm);

            var req = Assert.Single(f.Transport.Requests);
            Assert.Equal(10, req.RewindBeforeRowId);
            Assert.Equal("A (full skill body)", req.Messages.Last().Content);
            Assert.Equal(4, Assert.Single(f.Asked).MessagesRemoved);
            Assert.False(f.Asked[0].IsEdit);
            Assert.Equal(new[] { "A", "again" }, f.Vm.Messages.Select(m => m.Text));
        });
    }

    [Fact]
    public void Declining_the_confirmation_changes_nothing_and_sends_nothing()
    {
        RunSta(() =>
        {
            var f = Make(confirm: false);
            f.Vm.RegenerateMessageCommand!.Execute(f.B);
            Settle(f.Vm);
            Assert.Empty(f.Transport.Requests);
            Assert.Equal(new[] { "A", "a", "B", "b" }, f.Vm.Messages.Select(m => m.Text));

            f.Vm.EditMessageCommand!.Execute(f.B);
            f.Vm.InputText = "B2";
            f.Vm.SendCommand.Execute(null);
            Settle(f.Vm);
            Assert.Empty(f.Transport.Requests);
            Assert.True(f.Vm.IsEditing);                  // still editing; the text is not lost
            Assert.Equal("B2", f.Vm.InputText);
        });
    }

    [Fact]
    public void Without_a_confirmation_handler_the_cut_fails_closed()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.ConfirmRewind = null;
            f.Vm.RegenerateMessageCommand!.Execute(f.B);
            Settle(f.Vm);
            Assert.Empty(f.Transport.Requests);
            Assert.Equal(4, f.Vm.Messages.Count);
        });
    }

    [Theory]
    [InlineData(RewindRefusal.Busy)]
    [InlineData(RewindRefusal.Stale)]
    public void Gateway_refusal_restores_the_exact_original_transcript_and_keeps_the_edit(RewindRefusal why)
    {
        RunSta(() =>
        {
            var f = Make();
            var before = f.Convo.History.ToList();
            f.Transport.Respond = _ => [new RewindRefused(why, "refused")];
            f.Vm.EditMessageCommand!.Execute(f.B);
            f.Vm.InputText = "B edited";
            f.Vm.SendCommand.Execute(null);
            Settle(f.Vm);

            Assert.Equal(before, f.Convo.History);                 // same instances, same order
            Assert.Equal(before, f.Vm.Messages.ToList());
            Assert.Equal(Visibility.Visible, f.Vm.RewindNoticeVisibility);
            Assert.Contains(why == RewindRefusal.Busy ? "finish" : "can't be changed", f.Vm.RewindNotice, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("B edited", f.Vm.InputText);              // the user's edit survives
            Assert.True(f.Vm.IsEditing);
            Assert.Single(f.Transport.Requests);                   // never auto-resent
        });
    }

    [Fact]
    public void Protocol_error_after_a_rewind_leaves_the_new_prompt_visible_not_silently_restored()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Respond = _ => [new ProtocolError("boom")];
            f.Vm.RegenerateMessageCommand!.Execute(f.B);
            Settle(f.Vm);
            // Unlike a refusal, the gateway may have cut: show the truth we know, with the error on the reply.
            Assert.Equal("B", f.Vm.Messages[^2].Text);
            Assert.Contains("boom", f.Vm.Messages[^1].Activity);
        });
    }

    [Fact]
    public void Cancel_edit_restores_the_previous_draft()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.InputText = "half-typed draft";
            f.Vm.EditMessageCommand!.Execute(f.B);
            Assert.Equal("B", f.Vm.InputText);
            f.Vm.CancelEditCommand!.Execute(null);
            Assert.False(f.Vm.IsEditing);
            Assert.Equal("half-typed draft", f.Vm.InputText);
            Assert.Equal(Visibility.Collapsed, f.Vm.EditBannerVisibility);
        });
    }

    [Fact]
    public void Switching_chats_ends_edit_mode()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Vm.EditMessageCommand!.Execute(f.B);
            f.Vm.NewChat();
            Assert.False(f.Vm.IsEditing);
            f.Vm.SelectedConversation = f.Convo;
            Assert.False(f.Vm.IsEditing);
        });
    }

    [Fact]
    public void Messages_without_a_stored_row_cannot_be_edited_or_regenerated_and_assistants_never()
    {
        RunSta(() =>
        {
            var f = Make();
            var local = new MessageVM("You", "typed locally");              // no RowId yet
            Assert.False(f.Vm.EditMessageCommand!.CanExecute(local));
            Assert.False(f.Vm.RegenerateMessageCommand!.CanExecute(local));
            Assert.False(f.Vm.EditMessageCommand.CanExecute(f.Convo.History[1])); // assistant
            Assert.True(f.Vm.EditMessageCommand.CanExecute(f.B));
            Assert.False(f.Vm.EditMessageCommand.CanExecute(null));
        });
    }

    [Fact]
    public void While_a_turn_is_running_edit_and_regenerate_are_disabled()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Hold = new TaskCompletionSource();
            f.Vm.InputText = "next";
            f.Vm.SendCommand.Execute(null);
            Pump(TimeSpan.FromMilliseconds(100));
            Assert.False(f.Vm.EditMessageCommand!.CanExecute(f.B));
            Assert.False(f.Vm.RegenerateMessageCommand!.CanExecute(f.B));
            f.Vm.RegenerateMessageCommand.Execute(f.B);          // even if invoked, nothing happens
            Assert.Single(f.Transport.Requests);
            f.Vm.CancelCommand.Execute(null);
            Settle(f.Vm);
            Assert.True(f.Vm.EditMessageCommand.CanExecute(f.B));
        });
    }

    [Fact]
    public void A_normal_send_binds_the_row_so_that_message_becomes_editable()
    {
        RunSta(() =>
        {
            var f = Make();
            f.Transport.Respond = _ => [new UserRowBound(55), new TokenDelta("ok"), new StreamCompleted()];
            f.Vm.InputText = "fresh";
            f.Vm.SendCommand.Execute(null);
            Settle(f.Vm);
            var fresh = f.Convo.History[^2];
            Assert.Equal("fresh", fresh.Text);
            Assert.Equal(55, fresh.RowId);
            Assert.Null(f.Transport.Requests[0].RewindBeforeRowId);      // plain sends never truncate
            Assert.True(f.Vm.EditMessageCommand!.CanExecute(fresh));
        });
    }

    [Fact]
    public void Late_refusal_after_switching_chats_restores_the_origin_chat_without_touching_the_new_one()
    {
        RunSta(() =>
        {
            var f = Make();
            var before = f.Convo.History.ToList();
            f.Transport.Hold = new TaskCompletionSource();
            f.Transport.Respond = _ => [new RewindRefused(RewindRefusal.Stale, "gone")];
            f.Vm.RegenerateMessageCommand!.Execute(f.B);
            Pump(TimeSpan.FromMilliseconds(100));

            f.Vm.NewChat();
            var other = f.Vm.SelectedConversation!;
            f.Transport.Hold.SetResult();
            Pump(TimeSpan.FromMilliseconds(200));

            Assert.Equal(before, f.Convo.History);                       // origin restored
            Assert.Empty(other.History);
            Assert.Empty(f.Vm.Messages);                                  // new chat's list untouched
        });
    }

    // ---- STA helpers ---------------------------------------------------------------------------

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}

file sealed class MemPrefs : IPreferenceStore
{
    private string? _content;
    public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_content);
    public Task SaveAsync(string content, CancellationToken ct = default) { _content = content; return Task.CompletedTask; }
}

