using System.IO;
using Hikyaku.App;
using Hikyaku.Core;
using Xunit;

namespace Hikyaku.Tests;

/// <summary>
/// 0.5.1 — a picture pasted into the chat box and files dropped on the chat become pending attachments, through the same
/// rules as the "+" button; what cannot be added is said in words instead of being dropped silently.
/// (The clipboard itself is not touched here: a test must not overwrite the person's clipboard.)
/// </summary>
[Collection("StaMath")]
public class Checkpoint46AttachPasteWpfTests
{
    private sealed class MemPrefs : IPreferenceStore
    {
        private string? _c;
        public Task<string?> LoadAsync(CancellationToken ct = default) => Task.FromResult(_c);
        public Task SaveAsync(string content, CancellationToken ct = default) { _c = content; return Task.CompletedTask; }
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private static MainViewModel Make()
    {
        var selection = new ProfileSelectionService(new MemPrefs());
        selection.SetSelectedAsync([new ProfileKey("gw", "default")]).GetAwaiter().GetResult();
        var vm = new MainViewModel(selection, new NotificationGate(selection), () => throw new NotSupportedException(), () => "t",
            () => null, () => null, () => null, () => null, () => null, () => null, () => null, () => null);
        vm.NewChat();
        return vm;
    }

    [Fact]
    public void A_pasted_picture_and_dropped_files_are_added_and_a_folder_is_explained()
    {
        RunSta(() =>
        {
            var vm = Make();
            Assert.True(vm.CanAddAttachments);

            Assert.Null(vm.AddPastedImage([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]));
            var pasted = Assert.Single(vm.PendingAttachments);
            Assert.Equal("image/png", pasted.MimeType);
            Assert.StartsWith("Pasted image ", pasted.Name);

            var dir = Directory.CreateTempSubdirectory("hikyaku-drop-");
            try
            {
                var file = Path.Combine(dir.FullName, "notes.pdf");
                File.WriteAllBytes(file, [1, 2, 3]);
                var problems = vm.AddAttachmentFiles([file, dir.FullName]);
                Assert.Equal(2, vm.PendingAttachments.Count);
                Assert.Equal("application/pdf", vm.PendingAttachments[1].MimeType);
                Assert.Contains("not folders", Assert.Single(problems));
            }
            finally { dir.Delete(recursive: true); }
        });
    }
}
