using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RobloxAccountManager.Mvvm;

public class BoolToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility vis && vis == Visibility.Visible ? !Invert : Invert;
}

public class NullToVis : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        if (Invert) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b && !b;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b && !b;
}

public class HexToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)value)); }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class CountToVis : IValueConverter
{
    // Visible when count == 0 (for empty-state overlays)
    public bool WhenZero { get; set; } = true;
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int n = value is int i ? i : 0;
        bool show = WhenZero ? n == 0 : n > 0;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class UrlToImage : IValueConverter
{
    // Decode small (avatars/icons render tiny) and cache per URL so scrolling / re-render never
    // re-downloads or re-decodes. Frozen images are shareable and cheap for the GC.
    private static readonly Dictionary<string, BitmapImage> Cache = new();
    private const int DecodeWidth = 160;

    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not string url || string.IsNullOrEmpty(url)) return null;
        if (Cache.TryGetValue(url, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.UriSource = new Uri(url);
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            if (Cache.Count > 512) Cache.Clear();   // simple bound
            Cache[url] = bmp;
            return bmp;
        }
        catch { return null; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Resolves a resource key string (e.g. "Icon.Accounts") to its Geometry resource.</summary>
public class IconKeyConverter : IValueConverter
{
    public object? Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is string key && Application.Current.TryFindResource(key) is Geometry g) return g;
        return null;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Visible when the bound int equals the ConverterParameter int.</summary>
public class IndexToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int v = value is int i ? i : -1;
        int target = p != null && int.TryParse(p.ToString(), out var tp) ? tp : -2;
        return v == target ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>True when the bound int equals the ConverterParameter int (for nav highlight).</summary>
public class IndexToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int v = value is int i ? i : -1;
        int target = p != null && int.TryParse(p.ToString(), out var tp) ? tp : -2;
        return v == target;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>MultiBinding [string text, bool mask] -> masked string when mask is true.</summary>
public class MaskConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object p, CultureInfo c)
    {
        string text = values.Length > 0 && values[0] is string s ? s : "";
        bool mask = values.Length > 1 && values[1] is bool b && b;
        if (mask && !string.IsNullOrEmpty(text)) return new string('•', Math.Min(10, Math.Max(6, text.Length)));
        return text;
    }
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => Array.Empty<object>();
}

/// <summary>Multiplies a 0..1 fill value by the bound bar width (passed as parameter).</summary>
public class FillToWidth : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        double fill = value is double d ? d : 0;
        double max = p != null && double.TryParse(p.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var m) ? m : 60;
        return fill * max;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
