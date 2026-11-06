using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Reorder.Models;

/// <summary>
/// A single image file shown in the grid or a staging area.
/// </summary>
public sealed partial class ImageItem : INotifyPropertyChanged
{
    private string _path;
    private int _position;
    private bool _isMarkedForDeletion;
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

    /// <summary>1-based position in the pending order, or 0 when not numbered (staged or marked for deletion).</summary>
    public int Position
    {
        get => _position;
        set
        {
            if (_position == value) return;
            _position = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PositionText));
            OnPropertyChanged(nameof(PositionVisibility));
        }
    }

    public string PositionText => _position.ToString();

    public Visibility PositionVisibility => _position > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>When true the file is deleted (sent to the recycle bin) on save.</summary>
    public bool IsMarkedForDeletion
    {
        get => _isMarkedForDeletion;
        set
        {
            if (_isMarkedForDeletion == value) return;
            _isMarkedForDeletion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DeleteMarkVisibility));
            OnPropertyChanged(nameof(ContentOpacity));
        }
    }

    public Visibility DeleteMarkVisibility => _isMarkedForDeletion ? Visibility.Visible : Visibility.Collapsed;

    public double ContentOpacity => _isMarkedForDeletion ? 0.35 : 1.0;

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
