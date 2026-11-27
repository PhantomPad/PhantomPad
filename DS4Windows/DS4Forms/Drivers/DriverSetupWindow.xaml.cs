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

namespace DS4WinWPF.DS4Forms.Drivers
{
    /// <summary>
    /// PhantomPad: "Terminar instalacion". Instala los dos controladores oficiales de Nefarius
    /// que vienen dentro de PhantomPad (carpeta drivers): ViGEmBus (mandos virtuales) y
    /// HidHide (ocultar mandos). Cada instalador oficial se abre con su ventana normal, uno
    /// detras del otro, y despues se comprueba que quedaron instalados.
    /// </summary>
    public partial class DriverSetupWindow : Window
    {
        private bool installing;
        private bool hidHideJustInstalled;

        public DriverSetupWindow()
        {
            InitializeComponent();
            Refresh();
        }

        // ===================== Cuando se abre =====================

        /// <summary>Solo para pruebas: PHANTOMPAD_TEST_SETUP=show abre el asistente; =missing ademas finge que faltan los dos.</summary>
        private static readonly string TestMode = Environment.GetEnvironmentVariable("PHANTOMPAD_TEST_SETUP") ?? "";

        public static bool ViGEmOk => TestMode != "missing" && Global.IsViGEmBusInstalled() && Global.IsRunningSupportedViGEmBus();
        public static bool HidHideCompatible => Global.IsWin10OrGreater() && Environment.Is64BitOperatingSystem;
        public static bool HidHideOk => !HidHideCompatible || (TestMode != "missing" && Global.hidHideInstalled);

        /// <summary>
        /// Se abre si falta ViGEmBus (sin el no funciona nada), o justo despues de instalar
        /// PhantomPad si falta cualquiera de los dos.
        /// </summary>
        public static bool IsNeeded(bool firstRun) => TestMode.Length > 0 || !ViGEmOk || (firstRun && !HidHideOk);

        // ===================== Pantalla =====================

        private void Refresh()
        {
            bool vigem = ViGEmOk, hid = HidHideOk;
            introText.Text = PPText.T("PP_DrvIntro", "PhantomPad needs two official, free drivers by Nefarius. They come inside PhantomPad, nothing is downloaded.");

            driverList.Children.Clear();
            AddRow(PPText.T("PP_DrvViGEm", "ViGEmBus"), PPText.T("PP_DrvViGEmText", "Creates the virtual Xbox controller that games see. Required."),
                vigem, vigem && !string.IsNullOrEmpty(Global.vigembusVersion) ? Global.vigembusVersion : null);
            if (HidHideCompatible)
            {
                driverList.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10), Background = (Brush)FindResource("BorderColor") });
                AddRow(PPText.T("PP_DrvHidHide", "HidHide"), PPText.T("PP_DrvHidHideText", "Hides the physical controller from games so buttons are not doubled. Recommended."),
                    hid && !hidHideJustInstalled, null, hidHideJustInstalled);
            }

            bool allOk = vigem && hid;
            if (hidHideJustInstalled)
            {
                noteText.Text = PPText.T("PP_DrvRebootNote", "Done. Restart the PC so HidHide starts working. Save what you have open before restarting.");
                installBtn.Visibility = Visibility.Collapsed;
                rebootBtn.Visibility = Visibility.Visible;
                laterBtn.Content = PPText.T("PP_DrvRebootLater", "Restart later");
            }
            else if (allOk)
            {
                noteText.Text = PPText.T("PP_DrvAllOk", "Everything is installed. PhantomPad is ready.");
                installBtn.Visibility = Visibility.Collapsed;
                laterBtn.Content = PPText.T("PP_DrvClose", "Close");
            }
            else
            {
                noteText.Text = PPText.T("PP_DrvNote",
                    "When you press \"Install all\", the official installers open one after the other: accept the Windows notice and follow their steps. HidHide asks to restart the PC at the end. You can also install them later from Settings → Hide controllers.");
                installBtn.Visibility = Visibility.Visible;
                installBtn.IsEnabled = !installing;
                rebootBtn.Visibility = Visibility.Collapsed;
            }
            laterBtn.IsEnabled = !installing;
        }

        private void AddRow(string name, string text, bool ok, string version, bool needsReboot = false)
        {
            var row = new DockPanel();
            string stateText = needsReboot ? PPText.T("PP_DrvStateReboot", "Installed · restart needed")
                : ok ? (version != null ? PPText.T("PP_DrvStateOkVersion", "✓ Installed ({0})", version) : PPText.T("PP_DrvStateOk", "✓ Installed"))
                : installing ? PPText.T("PP_DrvStateWaiting", "Pending...")
                : PPText.T("PP_DrvStateMissing", "Not installed");
            var state = new TextBlock { Text = stateText, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            if (ok || needsReboot) state.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            else state.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
            DockPanel.SetDock(state, Dock.Right);
            row.Children.Add(state);

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
            var desc = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
            info.Children.Add(desc);
            row.Children.Add(info);
            driverList.Children.Add(row);
        }

        // ===================== Instalar =====================

        /// <summary>Instalador oficial incluido en PhantomPad (carpeta drivers), o null.</summary>
        public static string BundledInstaller(string pattern)
        {
            try
            {
                string dir = Path.Combine(Global.exedirpath, "drivers");
                return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).OrderByDescending(f => f).FirstOrDefault() : null;
            }
            catch { return null; }
        }

        private async void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            installing = true;
            installBtnText.Text = PPText.T("PP_DrvInstalling", "Installing...");
            Refresh();

            if (!ViGEmOk)
            {
                await RunInstaller("ViGEmBus_*.exe", "https://github.com/nefarius/ViGEmBus/releases");
                Global.RefreshViGEmBusInfo(); // al cerrar el asistente, PhantomPad conecta los mandos como siempre
                Refresh();
            }

            if (!HidHideOk)
            {
                await RunInstaller("HidHide_*.exe", "https://github.com/nefarius/HidHide/releases");
                Global.RefreshHidHideInfo();
                if (Global.hidHideInstalled) hidHideJustInstalled = true;
            }

            installing = false;
            installBtnText.Text = PPText.T("PP_DrvInstallAll", "Install all");
            Refresh();
        }

        /// <summary>Abre el instalador oficial y espera a que termine.</summary>
        private static async Task RunInstaller(string pattern, string pageUrl)
        {
            string installer = BundledInstaller(pattern);
            try
            {
                if (installer == null)
                {
                    // Copia sin instaladores incluidos: abrir la pagina oficial
                    Process.Start(new ProcessStartInfo(pageUrl) { UseShellExecute = true });
                    return;
                }
                Process p = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
                if (p != null) await Task.Run(() => p.WaitForExit());
            }
            catch { /* el usuario dijo "No" en el aviso de Windows */ }
        }

        private void RebootBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5 /c \"PhantomPad\"") { UseShellExecute = false, CreateNoWindow = true });
            }
            catch { }
            Close();
        }

        private void LaterBtn_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Mientras un instalador esta abierto no se cierra el asistente.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (installing) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
