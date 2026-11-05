using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Reorder.Models;
using Reorder.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;

namespace Reorder;

public sealed partial class MainPage : Page
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".heic", ".heif", ".avif",
    };

    private const int ThumbnailSize = 320;
    private const int ThumbnailWorkers = 4;

    private readonly History _history = new(capacity: 50);
    private string? _folder;
    private CancellationTokenSource? _thumbnailCts;
    private bool _suppressCollectionEvents;
    private bool _busy;
    private ImageItem? _contextItem;
    private List<ImageItem>? _dragSnapshot;
    private int _previewIndex = -1;
    private int _previewVersion;

    public MainPage()
    {
        InitializeComponent();

        // Hooked up here rather than in XAML: setting initial values during
        // InitializeComponent would otherwise fire these before all controls exist.
        PrefixBox.TextChanged += RenameOptions_TextChanged;
        SuffixBox.TextChanged += RenameOptions_TextChanged;
        StartBox.ValueChanged += RenameOptions_ValueChanged;
        DigitsBox.ValueChanged += RenameOptions_ValueChanged;

        Items.CollectionChanged += (_, _) =>
        {
            if (_suppressCollectionEvents) return;
            UpdatePositions();
            UpdateState();
        };

        RootGrid.AddHandler(DragOverEvent, new DragEventHandler(Root_DragOver), handledEventsToo: true);
        RootGrid.AddHandler(DropEvent, new DragEventHandler(Root_Drop), handledEventsToo: true);
        UpdateState();
    }

    public ObservableCollection<ImageItem> Items { get; } = [];

    // ------------------------------------------------------------------ Folder loading

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || App.MainWindow is not { } window) return;
        var picker = new FolderPicker(window.AppWindow.Id);
        var result = await picker.PickSingleFolderAsync();
        if (result is not null) await LoadFolderAsync(result.Path);
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (_busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "打开此文件夹";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (_busy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        string? folder;
        try
        {
            var dropped = await e.DataView.GetStorageItemsAsync();
            folder = dropped.FirstOrDefault() switch
            {
                StorageFolder f => f.Path,
                StorageFile f => Path.GetDirectoryName(f.Path),
                _ => null,
            };
        }
        finally
        {
            deferral.Complete();
        }
        if (!string.IsNullOrEmpty(folder)) await LoadFolderAsync(folder);
    }

    private async Task LoadFolderAsync(string folder)
    {
        ClosePreview();
        List<string> files;
        try
        {
            files = await Task.Run(() => new DirectoryInfo(folder)
                .EnumerateFiles()
                .Where(f => ImageExtensions.Contains(f.Extension) && !f.Attributes.HasFlag(System.IO.FileAttributes.Hidden))
                .Select(f => f.FullName)
                .ToList());
        }
        catch (Exception ex)
        {
            ShowStatus("无法读取文件夹", ex.Message, InfoBarSeverity.Error);
            return;
        }

        files.Sort((a, b) => NaturalComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));

        _thumbnailCts?.Cancel();
        _folder = folder;
        _history.Clear();
        _suppressCollectionEvents = true;
        Items.Clear();
        foreach (var file in files) Items.Add(new ImageItem(file));
        _suppressCollectionEvents = false;
        UpdatePositions();
        UpdateState();

        FolderNameText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        ToolTipService.SetToolTip(FolderNameText, folder);
        StatusBar.IsOpen = false;
        StartThumbnailLoading();
    }

    // ------------------------------------------------------------------ Thumbnails

    private void StartThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        var cts = _thumbnailCts = new CancellationTokenSource();
        var queue = Items.Where(i => i.Thumbnail is null).ToList();
        int next = -1;

        async Task Worker()
        {
            while (!cts.IsCancellationRequested)
            {
                int index = Interlocked.Increment(ref next);
                if (index >= queue.Count) return;
                await LoadThumbnailAsync(queue[index]);
            }
        }

        for (int i = 0; i < ThumbnailWorkers; i++) _ = Worker();
    }

    private static async Task LoadThumbnailAsync(ImageItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbnailSize, ThumbnailOptions.ResizeThumbnail);
            if (thumb is not null && thumb.Type == ThumbnailType.Image)
            {
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(thumb);
                item.Thumbnail = bmp;
                return;
            }
        }
        catch
        {
            // Fall back to decoding the file directly.
        }

        try
        {
            var bmp = new BitmapImage { DecodePixelWidth = ThumbnailSize };
            using var stream = new MemoryStream(await File.ReadAllBytesAsync(item.Path)).AsRandomAccessStream();
            await bmp.SetSourceAsync(stream);
            item.Thumbnail = bmp;
        }
        catch
        {
            // Leave the placeholder for undecodable files.
        }
    }

    // ------------------------------------------------------------------ Ordering

    private void UpdatePositions()
    {
        for (int i = 0; i < Items.Count; i++) Items[i].Position = i + 1;
    }

    /// <summary>Rearranges <see cref="Items"/> to match <paramref name="order"/> using moves.</summary>
    private void ApplyOrder(IReadOnlyList<ImageItem> order)
    {
        _suppressCollectionEvents = true;
        try
        {
            for (int i = 0; i < order.Count && i < Items.Count; i++)
            {
                int current = Items.IndexOf(order[i]);
                if (current >= 0 && current != i) Items.Move(current, i);
            }
        }
        finally
        {
            _suppressCollectionEvents = false;
        }
        UpdatePositions();
        UpdateState();
    }

    /// <summary>Applies a new order and records it in history.</summary>
    private void ChangeOrder(string description, IReadOnlyList<ImageItem> newOrder)
    {
        var before = Items.ToList();
        if (before.SequenceEqual(newOrder)) return;
        ApplyOrder(newOrder);
        _history.Push(new OrderEntry(description, before, newOrder.ToList()));
        UpdateState();
    }

    private void SortByName_Click(object sender, RoutedEventArgs e)
    {
        var sorted = Items.OrderBy(i => i.FileName, NaturalComparer.Instance).ToList();
        ChangeOrder("按文件名排序", sorted);
    }

    private void ImageGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragSnapshot = Items.ToList();
    }

    private void ImageGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var before = _dragSnapshot;
        _dragSnapshot = null;
        if (before is null || before.SequenceEqual(Items)) return;
        _history.Push(new OrderEntry("拖拽排序", before, Items.ToList()));
        UpdateState();
    }

    private void ItemMenu_Opening(object sender, object e)
    {
        _contextItem = (sender as FlyoutBase)?.Target?.DataContext as ImageItem;
    }

    /// <summary>Items targeted by a context-menu command: the selection if the item is part of it.</summary>
    private List<ImageItem> GetContextTargets()
    {
        if (_contextItem is null) return [];
        if (ImageGrid.SelectedItems.Contains(_contextItem))
            return ImageGrid.SelectedItems.Cast<ImageItem>().OrderBy(i => Items.IndexOf(i)).ToList();
        return [_contextItem];
    }

    private void MoveToStart_Click(object sender, RoutedEventArgs e) => MoveTargets(GetContextTargets(), toStart: true);

    private void MoveToEnd_Click(object sender, RoutedEventArgs e) => MoveTargets(GetContextTargets(), toStart: false);

    private void MoveTargets(List<ImageItem> targets, bool toStart)
    {
        if (targets.Count == 0 || _busy) return;
        var set = targets.ToHashSet();
        var rest = Items.Where(i => !set.Contains(i));
        var order = (toStart ? targets.Concat(rest) : rest.Concat(targets)).ToList();
        ChangeOrder(toStart ? "移到最前" : "移到最后", order);
        ImageGrid.ScrollIntoView(targets[0]);
    }

    // ------------------------------------------------------------------ Undo / redo

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || PreviewOverlay.Visibility == Visibility.Visible) return;
        if (_history.PopUndo() is not { } entry) return;
        if (await RevertAsync(entry, undo: true)) _history.PushRedo(entry);
        else _history.PushUndoKeepRedo(entry);
        UpdateState();
    }

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || PreviewOverlay.Visibility == Visibility.Visible) return;
        if (_history.PopRedo() is not { } entry) return;
        if (await RevertAsync(entry, undo: false)) _history.PushUndoKeepRedo(entry);
        else _history.PushRedo(entry);
        UpdateState();
    }

    /// <returns>True if the entry was applied successfully.</returns>
    private async Task<bool> RevertAsync(HistoryEntry entry, bool undo)
    {
        switch (entry)
        {
            case OrderEntry order:
                ApplyOrder(undo ? order.Before : order.After);
                ShowStatus(undo ? "已撤销" : "已重做", order.Description, InfoBarSeverity.Informational);
                return true;

            case RenameEntry rename:
                var plan = rename.Changes
                    .Select(c => undo ? (c.NewPath, c.OldPath) : (c.OldPath, c.NewPath))
                    .ToList();
                SetBusy(true);
                try
                {
                    await Task.Run(() => BatchRenamer.Rename(plan));
                    foreach (var c in rename.Changes) c.Item.Path = undo ? c.OldPath : c.NewPath;
                    ShowStatus(undo ? "已撤销" : "已重做", $"{rename.Description}（{rename.Changes.Count} 个文件）", InfoBarSeverity.Informational);
                    return true;
                }
                catch (Exception ex)
                {
                    ShowStatus(undo ? "撤销失败" : "重做失败", ex.Message, InfoBarSeverity.Error);
                    return false;
                }
                finally
                {
                    SetBusy(false);
                }
        }
        return false;
    }

    // ------------------------------------------------------------------ Rename

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Items.Count == 0) return;

        double start = double.IsNaN(StartBox.Value) ? 1 : StartBox.Value;
        int maxNumber = (int)start + Items.Count - 1;
        DigitsBox.Value = maxNumber.ToString().Length;
        StripOnRenameBox.IsChecked = false;
        UpdateRenamePreview();

        if (await RenameDialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (BuildRenamePlan(out _) is not { } plan) return;
        bool strip = StripOnRenameBox.IsChecked == true;

        var changes = plan
            .Where(p => !string.Equals(p.Item.Path, p.Target, StringComparison.Ordinal))
            .Select(p => (p.Item, OldPath: p.Item.Path, NewPath: p.Target))
            .ToList();

        SetBusy(true);
        try
        {
            if (changes.Count > 0)
            {
                await Task.Run(() => BatchRenamer.Rename(changes.Select(c => (c.OldPath, c.NewPath)).ToList()));
                foreach (var c in changes) c.Item.Path = c.NewPath;
                _history.Push(new RenameEntry("批量重命名", changes));
            }

            string message = changes.Count > 0 ? $"已重命名 {changes.Count} 个文件。" : "文件名均未变化。";
            if (strip)
            {
                var summary = await StripCoreAsync(Items.ToList());
                message += " " + summary.Message;
                ShowStatus("完成", message, summary.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus("完成", message, InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("重命名失败，已回滚", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private List<(ImageItem Item, string Target)>? BuildRenamePlan(out string? error)
    {
        error = null;
        string prefix = PrefixBox.Text ?? string.Empty;
        string suffix = SuffixBox.Text ?? string.Empty;
        var invalid = Path.GetInvalidFileNameChars();
        if (prefix.IndexOfAny(invalid) >= 0 || suffix.IndexOfAny(invalid) >= 0)
        {
            error = "前缀或后缀包含文件名不允许的字符。";
            return null;
        }
        if (_folder is null) return null;

        long start = double.IsNaN(StartBox.Value) ? 1 : (long)StartBox.Value;
        int digits = double.IsNaN(DigitsBox.Value) ? 1 : Math.Clamp((int)DigitsBox.Value, 1, 10);

        var plan = new List<(ImageItem, string)>(Items.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            string number = (start + i).ToString().PadLeft(digits, '0');
            string name = $"{prefix}{number}{suffix}{Path.GetExtension(item.Path)}";
            plan.Add((item, Path.Combine(Path.GetDirectoryName(item.Path)!, name)));
        }
        return plan;
    }

    private void UpdateRenamePreview()
    {
        var plan = BuildRenamePlan(out string? error);
        RenameErrorText.Text = error ?? string.Empty;
        RenameErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        RenameDialog.IsPrimaryButtonEnabled = plan is not null;
        if (plan is null)
        {
            RenamePreviewText.Text = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        void Line((ImageItem Item, string Target) p) =>
            sb.AppendLine($"{p.Item.FileName}  →  {Path.GetFileName(p.Target)}");

        int head = Math.Min(3, plan.Count);
        for (int i = 0; i < head; i++) Line(plan[i]);
        if (plan.Count > head + 1) sb.AppendLine("…");
        if (plan.Count > head) Line(plan[^1]);
        RenamePreviewText.Text = sb.ToString().TrimEnd();
    }

    private void RenameOptions_TextChanged(object sender, TextChangedEventArgs e) => UpdateRenamePreview();

    private void RenameOptions_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateRenamePreview();

    // ------------------------------------------------------------------ EXIF

    private async void StripExif_Click(object sender, RoutedEventArgs e)
    {
        var targets = ImageGrid.SelectedItems.Count > 0
            ? ImageGrid.SelectedItems.Cast<ImageItem>().ToList()
            : Items.ToList();
        await StripWithConfirmAsync(targets);
    }

    private async void MenuStripExif_Click(object sender, RoutedEventArgs e) => await StripWithConfirmAsync(GetContextTargets());

    private async Task StripWithConfirmAsync(List<ImageItem> targets)
    {
        if (_busy || targets.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "移除 EXIF 信息",
            Content = $"将移除 {targets.Count} 张图片中的 EXIF、XMP、GPS 等元数据（保留方向与色彩配置）。\n\n此操作会直接修改原文件，且无法撤销。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetBusy(true);
        try
        {
            var summary = await StripCoreAsync(targets);
            ShowStatus("移除 EXIF 完成", summary.Message, summary.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private sealed record StripSummary(int Stripped, int Clean, int Unsupported, int Failed, string? FirstError)
    {
        public string Message
        {
            get
            {
                var parts = new List<string> { $"已清理 {Stripped} 张" };
                if (Clean > 0) parts.Add($"{Clean} 张无需处理");
                if (Unsupported > 0) parts.Add($"{Unsupported} 张格式不支持（仅支持 JPEG / PNG / WebP）");
                if (Failed > 0) parts.Add($"{Failed} 张失败：{FirstError}");
                return string.Join("，", parts) + "。";
            }
        }
    }

    private static Task<StripSummary> StripCoreAsync(List<ImageItem> targets)
    {
        var paths = targets.Select(t => t.Path).ToList();
        return Task.Run(() =>
        {
            int stripped = 0, clean = 0, unsupported = 0, failed = 0;
            string? firstError = null;
            foreach (var path in paths)
            {
                try
                {
                    switch (MetadataStripper.Strip(path))
                    {
                        case StripResult.Stripped: stripped++; break;
                        case StripResult.AlreadyClean: clean++; break;
                        default: unsupported++; break;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    firstError ??= $"{Path.GetFileName(path)}：{ex.Message}";
                }
            }
            return new StripSummary(stripped, clean, unsupported, failed, firstError);
        });
    }

    // ------------------------------------------------------------------ Preview

    private void ImageGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ImageItem item) OpenPreview(item);
    }

    private void ImageGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && GetFocusedOrSelectedItem() is { } item)
        {
            OpenPreview(item);
            e.Handled = true;
        }
    }

    private ImageItem? GetFocusedOrSelectedItem()
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is GridViewItem container &&
            ImageGrid.ItemFromContainer(container) is ImageItem focused)
            return focused;
        return ImageGrid.SelectedItem as ImageItem ?? Items.FirstOrDefault();
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (GetFocusedOrSelectedItem() is { } item) OpenPreview(item);
    }

    private void MenuPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is not null) OpenPreview(_contextItem);
    }

    private void OpenPreview(ImageItem item)
    {
        int index = Items.IndexOf(item);
        if (index < 0) return;
        PreviewOverlay.Visibility = Visibility.Visible;
        _ = ShowPreviewAsync(index);
        ClosePreviewButton.Focus(FocusState.Programmatic);
    }

    private void ClosePreview()
    {
        if (PreviewOverlay.Visibility != Visibility.Visible) return;
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        _previewVersion++;
        if (_previewIndex >= 0 && _previewIndex < Items.Count)
        {
            var item = Items[_previewIndex];
            ImageGrid.ScrollIntoView(item);
            if (ImageGrid.ContainerFromItem(item) is GridViewItem container)
                container.Focus(FocusState.Programmatic);
        }
        _previewIndex = -1;
    }

    private async Task ShowPreviewAsync(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        _previewIndex = index;
        int version = ++_previewVersion;
        var item = Items[index];

        PreviewNameText.Text = item.FileName;
        PreviewCounterText.Text = $"{index + 1} / {Items.Count}";
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < Items.Count - 1;
        PreviewImage.Source = item.Thumbnail; // instant low-res placeholder

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(item.Path);
            if (version != _previewVersion) return;
            var bmp = new BitmapImage();
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            await bmp.SetSourceAsync(stream);
            if (version == _previewVersion) PreviewImage.Source = bmp;
        }
        catch
        {
            // Keep the thumbnail if full decode fails.
        }
    }

    private void StepPreview(int delta)
    {
        int target = Math.Clamp(_previewIndex + delta, 0, Items.Count - 1);
        if (target != _previewIndex) _ = ShowPreviewAsync(target);
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => StepPreview(-1);

    private void NextButton_Click(object sender, RoutedEventArgs e) => StepPreview(1);

    private void ClosePreview_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void PreviewOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PreviewOverlay)) ClosePreview();
    }

    private void PreviewOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(PreviewOverlay).Properties.MouseWheelDelta;
        StepPreview(delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (PreviewOverlay.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case VirtualKey.Left:
            case VirtualKey.Up:
            case VirtualKey.PageUp:
                StepPreview(-1);
                break;
            case VirtualKey.Right:
            case VirtualKey.Down:
            case VirtualKey.PageDown:
            case VirtualKey.Space:
                StepPreview(1);
                break;
            case VirtualKey.Home:
                StepPreview(-Items.Count);
                break;
            case VirtualKey.End:
                StepPreview(Items.Count);
                break;
            case VirtualKey.Escape:
            case VirtualKey.Enter:
                ClosePreview();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------ State

    private void ImageGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ImageGrid.CanReorderItems = !busy;
        ImageGrid.CanDragItems = !busy;
        UpdateState();
        if (!busy) StartThumbnailLoading();
    }

    private void UpdateState()
    {
        bool hasItems = Items.Count > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        OpenButton.IsEnabled = !_busy;
        SortButton.IsEnabled = !_busy && hasItems;
        PreviewButton.IsEnabled = hasItems;
        StripButton.IsEnabled = !_busy && hasItems;
        RenameButton.IsEnabled = !_busy && hasItems;
        UndoButton.IsEnabled = !_busy && _history.CanUndo;
        RedoButton.IsEnabled = !_busy && _history.CanRedo;
        ToolTipService.SetToolTip(UndoButton, _history.NextUndo is { } u ? $"撤销：{u.Description} (Ctrl+Z)" : "撤销 (Ctrl+Z)");
        ToolTipService.SetToolTip(RedoButton, _history.NextRedo is { } r ? $"重做：{r.Description} (Ctrl+Y)" : "重做 (Ctrl+Y)");

        if (_folder is null)
        {
            SummaryText.Text = "打开一个包含图片的文件夹开始";
            return;
        }
        int selected = ImageGrid.SelectedItems.Count;
        SummaryText.Text = selected > 0
            ? $"共 {Items.Count} 张 · 已选 {selected} 张 · 拖拽调整顺序"
            : $"共 {Items.Count} 张 · 拖拽调整顺序";
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
