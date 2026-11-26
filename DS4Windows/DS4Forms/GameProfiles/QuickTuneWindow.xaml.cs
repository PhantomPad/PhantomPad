/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System.Windows;
using System.Windows.Controls;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>PhantomPad: ventana "Ajustar" de un juego (zonas muertas y curvas).</summary>
    public partial class QuickTuneWindow : Window
    {
        private readonly GameEntry entry;
        private QuickTuneViewModel viewModel;

        public QuickTuneWindow(GameEntry entry)
        {
            InitializeComponent();
            this.entry = entry;
            viewModel = new QuickTuneViewModel(entry.SelectedProfileName, entry.Name);
            DataContext = viewModel;
            UpdateSharedWarning();
        }

        private void UpdateSharedWarning()
        {
            int users = entry.Owner.CountGamesUsing(viewModel.ProfileName);
            bool isNormalProfile = DS4Windows.Global.ProfilePath[0] == viewModel.ProfileName;
            sharedWarning.Visibility = (users > 1 || isNormalProfile) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PresetBtn_Click(object sender, RoutedEventArgs e)
        {
            viewModel.ApplyPreset((sender as Button)?.Tag as string);
        }

        private void OwnProfileBtn_Click(object sender, RoutedEventArgs e)
        {
            // Crea una copia del perfil con el nombre del juego y se la asigna. Los valores que ya
            // se ajustaron siguen en la ranura de edicion y se guardaran en el perfil nuevo.
            string newName = entry.Owner.CreateProfileForGame(entry);
            if (newName == null) return;

            viewModel.RetargetProfile(newName);
            UpdateSharedWarning();
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            viewModel.Save();
            DialogResult = true;
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
