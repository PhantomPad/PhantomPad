/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>
    /// PhantomPad: Escritorio → "Fuentes de datos". Explica de donde salen los datos de los juegos
    /// y permite activar IGDB con las claves gratis de Twitch (opcional).
    /// </summary>
    public class DataSourcesWindow : Window
    {
        private readonly CheckBox autoCk, igdbCk;
        private readonly TextBox idBox;
        private readonly PasswordBox secretBox;
        private readonly TextBlock status;

        public DataSourcesWindow()
        {
            Title = PPText.T("PP_SrcTitle", "Data sources");
            Width = 620; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(StyleProperty, "WindowStyle");
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/PhantomPad;component/DS4Forms/Themes/PhantomPadStyles.xaml", UriKind.Relative) });

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(new TextBlock { Text = Title, FontSize = 20, FontWeight = FontWeights.SemiBold });
            root.Children.Add(Muted(PPText.T("PP_SrcIntro",
                "Like Playnite, each game takes its data first from its own store and then from general databases. The first source that has each piece of data wins, and nothing you edited is ever changed."), 6, 12));

            root.Children.Add(Bold("1. " + PPText.T("PP_SrcStore", "The game's own store")));
            root.Children.Add(Muted(PPText.T("PP_SrcStoreText", "Epic: the catalog the Epic launcher keeps on your PC (cover, background, description). GOG: GOG's public database. No accounts."), 2, 8));
            root.Children.Add(Bold("2. Steam"));
            root.Children.Add(Muted(PPText.T("PP_SrcSteamText", "Description in Spanish, genres, year, developer and images, if the game is also on Steam. No account; only the game name is sent."), 2, 8));
            root.Children.Add(Bold("3. IGDB " + PPText.T("PP_SrcOptional", "(optional)")));
            root.Children.Add(Muted(PPText.T("PP_SrcIgdbText", "The database Playnite uses: almost every game, also the ones you add by hand (descriptions in English). It needs two free keys from a Twitch developer account."), 2, 8));

            autoCk = new CheckBox { Content = PPText.T("PP_SrcAuto", "Download data automatically for new games"), IsChecked = PhantomLibrary.AutoMetadata, Padding = new Thickness(0, 8, 0, 8) };
            autoCk.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
            root.Children.Add(autoCk);

            // ----- IGDB -----
            var card = new Border { Padding = new Thickness(18, 12, 18, 14), Margin = new Thickness(0, 8, 0, 0) };
            card.SetResourceReference(StyleProperty, "SectionCardStyle");
            var igdb = new StackPanel();
            igdbCk = new CheckBox { Content = PPText.T("PP_SrcIgdbUse", "Use IGDB"), IsChecked = PhantomLibrary.IgdbEnabled, Padding = new Thickness(0, 6, 0, 6) };
            igdbCk.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
            igdb.Children.Add(igdbCk);

            igdb.Children.Add(Muted("Client ID", 8, 4));
            idBox = new TextBox { Text = PhantomLibrary.IgdbClientId ?? "", Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
            igdb.Children.Add(idBox);
            igdb.Children.Add(Muted("Client Secret" + (PhantomLibrary.HasIgdbSecret ? "  " + PPText.T("PP_SrcSecretSaved", "(saved; leave it empty to keep it)") : ""), 8, 4));
            secretBox = new PasswordBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
            // Mismos colores que las cajas de texto del tema (claro u oscuro)
            secretBox.SetResourceReference(Control.BackgroundProperty, "BackgroundColor");
            secretBox.SetResourceReference(Control.ForegroundProperty, "ForegroundColor");
            secretBox.SetResourceReference(Control.BorderBrushProperty, "BorderColor");
            secretBox.SetResourceReference(PasswordBox.CaretBrushProperty, "ForegroundColor");
            igdb.Children.Add(secretBox);

            var testRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var test = new Button { Content = PPText.T("PP_SrcTest", "Test keys"), Padding = new Thickness(14, 5, 14, 5) };
            test.Click += async (s, e) =>
            {
                string secret = secretBox.Password.Length > 0 ? secretBox.Password : PhantomLibrary.IgdbSecret;
                status.Text = PPText.T("PP_SrcTesting", "Testing...");
                string err = await Igdb.TestAsync(idBox.Text.Trim(), secret ?? "");
                status.Text = err == null ? "✓ " + PPText.T("PP_SrcTestOk", "The keys work.") : PPText.T("PP_SrcTestFail", "They do not work: {0}", err);
            };
            DockPanel.SetDock(test, Dock.Left);
            testRow.Children.Add(test);
            status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
            status.SetResourceReference(TextBlock.ForegroundProperty, "AccentLightColor");
            testRow.Children.Add(status);
            igdb.Children.Add(testRow);

            igdb.Children.Add(Muted(PPText.T("PP_SrcHowTo",
                "How to get the keys (free, once): 1) Go to dev.twitch.tv/console and sign in with a Twitch account (it asks to turn on two-step verification). 2) \"Register your application\": name PhantomPad, OAuth redirect URL http://localhost, category \"Application Integration\", client type \"Confidential\". 3) Copy the Client ID, press \"New Secret\" and copy the Client Secret here. The secret is saved encrypted for your Windows user."), 12, 4));
            var link = new Button { Content = PPText.T("PP_SrcOpenTwitch", "Open the Twitch developer console"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 6, 0, 0) };
            link.Click += (s, e) => { try { Process.Start(new ProcessStartInfo("https://dev.twitch.tv/console/apps") { UseShellExecute = true }); } catch { } };
            igdb.Children.Add(link);
            card.Child = igdb;
            root.Children.Add(card);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = new Button { Content = PPText.T("PP_UninCancel", "Cancel"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            var save = new Button { Content = PPText.T("PP_LibEditSave", "Save"), Padding = new Thickness(22, 6, 22, 6), IsDefault = true };
            save.SetResourceReference(StyleProperty, "OutlineAccentButtonStyle");
            save.Click += (s, e) => Save();
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            root.Children.Add(buttons);

            // Si no cabe en la pantalla, se puede desplazar
            MaxHeight = SystemParameters.WorkArea.Height - 40;
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        }

        private void Save()
        {
            if (igdbCk.IsChecked == true && (idBox.Text.Trim().Length == 0 || (secretBox.Password.Length == 0 && !PhantomLibrary.HasIgdbSecret)))
            {
                status.Text = PPText.T("PP_SrcNeedKeys", "To use IGDB, write the Client ID and the Client Secret.");
                return;
            }
            PhantomLibrary.AutoMetadata = autoCk.IsChecked == true;
            PhantomLibrary.SetIgdb(igdbCk.IsChecked == true, idBox.Text, secretBox.Password.Length > 0 ? secretBox.Password : null);
            DialogResult = true;
        }

        private static TextBlock Bold(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

        private static TextBlock Muted(string text, double top, double bottom)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, top, 0, bottom) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
            return t;
        }
    }
}
