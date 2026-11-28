/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DS4Windows;
using Microsoft.Win32;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>
    /// PhantomPad: editar un juego de la biblioteca (nombre, descripcion, datos, caratula, icono,
    /// fondo y como se inicia). Los cambios solo se guardan al pulsar "Guardar".
    /// </summary>
    public partial class EditGameWindow : Window
    {
        private static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" };

        private readonly LibraryGame original;
        private readonly LibraryGame game; // copia: Cancelar no cambia nada

        // Imagenes pendientes: un archivo nuevo, o "volver a la de siempre"
        private string newCover, newBackground, newIconFile, newLogo;
        private BitmapSource newIconBitmap;
        private bool clearCover, clearBackground, clearIcon;
        /// <summary>Juego de Steam elegido con "Descargar de Steam" (se guarda al pulsar Guardar).</summary>
        private string pendingSteamId;

        public EditGameWindow(LibraryGame g)
        {
            InitializeComponent();
            original = g;
            game = JsonSerializer.Deserialize<LibraryGame>(JsonSerializer.Serialize(g));

            titleText.Text = g.Name;
            nameBox.Text = g.Name;
            descriptionBox.Text = g.Description ?? "";
            genresBox.Text = g.Genres ?? "";
            yearBox.Text = g.ReleaseYear?.ToString() ?? "";
            developerBox.Text = g.Developer ?? "";
            publisherBox.Text = g.Publisher ?? "";
            launchBox.Text = g.LaunchUri ?? "";
            argsBox.Text = g.Arguments ?? "";
            RefreshImages();
        }

        // ===================== Vista previa =====================

        private void RefreshImages()
        {
            var shown = JsonSerializer.Deserialize<LibraryGame>(JsonSerializer.Serialize(game));
            if (clearCover) shown.CoverPath = null;
            if (clearBackground) shown.BackgroundPath = null;
            if (clearIcon) shown.IconPath = null;

            string cover = newCover ?? PhantomLibrary.CoverFor(shown);
            coverImage.Source = cover == null ? null : LibraryControl.LoadBitmap(cover, 320);
            coverEmpty.Visibility = coverImage.Source == null ? Visibility.Visible : Visibility.Collapsed;

            string bg = newBackground ?? PhantomLibrary.BackgroundFor(shown);
            bgImage.Source = bg == null ? null : LibraryControl.LoadBitmap(bg, 440);
            bgEmpty.Visibility = bgImage.Source == null ? Visibility.Visible : Visibility.Collapsed;

            ImageSource icon = newIconBitmap;
            if (icon == null && newIconFile != null) icon = LibraryControl.LoadBitmap(newIconFile, 128);
            if (icon == null && PhantomLibrary.IconFileFor(shown) is string iconFile) icon = LibraryControl.LoadBitmap(iconFile, 128);
            if (icon == null) icon = LibraryControl.LoadExeIcon(PhantomLibrary.ExeFor(shown));
            iconImage.Source = icon;
        }

        // ===================== Elegir imagenes =====================

        private string PickImage(bool allowExe)
        {
            string exts = string.Join(";", ImageExts.Select(e => "*" + e));
            var dlg = new OpenFileDialog
            {
                Title = PPText.T("PP_LibEditPickTitle", "Choose an image"),
                Filter = allowExe
                    ? $"{PPText.T("PP_LibEditImages", "Images")} ({exts};*.exe)|{exts};*.exe"
                    : $"{PPText.T("PP_LibEditImages", "Images")} ({exts})|{exts}",
            };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }

        /// <summary>Comprueba que el archivo es una imagen que se puede mostrar.</summary>
        private bool ValidImage(string file)
        {
            if (file == null) return false;
            if (ImageExts.Contains(Path.GetExtension(file).ToLowerInvariant()) && LibraryControl.LoadBitmap(file, 64) != null) return true;
            MessageBox.Show(this, PPText.T("PP_LibEditBadImage", "That file is not an image PhantomPad can show (use PNG, JPG, BMP, GIF or ICO)."),
                "PhantomPad", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private void SetCover(string file)
        {
            if (!ValidImage(file)) return;
            newCover = file; clearCover = false; RefreshImages();
        }

        private void SetBackground(string file)
        {
            if (!ValidImage(file)) return;
            newBackground = file; clearBackground = false; RefreshImages();
        }

        private void SetIcon(string file)
        {
            if (file == null) return;
            if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Icono sacado de un .exe
                if (LibraryControl.LoadExeIcon(file) is BitmapSource bmp) { newIconBitmap = bmp; newIconFile = null; clearIcon = false; RefreshImages(); }
                return;
            }
            if (!ValidImage(file)) return;
            newIconFile = file; newIconBitmap = null; clearIcon = false; RefreshImages();
        }

        private void CoverPick_Click(object sender, RoutedEventArgs e) => SetCover(PickImage(false));
        private void BgPick_Click(object sender, RoutedEventArgs e) => SetBackground(PickImage(false));
        private void IconPick_Click(object sender, RoutedEventArgs e) => SetIcon(PickImage(true));

        private void CoverClear_Click(object sender, RoutedEventArgs e) { newCover = null; clearCover = true; RefreshImages(); }
        private void BgClear_Click(object sender, RoutedEventArgs e) { newBackground = null; clearBackground = true; RefreshImages(); }
        private void IconClear_Click(object sender, RoutedEventArgs e) { newIconFile = null; newIconBitmap = null; clearIcon = true; RefreshImages(); }

        // Arrastrar y soltar una imagen encima del recuadro
        private static string DroppedFile(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) ? (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault() : null;

        private void ImgBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void CoverBox_Drop(object sender, DragEventArgs e) => SetCover(DroppedFile(e));
        private void BgBox_Drop(object sender, DragEventArgs e) => SetBackground(DroppedFile(e));
        private void IconBox_Drop(object sender, DragEventArgs e) => SetIcon(DroppedFile(e));

        private void LaunchBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = PPText.T("PP_LibAddTitle", "Choose the game's .exe"),
                Filter = PPText.T("PP_LibAddFilter", "Games and shortcuts") + " (*.exe;*.lnk;*.url)|*.exe;*.lnk;*.url",
            };
            if (dlg.ShowDialog(this) == true) launchBox.Text = dlg.FileName;
        }

        // ===================== Guardar =====================

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = nameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show(this, PPText.T("PP_LibEditNeedName", "The game needs a name."), "PhantomPad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int? year = null;
            if (yearBox.Text.Trim().Length > 0)
            {
                if (!int.TryParse(yearBox.Text.Trim(), out int y) || y < 1950 || y > 2100)
                {
                    MessageBox.Show(this, PPText.T("PP_LibEditBadYear", "The year must be a number like 2008."), "PhantomPad", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                year = y;
            }

            if (name != original.Name) game.NameEditedByUser = true;
            game.Name = name;
            game.Description = Empty(descriptionBox.Text);
            game.Genres = Empty(genresBox.Text);
            game.ReleaseYear = year;
            game.Developer = Empty(developerBox.Text);
            game.Publisher = Empty(publisherBox.Text);
            game.LaunchUri = Empty(launchBox.Text);
            game.Arguments = Empty(argsBox.Text);
            if (game.Source == PhantomLibrary.SOURCE_MANUAL && File.Exists(game.LaunchUri))
            {
                game.InstallDir = Path.GetDirectoryName(game.LaunchUri);
                game.Installed = true;
            }

            if (pendingSteamId != null)
            {
                if (game.Source != GameProfiles.GameLibraryScanner.SOURCE_STEAM) game.MetadataSteamId = pendingSteamId;
                game.MetadataChecked = true;
            }

            try
            {
                game.LogoPath = ApplyImage(game.LogoPath, newLogo, null, false, "logo");
                game.CoverPath = ApplyImage(game.CoverPath, newCover, null, clearCover, "cover");
                game.BackgroundPath = ApplyImage(game.BackgroundPath, newBackground, null, clearBackground, "background");
                game.IconPath = ApplyImage(game.IconPath, newIconFile, newIconBitmap, clearIcon, "icon");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, PPText.T("PP_LibEditImageError", "Could not save the image: {0}", ex.Message), "PhantomPad", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PhantomLibrary.Update(game);
            DialogResult = true;
        }

        /// <summary>Copia la imagen nueva a la carpeta de la biblioteca y borra la anterior si era propia.</summary>
        private string ApplyImage(string current, string newFile, BitmapSource newBitmap, bool clear, string kind)
        {
            if (newFile != null || newBitmap != null)
            {
                string stored = newFile != null ? PhantomLibrary.StoreImage(game, kind, newFile) : PhantomLibrary.StoreImage(game, kind, newBitmap);
                PhantomLibrary.DeleteStoredImage(current);
                return stored;
            }
            if (clear)
            {
                PhantomLibrary.DeleteStoredImage(current);
                return null;
            }
            return current;
        }

        private static string Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // ===================== Descargar de Steam =====================

        private async void SteamBtn_Click(object sender, RoutedEventArgs e)
        {
            steamBtn.IsEnabled = false;
            steamStatus.Text = PPText.T("PP_LibSteamSearching", "Looking in the Steam store...");
            try
            {
                // Juego de Steam: su numero. Otros: el ya emparejado o el que encuentre por el nombre.
                string id = pendingSteamId ?? await PhantomLibrary.FindSteamIdAsync(game, false);
                if (id == null)
                {
                    string name = nameBox.Text.Trim();
                    var results = await SteamMetadata.SearchAsync(name);
                    id = SteamMetadata.BestMatch(name, results)?.Id ?? SteamPickWindow.Pick(this, name, results);
                }
                if (id == null)
                {
                    steamStatus.Text = PPText.T("PP_LibSteamNone", "No Steam game was chosen. Nothing changed.");
                    return;
                }

                SteamGameInfo info = await SteamMetadata.GetDetailsAsync(id);
                if (info == null)
                {
                    steamStatus.Text = PPText.T("PP_LibSteamNoData", "Steam has no data for that game.");
                    return;
                }
                pendingSteamId = id;
                if (info.Description != null) descriptionBox.Text = info.Description;
                if (info.Genres != null) genresBox.Text = info.Genres;
                if (info.Developer != null) developerBox.Text = info.Developer;
                if (info.Publisher != null) publisherBox.Text = info.Publisher;
                if (info.Year.HasValue) yearBox.Text = info.Year.Value.ToString();

                // Imagenes: los juegos de Steam ya las tienen en el PC; solo se descarga lo que falte
                bool steamLocal = game.Source == GameProfiles.GameLibraryScanner.SOURCE_STEAM;
                var shown = JsonSerializer.Deserialize<LibraryGame>(JsonSerializer.Serialize(game));
                shown.CoverPath = null; shown.BackgroundPath = null; shown.LogoPath = null;
                if (!steamLocal || PhantomLibrary.CoverFor(shown) == null)
                {
                    string c = await SteamMetadata.DownloadCoverAsync(id);
                    if (c != null) { newCover = c; clearCover = false; }
                }
                if (!steamLocal || PhantomLibrary.BackgroundFor(shown) == null)
                {
                    string b = await SteamMetadata.DownloadBackgroundAsync(id);
                    if (b != null) { newBackground = b; clearBackground = false; }
                }
                if (!steamLocal || PhantomLibrary.LogoFor(shown) == null)
                {
                    newLogo = await SteamMetadata.DownloadLogoAsync(id) ?? newLogo;
                }
                RefreshImages();
                steamStatus.Text = PPText.T("PP_LibSteamLoaded", "Loaded from Steam: {0}. Press \"Save\" to keep it, or \"Cancel\" to leave it as it was.", info.Name);
            }
            catch (Exception ex)
            {
                steamStatus.Text = PPText.T("PP_LibSteamError", "Could not connect to the Steam store. Check your internet connection. ({0})", ex.Message);
            }
            finally
            {
                steamBtn.IsEnabled = true;
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        /// <summary>Al cerrar se borran las imagenes descargadas que quedaron en la carpeta temporal.</summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            foreach (string f in new[] { newCover, newBackground, newLogo })
            {
                try
                {
                    if (f != null && Path.GetFileName(f).StartsWith("phantompad_") &&
                        Path.GetDirectoryName(f).TrimEnd('\\').Equals(Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        File.Delete(f);
                }
                catch { }
            }
        }
    }
}
