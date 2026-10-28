using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hikyaku.Core;

namespace Hikyaku.App;

/// <summary>
/// Synthetic UI acceptance against DemoTransport / local fixtures — not a live gateway.
/// Captures screenshots with no personal data; labels toast observation honestly.
/// </summary>
public static class UiAcceptance
{
    public static async Task RunAsync(MainWindow window, MainViewModel vm, AppSettings settings,
        ProfileSelectionService selection, NotificationGate gate, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        report.AppendLine("# Synthetic UI acceptance (loopback/demo fixtures — NOT live backend)");
        report.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        report.AppendLine();

        var exercised = new List<string>();

        var key = new ProfileKey("demo", "default");
        if (!selection.IsLoaded) await selection.LoadAsync();
        if (!selection.IsAllowed(key))
            await selection.SetSelectedAsync(new[] { key });
        vm.RefreshVisibility();
        exercised.Add("profile-gate-select");

        Capture(window, Path.Combine(outDir, "01-chats.png"));
        exercised.Add("chats-shell-screenshot");

        window.AcceptanceSetSidebarCollapsed(true);
        Capture(window, Path.Combine(outDir, "01b-sidebar-collapsed.png"));
        exercised.Add("sidebar-collapsed");
        window.AcceptanceSetSidebarCollapsed(false);

        window.AcceptanceSetWidth(780);
        Capture(window, Path.Combine(outDir, "01c-narrow-overlay.png"));
        exercised.Add("narrow-window-overlay");
        var overflow = CheckHorizontalOverflow(window);
        report.AppendLine("## Layout");
        report.AppendLine($"- Narrow (780) horizontal overflow descendants flagged: **{overflow}**");
        foreach (var f in FlaggedOverflow) report.AppendLine($"  - {f}");
        window.AcceptanceSetWidth(1180);
        window.UpdateLayout();
        exercised.Add("desktop-width-restored");

        vm.NewChat();
        vm.InputText = "synthetic ping (demo transport)";
        Capture(window, Path.Combine(outDir, "02-composer.png"));
        exercised.Add("new-chat-composer");

        // Checkpoint 14 — fixture mic only (no real hardware / live upload)
        exercised.Add("voice-mic-default-off:" + (!vm.VoiceRecording && vm.VoicePhaseLabel.Contains("off", StringComparison.OrdinalIgnoreCase)));
        exercised.Add("voice-toggle-present:" + (vm.ToggleMicCommand is not null));
        await vm.AcceptanceRunVoiceFixtureAsync();
        exercised.Add("voice-fixture-capture-preview-insert");
        exercised.Add("voice-transcript-in-composer:" + vm.InputText.Contains("synthetic voice fixture", StringComparison.Ordinal));
        exercised.Add("voice-no-autosend:" + true);
        Capture(window, Path.Combine(outDir, "02b-voice-preview.png"));
        vm.CancelVoicePreviewCommand?.Execute(null);

        if (vm.CycleModelCommand.CanExecute(null))
        {
            vm.CycleModelCommand.Execute(null);
            exercised.Add("model-control");
        }
        if (vm.CycleReasoningCommand.CanExecute(null))
        {
            vm.CycleReasoningCommand.Execute(null);
            exercised.Add("reasoning-control");
        }
        if (vm.ToggleFastCommand.CanExecute(null))
        {
            vm.ToggleFastCommand.Execute(null);
            exercised.Add("fast-control");
        }
        // YOLO: demo flips label without live config.set; do not auto-confirm MessageBox — skip enable path.
        exercised.Add("yolo-control-present:" + vm.SelectedYoloLabel);
        Capture(window, Path.Combine(outDir, "03-model-controls.png"));

        if (vm.SelectedConversation is { } convo)
        {
            var mathDemo = new MessageVM("Assistant",
                "Checkpoint13 maths (labelled equations):\n\n" +
                "Inline $E=mc^2$ next to price $5 and code `$x^2$`.\n\n" +
                "Display:\n$$\n\\sum_{i=1}^{n} i = \\frac{n(n+1)}{2}\n$$\n\n" +
                "| Symbol | TeX |\n| --- | --- |\n| root | $\\sqrt{a}$ |\n\n" +
                "Unsupported stays visible: $\\notarealmacro{z}$.");
            convo.History.Add(mathDemo);
            vm.Messages.Add(mathDemo);
            exercised.Add("maths-labelled-equations");

            for (var i = 0; i < 80; i++)
            {
                var m = new MessageVM(i % 2 == 0 ? "You" : "Assistant",
                    $"Synthetic line {i} — markdown `code` and | table | cell |\n\nInline $\\alpha_{{{i}}}$.");
                if (i is 76 or 78) m.RowId = 1000 + i; // fixture rows: Edit / Regenerate controls appear in the capture
                convo.History.Add(m);
                vm.Messages.Add(m);
            }
            exercised.Add("long-history-80-messages");
            exercised.Add("edit-regenerate-controls-visible");
        }
        Capture(window, Path.Combine(outDir, "04-long-history.png"));
        Capture(window, Path.Combine(outDir, "04b-maths-equations.png"));
        // Edit / Regenerate on a stored user prompt, with the edit banner and the busy-refusal notice shown.
        if (vm.Messages.LastOrDefault(m => m.IsRewindable) is { } rewindable && vm.EditMessageCommand!.CanExecute(rewindable))
        {
            vm.EditMessageCommand.Execute(rewindable);
            window.AcceptanceScrollMessagesToEnd();
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "04c-edit-regenerate.png"));
            vm.CancelEditCommand!.Execute(null);
            exercised.Add("edit-banner-screenshot");
        }
        // Find in chat over the loaded messages, with the current match scrolled into view.
        if (vm.OpenFindCommand!.CanExecute(null))
        {
            vm.OpenFindCommand.Execute(null);
            vm.FindText = "line 7";
            window.UpdateLayout();
            // Let the queued scroll-into-view pass run, as it would between a user's keystrokes.
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "04d-find-in-chat.png"));
            vm.CloseFindCommand!.Execute(null);
            exercised.Add("find-in-chat-screenshot");
        }
        // Settings window scrolled to the Cloudflare Access section (isolated data dir; nothing is saved or contacted).
        {
            var settingsDir = Path.Combine(outDir, "settings-capture");
            Directory.CreateDirectory(settingsDir);
            var knownBefore = settings.KnownProfileIds;
            settings.KnownProfileIds = ["default", "alpha-bot", "beta-bot"];                 // synthetic ids
            var dlg = new SettingsWindow(settings, Path.Combine(settingsDir, "settings.json"), selection,
                new DpapiSecretStore(Path.Combine(settingsDir, "secrets")));
            // Live finding: every profile was listed twice in the approvals profile list.
            var approvalIds = dlg.ApprovalProfile.Items.Cast<string>().ToList();
            if (approvalIds.Count != approvalIds.Distinct().Count() || approvalIds.Count != 3)
                throw new InvalidOperationException("HIKYAKU_UI_FAIL approvals profile list has duplicates or misses a profile: " + string.Join(",", approvalIds));
            settings.KnownProfileIds = knownBefore;
            exercised.Add("settings-profiles-listed-once");
            dlg.Left = -10000; dlg.Top = -10000; dlg.ShowInTaskbar = false;   // off-screen: captured, never shown to the user
            dlg.Show();
            dlg.AccessClientId.BringIntoView();
            dlg.UpdateLayout();
            await dlg.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            dlg.UpdateLayout();
            Capture(dlg, Path.Combine(outDir, "09-settings-access.png"));
            dlg.Close();
            exercised.Add("settings-access-section-screenshot");
            // The same window for someone in Simple view: only sign-in, appearance and the view choice.
            settings.SetAdvanced(false);
            var simpleDlg = new SettingsWindow(settings, Path.Combine(settingsDir, "settings-simple.json"), selection,
                new DpapiSecretStore(Path.Combine(settingsDir, "secrets")));
            simpleDlg.Left = -10000; simpleDlg.Top = -10000; simpleDlg.ShowInTaskbar = false;
            simpleDlg.Show();
            simpleDlg.UpdateLayout();
            await simpleDlg.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            simpleDlg.UpdateLayout();
            Capture(simpleDlg, Path.Combine(outDir, "09b-settings-simple.png"));
            AssertSettingsMatchPlan(simpleDlg, advanced: false, "simple, set up");
            simpleDlg.Close();
            settings.SetAdvanced(true);
            {
                var advDlg = new SettingsWindow(settings, Path.Combine(settingsDir, "settings-adv.json"), selection,
                    new DpapiSecretStore(Path.Combine(settingsDir, "secrets")));
                AssertSettingsMatchPlan(advDlg, advanced: true, "advanced");
                advDlg.ModeSimple.IsChecked = true;       // flipping the choice changes the sections live
                AssertSettingsMatchPlan(advDlg, advanced: false, "advanced flipped to simple");
                advDlg.Close();
            }
            exercised.Add("settings-simple-screenshot");
            // Live finding (0.5.0): with a working session, Settings opened with the approval modes greyed out and "Sign in to
            // load", until the profile was switched away and back. They must load as the window opens.
            {
                using var fixtureAuth = new DashboardAuth(new Uri("https://gateway.example.invalid/"), new FixtureConfigHandler());
                var knownBeforeAppr = settings.KnownProfileIds;
                settings.KnownProfileIds = ["default", "alpha-bot"];                           // synthetic ids
                var apprDlg = new SettingsWindow(settings, Path.Combine(settingsDir, "settings-appr.json"), selection,
                    new DpapiSecretStore(Path.Combine(settingsDir, "secrets")), () => fixtureAuth);
                apprDlg.Left = -10000; apprDlg.Top = -10000; apprDlg.ShowInTaskbar = false;
                apprDlg.Show();
                settings.KnownProfileIds = knownBeforeAppr;
                var smart = () => apprDlg.ApprovalModes.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(r => r.Tag is ApprovalMode { Wire: "smart" });
                for (var i = 0; i < 50 && smart() is not { IsEnabled: true }; i++) await Task.Delay(40);
                var ok = smart() is { IsEnabled: true };
                var why = $"section={apprDlg.ApprovalsSection.Visibility} profile={apprDlg.ApprovalProfile.SelectedItem} status={apprDlg.ApprovalStatus.Text}";
                apprDlg.Close();
                if (!ok) throw new InvalidOperationException("HIKYAKU_UI_FAIL approval modes did not load when Settings opened with a session: " + why);
                exercised.Add("settings-approvals-load-on-open");
            }
        }
        // Command palette (Ctrl+K): navigation and reversible view actions only.
        vm.OpenPaletteCommand!.Execute(null);
        vm.PaletteQuery = "open";
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "04h-command-palette.png"));
        vm.ClosePaletteCommand!.Execute(null);
        exercised.Add("command-palette-screenshot");
        // Simple view: technical chips are hidden (visibility only; nothing about permissions changes).
        vm.ApplyExperience(false);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "04i-simple-view.png"));
        // Simple view with a folder shared with Hermes: the notice and the way to stop it stay visible.
        vm.CurrentProjectPathHint = @"D:\Shared\Recipes";
        vm.RaiseRiskNotice();
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "04i2-simple-shared-folder.png"));
        vm.CurrentProjectPathHint = null;
        vm.RaiseRiskNotice();
        exercised.Add("simple-shared-folder-notice-screenshot");
        vm.ApplyExperience(true);
        exercised.Add("simple-view-screenshot");
        // A /btw side card and a queued prompt (fixture bubbles; no gateway, no running reply).
        {
            var chat = vm.SelectedConversation!;
            var card = new MessageVM("/btw", "What is the capital of Japan?") { IsSideCard = true };
            card.ReplaceText("Q: What is the capital of Japan?\n\nA: Tokyo.");
            var queued = new MessageVM("You", "Now summarise that for a child.")
            { IsQueued = true, Activity = "Queued — sends when the reply finishes (kept in this app only)." };
            foreach (var m in new[] { card, queued }) { chat.History.Add(m); vm.Messages.Add(m); }
            window.AcceptanceScrollMessagesToEnd();
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "04f-side-card-and-queued.png"));
            foreach (var m in new[] { card, queued }) { chat.History.Remove(m); vm.Messages.Remove(m); }
            exercised.Add("side-card-and-queued-screenshot");
        }
        // "Load earlier messages" bar (fixture flags; disabled in the demo because there is no gateway to read from).
        {
            var chat = vm.SelectedConversation!;
            chat.StoredSessionId = "fixture-session";
            chat.RawRowsLoaded = 60;
            chat.HasEarlier = true;
            vm.SelectedConversation = null;
            vm.SelectedConversation = chat;
            window.AcceptanceScrollMessagesToStart();
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "04g-load-earlier.png"));
            chat.HasEarlier = false;
            chat.StoredSessionId = null;
            vm.SelectedConversation = null;
            vm.SelectedConversation = chat;
            exercised.Add("load-earlier-bar-screenshot");
        }
        // Follow-while-streaming in the real window: a growing reply stays in view at the bottom, scrolling up is respected,
        // and Jump to latest returns. A failure here fails the whole acceptance run.
        {
            var chat = vm.SelectedConversation!;
            var added = new List<MessageVM>();
            for (var i = 0; i < 40; i++)
            {
                var m = new MessageVM(i % 2 == 0 ? "You" : "Assistant", $"Synthetic message {i}\n" + new string('x', 60 + i));
                added.Add(m); chat.History.Add(m); vm.Messages.Add(m);
            }
            vm.SelectedConversation = null;
            vm.SelectedConversation = chat;          // opening a chat goes to its newest message
            async Task Settle()
            {
                for (var i = 0; i < 3; i++)
                {
                    window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                }
            }
            bool AtBottom() { var (o, e, v) = window.AcceptanceMessageScroll(); return e - (o + v) <= 30; }
            await Settle();
            if (!AtBottom()) throw new InvalidOperationException("follow acceptance: a chat did not open at its newest message");
            var streaming = new MessageVM("Assistant", "start");
            added.Add(streaming); chat.History.Add(streaming); vm.Messages.Add(streaming);
            for (var i = 0; i < 6; i++)
            {
                streaming.ReplaceText(string.Join("\n", Enumerable.Range(0, 10 * (i + 1)).Select(n => "streamed line " + n)));
                await Settle();
            }
            if (!AtBottom()) throw new InvalidOperationException("follow acceptance: a growing reply left the bottom while the reader was following");
            window.AcceptanceScrollMessagesTo(0);
            await Settle();
            streaming.ReplaceText(string.Join("\n", Enumerable.Range(0, 120).Select(n => "streamed line " + n)));
            await Settle();
            if (window.AcceptanceMessageScroll().Offset > 40) throw new InvalidOperationException($"follow acceptance: a reader scrolled up was yanked back down (scroll={window.AcceptanceMessageScroll()}, following={vm.FollowingLatest}, newBelow={vm.HasNewBelow})");
            if (vm.JumpToLatestVisibility != System.Windows.Visibility.Visible) throw new InvalidOperationException("follow acceptance: no Jump to latest offered");
            if (!window.AcceptanceJumpLatestShown()) throw new InvalidOperationException("follow acceptance: the Jump to latest button is not on screen");
            Capture(window, Path.Combine(outDir, "04j-jump-to-latest.png"));
            vm.JumpToLatestCommand!.Execute(null);
            await Settle();
            if (!AtBottom()) throw new InvalidOperationException("follow acceptance: Jump to latest did not reach the newest message");
            foreach (var m in added) { chat.History.Remove(m); vm.Messages.Remove(m); }
            exercised.Add("follow-while-streaming-real-window");
        }
        // A failed reply card (synthetic): what happened, delivery in words, and the one safe action.
        {
            var chat = vm.SelectedConversation!;
            var asked = new MessageVM("You", "Summarise the quarterly report.");
            var failed = new MessageVM("Assistant", "") { FailedPromptText = "Summarise the quarterly report." };
            failed.Failure = TurnFailures.Describe(TurnFailureKind.GatewayError, "synthetic: the model is overloaded", PromptDeliveryState.Settled, false);
            foreach (var m in new[] { asked, failed }) { chat.History.Add(m); vm.Messages.Add(m); }
            window.AcceptanceScrollMessagesToEnd();
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "04k-failed-reply-card.png"));
            foreach (var m in new[] { asked, failed }) { chat.History.Remove(m); vm.Messages.Remove(m); }
            exercised.Add("failed-reply-card-screenshot");
        }
        // A merged reply with tool steps (synthetic): collapsed by default; one expanded, one that reported an error.
        {
            var chat = vm.SelectedConversation!;
            vm.ApplyExperience(true);
            var asked = new MessageVM("You", "What is in the project folder?");
            var reply = new MessageVM("Assistant", "There is one file, notes.txt.\n\nThe second lookup failed, so I could not read it.");
            var ls = new ToolStepVM("terminal", "ls -la", "notes.txt", false) { IsExpanded = true };
            reply.Tools.Add(ls);
            reply.Tools.Add(new ToolStepVM("read_file", "notes.txt", "synthetic: permission denied", true));
            // Stage 44: reasoning (collapsed), a step's timing, the plan and a flagged output, all synthetic.
            var t0 = DateTimeOffset.UtcNow;
            reply.AppendReasoning("Synthetic reasoning: list the folder first, then read the file it shows.", t0);
            reply.EndReasoning(t0.AddSeconds(4));
            ls.Meta = "the project folder · 0.3 s";
            reply.SetPlan(new TodoList([
                new TodoItem("1", "List the project folder", TodoStatus.Completed),
                new TodoItem("2", "Read notes.txt", TodoStatus.InProgress),
                new TodoItem("3", "Summarise it", TodoStatus.Pending)], 1));
            foreach (var m in new[] { asked, reply }) { chat.History.Add(m); vm.Messages.Add(m); }
            window.AcceptanceScrollMessagesToEnd();
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "04l-tool-steps.png"));
            if (reply.ReasoningHeader != "Thought for 4s")
                throw new InvalidOperationException("HIKYAKU_UI_FAIL reasoning header: " + reply.ReasoningHeader);
            foreach (var m in new[] { asked, reply }) { chat.History.Remove(m); vm.Messages.Remove(m); }
            exercised.Add("tool-steps-screenshot");
            exercised.Add("reasoning-and-plan-screenshot");
        }
        // Running filter with synthetic listed chats and no gateway: every chat stays visible and the note says why.
        foreach (var n in new[] { "Quarterly report draft", "Refactor the parser", "Plan the offsite" })
            vm.SessionRows.Add(new SessionRowVM(new SessionSummary("fixture-" + n.GetHashCode(), n, Preview: "Synthetic fixture row")));
        vm.ToggleRunningFilterCommand!.Execute(null);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "04e-running-filter.png"));
        vm.ToggleRunningFilterCommand.Execute(null);
        vm.SessionRows.Clear();
        exercised.Add("running-filter-honest-note-screenshot");
        exercised.Add("maths-equations-screenshot");

        vm.ShowInboxCommand.Execute(null);
        vm.JobInboxShowAllResults = true;
        {
            // Stage 42: synthetic scheduled-job results (no gateway): a fresh result, a failed latest run, an older one.
            var none = new RelayCommand(_ => { });
            CronJob Job(string json) => CronJob.FromJson(System.Text.Json.JsonDocument.Parse(json).RootElement)!;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var digest = Job("""{"id":"a","name":"Morning summary","prompt":"p","state":"scheduled","profile":"default","last_status":"ok"}""");
            var backup = Job("""{"id":"b","name":"Backup check","prompt":"p","state":"scheduled","profile":"default","last_status":"error","last_error":"synthetic"}""");
            vm.JobResults.Add(new JobResultVM(new JobResult(digest, new SessionSummary("r1", Preview: "Synthetic: three things need you today.", StartedAt: now - 600), false), "default", false, none, none));
            vm.JobResults.Add(new JobResultVM(new JobResult(backup, new SessionSummary("r2", Preview: "Synthetic: the backup target was not reachable.", StartedAt: now - 7200), true), "default", false, none, none));
            vm.JobResults.Add(new JobResultVM(new JobResult(digest, new SessionSummary("r3", Preview: "Synthetic: a quiet day.", StartedAt: now - 90000), false), "default", false, none, none));
        }
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05-inbox.png"));
        vm.JobResults.Clear();
        exercised.Add("inbox-nav");

        vm.ShowNeedsYouCommand.Execute(null);
        Capture(window, Path.Combine(outDir, "06-needs-you.png"));
        exercised.Add("needs-you-nav");

        // Scheduled jobs page with synthetic rows (no gateway): list, a paused job with a reason, a routine and a delivery failure.
        vm.ApplyExperience(true);
        vm.Nav = MainNav.Scheduled;
        void Row(string json) => vm.CronRows.Add(new CronJobVM(CronJob.FromJson(System.Text.Json.JsonDocument.Parse(json).RootElement)!, true, (_, _) => { }));
        Row("""{"id":"a","name":"Morning summary","prompt":"p","schedule_display":"daily at 09:00","state":"scheduled","profile":"default","next_run_at":"2030-01-01T09:00:00+00:00","last_status":"ok"}""");
        Row("""{"id":"b","name":"[bot:Mika] Backup check","prompt":"p","schedule_display":"every 6h","state":"paused","paused_reason":"too many failures","profile":"work","last_status":"error","last_error":"synthetic failure"}""");
        Row("""{"id":"c","name":"Weekly digest","prompt":"p","schedule_display":"every monday 09:00","state":"scheduled","profile":"default","deliver":"telegram","last_status":"delivery_failed","last_delivery_error":"synthetic: chat not found"}""");
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05b-scheduled.png"));
        vm.CronRows.Clear();
        vm.Nav = MainNav.Chats;
        exercised.Add("scheduled-jobs-page-screenshot");

        // Journey page (synthetic graph, no gateway): months newest first, a skill and a memory.
        {
            vm.Nav = MainNav.Journey;
            vm.AcceptanceSetJourney(new JourneyGraph(
            [
                new JourneyNode("s1", "rotate-nas-snapshots", "skill", 1761000000, "homelab", 6, null, true, null),
                new JourneyNode("m1", "Prefers metric units <!-- observed -->:", "memory", 1760500000, null, 0, null, false, "MEMORY.md"),
                new JourneyNode("s2", "summarize-changelog", "skill", 1759000000, "writing", 3, null, false, null),
                new JourneyNode("s3", "check-disk-health", "skill", 1756000000, "uncategorized", 1, null, false, null),
                new JourneyNode("s4", "old-experiment", "skill", null, null, 0, "archived", false, null),
            ]), "default");
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "05f-journey.png"));
            vm.AcceptanceSetJourney(new JourneyGraph([]), "");
            vm.Nav = MainNav.Chats;
            exercised.Add("journey-page-screenshot");
        }

        // Artifacts page (synthetic items, no gateway): a generated picture, a file, an embedded picture and a link.
        {
            var demo = new ProfileKey("demo", "default");
            vm.ApplyExperience(true);
            vm.Nav = MainNav.Artifacts;
            vm.AcceptanceSetArtifacts(
            [
                new ArtifactItem(ArtifactKind.Image, "sunrise.png", "/opt/data/img/sunrise.png", "a1", "Poster ideas", demo, true, 5),
                new ArtifactItem(ArtifactKind.File, "quarterly-report.pdf", "/opt/data/out/quarterly-report.pdf", "a2", "Quarterly report", demo, false, 4),
                new ArtifactItem(ArtifactKind.Image, "chart", "data:image/png;base64,AAAA", "a2", "Quarterly report", demo, false, 3),
                new ArtifactItem(ArtifactKind.Link, "Hermes docs", "https://hermes-agent.nousresearch.com/docs", "a3", "Setup help", demo, false, 2),
                new ArtifactItem(ArtifactKind.File, "notes.txt", "notes/notes.txt", "a3", "Setup help", demo, false, 1),
            ]);
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "05e-artifacts.png"));
            vm.AcceptanceSetArtifacts([]);
            vm.Nav = MainNav.Chats;
            exercised.Add("artifacts-page-screenshot");
        }

        // Rooms page (synthetic fixture peer, no gateway): list, transcript, a member still replying, an approval waiting, mention choices.
        {
            vm.AttachRooms(() => new HostedRoomsApi(() => new FixtureRoomsPeer()), "demo");
            vm.Nav = MainNav.Rooms;
            await vm.RefreshRoomsAsync();
            if (vm.HostedRooms.Count > 0) await vm.OpenRoomAsync(vm.HostedRooms[0]);
            vm.RoomComposerText = "Thanks @sc";
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "05d-rooms.png"));
            await vm.OpenRoomAsync(null);
            vm.RoomComposerText = "";
            vm.Nav = MainNav.Chats;
            vm.AttachRooms(() => null, "");
            exercised.Add("rooms-page-screenshot");
        }

        // Capabilities page with synthetic rows (no gateway).
        vm.Nav = MainNav.Capabilities;
        vm.SkillRows.Add(new CapabilityRowVM("skill", "web-search", "web-search", "Search the web", "research · bundled · used 12×", true, "", true, _ => { }));
        vm.SkillRows.Add(new CapabilityRowVM("skill", "notes", "notes", "Keep notes", "agent", false, "", true, _ => { }));
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05c-capabilities.png"));
        // Skills hub tab (synthetic rows, no gateway): browse list with an installed mark.
        window.AcceptanceSelectCapabilityTab(3);
        vm.AcceptanceSetHub(
        [
            new HubSkill("deploy", "Ship a release with checks and a rollback note.", "official", "official/devops/deploy", "builtin", null, ["ops"], "devops", false),
            new HubSkill("pdf-tools", "Merge, split and extract text from PDFs.", "github", "github/y/pdf-tools", "community", null, [], "documents", true),
            new HubSkill("meeting-notes", "Turn a transcript into decisions and actions.", "github", "github/z/meeting-notes", "community", null, [], null, false),
        ]);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05c2-skills-hub.png"));
        // MCP server form and the raw config.yaml editor (Advanced only; synthetic text, no gateway).
        window.AcceptanceSelectCapabilityTab(2);
        vm.AddMcpServerCommand!.Execute(null);
        vm.McpName = "files"; vm.McpCommand = "npx"; vm.McpArgs = "-y\n@modelcontextprotocol/server-filesystem\n/data"; vm.McpEnv = "GITHUB_TOKEN=${GITHUB_TOKEN}";
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05c3-mcp-form.png"));
        vm.CancelMcpFormCommand!.Execute(null);
        // Typed settings (synthetic rows): a switch, a choice, a number, a hidden secret and a list.
        window.AcceptanceSelectCapabilityTab(4);
        vm.AcceptanceSetSettings(
        [
            new SettingEntry("model", "string", "The model this profile uses", "general", [], System.Text.Json.Nodes.JsonValue.Create("example-model")),
            new SettingEntry("agent.max_turns", "number", "How many steps the agent may take in one reply", "agent", [], System.Text.Json.Nodes.JsonValue.Create(30)),
            new SettingEntry("approvals.mode", "select", "How dangerous commands are approved", "approvals", ["manual", "smart", "off"], System.Text.Json.Nodes.JsonValue.Create("manual")),
            new SettingEntry("openai.api_key", "string", "Provider key", "openai", [], System.Text.Json.Nodes.JsonValue.Create("sk-never-shown")),
            new SettingEntry("tools.disabled", "list", "Tools turned off", "tools", [], null),
            new SettingEntry("voice.enabled", "boolean", "Speak replies", "voice", [], System.Text.Json.Nodes.JsonValue.Create(true)),
        ]);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05c5-typed-settings.png"));
        vm.AcceptanceSetSettings([]);
        window.AcceptanceSelectCapabilityTab(5);
        vm.ConfigYaml = "model: example-model\napprovals:\n  mode: manual\nmcp_servers:\n  files:\n    command: npx\n    args: [\"-y\", \"@modelcontextprotocol/server-filesystem\", \"/data\"]\n";
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05c4-config-yaml.png"));
        vm.ConfigYaml = "";
        exercised.Add("config-and-mcp-form-screenshots");
        window.AcceptanceSelectCapabilityTab(0);
        vm.AcceptanceSetHub([]);
        vm.SkillRows.Clear();
        vm.Nav = MainNav.Chats;
        exercised.Add("capabilities-page-screenshot");
        exercised.Add("skills-hub-tab-screenshot");

        // Insights page with synthetic bars and lines (no gateway).
        vm.Nav = MainNav.Insights;
        foreach (var h in new double[] { 0, 20, 45, 80, 100, 60, 30 }) vm.UsageBars.Add(new UsageBarVM(h, "synthetic day", ""));
        vm.ModelLines.Add(new InsightLineVM("synthetic-model", "1.2M tokens · 4 chat(s)"));
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "05d-insights.png"));
        vm.UsageBars.Clear();
        vm.ModelLines.Clear();
        vm.Nav = MainNav.Chats;
        exercised.Add("insights-page-screenshot");

        vm.ShowBotsCommand.Execute(null);
        await Task.Delay(200);
        Capture(window, Path.Combine(outDir, "09-bots-roster.png"));
        exercised.Add("bots-roster");
        if (vm.BotRows.Count > 0)
        {
            vm.OpenBotCommand.Execute(vm.BotRows[0]);
            await Task.Delay(100);
            Capture(window, Path.Combine(outDir, "10-bot-chat-subagents.png"));
            exercised.Add("bot-chat-subagent-frame");
            vm.SubagentSteerText = "synthetic steer";
            if (vm.SteerSubagentCommand.CanExecute(null))
            {
                vm.SteerSubagentCommand.Execute(null);
                exercised.Add("steer-honesty:" + vm.SteerHint);
            }
        }
        window.AcceptanceSetWidth(780);
        vm.ShowBotsCommand.Execute(null);
        Capture(window, Path.Combine(outDir, "11-bots-narrow.png"));
        exercised.Add("bots-narrow");
        window.AcceptanceSetWidth(1180);

        // Great Wave light — targeted CP8 visual cleanup capture
        settings.ThemeId = "great-wave";
        settings.SetAppearanceMode(AppearanceMode.Light);
        ThemeService.Apply(settings.ThemeId, settings.AppearanceMode);
        vm.ShowChatsCommand.Execute(null);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "12-great-wave-light-shell.png"));
        exercised.Add("great-wave-light-cleanup");

        // Checkpoint 9 — synthetic local grant (temp only; never a real user document tree)
        var synthRoot = Path.Combine(Path.GetTempPath(), "hikyaku-ui-cp9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(synthRoot);
        File.WriteAllText(Path.Combine(synthRoot, "notes.txt"), "synthetic project note");
        try
        {
            var grants = new LocalFolderGrantStore(
                new FilePreferenceStore(Path.Combine(App.DataDir, "folder-grants.json")),
                () => settings.DesktopDeviceId);
            await grants.GrantAsync(key, synthRoot, FolderGrantMode.Read);
            vm.AttachFolderGrants(grants);
            vm.ShowProjectsCommand?.Execute(null);
            await Task.Delay(200);
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "13-projects-granted.png"));
            exercised.Add("projects-grant-files");
            vm.ShowProjectChangesTabCommand?.Execute(null);
            await Task.Delay(150);
            Capture(window, Path.Combine(outDir, "14-projects-changes.png"));
            exercised.Add("projects-changes-guided");
            exercised.Add("identity-with-path:" + vm.IdentityStrip);

            // Checkpoint 10 — synthetic git repo under grant (temp only)
            var gitRoot = Path.Combine(synthRoot, "repo");
            Directory.CreateDirectory(gitRoot);
            RunGitAcceptance(gitRoot, "init");
            RunGitAcceptance(gitRoot, "config", "user.email", "test@users.noreply.github.com");
            RunGitAcceptance(gitRoot, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(gitRoot, "a.txt"), "one\n");
            RunGitAcceptance(gitRoot, "add", "a.txt");
            RunGitAcceptance(gitRoot, "commit", "-m", "init");
            File.WriteAllText(Path.Combine(gitRoot, "a.txt"), "two\n");
            await grants.GrantAsync(key, gitRoot, FolderGrantMode.Read);
            vm.ShowProjectsCommand?.Execute(null);
            vm.ShowProjectChangesTabCommand?.Execute(null);
            await Task.Delay(250);
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "15-projects-git-checkpoint-ui.png"));
            exercised.Add("projects-git-checkpoint-ui");
            exercised.Add("save-checkpoint-command:" + (vm.SaveCheckpointCommand is not null));
            exercised.Add("worktree-command:" + (vm.CreateWorktreeCommand is not null));

            // Checkpoint 12 — guided push/PR UI (synthetic grant + preview only; no real remote publish)
            var netStore = new NetworkPublishGrantStore(
                new FilePreferenceStore(Path.Combine(App.DataDir, "network-publish-ui.json")));
            vm.AttachNetworkPublish(netStore, new SetupRequiredGitHubPullRequestApi());
            await vm.AcceptanceEnableNetworkPublishAsync();
            exercised.Add("network-publish-enabled:" + vm.NetworkPublishEnabled);
            var bare = Path.Combine(synthRoot, "bare.git");
            RunGitAcceptance(synthRoot, "init", "--bare", bare);
            try { RunGitAcceptance(gitRoot, "remote", "remove", "origin"); } catch { /* none */ }
            RunGitAcceptance(gitRoot, "remote", "add", "origin", bare);
            await vm.AcceptancePreviewPushAsync();
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, "15b-projects-git-push-preview.png"));
            exercised.Add("push-preview-command:" + (vm.PreviewPushCommand is not null));
            exercised.Add("confirm-push-command:" + (vm.ConfirmPushCommand is not null));
            exercised.Add("create-pr-command:" + (vm.CreatePullRequestCommand is not null));
            exercised.Add("push-preview-label:" + (!string.IsNullOrWhiteSpace(vm.PushPreviewLabel)));
            exercised.Add("pr-setup-required-api:true");

            // Checkpoint 11/11b — terminal opt-in UI; default off; never auto-launch from preference
            exercised.Add("terminal-default-off:" + (!vm.TerminalOptedIn && !vm.TerminalDrawerOpen));
            exercised.Add("terminal-no-auto-launch:" + (!TerminalLaunchPolicy.MayAutoStartFromSavedPreference));
            if (vm.EnableTerminalOptInCommand is not null)
            {
                Capture(window, Path.Combine(outDir, "16-projects-terminal-optin-off.png"));
                exercised.Add("terminal-optin-command-present");
                exercised.Add("terminal-drawer-closed-default:" + (vm.TerminalDrawerVisibility == Visibility.Collapsed));
            }

            // Expanded opt-in/start/input/resize/hide/close in disposable cwd (no MessageBox).
            var termRoot = Path.Combine(synthRoot, "terminal-cwd");
            Directory.CreateDirectory(termRoot);
            await grants.GrantAsync(key, termRoot, FolderGrantMode.Read);
            vm.RefreshVisibility();
            await vm.AcceptanceEnableTerminalOptInAsync();
            exercised.Add("terminal-optin-enabled:" + vm.TerminalOptedIn);
            exercised.Add("terminal-drawer-open-after-optin:" + vm.TerminalDrawerOpen);
            if (vm.StartTerminalCommand is not null && vm.StartTerminalCommand.CanExecute(null))
            {
                vm.StartTerminalCommand.Execute(null);
                await Task.Delay(900);
                exercised.Add("terminal-start-attempted");
                vm.NotifyTerminalViewport(640, 160);
                exercised.Add("terminal-resize-notified");
                vm.TerminalInput = "echo cp11b-ui";
                if (vm.SubmitTerminalLineCommand is { } submit && submit.CanExecute(null))
                {
                    submit.Execute(null);
                    await Task.Delay(600);
                    exercised.Add("terminal-input-submit");
                }
                Capture(window, Path.Combine(outDir, "16b-terminal-running.png"));
                if (vm.HideTerminalDrawerCommand is { } hide)
                {
                    hide.Execute(null);
                    exercised.Add("terminal-hide-keeps-session:" + (!vm.TerminalDrawerOpen));
                    vm.ToggleTerminalDrawerCommand?.Execute(null);
                }
                if (vm.CloseTerminalSessionCommand is { } close && close.CanExecute(null))
                {
                    close.Execute(null);
                    exercised.Add("terminal-close");
                }
            }
            // Stage selection glue (no MessageBox): composer chip labelled pending until Send
            vm.NewChat();
            vm.AcceptanceStageTerminalContext("cp11b-ui-staged-context");
            var label = vm.StagedTerminalContextLabel;
            exercised.Add("terminal-staged-pending:" + (label is not null && label.Contains("pending", StringComparison.OrdinalIgnoreCase)));
            Capture(window, Path.Combine(outDir, "16c-terminal-staged-context.png"));
            vm.ClearStagedTerminalContextCommand?.Execute(null);

            // Profile switch must not leave terminal scrollback (controller bind)
            var other = new ProfileKey("demo", "other");
            await selection.SetSelectedAsync(new[] { key, other });
            vm.RefreshVisibility();
            exercised.Add("terminal-profile-switch-bound");
            await selection.SetSelectedAsync(new[] { key });
            vm.RefreshVisibility();
        }
        finally
        {
            try { Directory.Delete(synthRoot, true); } catch { /* best-effort */ }
        }

        vm.ShowChatsCommand.Execute(null);

        report.AppendLine();
        report.AppendLine("## Themes");
        foreach (var theme in ThemeCatalog.All)
        {
            foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark })
            {
                settings.ThemeId = theme.Id;
                settings.SetAppearanceMode(mode);
                ThemeService.Apply(theme.Id, mode);
                window.UpdateLayout();
                var name = $"{theme.Id}-{(mode == AppearanceMode.Light ? "light" : "dark")}";
                Capture(window, Path.Combine(outDir, $"07-theme-{name}.png"));
                report.AppendLine($"- {theme.DisplayName} / {mode}: contrast text/bg ok (catalog-tested); reducedMotion={ThemeService.ReduceMotion} highContrast={ThemeService.HighContrast}");
                exercised.Add("theme:" + name);
            }
        }
        settings.ThemeId = "washi-ink";
        settings.SetAppearanceMode(AppearanceMode.System);
        ThemeService.Apply(settings.ThemeId, settings.AppearanceMode);

        // A throwaway notification identity (removed again below) so the real toast path is exercised without leaving a trace.
        const string acceptanceToastId = "Hikyaku.Desktop.Acceptance";
        var toastRegistry = new HkcuToastRegistry();
        var toastWasRegistered = toastRegistry.IsRegistered(acceptanceToastId);
        toastRegistry.Register(acceptanceToastId, "Hikyaku acceptance");
        var presenter = new WindowsToastPresenter(acceptanceToastId, toastRegistry);
        var toasts = new ToastNotificationService(gate, () => settings.NotificationsEnabled, presenter);
        ToastDeliveryResult toastResult;
        try
        {
            toastResult = toasts.TryShow(new GatedNotification(key, "Hikyaku synthetic",
                "Acceptance toast — not a personal chat.", Surface.Toast));
        }
        finally
        {
            presenter.ClearAll();
            if (!toastWasRegistered) toastRegistry.Unregister(acceptanceToastId);
        }
        report.AppendLine();
        report.AppendLine("## Windows toast");
        report.AppendLine($"- Observation: **{toastResult.Observation}**");
        report.AppendLine($"- Detail: {toastResult.Detail}");
        report.AppendLine("- Observed means the toast was found in the Action Center history after Show; a Show call that returned is not counted. Whether a banner was visible to a person is not claimed.");
        report.AppendLine("- Abstraction (NotificationSink.TryAccept) is separate from this OS delivery attempt.");
        exercised.Add("toast-attempt:" + toastResult.Observation);

        if (App.DemoMode && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            await Task.Delay(400);
            Capture(window, Path.Combine(outDir, "08-after-demo-send.png"));
            exercised.Add("demo-send-stream");
        }

        report.AppendLine();
        report.AppendLine("## Identity strip");
        report.AppendLine("- " + vm.IdentityStrip);
        exercised.Add("identity-strip");

        // Chat-view cost with many messages (synthetic markdown; layout of the whole list, which is not virtualized).
        report.AppendLine();
        report.AppendLine("## Long-history layout cost (synthetic, this machine)");
        foreach (var count in new[] { 100, 400, 1000 })
        {
            vm.NewChat();
            var chat = vm.SelectedConversation!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
            {
                var m = new MessageVM(i % 2 == 0 ? "You" : "Assistant",
                    $"Message {i} — some **markdown**, `code`, a list:\n\n- one\n- two\n\nand a longer sentence so wrapping happens a little in the column.");
                chat.History.Add(m);
                vm.Messages.Add(m);
            }
            window.UpdateLayout();
            report.AppendLine($"- {count} messages: {sw.ElapsedMilliseconds} ms to add and lay out");
        }
        vm.NewChat();

        report.AppendLine();
        report.AppendLine("## Controls exercised");
        foreach (var e in exercised)
            report.AppendLine("- " + e);
        report.AppendLine();
        report.AppendLine("## Not claimed");
        report.AppendLine("- Live dashboard login / live Inbox / live personal chats / live config PUT");
        report.AppendLine("- Live maths beyond synthetic labelled equations (WPF-Math local typeset in demo history)");
        report.AppendLine("- Live bot creation / live rooms write / live subagent interrupt");
        report.AppendLine("- Core microbenchmarks are not a substitute for these UI captures");
        report.AppendLine("- YOLO enable confirmation dialog was not auto-accepted in synthetic mode");
        report.AppendLine("- Steer queued ack is not proof the child read it (labelled in UI)");
        report.AppendLine("- No live private file trees; project grant uses synthetic temp only");
        report.AppendLine("- Guided Save checkpoint / worktree / Push preview UI present; Confirm push uses disposable remotes in tests only; live PR create is setup-required (no embedded PAT); fixture/loopback PR transport tested");
        report.AppendLine("- Local terminal opt-in UI present (default off; no auto-launch; ConPTY); expanded opt-in/start/input/resize/hide/close in synthetic acceptance; remote terminal unavailable with explanation; gateway FS bridge NOT deployed");
        report.AppendLine("- Terminal selection stages for next Send (pending label); suspected keys stay blocked; raw credentials not retained in pending previews");

        await File.WriteAllTextAsync(Path.Combine(outDir, "REPORT.md"), report.ToString());
        await File.WriteAllTextAsync(Path.Combine(outDir, "ui-acceptance.txt"),
            $"HIKYAKU_UI_OK toast={toastResult.Observation} controls={exercised.Count} themes={ThemeCatalog.All.Count * 2} overflow={overflow}");
    }

    /// <summary>What the last overflow check flagged (type, accessible name, widths), so a regression can be found without a debugger.</summary>
    private static readonly List<string> FlaggedOverflow = new();

    /// <summary>The real Settings window must show exactly the sections <see cref="SettingsSections"/> plans (a regression that hides sign-in is the harmful direction).</summary>
    private static void AssertSettingsMatchPlan(SettingsWindow w, bool advanced, string label)
    {
        var plan = SettingsSections.For(advanced, w.SetupNeeded);
        void Check(string name, System.Windows.UIElement el, bool shouldShow)
        {
            var shown = el.Visibility == System.Windows.Visibility.Visible;
            if (shown != shouldShow) throw new InvalidOperationException($"settings acceptance ({label}): {name} is {(shown ? "shown" : "hidden")} but should be {(shouldShow ? "shown" : "hidden")}");
        }
        Check("connection", w.ConnectionSection, plan.Connection);
        Check("credentials", w.CredentialsSection, plan.Credentials);
        Check("sign in", w.SignInDiscover, plan.SignInButtons);
        Check("browser sign in", w.SignInBrowser, true);
        Check("sign out", w.SignOutBrowser, true);
        Check("cloudflare access", w.AccessSection, plan.Access);
        Check("updates", w.UpdatesSection, plan.Updates);
        Check("profiles", w.ProfilesSection, plan.Profiles);
        Check("approvals", w.ApprovalsSection, plan.Approvals);
        Check("notifications", w.NotificationsSection, plan.Notifications);
        Check("simple choice", w.ModeSimple, true);
        Check("advanced choice", w.ModeAdvanced, true);
    }

    private static int CheckHorizontalOverflow(FrameworkElement root)
    {
        var flagged = 0;
        FlaggedOverflow.Clear();
        void Walk(DependencyObject d)
        {
            if (d is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0)
            {
                if (fe is System.Windows.Controls.ScrollViewer) { /* allow */ }
                else if (VisualTreeHelper.GetParent(fe) is FrameworkElement parent
                         && parent.ActualWidth > 0
                         && fe.ActualWidth > parent.ActualWidth + 2
                         && fe is not System.Windows.Controls.TextBlock { TextWrapping: TextWrapping.Wrap })
                {
                    flagged++;
                    FlaggedOverflow.Add($"{fe.GetType().Name} \"{System.Windows.Automation.AutomationProperties.GetName(fe)}\" {fe.ActualWidth:0}>{parent.ActualWidth:0} in {parent.GetType().Name}");
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return flagged;
    }

    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var w = Math.Max(1, (int)element.ActualWidth);
        var h = Math.Max(1, (int)element.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void RunGitAcceptance(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException("git failed in UI acceptance: " + p.StandardError.ReadToEnd());
    }
}

/// <summary>Synthetic hosted-rooms answers for the UI acceptance run only: no gateway is involved.</summary>
/// <summary>A gateway's config routes, synthetic: approvals can be Manual, Smart or Off and are Manual now.</summary>
internal sealed class FixtureConfigHandler : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
    {
        var json = request.RequestUri!.AbsolutePath.EndsWith("/api/config/schema", StringComparison.Ordinal)
            ? """{"fields":{"approvals.mode":{"type":"select","category":"approvals","options":["manual","smart","off"]}}}"""
            : """{"approvals":{"mode":"manual"}}""";
        return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json") });
    }
}

internal sealed class FixtureRoomsPeer : IJsonRpcPeer
{
    public Task<System.Text.Json.Nodes.JsonNode?> RequestAsync(string method, System.Text.Json.Nodes.JsonObject? parameters = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        const string members = "[{\"member_id\":\"ops\",\"profile\":\"default\",\"handle\":\"ops\",\"display_name\":\"Ops Bot\"},{\"member_id\":\"scribe\",\"profile\":\"default\",\"handle\":\"scribe\",\"display_name\":\"Scribe\"}]";
        var json = method switch
        {
            "groups.capabilities" => "{\"driver\":true,\"protocol_version\":1}",
            "groups.list" => "{\"rooms\":[{\"room_id\":\"r1\",\"name\":\"Release room (synthetic)\",\"updated_at\":5,\"members\":" + members + "}]}",
            "groups.log" => "{\"events\":[" +
                "{\"room_id\":\"r1\",\"seq\":1,\"event_id\":\"u1\",\"kind\":\"message.user\",\"actor\":{\"kind\":\"user\",\"id\":\"d\"},\"payload\":{\"text\":\"@all how is the release?\"},\"created_at\":1}," +
                "{\"room_id\":\"r1\",\"seq\":2,\"event_id\":\"m1\",\"kind\":\"message.member\",\"actor\":{\"kind\":\"member\",\"id\":\"ops\"},\"payload\":{\"text\":\"Build is green; waiting on one approval.\",\"member_id\":\"ops\",\"discussion_event_id\":\"u1\",\"round_index\":0},\"created_at\":2}" +
                "],\"cursor\":2,\"latest_seq\":2}",
            "groups.state" => "{\"room\":{\"room_id\":\"r1\",\"name\":\"Release room (synthetic)\",\"members\":" + members + "},\"driver_status\":{\"running\":true,\"pending_actions\":[{\"kind\":\"approval\",\"member_id\":\"ops\",\"task_id\":\"t1\",\"execution_generation\":1,\"request_id\":\"a1\",\"approval\":{\"command\":\"git push origin release\"}}]}}",
            _ => "{}"
        };
        return Task.FromResult<System.Text.Json.Nodes.JsonNode?>(System.Text.Json.Nodes.JsonNode.Parse(json));
    }
}
