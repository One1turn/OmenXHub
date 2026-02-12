using Microsoft.Win32;
using System;
using System.Windows;
using System.Windows.Media;

namespace OmenSuperHub.Services {
  public static class ThemeService {
    public static void Initialize() {
      try {
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        UpdateTheme();
      } catch (Exception ex) {
        Console.WriteLine("ThemeService Init Failed: " + ex.Message);
      }
    }

    private static void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) {
      if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color) {
        UpdateTheme();
      }
    }

    public static void UpdateTheme() {
      Application.Current.Dispatcher.Invoke(() => {
        try {
          // 1. Detect Light/Dark Mode
          bool isLight = false;
          try {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
              if (key != null) {
                object val = key.GetValue("AppsUseLightTheme");
                if (val is int i && i > 0) isLight = true;
              }
            }
          } catch { }

          // 2. Apply Monochrome Theme (Just swap dictionary)
          ApplyTheme(isLight);
        } catch { }
      });
    }

    private static void ApplyTheme(bool isLightTheme) {
      var dicts = Application.Current.Resources.MergedDictionaries;
      ResourceDictionary colorDict = null;

      // Find existing Colors dictionary
      foreach (var d in dicts) {
        if (d.Source != null && d.Source.OriginalString.Contains("Themes/Colors.")) {
          colorDict = d;
          break;
        }
      }

      string targetSource = isLightTheme ? "Themes/Colors.Light.xaml" : "Themes/Colors.Dark.xaml";

      // Swap dictionary if needed
      if (colorDict == null || !colorDict.Source.OriginalString.Equals(targetSource, StringComparison.OrdinalIgnoreCase)) {
        if (colorDict != null) dicts.Remove(colorDict);
        dicts.Insert(0, new ResourceDictionary { Source = new Uri(targetSource, UriKind.Relative) });
      }
      
      // No manual overrides needed - Dictionaries are now strictly monochrome
    }

    private static Color ChangeColorBrightness(Color color, float correctionFactor) {
        float red = (float)color.R;
        float green = (float)color.G;
        float blue = (float)color.B;

        if (correctionFactor < 0) {
            correctionFactor = 1 + correctionFactor;
            red *= correctionFactor;
            green *= correctionFactor;
            blue *= correctionFactor;
        } else {
            red = (255 - red) * correctionFactor + red;
            green = (255 - green) * correctionFactor + green;
            blue = (255 - blue) * correctionFactor + blue;
        }

        return Color.FromRgb((byte)red, (byte)green, (byte)blue);
    }
    
    private static Color MixColor(Color color1, Color color2, double percentage) {
        byte r = (byte)(color1.R * percentage + color2.R * (1 - percentage));
        byte g = (byte)(color1.G * percentage + color2.G * (1 - percentage));
        byte b = (byte)(color1.B * percentage + color2.B * (1 - percentage));
        return Color.FromRgb(r, g, b);
    }
  }
}
