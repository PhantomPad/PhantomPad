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
using System.Windows.Media.Imaging;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.Updates
{
    /// <summary>PhantomPad: Configuraciones → Actualizaciones.</summary>
    public partial class UpdatesControl : UserControl
    {
        private string shownImage;

        public UpdatesControl()
        {
            InitializeComponent();
            PhantomUpdater.Changed += () => Dispatcher.BeginInvoke((Action)Refresh);
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) return;
                Refresh();
                _ = PhantomUpdater.CheckAsync(); // al entrar en la seccion se vuelve a mirar
            };
        }

        private void Refresh()
        {
            string current = PhantomUpdater.CurrentVersion;
            PhantomUpdater.ReleaseInfo r = PhantomUpdater.Latest;
            checkBtn.IsEnabled = PhantomUpdater.State != PhantomUpdater.UpdateState.Checking
                && PhantomUpdater.State != PhantomUpdater.UpdateState.Downloading && PhantomUpdater.IsConfigured;

            switch (PhantomUpdater.State)
            {
                case PhantomUpdater.UpdateState.NotConfigured:
                    Show("", PPText.T("PP_UpdNotConfigured", "Updates are not set up yet"),
                        PPText.T("PP_UpdNotConfiguredText", "You have version {0}. To receive updates, PhantomPad must first be published on GitHub.", current));
                    break;
                case PhantomUpdater.UpdateState.Checking:
                    Show("", PPText.T("PP_UpdChecking", "Looking for updates..."), PPText.T("PP_UpdCurrent", "You have version {0}.", current));
                    break;
                case PhantomUpdater.UpdateState.UpToDate:
                case PhantomUpdater.UpdateState.Idle:
                    Show("", PPText.T("PP_UpdUpToDate", "You have the latest version"), PPText.T("PP_UpdCurrent", "You have version {0}.", current) + LastCheckedText());
                    break;
                case PhantomUpdater.UpdateState.Available:
                    Show("", PPText.T("PP_UpdAvailable", "New version available: {0}", r?.Version),
                        PhantomUpdater.CanSelfUpdate
                            ? PPText.T("PP_UpdAvailableText", "You have version {0}. Download it while you play; it is only installed when you restart the app.", current)
                            : PPText.T("PP_UpdManualText", "You have version {0}. This copy was not installed with the PhantomPad installer, so download the new version from its page.", current));
                    break;
                case PhantomUpdater.UpdateState.Downloading:
                    Show("", PPText.T("PP_UpdDownloading", "Downloading... {0} %", PhantomUpdater.DownloadPercent),
                        PPText.T("PP_UpdDownloadingText", "You can keep playing. Nothing is installed until you press \"Restart app\"."));
                    break;
                case PhantomUpdater.UpdateState.ReadyToRestart:
                    Show("", PPText.T("PP_UpdReady", "Ready to install"),
                        PPText.T("PP_UpdReadyText", "Press \"Restart app\" when you finish playing: PhantomPad closes, installs version {0} and opens again.", r?.Version));
                    break;
                case PhantomUpdater.UpdateState.Error:
                    Show("", PPText.T("PP_UpdError", "Could not check for updates"),
                        PPText.T("PP_UpdErrorText", "Check your internet connection and try again. ({0})", PhantomUpdater.ErrorText));
                    break;
            }

            // Tarjeta de la nueva version
            bool showRelease = r != null && (PhantomUpdater.State == PhantomUpdater.UpdateState.Available
                || PhantomUpdater.State == PhantomUpdater.UpdateState.Downloading || PhantomUpdater.State == PhantomUpdater.UpdateState.ReadyToRestart);
            releaseCard.Visibility = showRelease ? Visibility.Visible : Visibility.Collapsed;
            if (!showRelease) return;

            releaseTitle.Text = r.Title;
            releaseDate.Text = r.Date.HasValue ? PPText.T("PP_UpdDate", "Published on {0}", r.Date.Value.ToString("d MMMM yyyy")) : "";
            releaseNotes.Text = string.IsNullOrWhiteSpace(r.Notes) ? PPText.T("PP_UpdNoNotes", "No description.") : r.Notes;
            if (r.ImageUrl != shownImage)
            {
                shownImage = r.ImageUrl;
                releaseImage.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrEmpty(r.ImageUrl))
                {
                    try
                    {
                        releaseImage.Source = new BitmapImage(new Uri(r.ImageUrl));
                        releaseImage.Visibility = Visibility.Visible;
                    }
                    catch { /* imagen no disponible */ }
                }
            }

            bool downloading = PhantomUpdater.State == PhantomUpdater.UpdateState.Downloading;
            bool ready = PhantomUpdater.State == PhantomUpdater.UpdateState.ReadyToRestart;
            downloadProgress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            downloadProgress.Value = PhantomUpdater.DownloadPercent;
            downloadBtn.Visibility = PhantomUpdater.State == PhantomUpdater.UpdateState.Available && PhantomUpdater.CanSelfUpdate
                ? Visibility.Visible : Visibility.Collapsed;
            restartBtn.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>" Ultima comprobacion: hoy a las 20:15." (vacio si aun no se comprobo).</summary>
        private static string LastCheckedText() =>
            PhantomUpdater.LastChecked.HasValue
                ? " " + PPText.T("PP_UpdLastChecked", "Last check: today at {0}.", PhantomUpdater.LastChecked.Value.ToString("HH:mm:ss"))
                : "";

        private void Show(string icon, string title, string text)
        {
            stateIcon.Text = icon;
            stateTitle.Text = title;
            stateText.Text = text;
        }

        private void CheckBtn_Click(object sender, RoutedEventArgs e) => _ = PhantomUpdater.CheckAsync(force: true);

        private void DownloadBtn_Click(object sender, RoutedEventArgs e) => _ = PhantomUpdater.DownloadAsync();

        private void RestartBtn_Click(object sender, RoutedEventArgs e) => PhantomUpdater.RestartAndApply();

        private void PageBtn_Click(object sender, RoutedEventArgs e)
        {
            string url = PhantomUpdater.Latest?.PageUrl ?? PhantomUpdater.RepoUrl + "/releases";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
