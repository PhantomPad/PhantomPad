/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.HidePage
{
    /// <summary>PhantomPad: Configuraciones → Ocultar mandos.</summary>
    public partial class HideSettingsControl : UserControl
    {
        private bool loading;
        private bool rebootPending;

        public HideSettingsControl()
        {
            InitializeComponent();
            PhantomHide.Changed += () => Dispatcher.BeginInvoke((Action)Refresh);
            IsVisibleChanged += (s, e) => { if (IsVisible) Refresh(); };
        }

        /// <summary>Instalador oficial de HidHide que viene dentro de PhantomPad (carpeta drivers).</summary>
        public static string BundledInstaller()
        {
            try
            {
                string dir = Path.Combine(Global.exedirpath, "drivers");
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "HidHide_*.exe").OrderByDescending(f => f).FirstOrDefault() : null;
            }
            catch { return null; }
        }

        private void Refresh()
        {
            loading = true;
            hideToggle.IsChecked = PhantomHide.Enabled;
            loading = false;

            bool installed = PhantomHide.Installed;
            if (installed && !rebootPending)
            {
                driverTitle.Text = PPText.T("PP_HideDriverOk", "✓ HidHide is installed");
                driverText.Text = PPText.T("PP_HideDriverOkText", "Official free driver by Nefarius. PhantomPad uses it to hide the physical controllers.");
                installBtn.Visibility = Visibility.Collapsed;
            }
            else if (rebootPending)
            {
                driverTitle.Text = PPText.T("PP_HideDriverReboot", "Restart the PC to finish");
                driverText.Text = PPText.T("PP_HideDriverRebootText", "HidHide was installed. It starts working after restarting the PC.");
                installBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                driverTitle.Text = PPText.T("PP_HideDriverMissing", "HidHide is not installed");
                driverText.Text = PPText.T("PP_HideDriverMissingText", "Without it, some games see two controllers (the physical one and the virtual Xbox one) and the buttons get doubled. It is free and official (by Nefarius, the same author as ViGEmBus). You will need to restart the PC afterwards.");
                installBtn.Visibility = Visibility.Visible;
            }

            statusList.Children.Clear();
            var list = PhantomHide.ConnectedList();
            if (list.Count == 0)
            {
                var none = new TextBlock { Text = PPText.T("PP_CalNoController", "No controller connected") };
                none.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
                statusList.Children.Add(none);
            }
            for (int i = 0; i < list.Count; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                bool hidden = list[i].Hidden;
                var state = new TextBlock
                {
                    Text = hidden ? PPText.T("PP_HideStateHidden", "Hidden · games only see the virtual Xbox controller")
                                  : PPText.T("PP_HideStateVisible", "Visible to games"),
                    FontWeight = FontWeights.SemiBold,
                };
                if (hidden) state.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                else state.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
                DockPanel.SetDock(state, Dock.Right);
                row.Children.Add(state);
                row.Children.Add(new TextBlock { Text = list[i].Name, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
                statusList.Children.Add(row);
            }

            string err = PhantomHide.LastError;
            errorText.Text = err;
            errorText.Visibility = string.IsNullOrEmpty(err) || !PhantomHide.Enabled ? Visibility.Collapsed : Visibility.Visible;
        }

        private void HideToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            PhantomHide.Enabled = hideToggle.IsChecked == true;
        }

        private async void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            string installer = BundledInstaller();
            if (installer == null)
            {
                // Copia sin instalador: abrir la pagina oficial
                try { Process.Start(new ProcessStartInfo("https://github.com/nefarius/HidHide/releases") { UseShellExecute = true }); } catch { }
                return;
            }

            installBtn.IsEnabled = false;
            try
            {
                Process p = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                if (p != null) await Task.Run(() => p.WaitForExit());
                Global.RefreshHidHideInfo();
                if (Global.hidHideInstalled) rebootPending = true;
            }
            catch { /* el usuario dijo "No" en el aviso de Windows */ }
            installBtn.IsEnabled = true;
            Refresh();
        }
    }
}
