using System.Windows;

namespace MyNotes.Desktop.Services;

public class ThemeManager
{
    private static ThemeManager? _instance;
    public static ThemeManager Instance => _instance ??= new ThemeManager();

    public bool IsDarkMode { get; private set; } = true;

    public void ApplyTheme(bool dark)
    {
        IsDarkMode = dark;
        var app = Application.Current;
        var mergedDicts = app.Resources.MergedDictionaries;
        mergedDicts.Clear();

        var themeUri = dark
            ? new Uri("Themes/DarkTheme.xaml", UriKind.Relative)
            : new Uri("Themes/LightTheme.xaml", UriKind.Relative);

        mergedDicts.Add(new ResourceDictionary { Source = themeUri });
    }

    public void ToggleTheme()
    {
        ApplyTheme(!IsDarkMode);
    }
}
