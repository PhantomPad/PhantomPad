/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>
    /// PhantomPad: elegir el juego correcto de la tienda de Steam cuando la busqueda por nombre
    /// no da uno seguro. Se puede escribir otro nombre y volver a buscar.
    /// </summary>
    public class SteamPickWindow : Window
    {
        private readonly TextBox searchBox;
        private readonly ListBox list;
        private readonly TextBlock status;
        private string chosen;

        private class Row
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public override string ToString() => Name;
        }

        private SteamPickWindow(string name, List<(string Id, string Name)> results)
        {
            Title = PPText.T("PP_LibSteamPickTitle", "Which Steam game is it?");
            Width = 520; Height = 470; MinWidth = 420; MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(StyleProperty, "WindowStyle");

            var root = new DockPanel { Margin = new Thickness(20) };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var cancel = new Button { Content = PPText.T("PP_UninCancel", "Cancel"), Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
            var ok = new Button { Content = PPText.T("PP_LibSteamPickUse", "Use this game"), Padding = new Thickness(18, 6, 18, 6), IsDefault = true };
            ok.Click += (s, e) => Accept();
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var intro = new TextBlock { Text = PPText.T("PP_LibSteamPickIntro", "Choose the game from the list. If it is not there, write its name and press \"Search\"."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(intro, Dock.Top);
            root.Children.Add(intro);

            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var searchBtn = new Button { Content = PPText.T("PP_LibSteamPickSearch", "Search"), Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(8, 0, 0, 0) };
            searchBtn.Click += async (s, e) => await DoSearch();
            DockPanel.SetDock(searchBtn, Dock.Right);
            searchRow.Children.Add(searchBtn);
            searchBox = new TextBox { Text = name, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
            searchBox.KeyDown += async (s, e) => { if (e.Key == Key.Enter) { e.Handled = true; await DoSearch(); } };
            searchRow.Children.Add(searchBox);
            DockPanel.SetDock(searchRow, Dock.Top);
            root.Children.Add(searchRow);

            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedForegroundColor");
            DockPanel.SetDock(status, Dock.Bottom);
            root.Children.Add(status);

            list = new ListBox();
            list.MouseDoubleClick += (s, e) => Accept();
            root.Children.Add(list);

            Content = root;
            Fill(results);
        }

        private void Fill(List<(string Id, string Name)> results)
        {
            list.ItemsSource = results.Select(r => new Row { Id = r.Id, Name = r.Name }).ToList();
            if (results.Count > 0) list.SelectedIndex = 0;
            status.Text = results.Count == 0 ? PPText.T("PP_LibSteamPickEmpty", "Steam found nothing with that name.") : "";
        }

        private async System.Threading.Tasks.Task DoSearch()
        {
            status.Text = PPText.T("PP_LibSteamSearching", "Looking in the Steam store...");
            try { Fill(await SteamMetadata.SearchAsync(searchBox.Text.Trim())); }
            catch (Exception ex) { status.Text = ex.Message; }
        }

        private void Accept()
        {
            if (list.SelectedItem is Row r)
            {
                chosen = r.Id;
                DialogResult = true;
            }
        }

        /// <summary>Muestra la lista y devuelve el numero de Steam elegido (o null).</summary>
        public static string Pick(Window owner, string name, List<(string Id, string Name)> results)
        {
            var w = new SteamPickWindow(name, results) { Owner = owner };
            return w.ShowDialog() == true ? w.chosen : null;
        }
    }
}
