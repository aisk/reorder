using System.Runtime.InteropServices;

namespace Reorder.Services;

/// <summary>
/// Compares strings the same way Windows Explorer sorts file names
/// ("img2" before "img10").
/// </summary>
public sealed partial class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
