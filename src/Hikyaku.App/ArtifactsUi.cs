using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hikyaku.Core;

namespace Hikyaku.App;

public enum ArtifactFilter { All, Images, Files, Links }

/// <summary>One artifact in the gallery.</summary>
public sealed class ArtifactRowVM : Observable
{
    public ArtifactRowVM(ArtifactItem item, MainViewModel owner)
    {
        Item = item;
        SaveCommand = new RelayCommand(_ => _ = owner.SaveArtifactAsync(this), _ => item.CanSave);
        PreviewCommand = new RelayCommand(_ => _ = owner.PreviewArtifactAsync(this), _ => item.Kind == ArtifactKind.Image && item.CanSave);
        CopyCommand = new RelayCommand(_ => owner.CopyArtifactLink(this), _ => item.IsWeb);
        OpenChatCommand = new RelayCommand(_ => _ = owner.OpenArtifactChatAsync(this));
    }

    public ArtifactItem Item { get; }
    public string Name => Item.Name;
    public string KindLabel => Item.Kind switch { ArtifactKind.Image => Item.Generated ? "Picture · generated" : "Picture", ArtifactKind.Link => "Link", _ => "File" };
    public string Detail => "From “" + (string.IsNullOrWhiteSpace(Item.SessionTitle) ? "Untitled chat" : Item.SessionTitle) + "”"
        + (Item.IsInlineData ? " · embedded picture" : " · " + Shorten(Item.Source));
    public Visibility SaveVisibility => Item.CanSave ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PreviewVisibility => Item.Kind == ArtifactKind.Image && Item.CanSave ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CopyVisibility => Item.IsWeb ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoteVisibility => !Item.CanSave && !Item.IsWeb ? Visibility.Visible : Visibility.Collapsed;
    public string Note => "This is a path relative to the chat's working folder, which this app can't read.";
    public ICommand SaveCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenChatCommand { get; }

    private static string Shorten(string s) => s.Length <= 70 ? s : s[..34] + "…" + s[^30..];
}

/// <summary>
/// Artifacts (Hermes Desktop's gallery of what sessions generate: pictures, files, links; hidden in Simple mode). Desktop documents no
/// endpoint for it and Android has none, so this gallery is built only from what stored chats say, on request: the pictures
/// and files replies deliver (<c>MEDIA:</c>, Markdown images, <c>::preview</c>), <c>image_generate</c> results and links, read from a
/// bounded number of recent chats of the chosen profile. Files come through the authenticated gateway download, one at a time, on an
/// explicit Save or Preview. Links are shown and can be copied; they are not opened from here.
/// </summary>
public sealed partial class MainViewModel
{
    private const long ArtifactPreviewMaxBytes = 15L * 1024 * 1024;
    private List<ArtifactItem> _artifactAll = [];
    private ArtifactFilter _artifactFilter = ArtifactFilter.All;
    private string _artifactSearch = "";
    private string _artifactStatus = "Choose Scan to look through your recent chats for pictures, files and links.";
    private bool _artifactScanning;
    private int _artifactScanId;
    private ImageSource? _artifactPreview;
    private string _artifactPreviewName = "";

    public ObservableCollection<ArtifactRowVM> ArtifactRows { get; } = new();
    public ICommand? ShowArtifactsCommand { get; private set; }
    public ICommand? ScanArtifactsCommand { get; private set; }
    public ICommand? ClearArtifactPreviewCommand { get; private set; }
    public ICommand? SetArtifactFilterCommand { get; private set; }

    /// <summary>Asks where to save (suggested name) → a path, or null to cancel. Unset means nothing is saved.</summary>
    public Func<string, string?>? PickArtifactSavePath { get; set; }
    public Func<string, byte[], Task> WriteArtifactFile { get; set; } = (path, bytes) => File.WriteAllBytesAsync(path, bytes);
    public Action<string>? CopyArtifactText { get; set; }

    public string ArtifactStatus { get => _artifactStatus; private set { Set(ref _artifactStatus, value); Raise(nameof(ArtifactStatusVisibility)); } }
    public Visibility ArtifactStatusVisibility => _artifactStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool ArtifactScanning { get => _artifactScanning; private set { Set(ref _artifactScanning, value); (ScanArtifactsCommand as RelayCommand)?.Refresh(); } }
    public ImageSource? ArtifactPreviewImage { get => _artifactPreview; private set { Set(ref _artifactPreview, value); Raise(nameof(ArtifactPreviewVisibility)); } }
    public string ArtifactPreviewName { get => _artifactPreviewName; private set => Set(ref _artifactPreviewName, value); }
    public Visibility ArtifactPreviewVisibility => _artifactPreview is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ArtifactsVisibility =>
        ChatShellVisibility == Visibility.Visible && Nav == MainNav.Artifacts ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Desktop hides the Artifacts row in Simple mode; so does Hikyaku.</summary>
    public ArtifactFilter ArtifactFilterMode => _artifactFilter;
    public string ArtifactFilterLabel => _artifactFilter.ToString();

    public string ArtifactSearch
    {
        get => _artifactSearch;
        set { if (_artifactSearch == value) return; _artifactSearch = value ?? ""; Raise(nameof(ArtifactSearch)); ApplyArtifactFilter(); }
    }

    private void InitArtifacts()
    {
        ShowArtifactsCommand = new RelayCommand(_ => Nav = MainNav.Artifacts);
        ScanArtifactsCommand = new RelayCommand(_ => _ = ScanArtifactsAsync(), _ => !_artifactScanning);
        ClearArtifactPreviewCommand = new RelayCommand(_ => ClearArtifactPreviewIfAny());
        SetArtifactFilterCommand = new RelayCommand(p =>
        {
            if (p is ArtifactFilter f) SetArtifactFilter(f);
            else if (p is string s && Enum.TryParse<ArtifactFilter>(s, out var parsed)) SetArtifactFilter(parsed);
        });
    }

    /// <summary>Synthetic UI acceptance only: shows given items without any gateway.</summary>
    internal void AcceptanceSetArtifacts(IEnumerable<ArtifactItem> items)
    {
        _artifactAll = items.ToList();
        ApplyArtifactFilter();
        ArtifactStatus = $"{_artifactAll.Count} item(s) from 3 recent chat(s). (synthetic)";
    }

    private void ClearArtifactPreviewIfAny() { ArtifactPreviewImage = null; ArtifactPreviewName = ""; }

    public void SetArtifactFilter(ArtifactFilter f)
    {
        _artifactFilter = f;
        Raise(nameof(ArtifactFilterMode)); Raise(nameof(ArtifactFilterLabel));
        ApplyArtifactFilter();
    }

    private void ApplyArtifactFilter()
    {
        var q = _artifactSearch.Trim();
        bool Match(ArtifactItem i) =>
            (_artifactFilter == ArtifactFilter.All
             || (_artifactFilter == ArtifactFilter.Images && i.Kind == ArtifactKind.Image)
             || (_artifactFilter == ArtifactFilter.Files && i.Kind == ArtifactKind.File)
             || (_artifactFilter == ArtifactFilter.Links && i.Kind == ArtifactKind.Link))
            && (q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || i.Source.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (i.SessionTitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        ArtifactRows.Clear();
        foreach (var i in _artifactAll.Where(Match)) ArtifactRows.Add(new ArtifactRowVM(i, this));
    }

    /// <summary>Reads a bounded number of recent chats of the chosen profile (an explicit gesture) and rebuilds the gallery. A newer scan replaces an older one.</summary>
    public async Task ScanArtifactsAsync()
    {
        if (_sessions() is not { } catalog || catalog.ActiveProfile is not { } profile || !_gate.Allow(Surface.Conversation, profile))
        {
            ArtifactStatus = "Connect to a gateway and choose a profile first.";
            return;
        }
        var id = ++_artifactScanId;
        ArtifactScanning = true;
        ArtifactStatus = "Looking through your recent chats…";
        try
        {
            var chats = await catalog.LoadRecentAsync().ConfigureAwait(true);
            if (id != _artifactScanId) return;
            var result = await ArtifactScanner.ScanAsync(chats, profile,
                (chatId, ct) => catalog.LoadMessagesPageAsync(profile, chatId, 0, ArtifactScanner.RowsPerChat, ct)).ConfigureAwait(true);
            if (id != _artifactScanId) return;
            _artifactAll = result.Items.ToList();
            ApplyArtifactFilter();
            ArtifactStatus = _artifactAll.Count == 0
                ? $"Nothing found in {result.ChatsRead} recent chat(s)."
                : $"{_artifactAll.Count} item(s) from {result.ChatsRead} recent chat(s)"
                  + (result.ChatsFailed > 0 ? $"; {result.ChatsFailed} could not be read" : "")
                  + (result.Truncated ? ". Only the most recent chats are scanned." : ".");
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthFailedException or InvalidOperationException or TaskCanceledException)
        {
            if (id == _artifactScanId) ArtifactStatus = "Couldn't read your chats: " + ex.Message;
        }
        finally { if (id == _artifactScanId) ArtifactScanning = false; }
    }

    private async Task<byte[]?> FetchArtifactAsync(ArtifactItem item)
    {
        if (!_gate.Allow(Surface.Conversation, item.Profile)) { ArtifactStatus = "That chat's profile is not chosen in Hikyaku."; return null; }
        if (item.IsInlineData)
        {
            var bytes = ArtifactData.DecodeDataUrl(item.Source);
            if (bytes is null) ArtifactStatus = "That embedded picture is damaged or too large.";
            return bytes;
        }
        if (!item.OnGateway) { ArtifactStatus = "That item can't be fetched from here."; return null; }
        if (_media() is not { } media) { ArtifactStatus = "Not connected."; return null; }
        try { return await media.DownloadAsync(item.GatewayPath, item.Profile.ProfileId).ConfigureAwait(true); }
        catch (Exception ex) when (ex is HttpRequestException or AuthFailedException or InvalidOperationException or TaskCanceledException)
        {
            ArtifactStatus = "Couldn't get the file: " + ex.Message;
            return null;
        }
    }

    /// <summary>Saves one artifact where the person chooses. Nothing is fetched until a path is picked, and nothing is saved without a handler.</summary>
    public async Task SaveArtifactAsync(ArtifactRowVM row)
    {
        var item = row.Item;
        if (!item.CanSave || PickArtifactSavePath is null) return;
        var path = PickArtifactSavePath(ArtifactData.SafeName(item));
        if (string.IsNullOrWhiteSpace(path)) return;
        ArtifactStatus = "Getting " + item.Name + "…";
        var bytes = await FetchArtifactAsync(item).ConfigureAwait(true);
        if (bytes is null) return;
        try
        {
            await WriteArtifactFile(path, bytes).ConfigureAwait(true);
            ArtifactStatus = "Saved " + item.Name + ".";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ArtifactStatus = "Couldn't save the file: " + ex.Message;
        }
    }

    /// <summary>Shows a picture in the page (it is fetched on this click only; large ones are not previewed, only saved).</summary>
    public async Task PreviewArtifactAsync(ArtifactRowVM row)
    {
        var item = row.Item;
        if (item.Kind != ArtifactKind.Image || !item.CanSave) return;
        ArtifactStatus = "Getting " + item.Name + "…";
        var bytes = await FetchArtifactAsync(item).ConfigureAwait(true);
        if (bytes is null) return;
        if (bytes.LongLength > ArtifactPreviewMaxBytes) { ArtifactStatus = "That picture is too large to preview; use Save."; return; }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            ArtifactPreviewImage = image;
            ArtifactPreviewName = item.Name;
            ArtifactStatus = "";
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException or ArgumentException)
        {
            ArtifactStatus = "That file can't be shown as a picture; use Save.";
        }
    }

    public void CopyArtifactLink(ArtifactRowVM row)
    {
        if (!row.Item.IsWeb || CopyArtifactText is null) return;
        CopyArtifactText(row.Item.Source);
        ArtifactStatus = "Link copied.";
    }

    /// <summary>Jumps back to the chat that produced it.</summary>
    public async Task OpenArtifactChatAsync(ArtifactRowVM row)
    {
        if (!_gate.Allow(Surface.Conversation, row.Item.Profile)) return;
        Nav = MainNav.Chats;
        await OpenStoredAsync(row.Item.SessionId, string.IsNullOrWhiteSpace(row.Item.SessionTitle) ? row.Item.SessionId : row.Item.SessionTitle!, row.Item.Profile, select: true).ConfigureAwait(true);   // under the profile that produced it
    }
}
