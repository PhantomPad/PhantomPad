/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DS4Windows;
using Microsoft.Win32;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>PhantomPad: seccion "Juegos" estilo app de NVIDIA.</summary>
    public partial class GameProfilesControl : UserControl
    {
        private GameProfilesViewModel viewModel;
        private bool updatingSlider;

        /// <summary>Abrir el editor avanzado de reglas (perfiles automaticos originales).</summary>
        public event EventHandler AdvancedEditorRequested;
        /// <summary>Abrir la lista de perfiles (gestion avanzada).</summary>
        public event EventHandler ManageProfilesRequested;
        /// <summary>Abrir el editor completo de un perfil.</summary>
        public event EventHandler<string> EditProfileRequested;

        public GameProfilesViewModel ViewModel => viewModel;

        public GameProfilesControl()
        {
            InitializeComponent();
        }

        public void Setup(AutoProfileHolder holder, ProfileList profileList)
        {
            viewModel = new GameProfilesViewModel(holder, profileList);
            viewModel.SelectedGameChanged += (s, e) => Dispatcher.BeginInvoke((Action)UpdateDetail);
            DataContext = viewModel;
            BuildStyleLabels();
            UpdateDetail();
            // Deteccion automatica al iniciar
            _ = viewModel.ScanAndAutoAddAsync();
        }

        private GameEntry Selected => viewModel?.SelectedGame;

        private void BuildStyleLabels()
        {
            styleLabels.Children.Clear();
            for (int i = 0; i < 4; i++)
            {
                styleLabels.Children.Add(new TextBlock
                {
                    Text = GameEntry.StyleDisplay((ControlStyle)i),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = FontWeights.SemiBold,
                });
            }
        }

        /// <summary>Actualiza la barra de estilo y la marca "Recomendado" del juego seleccionado.</summary>
        private void UpdateDetail()
        {
            GameEntry g = Selected;
            detailPanel.Visibility = g == null ? Visibility.Collapsed : Visibility.Visible;
            if (g == null) return;

            g.StyleIndexChanged -= Game_StyleIndexChanged;
            g.StyleIndexChanged += Game_StyleIndexChanged;

            sourceChip.Visibility = string.IsNullOrEmpty(g.Source) ? Visibility.Collapsed : Visibility.Visible;

            updatingSlider = true;
            styleSlider.Value = g.StyleIndex >= 0 ? g.StyleIndex : g.RecommendedStyleIndex;
            updatingSlider = false;
            customNote.Visibility = g.IsCustom && !g.IsGeneral ? Visibility.Visible : Visibility.Collapsed;

            var green = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x34, 0xD3, 0x99));
            recommendedMarks.Children.Clear();
            for (int i = 0; i < 4; i++)
            {
                var mark = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                if (i == g.RecommendedStyleIndex)
                {
                    mark.Children.Add(new TextBlock { Text = "▲", Foreground = green, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 });
                    mark.Children.Add(new TextBlock { Text = PPText.T("PP_HubRecommended", "Recommended"), Foreground = green, FontSize = 11,
                        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
                }

                recommendedMarks.Children.Add(mark);
            }

            UpdatePrecision(g);
            UpdateRumble(g);
            UpdateTurbo(g);
        }

        // ================= Turbo de este juego (solo offline) =================
        private bool updatingTurbo;
        private static readonly System.Windows.Media.Brush WarnBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));

        private static List<string> TurboButtonNames() => new List<string>
        {
            "R2", "R1", "L2", PPText.T("PP_BtnCross", "X (Cross)"), PPText.T("PP_BtnSquare", "Square"),
            PPText.T("PP_BtnCircle", "Circle"), PPText.T("PP_BtnTriangle", "Triangle"),
        };

        private void UpdateTurbo(GameEntry g)
        {
            turboCard.Visibility = g.IsGeneral ? Visibility.Collapsed : Visibility.Visible;
            if (g.IsGeneral) return;
            PhantomTurbo.TurboSetting s = PhantomTurbo.Get(g.Entity.Path);
            updatingTurbo = true;
            if (turboButtonCombo.ItemsSource == null) turboButtonCombo.ItemsSource = TurboButtonNames();
            turboToggle.IsEnabled = true;
            turboToggle.IsChecked = s.Enabled;
            turboButtonCombo.SelectedIndex = (int)s.Button;
            turboRateSlider.Value = s.Rate;
            updatingTurbo = false;
            turboRateValue.Text = PPText.T("PP_TurboRateValue", "{0} per second", s.Rate);
            turboOptions.IsEnabled = s.Enabled;
            turboOptions.Opacity = s.Enabled ? 1.0 : 0.45;
            SetTurboNote(s.Enabled
                ? PPText.T("PP_TurboNoteOn", "Hold the chosen button and PhantomPad presses it for you. It stays on in this game until you turn it off.")
                : PPText.T("PP_TurboNoteOff", "Only for offline games. Using it online can get you banned."), s.Enabled);
        }

        private void SetTurboNote(string text, bool warn)
        {
            turboNote.Text = text;
            if (warn) turboNote.Foreground = WarnBrush;
            else turboNote.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
        }

        private async void TurboToggle_Changed(object sender, RoutedEventArgs e)
        {
            GameEntry g = Selected;
            if (updatingTurbo || g == null || g.IsGeneral) return;
            string path = g.Entity.Path;

            if (turboToggle.IsChecked != true)
            {
                PhantomTurbo.SetEnabled(path, false);
                UpdateTurbo(g);
                return;
            }

            // Antes de activarlo se revisa la carpeta del juego en busca de un anti-trampas
            turboToggle.IsEnabled = false;
            SetTurboNote(PPText.T("PP_TurboChecking", "Checking the game folder for anti-cheat..."), false);
            string found = await System.Threading.Tasks.Task.Run(() => PhantomTurbo.FindAntiCheat(path));
            if (found != null)
            {
                PhantomTurbo.SetEnabled(path, false);
                if (Selected == g)
                {
                    UpdateTurbo(g);
                    SetTurboNote(PPText.T("PP_TurboBlockedNote",
                        "Blocked: this game has an anti-cheat ({0}), so it is played online. Turbo is only for offline games.", found), true);
                }
                return;
            }

            PhantomTurbo.SetEnabled(path, true);
            if (Selected == g) UpdateTurbo(g);
        }

        private void TurboButtonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingTurbo || Selected == null || Selected.IsGeneral || turboButtonCombo.SelectedIndex < 0) return;
            PhantomTurbo.SetButton(Selected.Entity.Path, (PhantomTurbo.TurboButton)turboButtonCombo.SelectedIndex);
        }

        private void TurboRateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (turboRateValue == null) return;
            int rate = (int)Math.Round(e.NewValue);
            turboRateValue.Text = PPText.T("PP_TurboRateValue", "{0} per second", rate);
            if (updatingTurbo || Selected == null || Selected.IsGeneral) return;
            PhantomTurbo.SetRate(Selected.Entity.Path, rate);
        }

        // ================= Vibracion de este juego =================
        private bool updatingRumble;

        private void UpdateRumble(GameEntry g)
        {
            rumbleCard.Visibility = g.IsGeneral ? Visibility.Collapsed : Visibility.Visible;
            if (g.IsGeneral) return;
            int pct = PhantomGameRumble.Get(g.Entity.Path);
            updatingRumble = true;
            rumbleSlider.Value = pct;
            updatingRumble = false;
            ShowRumble(pct);
        }

        private void ShowRumble(int pct)
        {
            rumbleValue.Text = $"{pct} %";
            rumbleState.Text = pct == 0 ? PPText.T("PP_RumbleOff", "No vibration")
                : pct == PhantomGameRumble.DEFAULT_PCT ? PPText.T("PP_RumbleNormal", "Normal vibration")
                : pct < PhantomGameRumble.DEFAULT_PCT ? PPText.T("PP_RumbleSoft", "Softer vibration")
                : PPText.T("PP_RumbleStrong", "Stronger vibration");
        }

        private void RumbleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (rumbleValue == null) return;
            int pct = (int)Math.Round(e.NewValue);
            ShowRumble(pct);
            if (updatingRumble || Selected == null || Selected.IsGeneral) return;
            PhantomGameRumble.Set(Selected.Entity.Path, pct);
        }

        // ================= Precision al disparar =================
        private bool updatingPrecision;

        private void UpdatePrecision(GameEntry g)
        {
            precisionCard.Visibility = g.IsGeneral ? Visibility.Collapsed : Visibility.Visible;
            if (g.IsGeneral) return;

            PhantomAimPrecision.PrecisionSetting s = PhantomAimPrecision.Get(g.Entity.Path);
            updatingPrecision = true;
            precisionToggle.IsChecked = s.Enabled;
            precisionAimSlider.Value = s.Aim;
            precisionFireSlider.Value = s.Fire;
            updatingPrecision = false;
            precisionAimValue.Text = $"{s.Aim} %";
            precisionFireValue.Text = $"{s.Fire} %";
            precisionSliders.IsEnabled = s.Enabled;
            precisionSliders.Opacity = s.Enabled ? 1.0 : 0.45;
        }

        private void PrecisionToggle_Changed(object sender, RoutedEventArgs e)
        {
            GameEntry g = Selected;
            if (updatingPrecision || g == null || g.IsGeneral) return;
            PhantomAimPrecision.SetEnabled(g.Entity.Path, precisionToggle.IsChecked == true);
            UpdatePrecision(g);
        }

        private void PrecisionAimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (precisionAimValue == null) return;
            int value = (int)Math.Round(e.NewValue);
            precisionAimValue.Text = $"{value} %";
            if (updatingPrecision || Selected == null || Selected.IsGeneral) return;
            PhantomAimPrecision.SetAimSens(Selected.Entity.Path, value);
        }

        private void PrecisionFireSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (precisionFireValue == null) return;
            int value = (int)Math.Round(e.NewValue);
            precisionFireValue.Text = $"{value} %";
            if (updatingPrecision || Selected == null || Selected.IsGeneral) return;
            PhantomAimPrecision.SetFireSens(Selected.Entity.Path, value);
        }

        /// <summary>La barra va del centro de la primera etiqueta al centro de la ultima.</summary>
        private void StyleArea_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double half = styleLabels.ActualWidth / 8.0;
            if (half > 0) styleSlider.Margin = new Thickness(half - 5, 0, half - 5, 0);
        }

        private void Game_StyleIndexChanged(object sender, EventArgs e)
        {
            if (sender == Selected) Dispatcher.BeginInvoke((Action)UpdateDetail);
        }

        private void StyleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (updatingSlider || Selected == null) return;
            int index = (int)Math.Round(e.NewValue);
            if (index != Selected.StyleIndex) viewModel.ApplyStyle(Selected, (ControlStyle)index);
        }

        private void PlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Selected != null) viewModel.Launch(Selected);
        }

        private void RescanBtn_Click(object sender, RoutedEventArgs e)
        {
            _ = viewModel.ScanAndAutoAddAsync();
        }

        // ================= Menu ⋮ de la lista =================
        private void ListMenuBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeItem(PPText.T("PP_HubRescan", "Search for games again"), (s2, e2) => _ = viewModel.ScanAndAutoAddAsync()));
            menu.Items.Add(MakeItem(PPText.T("PP_HubAddExe", "Add a game manually (.exe)..."), (s2, e2) => AddExe()));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeCheck(PPText.T("PP_HubUseSteam", "Ask Steam for the game type (only the game number is sent)"), viewModel.UseSteamGenres,
                v => viewModel.UseSteamGenres = v));
            menu.Items.Add(MakeCheck(PPText.T("PP_GamesRevertOnClose", "Return to the normal profile when leaving the game"), viewModel.RevertOnClose,
                v => viewModel.RevertOnClose = v));
            var notify = new MenuItem { Header = PPText.T("PP_GamesNotify", "Notify on profile change") };
            foreach (var choice in viewModel.NotifyChoices)
            {
                var c = choice;
                var item = new MenuItem { Header = c.DisplayName, IsCheckable = true, IsChecked = viewModel.NotifyChoice == c.ChoiceValue };
                item.Click += (s2, e2) => viewModel.NotifyChoice = c.ChoiceValue;
                notify.Items.Add(item);
            }
            menu.Items.Add(notify);
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem(PPText.T("PP_HubManageProfiles", "Manage profiles (advanced)"), (s2, e2) => ManageProfilesRequested?.Invoke(this, EventArgs.Empty)));
            menu.Items.Add(MakeItem(PPText.T("PP_HubRulesEditor", "Rules editor (advanced)"), (s2, e2) => AdvancedEditorRequested?.Invoke(this, EventArgs.Empty)));
            OpenMenu(menu, sender as Button);
        }

        // ================= Menu ⋮ del juego =================
        private void GameMenuBtn_Click(object sender, RoutedEventArgs e)
        {
            GameEntry g = Selected;
            if (g == null) return;
            var menu = new ContextMenu();
            if (!g.IsGeneral)
            {
                menu.Items.Add(MakeItem(PPText.T("PP_HubCustomize", "Customize by hand..."), (s2, e2) =>
                {
                    new QuickTuneWindow(g) { Owner = Window.GetWindow(this) }.ShowDialog();
                    g.RefreshValues();
                    UpdateDetail();
                }));

                var type = new MenuItem { Header = PPText.T("PP_HubGameType", "Game type") };
                foreach (GameCategory cat in Enum.GetValues(typeof(GameCategory)))
                {
                    var c = cat;
                    var item = new MenuItem { Header = GameEntry.CategoryDisplay(c), IsCheckable = true, IsChecked = g.Category == c };
                    item.Click += (s2, e2) => { viewModel.SetCategory(g, c); UpdateDetail(); };
                    type.Items.Add(item);
                }
                menu.Items.Add(type);
            }

            menu.Items.Add(MakeItem(PPText.T("PP_HubReset", "Restore recommended settings"), (s2, e2) => { viewModel.ResetToRecommended(g); UpdateDetail(); }));
            menu.Items.Add(MakeItem(PPText.T("PP_HubFullEditor", "Full profile editor (experts)"), (s2, e2) => EditProfileRequested?.Invoke(this, g.SelectedProfileName)));
            if (!g.IsGeneral)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MakeItem(PPText.T("PP_HubRemove", "Remove from list"), (s2, e2) => viewModel.Remove(g)));
            }

            OpenMenu(menu, sender as Button);
        }

        private void AddExe()
        {
            var dialog = new OpenFileDialog { Filter = "Exe (*.exe)|*.exe", Title = PPText.Name("Select Program") };
            if (dialog.ShowDialog() == true) viewModel.AddManualExe(dialog.FileName);
        }

        private static MenuItem MakeItem(string header, RoutedEventHandler click)
        {
            var item = new MenuItem { Header = header };
            item.Click += click;
            return item;
        }

        private static MenuItem MakeCheck(string header, bool isChecked, Action<bool> onChange)
        {
            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
            item.Click += (s, e) => onChange(item.IsChecked);
            return item;
        }

        private static void OpenMenu(ContextMenu menu, Button target)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
