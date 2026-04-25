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
