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
using DS4Windows;

namespace DS4WinWPF.DS4Forms.ViewModels
{
    /// <summary>
    /// PhantomPad: un comando de la seccion Opciones → Comandos (que hace, con que botones y
    /// donde se ajusta). Los configurables muestran una lista para cambiar los botones.
    /// </summary>
    public class CommandInfo
    {
        public string Icon { get; init; }
        public string Name { get; init; }
        public string Description { get; init; }
        public string Where { get; init; }

        /// <summary>Botones que lo activan (solo informativo).</summary>
        public List<string> Buttons { get; init; } = new List<string>();

        /// <summary>Si tiene opciones, se puede elegir con que botones se activa.</summary>
        public List<string> Options { get; init; }

        public Action<int> Apply { get; init; }

        /// <summary>Titulo "BOTONES" ya traducido (sin lex:Loc dentro de la plantilla).</summary>
        public string ButtonsLabel { get; } = PPText.T("PP_CommandButtons", "Buttons").ToUpperInvariant();

        public bool IsConfigurable => Options != null;
        public Visibility ConfigVisibility => IsConfigurable ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ButtonsVisibility => IsConfigurable ? Visibility.Collapsed : Visibility.Visible;

        private int selectedIndex;
        public int SelectedIndex
        {
            get => selectedIndex;
            set
            {
                if (selectedIndex == value || value < 0) return;
                selectedIndex = value;
                Apply?.Invoke(value);
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public event EventHandler SelectedIndexChanged;

        /// <summary>Valor inicial sin aplicar nada.</summary>
        public CommandInfo WithSelected(int index)
        {
            selectedIndex = index;
            return this;
        }

        /// <summary>La lista de todos los comandos de PhantomPad (textos en el idioma actual).</summary>
        public static List<CommandInfo> BuildAll()
        {
            return new List<CommandInfo>
            {
                new CommandInfo
                {
                    Icon = "",
                    Name = PPText.T("PP_CmdPowerOffName", "Turn off the controller"),
                    Description = PPText.T("PP_CmdPowerOffDesc",
                        "Turns off at once the controller that presses the combo (Bluetooth). While you press it, the game does not receive those buttons. By USB cable it cannot be turned off: unplug the cable."),
                    Where = PPText.T("PP_CmdPowerOffWhere", "Works for every controller. Choose the buttons here."),
                    Options = new List<string>
                    {
                        PPText.T("PP_ComboNone", "Off"), PPText.T("PP_ComboPSCross", "PS + X"), PPText.T("PP_ComboPSCircle", "PS + Circle"),
                        PPText.T("PP_ComboPSSquare", "PS + Square"), PPText.T("PP_ComboPSTriangle", "PS + Triangle"), "PS + Options", "PS + Share",
                    },
                    Apply = i => PhantomCommands.PowerOff = (PhantomDevice.PowerCombo)i,
                }.WithSelected((int)PhantomCommands.PowerOff),

                new CommandInfo
                {
                    Icon = "",
                    Name = PPText.T("PP_CmdGyroName", "Gyro aim"),
                    Description = PPText.T("PP_CmdGyroDesc",
                        "While you hold L2, tilting the controller moves the aim. Release L2 and it stops. It pauses in racing games if you choose so."),
                    Where = PPText.T("PP_CmdGyroWhere", "Turn it on in Controllers → Controller settings (on each controller)."),
                    Buttons = new List<string> { PPText.T("PP_BtnHoldL2", "Hold L2"), PPText.T("PP_BtnTilt", "Tilt the controller") },
                },

                new CommandInfo
                {
                    Icon = "",
                    Name = PPText.T("PP_CmdAimSensName", "Aiming sensitivity"),
                    Description = PPText.T("PP_CmdAimSensDesc",
                        "Changes how fast the right stick turns while you aim and while you aim and shoot. It never moves the aim on its own."),
                    Where = PPText.T("PP_CmdAimSensWhere", "Turn it on in Games → each game (off by default)."),
                    Buttons = new List<string> { "L2", "L2 + R2" },
                },

                new CommandInfo
                {
                    Icon = "",
                    Name = PPText.T("PP_CmdTurboName", "Turbo (rapid fire)"),
                    Description = PPText.T("PP_CmdTurboDesc",
                        "While you hold the chosen button, PhantomPad presses and releases it for you many times per second. Only for offline games: it is blocked if the game has an anti-cheat."),
                    Where = PPText.T("PP_CmdTurboWhere", "Turn it on in Games → each game (off by default). It stays on until you turn it off."),
                    Buttons = new List<string> { PPText.T("PP_CmdTurboButtons", "Hold the chosen button (R2 by default)") },
                },
            };
        }
    }
}
