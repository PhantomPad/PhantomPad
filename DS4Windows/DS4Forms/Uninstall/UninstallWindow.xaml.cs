/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DS4Windows;
using Microsoft.Win32;

namespace DS4WinWPF.DS4Forms.Uninstall
{
    /// <summary>
    /// PhantomPad: Configuraciones → General → "Desinstalar todo". Deja el PC como si PhantomPad
    /// nunca hubiera estado: HidHide y ViGEmBus con sus desinstaladores oficiales (MSI), quita
    /// "Iniciar con Windows", y al cerrarse PhantomPad se desinstala (Velopack) y borra sus ajustes.
    /// </summary>
    public partial class UninstallWindow : Window
    {
        /// <summary>Solo para pruebas: PHANTOMPAD_TEST_UNINSTALL=1 muestra lo que haria sin hacerlo.</summary>
        private static readonly bool DryRun = Environment.GetEnvironmentVariable("PHANTOMPAD_TEST_UNINSTALL") == "1";

        private class Step
        {
            public string Text;
            public string State = "";
            public bool Done, Failed;
        }

        private readonly Step stopStep, startupStep, hidStep, vigemStep, appStep;
        private readonly List<Step> steps = new List<Step>();
        private readonly InstalledProduct hidHide, vigem, phantomPad;
        private bool working;

        private class InstalledProduct
        {
            public string Name, ProductCode, QuietUninstall, Uninstall;
        }

        public UninstallWindow()
        {
            InitializeComponent();
            hidHide = FindProduct("HidHide");
            vigem = FindProduct("ViGEm Bus Driver");
            phantomPad = FindProduct("PhantomPad");

            steps.Add(stopStep = new Step { Text = PPText.T("PP_UninStepStop", "Stop PhantomPad and show the hidden controllers again") });
            steps.Add(startupStep = new Step { Text = PPText.T("PP_UninStepStartup", "Remove \"Start with Windows\"") });
            steps.Add(hidStep = new Step { Text = PPText.T("PP_UninStepHidHide", "Uninstall HidHide") });
            steps.Add(vigemStep = new Step { Text = PPText.T("PP_UninStepViGEm", "Uninstall ViGEmBus") });
            steps.Add(appStep = new Step { Text = PPText.T("PP_UninStepApp", "Close and uninstall PhantomPad") });
            if (hidHide == null) { hidStep.State = PPText.T("PP_UninNotInstalled", "not installed"); hidStep.Done = true; }
            if (vigem == null) { vigemStep.State = PPText.T("PP_UninNotInstalled", "not installed"); vigemStep.Done = true; }
            if (phantomPad == null) appStep.State = PPText.T("PP_UninAppNotInstalled", "this copy was not installed with Setup.exe: only the settings are deleted");
            if (DryRun) Title += " (prueba)";
            RefreshSteps();
        }

        // ===================== Lista de pasos =====================

        private void RefreshSteps()
        {
            stepList.Children.Clear();
            foreach (Step s in steps)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var state = new TextBlock { Text = s.State, FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 230, TextAlignment = TextAlignment.Right };
                if (s.Failed) state.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                else if (s.Done) state.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
                else state.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
                DockPanel.SetDock(state, Dock.Right);
                row.Children.Add(state);
                row.Children.Add(new TextBlock { Text = "• " + s.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                stepList.Children.Add(row);
            }
        }

        private void Set(Step s, string state, bool done = false, bool failed = false)
        {
            Dispatcher.Invoke(() =>
            {
                s.State = state;
                s.Done = done;
                s.Failed = failed;
                RefreshSteps();
            });
        }

        // ===================== Desinstalar =====================

        private async void UninstallBtn_Click(object sender, RoutedEventArgs e)
        {
            working = true;
            uninstallBtn.IsEnabled = cancelBtn.IsEnabled = settingsCk.IsEnabled = false;
            uninstallBtnText.Text = PPText.T("PP_UninWorking", "Uninstalling...");
            bool deleteSettings = settingsCk.IsChecked == true;

            // 1. Detener: los mandos ocultos se vuelven a mostrar y se suelta ViGEmBus
            Set(stopStep, PPText.T("PP_UninRunning", "in progress..."));
            if (!DryRun)
            {
                await Task.Run(() =>
                {
                    try { if (App.rootHub != null && App.rootHub.running) App.rootHub.Stop(); } catch { }
                    try { PhantomHide.RestoreAll(); } catch { }
                });
            }
            Set(stopStep, "✓", done: true);

            // 2. Iniciar con Windows
            if (!DryRun)
            {
                try { StartupMethods.DeleteStartProgEntry(); } catch { }
                try { StartupMethods.DeleteTaskEntry(); } catch { }
            }
            Set(startupStep, "✓", done: true);

            // 3 y 4. Controladores (desinstalador oficial de Windows; sale el aviso de permiso)
            if (hidHide != null) await UninstallMsi(hidHide, hidStep);
            if (vigem != null) await UninstallMsi(vigem, vigemStep);

            // 5. PhantomPad: al cerrarse, un script espera, lo desinstala y borra los ajustes
            string script = WriteFinalScript(deleteSettings);
            if (DryRun)
            {
                Set(appStep, PPText.T("PP_UninDryScript", "(test) script: {0}", script), done: true);
                uninstallBtnText.Text = PPText.T("PP_UninButton", "Uninstall everything");
                working = false;
                cancelBtn.IsEnabled = true;
                cancelBtn.Content = PPText.T("PP_DrvClose", "Close");
                return;
            }

            Set(appStep, PPText.T("PP_UninClosing", "closing..."));
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            catch (Exception ex)
            {
                Set(appStep, ex.Message, failed: true);
                working = false;
                cancelBtn.IsEnabled = true;
                return;
            }

            working = false;
            (Owner as MainWindow)?.ExitApp();
            Close();
            Application.Current.Shutdown();
        }

        private async Task UninstallMsi(InstalledProduct p, Step step)
        {
            if (p.ProductCode == null)
            {
                Set(step, PPText.T("PP_UninError", "error {0}", "?"), failed: true);
                return;
            }
            string args = $"/x {p.ProductCode} /passive /norestart";
            if (DryRun)
            {
                Set(step, "(prueba) msiexec " + args, done: true);
                return;
            }

            Set(step, PPText.T("PP_UninAcceptWindows", "accept the Windows notice..."));
            int code = -1;
            try
            {
                Process proc = Process.Start(new ProcessStartInfo("msiexec.exe", args) { UseShellExecute = true });
                if (proc != null)
                {
                    await Task.Run(() => proc.WaitForExit());
                    code = proc.ExitCode;
                }
            }
            catch { code = 1602; } // "No" en el aviso de Windows

            // 0 = listo, 3010/1641 = listo (hay que reiniciar), 1605 = ya no estaba
            if (code == 0 || code == 3010 || code == 1641 || code == 1605)
                Set(step, "✓", done: true);
            else if (code == 1602)
                Set(step, PPText.T("PP_UninCancelled", "cancelled"), failed: true);
            else
                Set(step, PPText.T("PP_UninError", "error {0}", code), failed: true);
        }

        /// <summary>Script que corre cuando PhantomPad ya se cerro: desinstala la app y borra lo que queda.</summary>
        private string WriteFinalScript(bool deleteSettings)
        {
            static string Q(string s) => "'" + s.Replace("'", "''") + "'";
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine($"Wait-Process -Id {Environment.ProcessId} -Timeout 60");
            sb.AppendLine("Start-Sleep -Seconds 2");

            string appDir = null;
            if (phantomPad != null)
            {
                (string exe, string args) = SplitCommand(phantomPad.QuietUninstall ?? phantomPad.Uninstall);
                if (!string.IsNullOrEmpty(exe))
                {
                    if (phantomPad.QuietUninstall == null) args += " --silent";
                    sb.AppendLine($"Start-Process -FilePath {Q(exe)} -ArgumentList {Q(args.Trim())} -Wait");
                    appDir = Path.GetDirectoryName(exe);
                }
            }

            // Ajustes (%appdata%\PhantomPad): solo si es una carpeta "PhantomPad" de verdad
            string settings = Global.appdatapath;
            if (deleteSettings && !string.IsNullOrEmpty(settings) &&
                string.Equals(Path.GetFileName(settings.TrimEnd('\\')), "PhantomPad", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"Remove-Item -LiteralPath {Q(settings)} -Recurse -Force");
            }
            // Lo que Velopack pueda dejar en %localappdata%\PhantomPad
            if (appDir != null && string.Equals(Path.GetFileName(appDir), "PhantomPad", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"Remove-Item -LiteralPath {Q(appDir)} -Recurse -Force");
            }

            sb.AppendLine("Add-Type -AssemblyName PresentationFramework");
            sb.AppendLine($"[System.Windows.MessageBox]::Show({Q(PPText.T("PP_UninFinished", "PhantomPad, ViGEmBus and HidHide were uninstalled. Restart the PC before trying the installer again."))}, 'PhantomPad') | Out-Null");
            sb.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force");

            string path = Path.Combine(Path.GetTempPath(), "PhantomPad-desinstalar.ps1");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        /// <summary>"C:\ruta\Update.exe" --uninstall → (ruta, argumentos).</summary>
        private static (string exe, string args) SplitCommand(string cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return (null, "");
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) return (cmd.Substring(1, end - 1), cmd.Substring(end + 1));
            }
            int sp = cmd.IndexOf(' ');
            return sp < 0 ? (cmd, "") : (cmd.Substring(0, sp), cmd.Substring(sp + 1));
        }

        /// <summary>Busca un programa instalado por su nombre en la lista de "Aplicaciones" de Windows.</summary>
        private static InstalledProduct FindProduct(string displayName)
        {
            var roots = new (RegistryKey hive, string path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            foreach (var (hive, path) in roots)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
                    using RegistryKey list = baseKey.OpenSubKey(path);
                    if (list == null) continue;
                    foreach (string sub in list.GetSubKeyNames())
                    {
                        using RegistryKey k = list.OpenSubKey(sub);
                        if (!string.Equals(k?.GetValue("DisplayName") as string, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        return new InstalledProduct
                        {
                            Name = displayName,
                            ProductCode = sub.StartsWith("{") ? sub : null,
                            QuietUninstall = k.GetValue("QuietUninstallString") as string,
                            Uninstall = k.GetValue("UninstallString") as string,
                        };
                    }
                }
                catch { }
            }
            return null;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (working) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
