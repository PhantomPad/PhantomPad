/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: "Sensibilidad al apuntar" por juego (apagado por defecto).
    /// Cambia la velocidad del stick derecho en dos momentos:
    ///  - apuntando (L2 apretado),
    ///  - apuntando y disparando (L2 + R2 apretados).
    /// 100 % = normal; menos = mas lento y preciso; mas = mas rapido.
    /// NUNCA mueve la mira por si solo (sin el stick, no hace nada). Es como la "sensibilidad
    /// al apuntar" que traen muchos juegos.
    /// Se guarda en %appdata%\PhantomPad\PhantomPadAimPrecision.json.
    /// </summary>
    public static class PhantomAimPrecision
    {
        private const string FILE_NAME = "PhantomPadAimPrecision.json";
        public const int MIN_SENS = 40, MAX_SENS = 150;
        public const int DEFAULT_AIM_SENS = 100;
        public const int DEFAULT_FIRE_SENS = 75;
        private const double KEEP_ZONE = 32.0;      // primer tramo del stick (de 127) que no se toca,
                                                    // para que los toques pequenos sigan pasando la zona muerta del juego
        private const byte TRIGGER_HELD = 100;      // gatillo "apretado" (de 255)

        public class PrecisionSetting
        {
            public bool Enabled { get; set; }
            /// <summary>Sensibilidad apuntando (solo L2), en %.</summary>
            public int? AimSens { get; set; }
            /// <summary>Sensibilidad apuntando y disparando (L2 + R2), en %.</summary>
            public int? FireSens { get; set; }
            /// <summary>Formato anterior (intensidad 5..100). Solo se lee para convertirlo.</summary>
            public int? Strength { get; set; }

            [System.Text.Json.Serialization.JsonIgnore]
            public int Aim => Math.Clamp(AimSens ?? DEFAULT_AIM_SENS, MIN_SENS, MAX_SENS);
            [System.Text.Json.Serialization.JsonIgnore]
            public int Fire => Math.Clamp(FireSens ?? (Strength.HasValue
                ? (int)Math.Round(100 - 0.6 * Strength.Value) // intensidad antigua -> sensibilidad
                : DEFAULT_FIRE_SENS), MIN_SENS, MAX_SENS);
        }

        private static readonly object fileLock = new object();
        private static Dictionary<string, PrecisionSetting> settings = new Dictionary<string, PrecisionSetting>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;
        private static Timer saveTimer;
        private static string activeGamePath;

        /// <summary>Factores actuales del stick derecho (1 = sin cambio).</summary>
        private static double aimScale = 1.0, fireScale = 1.0;

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
                    var data = JsonSerializer.Deserialize<Dictionary<string, PrecisionSetting>>(File.ReadAllText(FilePath));
                    if (data != null) settings = new Dictionary<string, PrecisionSetting>(data, StringComparer.OrdinalIgnoreCase);
                }
                catch { /* archivo danado: todo apagado */ }
            }
        }

        public static PrecisionSetting Get(string rulePath)
        {
            EnsureLoaded();
            lock (fileLock)
            {
                return rulePath != null && settings.TryGetValue(rulePath, out PrecisionSetting s)
                    ? new PrecisionSetting { Enabled = s.Enabled, AimSens = s.Aim, FireSens = s.Fire }
                    : new PrecisionSetting();
            }
        }

        public static void SetEnabled(string rulePath, bool value) => Update(rulePath, s => s.Enabled = value);
        public static void SetAimSens(string rulePath, int value) => Update(rulePath, s => s.AimSens = Math.Clamp(value, MIN_SENS, MAX_SENS));
        public static void SetFireSens(string rulePath, int value) => Update(rulePath, s => s.FireSens = Math.Clamp(value, MIN_SENS, MAX_SENS));

        private static void Update(string rulePath, Action<PrecisionSetting> change)
        {
            if (string.IsNullOrEmpty(rulePath)) return;
            EnsureLoaded();
            lock (fileLock)
            {
                if (!settings.TryGetValue(rulePath, out PrecisionSetting s))
                {
                    s = new PrecisionSetting();
                    settings[rulePath] = s;
                }

                // Pasar al formato nuevo (se guardan las dos sensibilidades, sin la intensidad antigua)
                s.AimSens = s.Aim;
                s.FireSens = s.Fire;
                s.Strength = null;
                change(s);
            }

            ScheduleSave();
            Recalculate();
        }

        /// <summary>Lo llama el cambio automatico de perfil al entrar o salir de un juego.</summary>
        public static void SetActiveGame(string rulePath)
        {
            if (string.Equals(activeGamePath, rulePath, StringComparison.OrdinalIgnoreCase)) return;
            activeGamePath = rulePath;
            Recalculate();
        }

        private static void Recalculate()
        {
            string game = activeGamePath;
            PrecisionSetting s = Get(game);
            bool on = game != null && s.Enabled;
            Interlocked.Exchange(ref aimScale, on ? s.Aim / 100.0 : 1.0);
            Interlocked.Exchange(ref fireScale, on ? s.Fire / 100.0 : 1.0);
        }

        /// <summary>
        /// Se llama en cada lectura del mando, justo antes de enviarla al mando virtual.
        /// l2 / r2 = gatillos del mando fisico.
        /// </summary>
        public static void Apply(byte l2, byte r2, DS4State output)
        {
            if (output == null || l2 < TRIGGER_HELD) return; // sin apuntar: normal
            double scale = r2 >= TRIGGER_HELD ? Volatile.Read(ref fireScale) : Volatile.Read(ref aimScale);
            if (scale == 1.0) return;

            double dx = output.RX - 128.0, dy = output.RY - 128.0;
            double mag = Math.Sqrt(dx * dx + dy * dy);
            if (mag <= KEEP_ZONE) return; // toques pequenos: igual que siempre

            double newMag = KEEP_ZONE + (mag - KEEP_ZONE) * scale;
            double k = newMag / mag;
            output.RX = (byte)Math.Clamp(Math.Round(128.0 + dx * k), 0, 255);
            output.RY = (byte)Math.Clamp(Math.Round(128.0 + dy * k), 0, 255);
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
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    };
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, options));
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_AimPrecisionSaveFailed", "Could not save the aim precision settings: {0}", e.Message), false);
                }
            }
        }
    }
}
