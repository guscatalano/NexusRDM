using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using NexusRDM.Core.Models;

namespace NexusRDM.Converters;

/// <summary>Flips a bool — used to disable controls while IsBusy is true.</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)     => value is true ? false : true;
    public object ConvertBack(object value, Type t, object p, string l) => value is true ? false : true;
}

/// <summary>bool IsDirectory → Segoe Fluent glyph. Used by the SFTP
/// file manager rows since x:Bind doesn't allow inline string literals
/// in a ternary expression.</summary>
/// <summary>long bytes -> human size ("12.3 KB", "1.2 MB", "-" for
/// 0/negative which we use as the "size N/A" sentinel for directory
/// rows). Used by the SFTP file manager so the Size column renders
/// something readable instead of raw bytes (and directories show a
/// dash instead of "0").</summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        long n = value switch
        {
            long l1 => l1,
            int  i1 => i1,
            _       => 0,
        };
        if (n <= 0)                  return "—"; // em dash
        if (n < 1024)                return $"{n} B";
        if (n < 1024 * 1024)         return $"{n / 1024.0:F1} KB";
        if (n < 1024L * 1024 * 1024) return $"{n / (1024.0 * 1024):F1} MB";
        return $"{n / (1024.0 * 1024 * 1024):F2} GB";
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
}

public sealed class DirectoryGlyphConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        value is true ? "" /* FilesFolder */ : "" /* Document */;
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
}

/// <summary>string.Length > 0 → true. Drives InfoBar.IsOpen from an error string.</summary>
public sealed class NonZeroToBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)     => value is int i && i > 0;
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
}

/// <summary>ConnectionProtocol.Ssh → Visible, anything else → Collapsed.</summary>
public sealed class SshVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        value is ConnectionProtocol.Ssh ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
}

/// <summary>ConnectionProtocol.Rdp → Visible, anything else → Collapsed.</summary>
public sealed class RdpVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l) =>
        value is ConnectionProtocol.Rdp ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
}

/// <summary>
/// SshAuthMethod.PrivateKey → Visible (so the key-path box shows for key auth).
/// Pass ConverterParameter="Invert" to flip — used to hide the password box
/// when key auth is selected.
/// </summary>
public sealed class PrivKeyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, string l)
    {
        bool isKey = value is SshAuthMethod.PrivateKey;
        bool invert = p is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        return (isKey ^ invert) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
}
