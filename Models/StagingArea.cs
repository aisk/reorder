using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;

namespace Reorder.Models;

/// <summary>
/// A named holding area for images that are temporarily taken out of the main order.
/// </summary>
public sealed partial class StagingArea : INotifyPropertyChanged
{
    private string _name;

    public StagingArea(string name)
    {
        _name = name;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(EmptyVisibility));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ImageItem> Items { get; } = [];

    public string CountText => $"{Items.Count} 张";

    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
