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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DS4Windows;
using Microsoft.Win32.SafeHandles;

namespace DS4WinWPF.DS4Forms.ControllerDefs
{
    /// <summary>PhantomPad: Configuraciones → Mandos compatibles.</summary>
    public partial class ControllerDefsControl : UserControl
    {
        /// <summary>Tipos que se pueden elegir al anadir (orden del desplegable).</summary>
        private static readonly string[] AddTypes = { "DS4", "DualSense", "SwitchPro", "DS3" };

        private (int Vid, int Pid) adding;

        public ControllerDefsControl()
        {
            InitializeComponent();
            IsVisibleChanged += (s, e) =>
            {
                if (!IsVisible) return;
                PhantomControllerDefs.EnsureApplied();
                RefreshAll();
            };
        }

        private bool loadingXbox;

        private void XboxToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingXbox) return;
            bool on = xboxToggle.IsChecked == true;
            PhantomXboxInput.Enabled = on;
            // Al momento: encendido → buscar mandos de Xbox; apagado → soltar los que haya
            System.Threading.Tasks.Task.Run(() =>
            {
                if (!on) DS4Devices.ReleaseXboxControllers();
                if (App.rootHub != null && App.rootHub.running) App.rootHub.HotPlug();
            });
            _ = RefreshUnknownAsync();
        }

        private void RefreshAll()
        {
            loadingXbox = true;
            xboxToggle.IsChecked = PhantomXboxInput.Enabled;
            loadingXbox = false;

            errorText.Text = PhantomControllerDefs.LastError == null ? "" :
                PPText.T("PP_DefsFileError", "The Mandos.json file has an error and is being ignored: {0}", PhantomControllerDefs.LastError);
            errorText.Visibility = PhantomControllerDefs.LastError == null ? Visibility.Collapsed : Visibility.Visible;
            RefreshCustom();
            RefreshBuiltIn();
            _ = RefreshUnknownAsync();
        }

        // ===================== Mandos conectados que no se reconocen =====================

        private bool searching;

        private async Task RefreshUnknownAsync()
        {
            if (searching) return;
            searching = true;
            try
            {
                unknownList.Children.Clear();
                unknownList.Children.Add(Muted(PPText.T("PP_DefsSearching", "Looking for connected controllers...")));
                // Un momento visible de "Buscando...", asi se nota que el boton hizo algo
                var search = Task.Run(FindConnectedGamepads);
                await Task.WhenAll(search, Task.Delay(800));
                List<(int Vid, int Pid, string Name, bool Known)> found = search.Result;

                unknownList.Children.Clear();
                int known = found.Count(f => f.Known), unknown = found.Count - known;
                var summary = new TextBlock
                {
                    Text = PPText.T("PP_DefsSearchSummary", "Searched at {0}: {1} controller(s) connected, {2} recognized, {3} not recognized.",
                        DateTime.Now.ToString("HH:mm:ss"), found.Count, known, unknown),
                    FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap,
                };
                unknownList.Children.Add(summary);

                if (found.Count == 0)
                {
                    unknownList.Children.Add(Muted(PPText.T("PP_DefsNoneConnected",
                        "No controller is connected. Turn it on or connect it by USB or Bluetooth and press \"Search again\".")));
                    return;
                }

                foreach (var g in found.OrderBy(f => f.Known))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                    if (g.Known)
                    {
                        var ok = new TextBlock { Text = "✓ " + PPText.T("PP_DefsRecognized", "Recognized"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99)) };
                        DockPanel.SetDock(ok, Dock.Right);
                        row.Children.Add(ok);
                    }
                    else
                    {
                        var add = new Button { Content = PPText.T("PP_DefsAdd", "Add"), Padding = new Thickness(16, 0, 16, 0), Height = 30 };
                        add.Style = (Style)FindResource("OutlineAccentButtonStyle");
                        var info = g;
                        add.Click += (s, e) => OpenAddForm(info.Vid, info.Pid, info.Name);
                        DockPanel.SetDock(add, Dock.Right);
                        row.Children.Add(add);
                    }

                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    text.Children.Add(new TextBlock { Text = g.Name, FontWeight = FontWeights.SemiBold });
                    text.Children.Add(Muted($"VID {PhantomControllerDefs.FormatId(g.Vid)} · PID {PhantomControllerDefs.FormatId(g.Pid)}"));
                    row.Children.Add(text);
                    unknownList.Children.Add(row);
                }

                if (unknown == 0)
                {
                    unknownList.Children.Add(Muted(PPText.T("PP_DefsNoUnknown",
                        "All connected controllers are recognized. If yours is not, connect it by USB or Bluetooth and press \"Search again\".")));
                }
            }
            finally
            {
                searching = false;
            }
        }

        /// <summary>Mandos (joystick o gamepad HID) conectados, reconocidos o no.</summary>
        private static List<(int Vid, int Pid, string Name, bool Known)> FindConnectedGamepads()
        {
            var result = new List<(int, int, string, bool)>();
            var seen = new HashSet<(int, int)>();
            try
            {
                foreach (HidDevice d in HidDevices.Enumerate())
                {
                    try
                    {
                        if (d.Capabilities.UsagePage != 0x01 || (d.Capabilities.Usage != 0x04 && d.Capabilities.Usage != 0x05)) continue;
                        int vid = d.Attributes.VendorId, pid = d.Attributes.ProductId;
                        if (!seen.Add((vid, pid))) continue;
                        if (Global.CheckIfVirtualDevice(d.DevicePath)) continue; // mandos virtuales (ViGEm)
                        if (d.DevicePath.IndexOf("&ig_", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Mando de Xbox (XInput): se reconoce con el interruptor "Usar mandos de Xbox"
                            result.Add((vid, pid, PhantomXboxInput.NameFor(vid, pid) +
                                (PhantomXboxInput.Enabled ? "" : " · " + PPText.T("PP_XboxOff", "Xbox controllers turned off")), true));
                            continue;
                        }
                        VidPidInfo knownInfo = DS4Devices.KnownDevices.FirstOrDefault(i => i.vid == vid && i.pid == pid);
                        string name = knownInfo?.name ?? ReadProductName(d.DevicePath);
                        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(d.Description) ? PPText.T("PP_DefsUnnamed", "Controller") : d.Description;
                        result.Add((vid, pid, name.Trim(), knownInfo != null));
                    }
                    catch { /* dispositivo que no se deja leer */ }
                }
            }
            catch { }

            return result;
        }

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int length);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        private static string ReadProductName(string path)
        {
            try
            {
                using SafeFileHandle h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) return null;
                var buffer = new byte[254];
                if (!HidD_GetProductString(h, buffer, buffer.Length)) return null;
                return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            }
            catch
            {
                return null;
            }
        }

        private void RescanBtn_Click(object sender, RoutedEventArgs e) => _ = RefreshUnknownAsync();

        // ===================== Anadir =====================

        private void OpenAddForm(int vid, int pid, string name)
        {
            adding = (vid, pid);
            addTitle.Text = PPText.T("PP_DefsAddTitle", "Add controller (VID {0} · PID {1})", PhantomControllerDefs.FormatId(vid), PhantomControllerDefs.FormatId(pid));
            addName.Text = name;
            addType.ItemsSource = new List<string>
            {
                PPText.T("PP_DefsTypeDS4", "PS4 compatible (DS4) — most generic controllers"),
                PPText.T("PP_DefsTypeDualSense", "PS5 compatible (DualSense)"),
                PPText.T("PP_DefsTypeSwitch", "Nintendo Switch Pro compatible"),
                PPText.T("PP_DefsTypeDS3", "PS3 (DualShock 3)"),
            };
            addType.SelectedIndex = 0;
            optNoOutput.IsChecked = false;
            optNoBattery.IsChecked = false;
            optNoGyroCalib.IsChecked = true; // la mayoria de mandos genericos no aguantan la calibracion del giroscopio
            addForm.Visibility = Visibility.Visible;
            addName.Focus();
        }

        private void AddSaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var options = new List<string>();
            if (optNoOutput.IsChecked == true) options.Add("SinLuzNiVibracion");
            if (optNoBattery.IsChecked == true) options.Add("SinBateria");
            if (optNoGyroCalib.IsChecked == true) options.Add("SinCalibracionGiroscopio");
            PhantomControllerDefs.AddOrUpdate(new PhantomControllerDefs.Def
            {
                Nombre = string.IsNullOrWhiteSpace(addName.Text) ? PPText.T("PP_DefsUnnamed", "Controller") : addName.Text.Trim(),
                VID = PhantomControllerDefs.FormatId(adding.Vid),
                PID = PhantomControllerDefs.FormatId(adding.Pid),
                Tipo = AddTypes[Math.Max(0, addType.SelectedIndex)],
                Opciones = options,
                Activo = true,
            });
            addForm.Visibility = Visibility.Collapsed;
            RefreshAll();
            AppLogger.LogToGui(PPText.T("PPC_ControllerDefAdded", "Controller \"{0}\" added. Reconnect it (or press Stop and Start) to use it.", addName.Text.Trim()), false);
        }

        private void AddCancelBtn_Click(object sender, RoutedEventArgs e) => addForm.Visibility = Visibility.Collapsed;

        // ===================== Mandos anadidos =====================

        private void RefreshCustom()
        {
            customList.Children.Clear();
            var builtIn = new HashSet<(int, int)>(DS4Devices.BuiltInDevices.Select(i => (i.vid, i.pid)));
            List<PhantomControllerDefs.Def> custom = PhantomControllerDefs.Defs
                .Where(d => PhantomControllerDefs.TryParseId(d.VID, out int v) && PhantomControllerDefs.TryParseId(d.PID, out int p) && !builtIn.Contains((v, p)))
                .ToList();
            if (custom.Count == 0)
            {
                customList.Children.Add(Muted(PPText.T("PP_DefsNoCustom", "You have not added any controller yet.")));
                return;
            }

            foreach (PhantomControllerDefs.Def d in custom)
            {
                PhantomControllerDefs.TryParseId(d.VID, out int vid);
                PhantomControllerDefs.TryParseId(d.PID, out int pid);
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var remove = new Button { Style = (Style)FindResource("LinkButtonStyle"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
                var removeText = new TextBlock { Text = PPText.T("PP_DefsRemove", "Remove") };
                removeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentLightColor");
                remove.Content = removeText;
                remove.Click += (s, e) => { PhantomControllerDefs.Remove(vid, pid); RefreshAll(); };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);

                var toggle = new CheckBox { Style = (Style)FindResource("ToggleSwitchStyle"), IsChecked = d.Activo, VerticalAlignment = VerticalAlignment.Center };
                var def = d;
                toggle.Checked += (s, e) => { def.Activo = true; PhantomControllerDefs.AddOrUpdate(def); };
                toggle.Unchecked += (s, e) => { def.Activo = false; PhantomControllerDefs.AddOrUpdate(def); };
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = d.Nombre, FontWeight = FontWeights.SemiBold });
                text.Children.Add(Muted($"VID {d.VID} · PID {d.PID} · {d.Tipo}"));
                row.Children.Add(text);
                customList.Children.Add(row);
            }
        }

        // ===================== Mandos incluidos =====================

        private void RefreshBuiltIn()
        {
            builtInList.Children.Clear();
            IReadOnlyList<VidPidInfo> list = DS4Devices.BuiltInDevices;
            builtInHeader.Text = PPText.T("PP_DefsBuiltInTitle", "Built-in controllers ({0})", list.Count).ToUpperInvariant();
            foreach (VidPidInfo info in list.OrderBy(i => i.name))
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var toggle = new CheckBox { Style = (Style)FindResource("ToggleSwitchStyle"), IsChecked = PhantomControllerDefs.IsBuiltInActive(info), VerticalAlignment = VerticalAlignment.Center };
                var i = info;
                toggle.Checked += (s, e) => PhantomControllerDefs.SetBuiltInActive(i, true);
                toggle.Unchecked += (s, e) => PhantomControllerDefs.SetBuiltInActive(i, false);
                DockPanel.SetDock(toggle, Dock.Right);
                row.Children.Add(toggle);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = info.name });
                text.Children.Add(Muted($"VID {PhantomControllerDefs.FormatId(info.vid)} · PID {PhantomControllerDefs.FormatId(info.pid)} · {info.inputDevType}"));
                row.Children.Add(text);
                builtInList.Children.Add(row);
            }
        }

        // ===================== Archivo =====================

        private void OpenFileBtn_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{PhantomControllerDefs.FilePath}\"") { UseShellExecute = true }); } catch { }
        }

        private void ReloadBtn_Click(object sender, RoutedEventArgs e)
        {
            PhantomControllerDefs.Reload();
            RefreshAll();
        }

        private static TextBlock Muted(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
            return t;
        }
    }
}
