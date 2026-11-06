using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Reorder.Models;
using Reorder.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.UI.Core;

namespace Reorder;

public sealed partial class MainPage : Page
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".gif", ".bmp", ".webp",
        ".tif", ".tiff", ".heic", ".heif", ".avif",
    };

    private const int ThumbnailSize = 360;
    private const int ThumbnailWorkers = 4;
    private const double ZoomStep = 16;
    private const double DefaultZoom = 148;
    private const string ZoomSettingKey = "ThumbnailZoom";

    private readonly History _history = new(capacity: 50);
    private readonly HashSet<ListViewBase> _stagingGrids = [];
    private string? _folder;
    private CancellationTokenSource? _thumbnailCts;
    private bool _suppressCollectionEvents;
    private bool _busy;
    private int _stagingCounter;

    /// <summary>Main order as it is on disk (after load or the last save).</summary>
    private List<ImageItem> _savedOrder = [];

    private ImageItem? _contextItem;
    private ListViewBase? _contextGrid;

    private ListViewBase? _dragSource;
    private List<ImageItem>? _dragItems;
    private LayoutSnapshot? _dragSnapshot;

    private IList<ImageItem>? _previewList;
    private int _previewIndex = -1;
    private int _previewVersion;

    public MainPage()
    {
        InitializeComponent();

        // Hooked up here rather than in XAML: setting initial values during
        // InitializeComponent would otherwise fire these before all controls exist.
        PrefixBox.TextChanged += (_, _) => UpdateSavePreview();
        SuffixBox.TextChanged += (_, _) => UpdateSavePreview();
        StartBox.ValueChanged += (_, _) => UpdateSavePreview();
        DigitsBox.ValueChanged += (_, _) => UpdateSavePreview();
        RenameOnSaveBox.Checked += (_, _) => UpdateSavePreview();
        RenameOnSaveBox.Unchecked += (_, _) => UpdateSavePreview();
        StripOnRenameBox.Checked += (_, _) => UpdateSavePreview();
        StripOnRenameBox.Unchecked += (_, _) => UpdateSavePreview();
        StagingModeBox.SelectionChanged += (_, _) => UpdateSavePreview();

        ZoomSlider.Value = LoadZoom();
        ZoomSlider.ValueChanged += (_, _) =>
        {
            ApplyZoom();
            SaveZoom();
        };
        AddZoomAccelerator((VirtualKey)187, ZoomStep);  // '=' / '+'
        AddZoomAccelerator(VirtualKey.Add, ZoomStep);
        AddZoomAccelerator((VirtualKey)189, -ZoomStep); // '-'
        AddZoomAccelerator(VirtualKey.Subtract, -ZoomStep);
        AddZoomAccelerator(VirtualKey.Number0, 0);
        AddZoomAccelerator(VirtualKey.NumberPad0, 0);

        Items.CollectionChanged += OnListChanged;
        StagingAreas.CollectionChanged += OnListChanged;

        RootGrid.AddHandler(DragOverEvent, new DragEventHandler(Root_DragOver), handledEventsToo: true);
        RootGrid.AddHandler(DropEvent, new DragEventHandler(Root_Drop), handledEventsToo: true);
        ImageGrid.AddHandler(DragOverEvent, new DragEventHandler(ItemGrid_DragOver), handledEventsToo: true);
        ImageGrid.AddHandler(DropEvent, new DragEventHandler(ItemGrid_Drop), handledEventsToo: true);
        UpdateState();
    }

    public ObservableCollection<ImageItem> Items { get; } = [];

    public ObservableCollection<StagingArea> StagingAreas { get; } = [];

    /// <summary>True when there are unsaved reorders, staged images or deletion marks.</summary>
    public bool HasPendingChanges =>
        _folder is not null && (AllItems().Any(i => i.IsMarkedForDeletion) || !Items.SequenceEqual(_savedOrder));

    private IEnumerable<ImageItem> StagedItems => StagingAreas.SelectMany(a => a.Items);

    private IEnumerable<ImageItem> AllItems() => Items.Concat(StagedItems);

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionEvents) return;
        Refresh();
    }

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
        if (_busy || _dragItems is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "打开此文件夹";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (_busy || _dragItems is not null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
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
        if (!await ConfirmDiscardAsync()) return;

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
        StagingAreas.Clear();
        _stagingCounter = 0;
        Items.Clear();
        foreach (var file in files) Items.Add(new ImageItem(file));
        _suppressCollectionEvents = false;
        _savedOrder = Items.ToList();
        Refresh();

        FolderNameText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        ToolTipService.SetToolTip(FolderNameText, folder);
        StatusBar.IsOpen = false;
        StartThumbnailLoading();
    }

    /// <returns>True if there is nothing to lose or the user agreed to discard pending changes.</returns>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasPendingChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "有未保存的更改",
            Content = "当前的排序、暂存区和删除标记尚未保存，确定要放弃吗？",
            PrimaryButtonText = "放弃更改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            // Another dialog is already open.
            return false;
        }
    }

    // ------------------------------------------------------------------ Thumbnails

    private void StartThumbnailLoading()
    {
        _thumbnailCts?.Cancel();
        var cts = _thumbnailCts = new CancellationTokenSource();
        var queue = AllItems().Where(i => i.Thumbnail is null).ToList();
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

    // ------------------------------------------------------------------ Zoom

    private void ImageGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (ImageGrid.ItemsPanelRoot is { } panel)
        {
            // The panel lives inside the ScrollViewer, so handling the wheel here
            // keeps Ctrl+wheel from also scrolling the list.
            panel.PointerWheelChanged -= ItemsPanel_PointerWheelChanged;
            panel.PointerWheelChanged += ItemsPanel_PointerWheelChanged;
        }
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (ImageGrid.ItemsPanelRoot is not ItemsWrapGrid panel) return;
        double size = ZoomSlider.Value;
        panel.ItemWidth = size + 16;
        panel.ItemHeight = size + 44;
    }

    private void Zoom(double delta)
    {
        double value = delta == 0 ? DefaultZoom : ZoomSlider.Value + delta;
        ZoomSlider.Value = Math.Clamp(value, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    private void ItemsPanel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!IsControlDown()) return;
        int delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        Zoom(delta > 0 ? ZoomStep : -ZoomStep);
        e.Handled = true;
    }

    private void AddZoomAccelerator(VirtualKey key, double delta)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        accelerator.Invoked += (_, args) =>
        {
            if (PreviewOverlay.Visibility == Visibility.Visible) return;
            Zoom(delta);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private static bool IsControlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);

    private static double LoadZoom()
    {
        try
        {
            if (ApplicationData.Current.LocalSettings.Values[ZoomSettingKey] is double value) return value;
        }
        catch
        {
            // No package identity (unpackaged run); settings are not persisted.
        }
        return DefaultZoom;
    }

    private void SaveZoom()
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[ZoomSettingKey] = ZoomSlider.Value;
        }
        catch
        {
            // See LoadZoom.
        }
    }

    // ------------------------------------------------------------------ Layout changes

    private void Refresh()
    {
        UpdatePositions();
        UpdateState();
    }

    private void UpdatePositions()
    {
        int n = 0;
        foreach (var item in Items) item.Position = item.IsMarkedForDeletion ? 0 : ++n;
        foreach (var item in StagedItems) item.Position = 0;
    }

    private LayoutSnapshot Capture() => new(
        Items.ToList(),
        StagingAreas.Select(a => (a, (IReadOnlyList<ImageItem>)a.Items.ToList())).ToList(),
        AllItems().Where(i => i.IsMarkedForDeletion).ToList());

    private void ApplyLayout(LayoutSnapshot layout)
    {
        _suppressCollectionEvents = true;
        try
        {
            Sync(Items, layout.Main);
            Sync(StagingAreas, layout.Areas.Select(a => a.Area).ToList());
            foreach (var (area, items) in layout.Areas) Sync(area.Items, items);
            var deleted = layout.Deleted.ToHashSet();
            foreach (var item in AllItems()) item.IsMarkedForDeletion = deleted.Contains(item);
        }
        finally
        {
            _suppressCollectionEvents = false;
        }
        Refresh();
    }

    /// <summary>Rearranges <paramref name="target"/> to equal <paramref name="desired"/> with minimal churn.</summary>
    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var keep = desired.ToHashSet();
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        }
        for (int i = 0; i < desired.Count; i++)
        {
            int current = target.IndexOf(desired[i]);
            if (current == i) continue;
            if (current >= 0) target.Move(current, i);
            else target.Insert(i, desired[i]);
        }
    }

    /// <summary>Performs a layout change and records it in history.</summary>
    private void Mutate(string description, Action change)
    {
        var before = Capture();
        _suppressCollectionEvents = true;
        try
        {
            change();
        }
        finally
        {
            _suppressCollectionEvents = false;
        }
        Commit(description, before);
    }

    private void Commit(string description, LayoutSnapshot before)
    {
        var after = Capture();
        if (!before.SameAs(after)) _history.Push(new LayoutEntry(description, before, after));
        Refresh();
    }

    private ObservableCollection<ImageItem>? FindOwner(ImageItem item) =>
        Items.Contains(item) ? Items : StagingAreas.FirstOrDefault(a => a.Items.Contains(item))?.Items;

    /// <summary>
    /// Moves <paramref name="items"/> (from wherever they are) into <paramref name="target"/>
    /// at <paramref name="index"/>, which is relative to the target before the move.
    /// </summary>
    private void MoveItems(IReadOnlyList<ImageItem> items, ObservableCollection<ImageItem> target, int index)
    {
        foreach (var item in items)
        {
            var owner = FindOwner(item);
            if (owner is null) continue;
            int current = owner.IndexOf(item);
            if (ReferenceEquals(owner, target) && current < index) index--;
            owner.RemoveAt(current);
        }
        index = Math.Clamp(index, 0, target.Count);
        foreach (var item in items) target.Insert(index++, item);
    }

    private void SortByName_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var sorted = Items.OrderBy(i => i.FileName, NaturalComparer.Instance).ToList();
        Mutate("按文件名排序", () => Sync(Items, sorted));
    }

    private void MoveToStart_Click(object sender, RoutedEventArgs e) => MoveToMain(GetContextTargets(), toStart: true);

    private void MoveToEnd_Click(object sender, RoutedEventArgs e) => MoveToMain(GetContextTargets(), toStart: false);

    private void MoveToMain(List<ImageItem> targets, bool toStart)
    {
        if (targets.Count == 0 || _busy) return;
        Mutate(toStart ? "移到最前" : "移到最后", () => MoveItems(targets, Items, toStart ? 0 : int.MaxValue));
        ImageGrid.ScrollIntoView(targets[0]);
    }

    // ------------------------------------------------------------------ Drag and drop

    private void AnyGrid_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var grid = (ListViewBase)sender;
        var source = grid.ItemsSource as IList<ImageItem>;
        _dragSource = grid;
        _dragItems = e.Items.Cast<ImageItem>().OrderBy(i => source?.IndexOf(i) ?? 0).ToList();
        _dragSnapshot = Capture();
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void AnyGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var before = _dragSnapshot;
        _dragSource = null;
        _dragItems = null;
        _dragSnapshot = null;
        // Reorders within one grid are applied by the control itself; record them here.
        if (before is not null) Commit("拖拽调整", before);
    }

    /// <summary>True for an in-app drag coming from a different grid than <paramref name="target"/>.</summary>
    private bool IsForeignItemDrag(object target) =>
        !_busy && _dragItems is not null && !ReferenceEquals(_dragSource, target);

    private void ItemGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!IsForeignItemDrag(sender)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = ReferenceEquals(sender, ImageGrid)
            ? "放回列表"
            : $"移到「{((sender as FrameworkElement)?.DataContext as StagingArea)?.Name}」";
        e.Handled = true;
    }

    private void ItemGrid_Drop(object sender, DragEventArgs e)
    {
        if (!IsForeignItemDrag(sender) || _dragItems is not { } items || _dragSnapshot is not { } before) return;
        var grid = (ListViewBase)sender;
        if (grid.ItemsSource is not ObservableCollection<ImageItem> target) return;

        int index = GetInsertIndex(grid, target.Count, e.GetPosition(grid));
        _dragSnapshot = null;
        _suppressCollectionEvents = true;
        try
        {
            MoveItems(items, target, index);
        }
        finally
        {
            _suppressCollectionEvents = false;
        }
        Commit(ReferenceEquals(grid, ImageGrid) ? "放回列表" : "移到暂存区", before);
        e.Handled = true;
    }

    /// <summary>Finds the insertion index closest to <paramref name="point"/> among realized containers.</summary>
    private static int GetInsertIndex(ListViewBase grid, int count, Point point)
    {
        int best = -1;
        bool after = false;
        double bestScore = double.MaxValue;
        for (int i = 0; i < count; i++)
        {
            if (grid.ContainerFromIndex(i) is not FrameworkElement container) continue;
            var r = container.TransformToVisual(grid).TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));
            double dy = point.Y < r.Top ? r.Top - point.Y : point.Y > r.Bottom ? point.Y - r.Bottom : 0;
            double centerX = r.X + r.Width / 2;
            // Prefer the row under the pointer, then the closest column within it.
            double score = dy * 100000 + Math.Abs(point.X - centerX);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                after = point.X > centerX;
            }
        }
        return best < 0 ? count : best + (after ? 1 : 0);
    }

    private void StagingGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var grid = (ListViewBase)sender;
        if (!_stagingGrids.Add(grid)) return;
        grid.AddHandler(DragOverEvent, new DragEventHandler(ItemGrid_DragOver), handledEventsToo: true);
        grid.AddHandler(DropEvent, new DragEventHandler(ItemGrid_Drop), handledEventsToo: true);
    }

    private void StagingGrid_Unloaded(object sender, RoutedEventArgs e)
    {
        var grid = (ListViewBase)sender;
        if (!_stagingGrids.Remove(grid)) return;
        grid.RemoveHandler(DragOverEvent, new DragEventHandler(ItemGrid_DragOver));
        grid.RemoveHandler(DropEvent, new DragEventHandler(ItemGrid_Drop));
    }

    private void NewStagingDropZone_DragOver(object sender, DragEventArgs e)
    {
        if (_busy || _dragItems is null) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = "新建暂存区";
        e.Handled = true;
    }

    private void NewStagingDropZone_Drop(object sender, DragEventArgs e)
    {
        if (_busy || _dragItems is not { } items || _dragSnapshot is not { } before) return;
        _dragSnapshot = null;
        _suppressCollectionEvents = true;
        try
        {
            MoveItems(items, CreateArea().Items, 0);
        }
        finally
        {
            _suppressCollectionEvents = false;
        }
        Commit("新建暂存区", before);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ Staging areas

    private StagingArea CreateArea()
    {
        var area = new StagingArea($"暂存区 {++_stagingCounter}");
        area.Items.CollectionChanged += OnListChanged;
        StagingAreas.Add(area);
        return area;
    }

    private void AddStaging_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Mutate("新建暂存区", () => CreateArea());
    }

    private void MoveToStaging(List<ImageItem> targets, StagingArea? area)
    {
        if (targets.Count == 0 || _busy) return;
        Mutate("移到暂存区", () => MoveItems(targets, (area ?? CreateArea()).Items, int.MaxValue));
        ShowStagingPanel(true);
    }

    private void StagingRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.Tag is not StagingArea area) return;
        Mutate("全部放回", () => MoveItems(area.Items.ToList(), Items, int.MaxValue));
    }

    private void StagingRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.Tag is not StagingArea area) return;
        Mutate("移除暂存区", () =>
        {
            MoveItems(area.Items.ToList(), Items, int.MaxValue);
            StagingAreas.Remove(area);
        });
    }

    private void StagingToggle_Click(object sender, RoutedEventArgs e) => ShowStagingPanel(StagingToggle.IsChecked == true);

    private void ShowStagingPanel(bool show)
    {
        StagingToggle.IsChecked = show;
        StagingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ Context menu & selection

    private void ItemMenu_Opening(object sender, object e)
    {
        var menu = (MenuFlyout)sender;
        _contextItem = menu.Target?.DataContext as ImageItem;
        _contextGrid = FindAncestor<ListViewBase>(menu.Target);
        var targets = GetContextTargets();

        foreach (var item in menu.Items)
        {
            if (item is MenuFlyoutItem { Tag: "DeleteItem" } deleteItem)
            {
                deleteItem.Text = targets.Count > 0 && targets.All(t => t.IsMarkedForDeletion) ? "取消删除标记" : "标记删除";
            }
            else if (item is MenuFlyoutSubItem staging)
            {
                staging.Items.Clear();
                foreach (var area in StagingAreas)
                {
                    var areaItem = new MenuFlyoutItem
                    {
                        Text = area.Name,
                        IsEnabled = !targets.All(area.Items.Contains),
                    };
                    areaItem.Click += (_, _) => MoveToStaging(targets, area);
                    staging.Items.Add(areaItem);
                }
                if (StagingAreas.Count > 0) staging.Items.Add(new MenuFlyoutSeparator());
                var create = new MenuFlyoutItem { Text = "新建暂存区", Icon = new FontIcon { Glyph = "" } };
                create.Click += (_, _) => MoveToStaging(targets, null);
                staging.Items.Add(create);
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T) element = VisualTreeHelper.GetParent(element);
        return element as T;
    }

    /// <summary>Items targeted by a context-menu command: the selection if the item is part of it.</summary>
    private List<ImageItem> GetContextTargets()
    {
        if (_contextItem is null) return [];
        if (_contextGrid is not null && _contextGrid.SelectedItems.Contains(_contextItem))
            return OrderedSelection(_contextGrid);
        return [_contextItem];
    }

    private static List<ImageItem> OrderedSelection(ListViewBase grid)
    {
        var source = grid.ItemsSource as IList<ImageItem>;
        return grid.SelectedItems.Cast<ImageItem>().OrderBy(i => source?.IndexOf(i) ?? 0).ToList();
    }

    /// <summary>Selected items across the main grid and all staging areas.</summary>
    private List<ImageItem> AllSelected() =>
        OrderedSelection(ImageGrid).Concat(_stagingGrids.SelectMany(OrderedSelection)).ToList();

    private void AnyGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var grid = (ListViewBase)sender;
        switch (e.Key)
        {
            case VirtualKey.Enter when GetFocusedOrSelectedItem(grid) is { } item:
                OpenPreview(item);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                var targets = OrderedSelection(grid);
                if (targets.Count == 0 && GetFocusedOrSelectedItem(grid) is { } focused) targets = [focused];
                ToggleDeletion(targets);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------------ Deletion marks

    private void ToggleDelete_Click(object sender, RoutedEventArgs e) => ToggleDeletion(AllSelected());

    private void MenuToggleDelete_Click(object sender, RoutedEventArgs e) => ToggleDeletion(GetContextTargets());

    private void ToggleDeletion(List<ImageItem> targets)
    {
        if (targets.Count == 0 || _busy) return;
        bool mark = !targets.All(t => t.IsMarkedForDeletion);
        Mutate(mark ? "标记删除" : "取消删除标记", () =>
        {
            foreach (var t in targets) t.IsMarkedForDeletion = mark;
        });
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
            case LayoutEntry layout:
                ApplyLayout(undo ? layout.Before : layout.After);
                ShowStatus(undo ? "已撤销" : "已重做", layout.Description, InfoBarSeverity.Informational);
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

    // ------------------------------------------------------------------ Save

    private bool RenameOnSave => RenameOnSaveBox.IsChecked == true;

    private bool AppendStaged => StagingModeBox.SelectedIndex == 0;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _folder is null || !AllItems().Any()) return;
        ClosePreview();

        double start = double.IsNaN(StartBox.Value) ? 1 : StartBox.Value;
        int maxNumber = (int)start + Math.Max(1, AllItems().Count(i => !i.IsMarkedForDeletion)) - 1;
        DigitsBox.Value = maxNumber.ToString().Length;
        StripOnRenameBox.IsChecked = false;
        StagingModeBox.Visibility = StagedItems.Any(i => !i.IsMarkedForDeletion) ? Visibility.Visible : Visibility.Collapsed;
        UpdateSavePreview();

        if (await SaveDialog.ShowAsync() != ContentDialogResult.Primary) return;
        await SaveAsync();
    }

    private List<ImageItem> NumberedItems()
    {
        var items = Items.Where(i => !i.IsMarkedForDeletion);
        if (AppendStaged) items = items.Concat(StagedItems.Where(i => !i.IsMarkedForDeletion));
        return items.ToList();
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

        var numbered = NumberedItems();
        var plan = new List<(ImageItem, string)>(numbered.Count);
        for (int i = 0; i < numbered.Count; i++)
        {
            var item = numbered[i];
            string number = (start + i).ToString().PadLeft(digits, '0');
            string name = $"{prefix}{number}{suffix}{Path.GetExtension(item.Path)}";
            plan.Add((item, Path.Combine(Path.GetDirectoryName(item.Path)!, name)));
        }

        if (!AppendStaged)
        {
            var targets = plan.Select(p => p.Item2).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (StagedItems.FirstOrDefault(i => !i.IsMarkedForDeletion && targets.Contains(i.Path)) is { } clash)
            {
                error = $"暂存区中的 {clash.FileName} 与新文件名冲突，请改为追加到末尾或调整前缀 / 起始序号。";
                return null;
            }
        }
        return plan;
    }

    private void UpdateSavePreview()
    {
        if (SaveDialog is null) return;
        bool rename = RenameOnSave;
        RenameOptionsPanel.Visibility = rename ? Visibility.Visible : Visibility.Collapsed;

        string? error = null;
        int renameCount = 0;
        if (rename)
        {
            var plan = BuildRenamePlan(out error);
            if (plan is null)
            {
                RenamePreviewText.Text = string.Empty;
            }
            else
            {
                renameCount = plan.Count(p => !string.Equals(p.Item.Path, p.Target, StringComparison.Ordinal));
                var sb = new StringBuilder();
                void Line((ImageItem Item, string Target) p) =>
                    sb.AppendLine($"{p.Item.FileName}  →  {Path.GetFileName(p.Target)}");

                int head = Math.Min(3, plan.Count);
                for (int i = 0; i < head; i++) Line(plan[i]);
                if (plan.Count > head + 1) sb.AppendLine("…");
                if (plan.Count > head) Line(plan[^1]);
                RenamePreviewText.Text = plan.Count == 0 ? "（没有要命名的图片）" : sb.ToString().TrimEnd();
            }
        }

        int deleteCount = AllItems().Count(i => i.IsMarkedForDeletion);
        var lines = new List<string>();
        if (deleteCount > 0) lines.Add($"将把 {deleteCount} 个标记删除的文件移到回收站（之后无法在本应用中撤销）。");
        if (rename && error is null) lines.Add(renameCount > 0 ? $"将重命名 {renameCount} 个文件。" : "文件名均无需变化。");
        if (!rename && !Items.SequenceEqual(_savedOrder)) lines.Add("不重命名时，当前排序与暂存区不会被保存。");
        SaveSummaryText.Text = string.Join("\n", lines);

        RenameErrorText.Text = error ?? string.Empty;
        RenameErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        bool strip = rename && StripOnRenameBox.IsChecked == true;
        SaveDialog.IsPrimaryButtonEnabled = error is null && (deleteCount > 0 || renameCount > 0 || strip);
    }

    private async Task SaveAsync()
    {
        bool rename = RenameOnSave;
        bool strip = rename && StripOnRenameBox.IsChecked == true;
        List<(ImageItem Item, string Target)>? plan = null;
        if (rename)
        {
            plan = BuildRenamePlan(out string? error);
            if (plan is null)
            {
                ShowStatus("无法保存", error ?? string.Empty, InfoBarSeverity.Error);
                return;
            }
        }

        var toDelete = AllItems().Where(i => i.IsMarkedForDeletion).ToList();
        if (plan is not null && FindDiskConflict(plan, toDelete) is { } conflict)
        {
            ShowStatus("无法保存", $"目标文件已存在且不在列表中：{conflict}", InfoBarSeverity.Error);
            return;
        }

        var messages = new List<string>();
        var severity = InfoBarSeverity.Success;
        SetBusy(true);
        try
        {
            if (toDelete.Count > 0)
            {
                var (deleted, failed, firstError) = await DeleteFilesAsync(toDelete);
                if (deleted.Count > 0)
                {
                    RemoveFromLists(deleted);
                    var gone = deleted.ToHashSet();
                    _savedOrder = _savedOrder.Where(i => !gone.Contains(i)).ToList();
                    // Older layout entries still reference deleted files.
                    _history.Clear();
                    messages.Add($"已将 {deleted.Count} 个文件移到回收站");
                }
                if (failed > 0)
                {
                    ShowStatus("部分文件删除失败，未进行重命名", $"{failed} 个文件无法删除：{firstError}", InfoBarSeverity.Error);
                    return;
                }
            }

            if (plan is not null)
            {
                if (AppendStaged && StagedItems.Any())
                {
                    Mutate("暂存区追加到末尾", () => MoveItems(StagedItems.ToList(), Items, int.MaxValue));
                }

                var changes = plan
                    .Where(p => !string.Equals(p.Item.Path, p.Target, StringComparison.Ordinal))
                    .Select(p => (p.Item, OldPath: p.Item.Path, NewPath: p.Target))
                    .ToList();
                if (changes.Count > 0)
                {
                    await Task.Run(() => BatchRenamer.Rename(changes.Select(c => (c.OldPath, c.NewPath)).ToList()));
                    foreach (var c in changes) c.Item.Path = c.NewPath;
                    _history.Push(new RenameEntry("保存重命名", changes));
                }
                _savedOrder = Items.ToList();
                messages.Add(changes.Count > 0 ? $"已重命名 {changes.Count} 个文件" : "文件名均未变化");

                if (strip)
                {
                    var summary = await StripCoreAsync(plan.Select(p => p.Item).ToList());
                    messages.Add(summary.Message.TrimEnd('。'));
                    if (summary.Failed > 0) severity = InfoBarSeverity.Warning;
                }
            }

            ShowStatus("已保存", string.Join("；", messages) + "。", severity);
        }
        catch (Exception ex)
        {
            var prefix = messages.Count > 0 ? string.Join("；", messages) + "。" : string.Empty;
            ShowStatus("重命名失败，已回滚", prefix + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Returns the name of a planned target that is occupied by a file outside the plan.</summary>
    private static string? FindDiskConflict(List<(ImageItem Item, string Target)> plan, List<ImageItem> toDelete)
    {
        var freed = new HashSet<string>(plan.Select(p => p.Item.Path).Concat(toDelete.Select(i => i.Path)), StringComparer.OrdinalIgnoreCase);
        foreach (var (_, target) in plan)
        {
            if (!freed.Contains(target) && File.Exists(target)) return Path.GetFileName(target);
        }
        return null;
    }

    private static async Task<(List<ImageItem> Deleted, int Failed, string? FirstError)> DeleteFilesAsync(List<ImageItem> items)
    {
        var deleted = new List<ImageItem>();
        int failed = 0;
        string? firstError = null;
        foreach (var item in items)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Path);
                await file.DeleteAsync(StorageDeleteOption.Default); // recycle bin when available
                deleted.Add(item);
            }
            catch (FileNotFoundException)
            {
                deleted.Add(item);
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= $"{item.FileName}：{ex.Message}";
            }
        }
        return (deleted, failed, firstError);
    }

    private void RemoveFromLists(IEnumerable<ImageItem> items)
    {
        _suppressCollectionEvents = true;
        try
        {
            foreach (var item in items) FindOwner(item)?.Remove(item);
        }
        finally
        {
            _suppressCollectionEvents = false;
        }
        Refresh();
    }

    // ------------------------------------------------------------------ EXIF

    private async void StripExif_Click(object sender, RoutedEventArgs e)
    {
        var selected = AllSelected();
        await StripWithConfirmAsync(selected.Count > 0 ? selected : AllItems().ToList());
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

    private void AnyGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ImageItem item) OpenPreview(item);
    }

    private ImageItem? GetFocusedOrSelectedItem(ListViewBase grid)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is SelectorItem container &&
            ItemsControl.ItemsControlFromItemContainer(container) == grid &&
            grid.ItemFromContainer(container) is ImageItem focused)
            return focused;
        return grid.SelectedItem as ImageItem ?? (grid.ItemsSource as IList<ImageItem>)?.FirstOrDefault();
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        var item = (FocusManager.GetFocusedElement(XamlRoot) as SelectorItem)?.Content as ImageItem
            ?? AllSelected().FirstOrDefault()
            ?? Items.FirstOrDefault();
        if (item is not null) OpenPreview(item);
    }

    private void MenuPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is not null) OpenPreview(_contextItem);
    }

    private void OpenPreview(ImageItem item)
    {
        if (FindOwner(item) is not { } list) return;
        _previewList = list;
        PreviewOverlay.Visibility = Visibility.Visible;
        _ = ShowPreviewAsync(list.IndexOf(item));
        ClosePreviewButton.Focus(FocusState.Programmatic);
    }

    private void ClosePreview()
    {
        if (PreviewOverlay.Visibility != Visibility.Visible) return;
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        _previewVersion++;
        if (ReferenceEquals(_previewList, Items) && _previewIndex >= 0 && _previewIndex < Items.Count)
        {
            var item = Items[_previewIndex];
            ImageGrid.ScrollIntoView(item);
            if (ImageGrid.ContainerFromItem(item) is GridViewItem container)
                container.Focus(FocusState.Programmatic);
        }
        _previewIndex = -1;
        _previewList = null;
    }

    private async Task ShowPreviewAsync(int index)
    {
        if (_previewList is not { } list || index < 0 || index >= list.Count) return;
        _previewIndex = index;
        int version = ++_previewVersion;
        var item = list[index];

        PreviewNameText.Text = item.IsMarkedForDeletion ? $"{item.FileName}（待删除）" : item.FileName;
        PreviewCounterText.Text = $"{index + 1} / {list.Count}";
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < list.Count - 1;
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
        if (_previewList is not { Count: > 0 } list) return;
        int target = Math.Clamp(_previewIndex + delta, 0, list.Count - 1);
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
        int count = _previewList?.Count ?? 0;
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
                StepPreview(-count);
                break;
            case VirtualKey.End:
                StepPreview(count);
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
        foreach (var grid in _stagingGrids.Append(ImageGrid))
        {
            grid.CanReorderItems = !busy;
            grid.CanDragItems = !busy;
        }
        UpdateState();
        if (!busy) StartThumbnailLoading();
    }

    private void UpdateState()
    {
        int stagedCount = StagedItems.Count();
        bool hasItems = Items.Count > 0 || stagedCount > 0;
        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        OpenButton.IsEnabled = !_busy;
        SortButton.IsEnabled = !_busy && Items.Count > 0;
        PreviewButton.IsEnabled = hasItems;
        DeleteButton.IsEnabled = !_busy && hasItems;
        StripButton.IsEnabled = !_busy && hasItems;
        SaveButton.IsEnabled = !_busy && hasItems;
        AddStagingButton.IsEnabled = !_busy && _folder is not null;
        UndoButton.IsEnabled = !_busy && _history.CanUndo;
        RedoButton.IsEnabled = !_busy && _history.CanRedo;
        ToolTipService.SetToolTip(UndoButton, _history.NextUndo is { } u ? $"撤销：{u.Description} (Ctrl+Z)" : "撤销 (Ctrl+Z)");
        ToolTipService.SetToolTip(RedoButton, _history.NextRedo is { } r ? $"重做：{r.Description} (Ctrl+Y)" : "重做 (Ctrl+Y)");

        if (_folder is null)
        {
            SummaryText.Text = "打开一个包含图片的文件夹开始";
            return;
        }
        var parts = new List<string> { $"列表 {Items.Count} 张" };
        if (stagedCount > 0) parts.Add($"暂存 {stagedCount} 张");
        int deleted = AllItems().Count(i => i.IsMarkedForDeletion);
        if (deleted > 0) parts.Add($"待删除 {deleted} 张");
        int selected = ImageGrid.SelectedItems.Count;
        if (selected > 0) parts.Add($"已选 {selected} 张");
        parts.Add(HasPendingChanges ? "有未保存的更改" : "拖拽调整顺序");
        SummaryText.Text = string.Join(" · ", parts);
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
