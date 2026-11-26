/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: comandos con botones del mando que valen para TODOS los mandos
    /// (se muestran y se configuran en Opciones → Comandos).
    /// Se guarda en %appdata%\PhantomPad\PhantomPadCommands.json.
    /// </summary>
    public static class PhantomCommands
    {
        private const string FILE_NAME = "PhantomPadCommands.json";

        private class CommandPrefs
        {
            public PhantomDevice.PowerCombo PowerOff { get; set; } = PhantomDevice.PowerCombo.PSCross;
        }

        private static readonly object fileLock = new object();
        private static CommandPrefs prefs;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        private static CommandPrefs Prefs
        {
            get
            {
                if (prefs != null) return prefs;
                lock (fileLock)
                {
                    if (prefs != null) return prefs;
                    CommandPrefs loaded = null;
                    try
                    {
                        if (File.Exists(FilePath))
                            loaded = JsonSerializer.Deserialize<CommandPrefs>(File.ReadAllText(FilePath), JsonOptions);
                    }
                    catch { /* archivo danado: valores por defecto */ }

                    prefs = loaded ?? new CommandPrefs();
                    return prefs;
                }
            }
        }

        /// <summary>Combo que apaga el mando que lo pulsa (Desactivado = None).</summary>
        public static PhantomDevice.PowerCombo PowerOff
        {
            get => Prefs.PowerOff;
            set
            {
                if (Prefs.PowerOff == value) return;
                Prefs.PowerOff = value;
                Save();
            }
        }

        private static void Save()
        {
            lock (fileLock)
            {
                try
                {
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(prefs, JsonOptions));
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_CommandsSaveFailed", "Could not save the commands: {0}", e.Message), false);
                }
            }
        }
    }
}
