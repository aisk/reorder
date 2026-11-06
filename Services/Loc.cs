using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Reorder.Services;

/// <summary>
/// Looks up localized strings from Strings/&lt;language&gt;/Resources.resw.
/// </summary>
public static class Loc
{
    private static readonly ResourceLoader Loader = new();

    public static string Get(string key)
    {
        string value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
