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
    /// PhantomPad: interruptor "Usar mandos de Xbox" (Configuraciones → Mandos compatibles).
    /// Se guarda en %appdata%\PhantomPad\PhantomPadXbox.json.
    /// </summary>
    public static class PhantomXboxInput
    {
        private const string FILE_NAME = "PhantomPadXbox.json";

        private class XboxPrefs
        {
            public bool Enabled { get; set; } = true;
        }

        private static readonly object fileLock = new object();
        private static XboxPrefs prefs;

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        private static XboxPrefs Prefs
        {
            get
            {
                if (prefs != null) return prefs;
                lock (fileLock)
                {
                    if (prefs != null) return prefs;
                    XboxPrefs read = null;
                    try
                    {
                        if (File.Exists(FilePath)) read = JsonSerializer.Deserialize<XboxPrefs>(File.ReadAllText(FilePath));
                    }
                    catch { }
                    prefs = read ?? new XboxPrefs();
                    return prefs;
                }
            }
        }

        /// <summary>PhantomPad lee los mandos de Xbox y les da su mando virtual con las mejoras.</summary>
        public static bool Enabled
        {
            get => Prefs.Enabled;
            set
            {
                if (Prefs.Enabled == value) return;
                Prefs.Enabled = value;
                Save();
            }
        }

        /// <summary>Nombre del mando segun su PID de Microsoft (o generico).</summary>
        public static string NameFor(int vid, int pid)
        {
            if (vid != 0x045E) return "Xbox (XInput)";
            switch (pid)
            {
                case 0x028E: case 0x028F: case 0x0719: return "Xbox 360";
                case 0x02D1: case 0x02DD: case 0x02EA: case 0x02FD: case 0x02E0: return "Xbox One";
                case 0x02E3: case 0x0B00: case 0x0B05: case 0x0B22: return "Xbox Elite";
                case 0x0B12: case 0x0B13: case 0x0B20: case 0x0B21: return "Xbox Series";
                default: return "Xbox";
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
                catch { }
            }
        }
    }
}
