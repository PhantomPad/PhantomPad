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
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: control rapido de la luz de cada mando (encendida/apagada y brillo).
    /// Se aplica al final del calculo de color de DS4LightBar, encima del color del perfil
    /// o del color personalizado. Se guarda en %appdata%\PhantomPad\PhantomPadLight.json.
    /// </summary>
    public static class PhantomLight
    {
        private const string FILE_NAME = "PhantomPadLight.json";

        private class LightPrefs
        {
            public bool[] Enabled { get; set; }
            public int[] Brightness { get; set; }
        }

        private static readonly object fileLock = new object();
        private static readonly bool[] enabled = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] brightness = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool loaded;
        private static Timer saveTimer;

        static PhantomLight()
        {
            for (int i = 0; i < enabled.Length; i++)
            {
                enabled[i] = true;
                brightness[i] = 100;
            }
        }

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        public static void EnsureLoaded()
        {
            if (loaded) return;
            lock (fileLock)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        LightPrefs prefs = JsonSerializer.Deserialize<LightPrefs>(File.ReadAllText(FilePath));
                        for (int i = 0; i < enabled.Length; i++)
                        {
                            if (prefs?.Enabled != null && i < prefs.Enabled.Length) enabled[i] = prefs.Enabled[i];
                            if (prefs?.Brightness != null && i < prefs.Brightness.Length) brightness[i] = Math.Clamp(prefs.Brightness[i], 0, 100);
                        }
                    }
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_LightPrefsLoadFailed", "Could not read lightbar preferences: {0}", e.Message), false);
                }
            }
        }

        public static bool IsEnabled(int index)
        {
            EnsureLoaded();
            return enabled[index];
        }

        public static void SetEnabled(int index, bool value)
        {
            EnsureLoaded();
            enabled[index] = value;
            ScheduleSave();
            PhantomDevice.Capture(index);
        }

        public static int GetBrightness(int index)
        {
            EnsureLoaded();
            return brightness[index];
        }

        public static void SetBrightness(int index, int value)
        {
            EnsureLoaded();
            brightness[index] = Math.Clamp(value, 0, 100);
            ScheduleSave();
            PhantomDevice.Capture(index);
        }

        /// <summary>Pone los valores de un mando recien conectado en su ranura (sin guardar por ranura).</summary>
        internal static void SetRuntime(int index, bool isEnabled, int level)
        {
            EnsureLoaded();
            enabled[index] = isEnabled;
            brightness[index] = Math.Clamp(level, 0, 100);
        }

        /// <summary>Aplica encendido/apagado y brillo al color calculado para el mando.</summary>
        public static DS4Color Apply(int index, DS4Color color)
        {
            if (index < 0 || index >= enabled.Length) return color;
            EnsureLoaded();
            if (!enabled[index])
            {
                return new DS4Color(0, 0, 0);
            }

            int level = brightness[index];
            if (level >= 100) return color;
            double factor = level / 100.0;
            return new DS4Color((byte)(color.red * factor), (byte)(color.green * factor), (byte)(color.blue * factor));
        }

        // Guarda con un pequeno retraso para no escribir el archivo en cada movimiento del deslizador
        private static void ScheduleSave()
        {
            lock (fileLock)
            {
                if (saveTimer == null)
                {
                    saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
                    // PhantomPad: si se cierra el programa con un cambio pendiente, se guarda antes de salir
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => Save();
                }
                saveTimer.Change(500, Timeout.Infinite);
            }
        }

        private static void Save()
        {
            lock (fileLock)
            {
                try
                {
                    var prefs = new LightPrefs { Enabled = (bool[])enabled.Clone(), Brightness = (int[])brightness.Clone() };
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_LightPrefsSaveFailed", "Could not save lightbar preferences: {0}", e.Message), false);
                }
            }
        }
    }
}
