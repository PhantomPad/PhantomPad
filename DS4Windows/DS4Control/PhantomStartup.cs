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

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: opciones de Configuraciones → Inicio que no existian en el programa original.
    /// Se guarda en %appdata%\PhantomPad\PhantomPadStartup.json.
    /// </summary>
    public static class PhantomStartup
    {
        private const string FILE_NAME = "PhantomPadStartup.json";

        private class StartupPrefs
        {
            /// <summary>Al abrir PhantomPad, empezar a usar los mandos sin pulsar "Iniciar".</summary>
            public bool AutoConnect { get; set; } = true;
        }

        private static readonly object fileLock = new object();
        private static StartupPrefs prefs;

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        private static StartupPrefs Prefs
        {
            get
            {
                if (prefs != null) return prefs;
                lock (fileLock)
                {
                    if (prefs != null) return prefs;
                    StartupPrefs read = null;
                    try
                    {
                        if (File.Exists(FilePath)) read = JsonSerializer.Deserialize<StartupPrefs>(File.ReadAllText(FilePath));
                    }
                    catch { }

                    prefs = read ?? new StartupPrefs();
                    return prefs;
                }
            }
        }

        public static bool AutoConnect
        {
            get => Prefs.AutoConnect;
            set
            {
                if (Prefs.AutoConnect == value) return;
                Prefs.AutoConnect = value;
                Save();
            }
        }

        private static void Save()
        {
            lock (fileLock)
            {
                try
                {
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
                    else File.Move(temp, FilePath);
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_StartupSaveFailed", "Could not save the startup settings: {0}", e.Message), false);
                }
            }
        }
    }
}
