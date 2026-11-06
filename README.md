<img src="Assets/AppIcon.svg" width="96" align="right" alt="">

# Reorder

A small Windows tool for reordering the images in a folder and batch-renaming them in the new order. Built with WinUI 3.

## Features

- Drag and drop to reorder images, with multi-select
- Adjustable thumbnail size (slider, Ctrl+mouse wheel, Ctrl+Plus / Ctrl+Minus)
- Multiple staging areas to temporarily take images out of the list and put them back anywhere later
- Mark images for deletion (Delete key); they are moved to the Recycle Bin on save
- All changes stay pending until you click **Save** (Ctrl+S), which deletes the marked files and renames the rest in the current order (configurable prefix, suffix, start number and digits)
- Undo / redo (Ctrl+Z / Ctrl+Y)
- Strip EXIF and other metadata (JPEG / PNG / WebP)
- Full-size preview (Enter or double-click, arrow keys to navigate)

## Build and run

Requires Windows 10 1809 or later and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run                                   # Debug
dotnet run -c Release -p:Platform=x64        # Release (trimmed)
```

You can also open `Reorder.csproj` in Visual Studio and run it from there.

## Icon

The icon source is `Assets/AppIcon.svg` (16–24 px sizes use the simplified `Assets/AppIcon.small.svg`). After editing, regenerate the PNG assets and `AppIcon.ico` with (requires Microsoft Edge):

```powershell
pwsh tools/Generate-Icons.ps1
```

## License

[GPL-3.0](LICENSE)
