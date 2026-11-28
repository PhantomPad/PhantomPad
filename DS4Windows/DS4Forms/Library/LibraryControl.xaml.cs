/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DS4Windows;
using DS4WinWPF.DS4Forms.GameProfiles;
using Microsoft.Win32;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>Un juego tal como se muestra en la cuadricula.</summary>
    public class LibraryItem : INotifyPropertyChanged
    {
        public LibraryGame Game { get; set; }
        public string Name => Game.Name;
        public string SourceText { get; set; }
        public bool NotInstalled => !Game.Installed;
        public string NotInstalledText => PPText.T("PP_LibNotInstalled", "Not installed");

        private ImageSource cover, icon;
        public ImageSource Cover { get => cover; set { cover = value; Raise(nameof(Cover)); Raise(nameof(HasCover)); } }
        public bool HasCover => cover != null;
        public ImageSource Icon { get => icon; set { icon = value; Raise(nameof(Icon)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>PhantomPad: Escritorio → biblioteca de juegos.</summary>
    public partial class LibraryControl : UserControl
    {
        /// <summary>"Ajustes del mando" de un juego: MainWindow abre Mando → Juegos.</summary>
        public event Action<LibraryGame> ControllerSettingsRequested;

        private string filter = "";
        private bool loaded, syncing;
        private readonly Dictionary<string, ImageSource> coverCache = new Dictionary<string, ImageSource>();
        private readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>();

        public LibraryControl()
        {
            InitializeComponent();
            BuildFilters();
            PhantomLibrary.Changed += () => Dispatcher.BeginInvoke((Action)RefreshList);
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible || loaded) return;
                loaded = true;
                loadingToggle = true;
                autoMetaToggle.IsChecked = PhantomLibrary.AutoMetadata;
                loadingToggle = false;
                RefreshList();
                _ = SyncAsync(); // al entrar la primera vez se buscan juegos nuevos
            };
        }

        // ===================== Filtros y busqueda =====================

        private void BuildFilters()
        {
            void Add(string key, string text, bool isChecked = false)
            {
                var rb = new RadioButton { Content = text, Tag = key, GroupName = "libFilter", IsChecked = isChecked, Style = (Style)FindResource("FilterChipStyle") };
                rb.Checked += (s, e) => { filter = key; RefreshList(); };
                filterPanel.Children.Add(rb);
            }
            Add("", PPText.T("PP_LibFilterAll", "All"), true);
            Add(GameLibraryScanner.SOURCE_STEAM, "Steam");
            Add(GameLibraryScanner.SOURCE_EPIC, "Epic");
            Add(GameLibraryScanner.SOURCE_GOG, "GOG");
            Add(PhantomLibrary.SOURCE_MANUAL, PPText.T("PP_LibFilterManual", "Added by you"));
            Add("installed", PPText.T("PP_LibFilterInstalled", "Installed"));
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchHint.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            RefreshList();
        }

        private static string SourceName(LibraryGame g) =>
            g.Source == PhantomLibrary.SOURCE_MANUAL ? PPText.T("PP_LibSourceManual", "Added by you") : g.Source;

        private void RefreshList()
        {
            string selectedId = (gameList.SelectedItem as LibraryItem)?.Game.Id;
            string text = searchBox.Text?.Trim() ?? "";

            List<LibraryGame> games = PhantomLibrary.Games
                .Where(g => !g.Hidden)
                .Where(g => filter == "" || (filter == "installed" ? g.Installed : g.Source == filter))
                .Where(g => text.Length == 0 || (g.Name ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderByDescending(g => g.Installed)
                .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var items = games.Select(g => new LibraryItem { Game = g, SourceText = SourceName(g) }).ToList();
            gameList.ItemsSource = items;
            foreach (LibraryItem item in items) LoadImagesAsync(item);

            emptyText.Text = PhantomLibrary.Games.Count(g => !g.Hidden) == 0
                ? (syncing ? PPText.T("PP_LibSearching", "Looking for your games...") : PPText.T("PP_LibEmpty", "No games yet. Press \"Search games\" or \"Add game\"."))
                : PPText.T("PP_LibNoMatch", "No game matches the search.");
            emptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            LibraryItem again = items.FirstOrDefault(i => i.Game.Id == selectedId);
            if (again != null) gameList.SelectedItem = again;
            else ShowDetails(null);
        }

        private async Task SyncAsync()
        {
            if (syncing) return;
            syncing = true;
            refreshBtn.IsEnabled = false;
            RefreshList();
            try { await Task.Run(PhantomLibrary.Sync); } catch { }
            syncing = false;
            refreshBtn.IsEnabled = true;
            RefreshList();
            await AutoMetadataAsync();
        }

        // ===================== Datos de Steam automaticos =====================

        private bool loadingToggle;

        private async Task AutoMetadataAsync()
        {
            if (!PhantomLibrary.AutoMetadata) return;
            int pending = PhantomLibrary.Games.Count(g => !g.Hidden && !g.MetadataChecked);
            if (pending == 0) return;
            metaStatus.Text = PPText.T("PP_LibAutoMetaRunning", "Downloading data and images from Steam for {0} game(s)...", pending);
            metaStatus.Visibility = Visibility.Visible;
            try { await PhantomLibrary.AutoFillAsync(); } catch { }
            metaStatus.Visibility = Visibility.Collapsed;
            ShowDetails(Selected);
        }

        private void AutoMetaToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingToggle) return;
            PhantomLibrary.AutoMetadata = autoMetaToggle.IsChecked == true;
            if (PhantomLibrary.AutoMetadata) _ = AutoMetadataAsync();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e) => _ = SyncAsync();

        // ===================== Imagenes =====================

        private async void LoadImagesAsync(LibraryItem item)
        {
            LibraryGame g = item.Game;
            string coverPath = PhantomLibrary.CoverFor(g);
            if (coverPath != null)
            {
                if (!coverCache.TryGetValue(coverPath, out ImageSource img))
                {
                    img = await Task.Run(() => LoadBitmap(coverPath, 300));
                    if (img != null) coverCache[coverPath] = img;
                }
                item.Cover = img;
                if (img != null) return;
            }

            // Sin portada: el icono elegido por el usuario o el del .exe
            item.Icon = await IconForAsync(g);
        }

        /// <summary>Icono del juego: el propio (Editar) o el de su .exe.</summary>
        private async Task<ImageSource> IconForAsync(LibraryGame g)
        {
            string key = PhantomLibrary.IconFileFor(g) ?? "exe:" + g.Id;
            if (iconCache.TryGetValue(key, out ImageSource ico)) return ico;
            string file = PhantomLibrary.IconFileFor(g);
            ico = await Task.Run(() => file != null ? LoadBitmap(file, 128) : LoadExeIcon(PhantomLibrary.ExeFor(g)));
            iconCache[key] = ico;
            return ico;
        }

        /// <summary>Carga una imagen sin bloquear el archivo (se puede cambiar despues).</summary>
        public static ImageSource LoadBitmap(string path, int decodeWidth)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        public static ImageSource LoadExeIcon(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            try
            {
                using System.Drawing.Icon ico = System.Drawing.Icon.ExtractIcon(exe, 0, 128) ?? System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico == null) return null;
                BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        // ===================== Ficha del juego =====================

        private LibraryGame Selected => (gameList.SelectedItem as LibraryItem)?.Game;

        private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(Selected);

        private async void ShowDetails(LibraryGame g)
        {
            detailsPanel.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
            noSelectionText.Visibility = g == null ? Visibility.Visible : Visibility.Collapsed;
            if (g == null) return;

            detailName.Text = g.Name;
            var info = new List<string> { SourceName(g) };
            info.Add(g.Installed ? PPText.T("PP_LibInstalled", "Installed") : PPText.T("PP_LibNotInstalled", "Not installed"));
            if (g.LastPlayed.HasValue) info.Add(PPText.T("PP_LibLastPlayed", "Last played: {0}", g.LastPlayed.Value.ToString("d MMM yyyy")));
            if (!string.IsNullOrWhiteSpace(g.Genres)) info.Add(g.Genres);
            if (g.ReleaseYear.HasValue) info.Add(g.ReleaseYear.Value.ToString());
            if (!string.IsNullOrWhiteSpace(g.Developer)) info.Add(g.Developer);
            detailInfo.Text = string.Join("  ·  ", info);
            detailDescription.Text = g.Description ?? "";
            playBtn.IsEnabled = g.Installed && !string.IsNullOrWhiteSpace(g.LaunchUri);
            folderBtn.IsEnabled = Directory.Exists(g.InstallDir);

            string id = g.Id;
            string bg = PhantomLibrary.BackgroundFor(g), logo = PhantomLibrary.LogoFor(g);
            ImageSource bgImg = bg == null ? null : await Task.Run(() => LoadBitmap(bg, 740));
            ImageSource logoImg = logo == null ? null : await Task.Run(() => LoadBitmap(logo, 600));
            // Sin fondo: el icono del juego en grande
            bool iconInstead = bgImg == null;
            if (iconInstead) bgImg = await IconForAsync(g);
            if (Selected?.Id != id) return; // se eligio otro mientras cargaba
            bgImage.Source = bgImg;
            bgImage.Stretch = iconInstead ? Stretch.Uniform : Stretch.UniformToFill;
            bgImage.MaxHeight = iconInstead ? 110 : double.PositiveInfinity;
            logoImage.Source = logoImg;
            // Sin logo de Steam: el nombre ya se ve debajo
        }

        // ===================== Acciones =====================

        private void Play(LibraryGame g)
        {
            if (g == null || !g.Installed) return;
            string error = PhantomLibrary.Launch(g);
            if (error != null)
                MessageBox.Show(PPText.T("PP_LibLaunchError", "Could not start {0}: {1}", g.Name, error), "PhantomPad", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void PlayBtn_Click(object sender, RoutedEventArgs e) => Play(Selected);

        private void GameList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(gameList, d) is ListBoxItem) Play(Selected);
        }

        private void GameList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { Play(Selected); e.Handled = true; }
            else if (e.Key == Key.F2) { Edit(Selected); e.Handled = true; }
        }

        private void Edit(LibraryGame g)
        {
            if (g == null) return;
            new EditGameWindow(g) { Owner = Window.GetWindow(this) }.ShowDialog();
            ShowDetails(Selected); // datos e imagenes nuevos en la ficha
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e) => Edit(Selected);

        private void ControllerBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Selected != null) ControllerSettingsRequested?.Invoke(Selected);
        }

        private void FolderBtn_Click(object sender, RoutedEventArgs e)
        {
            LibraryGame g = Selected;
            if (g == null || !Directory.Exists(g.InstallDir)) return;
            try { Process.Start(new ProcessStartInfo(g.InstallDir) { UseShellExecute = true }); } catch { }
        }

        private void RemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            LibraryGame g = Selected;
            if (g == null) return;
            string text = g.Source == PhantomLibrary.SOURCE_MANUAL
                ? PPText.T("PP_LibRemoveManual", "Remove {0} from the library? The game itself is not uninstalled.", g.Name)
                : PPText.T("PP_LibRemoveDetected", "Hide {0} from the library? The game itself is not uninstalled. It will not appear again when searching for games.", g.Name);
            if (MessageBox.Show(text, "PhantomPad", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PhantomLibrary.Remove(g);
        }

        private void AddBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = PPText.T("PP_LibAddTitle", "Choose the game's .exe"),
                Filter = PPText.T("PP_LibAddFilter", "Games and shortcuts") + " (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            LibraryGame g = PhantomLibrary.AddManual(dlg.FileName);
            Dispatcher.BeginInvoke((Action)(() =>
            {
                LibraryItem item = (gameList.ItemsSource as IEnumerable<LibraryItem>)?.FirstOrDefault(i => i.Game.Id == g.Id);
                if (item != null)
                {
                    gameList.SelectedItem = item;
                    gameList.ScrollIntoView(item);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CardMenu_Opened(object sender, RoutedEventArgs e)
        {
            cardMenu.Items.Clear();
            LibraryGame g = Selected;
            if (g == null) { cardMenu.IsOpen = false; return; }
            MenuItem Item(string text, Action act, bool enabled = true)
            {
                var mi = new MenuItem { Header = text, IsEnabled = enabled };
                mi.Click += (s, a) => act();
                cardMenu.Items.Add(mi);
                return mi;
            }
            Item(PPText.T("PP_LibPlay", "Play"), () => Play(g), g.Installed);
            Item(PPText.T("PP_LibEdit", "Edit..."), () => Edit(g));
            Item(PPText.T("PP_LibControllerSettings", "Controller settings"), () => ControllerSettingsRequested?.Invoke(g));
            Item(PPText.T("PP_LibOpenFolder", "Open folder"), () => FolderBtn_Click(null, null), Directory.Exists(g.InstallDir));
            Item(PPText.T("PP_LibRemove", "Remove"), () => RemoveBtn_Click(null, null));
        }
    }
}
