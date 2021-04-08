using MCLauncher;
using Newtonsoft.Json;
using System;
using System.Windows;
using System.IO;

namespace REghZyFramework.Themes
{
    public static class ThemesController
    {
        public enum ThemeTypes
        {
            Light, ColourfulLight,
            Dark, ColourfulDark
        }

        public static ThemeTypes CurrentTheme { get; set; }

        private static ResourceDictionary ThemeDictionary
        {
            get { return Application.Current.Resources.MergedDictionaries[0]; }
            set { Application.Current.Resources.MergedDictionaries[0] = value; }
        }

        private static void ChangeTheme(Uri uri)
        {
            ThemeDictionary = new ResourceDictionary() { Source = uri };
        }
        public static void SetTheme(ThemeTypes theme)
        {
            string themeName = GetThemeName(theme);
            CurrentTheme = theme;

            try
            {
                if (!string.IsNullOrEmpty(themeName))
                    ChangeTheme(new Uri($"Themes/{themeName}.xaml", UriKind.Relative));
            }
            catch { }
        }

        public static string GetThemeName(ThemeTypes theme)
        {
            switch (theme)
            {
                case ThemeTypes.Dark:
                    return  "DarkTheme";
                case ThemeTypes.Light:
                    return "LightTheme";
                case ThemeTypes.ColourfulDark:
                    return "ColourfulDarkTheme";
                case ThemeTypes.ColourfulLight:
                    return "ColourfulLightTheme";
                default:
                    return "Error";
            }
        }
    }
}
