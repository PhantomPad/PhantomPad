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
    /// PhantomPad: gyro aim global por mando. Si esta activado, en TODOS los juegos el giroscopio
    /// mueve el stick derecho mientras se mantiene L2 (apuntar). Se aplica encima del perfil cada
    /// vez que se carga uno. Opcional: se pausa en juegos de carreras (alli L2 es el freno).
    /// Se guarda en %appdata%\PhantomPad\PhantomPadGyro.json.
    /// </summary>
    public static class PhantomGyro
    {
        private const string FILE_NAME = "PhantomPadGyro.json";
        private const string TRIGGER_L2 = "5";       // indice de L2 en la lista de activadores
        private const int BASE_MAXZONE = 830;         // valor de DS4Windows = sensibilidad 100 %

        private class GyroPrefs
        {
            public bool[] Enabled { get; set; }
            public int[] Sensitivity { get; set; }
            public bool PauseInRacing { get; set; } = true;
        }

        private static readonly object fileLock = new object();
        private static readonly bool[] enabled = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly int[] sensitivity = new int[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool pauseInRacing = true;
        private static bool loaded;
        private static Timer saveTimer;
        private static string activeGamePath;

        /// <summary>Lo asigna la seccion Juegos: dice si una regla de juego es de carreras.</summary>
        public static Func<string, bool> IsRacingGame { get; set; }

        static PhantomGyro()
        {
            for (int i = 0; i < sensitivity.Length; i++) sensitivity[i] = 100;
        }

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (fileLock)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (!File.Exists(FilePath)) return;
                    GyroPrefs prefs = JsonSerializer.Deserialize<GyroPrefs>(File.ReadAllText(FilePath));
                    if (prefs == null) return;
                    for (int i = 0; i < enabled.Length; i++)
                    {
                        if (prefs.Enabled != null && i < prefs.Enabled.Length) enabled[i] = prefs.Enabled[i];
                        if (prefs.Sensitivity != null && i < prefs.Sensitivity.Length) sensitivity[i] = Math.Clamp(prefs.Sensitivity[i], 25, 300);
                    }

                    pauseInRacing = prefs.PauseInRacing;
                }
                catch { /* archivo danado: valores por defecto (gyro apagado) */ }
            }
        }

        public static bool IsEnabled(int index) { EnsureLoaded(); return index >= 0 && index < enabled.Length && enabled[index]; }
        public static void SetEnabled(int index, bool value) { EnsureLoaded(); enabled[index] = value; ScheduleSave(); ApplyTo(index); PhantomDevice.Capture(index); }

        public static int GetSensitivity(int index) { EnsureLoaded(); return sensitivity[index]; }
        public static void SetSensitivity(int index, int value) { EnsureLoaded(); sensitivity[index] = Math.Clamp(value, 25, 300); ScheduleSave(); ApplyTo(index); PhantomDevice.Capture(index); }

        /// <summary>Pone los valores de un mando recien conectado en su ranura (sin guardar por ranura).</summary>
        internal static void SetRuntime(int index, bool isEnabled, int level)
        {
            EnsureLoaded();
            enabled[index] = isEnabled;
            sensitivity[index] = Math.Clamp(level, 25, 300);
        }

        public static bool PauseInRacing
        {
            get { EnsureLoaded(); return pauseInRacing; }
            set { EnsureLoaded(); pauseInRacing = value; ScheduleSave(); ApplyAll(); }
        }

        /// <summary>Lo llama el cambio automatico de perfil al entrar o salir de un juego.</summary>
        public static void SetActiveGame(string rulePath)
        {
            if (string.Equals(activeGamePath, rulePath, StringComparison.OrdinalIgnoreCase)) return;
            activeGamePath = rulePath;
            ApplyAll();
        }

        private static bool PausedNow =>
            pauseInRacing && activeGamePath != null && (IsRacingGame?.Invoke(activeGamePath) ?? false);

        /// <summary>true si el gyro esta activado pero pausado ahora (juego de carreras en primer plano).</summary>
        public static bool IsPausedNow(int index) => IsEnabled(index) && PausedNow;

        public static void ApplyAll()
        {
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++) ApplyTo(i);
        }

        /// <summary>
        /// Aplica el gyro aim sobre la configuracion ya cargada del mando (se llama tras cargar
        /// cualquier perfil). Si esta desactivado no toca nada: manda lo que diga el perfil.
        /// </summary>
        public static void ApplyTo(int device)
        {
            if (device < 0 || device >= Global.MAX_DS4_CONTROLLER_COUNT) return; // la ranura del editor no se toca
            EnsureLoaded();
            if (!enabled[device]) return;

            if (PausedNow)
            {
                Global.GyroOutputMode[device] = GyroOutMode.Controls;
                return;
            }

            Global.GyroOutputMode[device] = GyroOutMode.MouseJoystick;
            Global.SAMousestickTriggers[device] = TRIGGER_L2;
            Global.SAMouseStickTriggerCond[device] = true;
            Global.GyroMouseStickTriggerTurns[device] = true;

            GyroMouseStickInfo info = Global.GyroMouseStickInf[device];
            info.outputStick = GyroMouseStickInfo.OutputStick.RightStick;
            info.maxZone = Math.Max(60, (int)Math.Round(BASE_MAXZONE * 100.0 / sensitivity[device]));
            info.useSmoothing = true;
            info.smoothingMethod = GyroMouseStickInfo.SmoothingMethod.OneEuro;
        }

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
                    var prefs = new GyroPrefs
                    {
                        Enabled = (bool[])enabled.Clone(),
                        Sensitivity = (int[])sensitivity.Clone(),
                        PauseInRacing = pauseInRacing,
                    };
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_GyroPrefsSaveFailed", "Could not save gyro preferences: {0}", e.Message), false);
                }
            }
        }
    }
}
