/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.BatteryPage
{
    /// <summary>PhantomPad: Configuraciones → Bateria (aviso de bateria baja y estado de los mandos).</summary>
    public partial class BatterySettingsControl : UserControl
    {
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private bool loading;

        public BatterySettingsControl()
        {
            InitializeComponent();
            timer.Tick += (s, e) => RefreshStatus();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) { LoadPrefs(); RefreshStatus(); timer.Start(); }
                else timer.Stop();
            };
        }

        private void LoadPrefs()
        {
            loading = true;
            PhantomBattery.BatteryPrefs p = PhantomBattery.Prefs;
            warnToggle.IsChecked = p.WarnEnabled;
            flashToggle.IsChecked = p.FlashLight;
            thresholdCombo.ItemsSource = PhantomBattery.ThresholdChoices.Select(v => $"{v} %").ToList();
            thresholdCombo.SelectedIndex = Array.IndexOf(PhantomBattery.ThresholdChoices, p.Threshold);
            loading = false;
        }

        private void WarnToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!loading) PhantomBattery.SetWarnEnabled(warnToggle.IsChecked == true);
        }

        private void FlashToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!loading) PhantomBattery.SetFlashLight(flashToggle.IsChecked == true);
        }

        private void ThresholdCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loading || thresholdCombo.SelectedIndex < 0) return;
            PhantomBattery.SetThreshold(PhantomBattery.ThresholdChoices[thresholdCombo.SelectedIndex]);
        }

        /// <summary>Lista de mandos conectados con su bateria.</summary>
        private void RefreshStatus()
        {
            statusList.Children.Clear();
            int limit = PhantomBattery.Prefs.Threshold;
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++)
            {
                DS4Device dev = Program.rootHub?.DS4Controllers[i];
                if (dev == null || !dev.PrimaryDevice) continue;
                int battery = dev.getBattery();
                bool charging = dev.isCharging();
                bool low = !charging && battery > 0 && battery <= limit;

                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var state = new TextBlock
                {
                    Text = charging ? PPText.T("PP_BatCharging", "charging")
                        : low ? PPText.T("PP_BatLow", "low battery")
                        : PPText.T("PP_BatOk", "ok"),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = low ? new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
                        : charging ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)) : null,
                };
                if (state.Foreground == null) state.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
                DockPanel.SetDock(state, Dock.Right);
                row.Children.Add(state);

                var bar = new ProgressBar { Width = 140, Height = 8, Minimum = 0, Maximum = 100, Value = battery, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(bar, Dock.Right);
                row.Children.Add(bar);

                row.Children.Add(new TextBlock
                {
                    Text = PPText.T("PP_BatRow", "Controller {0} · {1} · {2} %", i + 1, dev.DisplayName, battery),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                statusList.Children.Add(row);
            }

            if (statusList.Children.Count == 0)
            {
                var none = new TextBlock { Text = PPText.T("PP_CalNoController", "No controller connected") };
                none.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
                statusList.Children.Add(none);
            }
        }
    }
}
