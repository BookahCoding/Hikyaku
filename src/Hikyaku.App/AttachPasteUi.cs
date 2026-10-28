using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Hikyaku.App;

/// <summary>
/// 0.5.1 (live finding: pasting a picture did nothing): Ctrl+V in the chat box adds a copied picture (a screenshot, an image
/// copied in a browser) or files copied in Explorer as attachments, and files dropped anywhere on the chat are added too, as in
/// Claude Desktop. Copied text still pastes as text: when the clipboard holds text, the chat box gets the text.
/// </summary>
public partial class MainWindow
{
    private void HookAttachmentPasteAndDrop()
    {
        CommandManager.AddPreviewCanExecuteHandler(Composer, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachment()) return;
            e.CanExecute = true;                     // a TextBox only offers Paste for text
            e.Handled = true;
        });
        CommandManager.AddPreviewExecutedHandler(Composer, (_, e) =>
        {
            if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachment()) return;
            e.Handled = true;
            ShowAttachProblems(PasteAttachments());
        });
        ChatArea.AllowDrop = true;
        ChatArea.PreviewDragOver += (_, e) =>
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = _vm.CanAddAttachments ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;                        // the chat box would otherwise refuse anything that is not text
        };
        ChatArea.PreviewDrop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
            e.Handled = true;
            ShowAttachProblems(_vm.AddAttachmentFiles(paths));
            Composer.Focus();
        };
    }

    /// <summary>Text wins: only a clipboard without text, holding files or a picture, becomes an attachment.</summary>
    private static bool ClipboardHasAttachment()
    {
        try { return !Clipboard.ContainsText() && (Clipboard.ContainsFileDropList() || Clipboard.ContainsImage()); }
        catch (System.Runtime.InteropServices.COMException) { return false; }   // another app holds the clipboard
    }

    private IReadOnlyList<string> PasteAttachments()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
                return _vm.AddAttachmentFiles(Clipboard.GetFileDropList().Cast<string>().ToList());
            // Browsers and the Snipping Tool also put the picture on the clipboard as PNG, which keeps transparency;
            // otherwise the bitmap is encoded as PNG here.
            if (Clipboard.GetData("PNG") is MemoryStream png && png.Length > 0)
                return _vm.AddPastedImage(png.ToArray()) is { } err ? [err] : [];
            if (Clipboard.GetImage() is { } bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return _vm.AddPastedImage(ms.ToArray()) is { } err ? [err] : [];
            }
            return [];
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException)
        {
            return ["Could not read the clipboard: " + ex.Message];
        }
    }

    private void ShowAttachProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count > 0) MessageBox.Show(this, string.Join("\n", problems), "Hikyaku", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
