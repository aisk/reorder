using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace Reorder.Models;

/// <summary>
/// A single image file shown in the grid.
/// </summary>
public sealed partial class ImageItem : INotifyPropertyChanged
{
    private string _path;
    private int _position;
    private ImageSource? _thumbnail;

    public ImageItem(string path)
    {
        _path = path;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Full path of the file on disk.</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (_path == value) return;
            _path = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileName));
        }
    }

    public string FileName => System.IO.Path.GetFileName(_path);

    /// <summary>1-based position in the current order.</summary>
    public int Position
    {
        get => _position;
        set
        {
            if (_position == value) return;
            _position = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionText));
        }
    }

    public string PositionText => _position.ToString();

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
