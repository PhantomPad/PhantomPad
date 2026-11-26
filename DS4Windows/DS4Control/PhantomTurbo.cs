/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: turbo / disparo rapido por juego (Juegos → cada juego). Apagado por defecto y
    /// SOLO para juegos offline: si en la carpeta del juego hay un anti-trampas, se bloquea.
    /// Mientras se mantiene el boton elegido, PhantomPad lo pulsa y lo suelta solo varias veces
    /// por segundo. Queda activado en ese juego hasta que el usuario lo apague.
    /// Se guarda en %appdata%\PhantomPad\PhantomPadTurbo.json.
    /// </summary>
    public static class PhantomTurbo
    {
        private const string FILE_NAME = "PhantomPadTurbo.json";
        public const int MIN_RATE = 5, MAX_RATE = 20, DEFAULT_RATE = 10;

        public enum TurboButton { R2, R1, L2, Cross, Square, Circle, Triangle }

        public class TurboSetting
        {
            public bool Enabled { get; set; }
            public TurboButton Button { get; set; } = TurboButton.R2;
            public int Rate { get; set; } = DEFAULT_RATE; // pulsaciones por segundo
        }

        private static readonly object fileLock = new object();
        private static Dictionary<string, TurboSetting> settings = new Dictionary<string, TurboSetting>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;
        private static Timer saveTimer;
        private static string activeGamePath;
        private static readonly Stopwatch clock = Stopwatch.StartNew();

        // Lo que se usa en cada lectura del mando (se cambia de golpe)
        private static volatile TurboSetting active;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (fileLock)
            {
                if (loaded) return;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var data = JsonSerializer.Deserialize<Dictionary<string, TurboSetting>>(File.ReadAllText(FilePath), JsonOptions);
                        if (data != null) settings = new Dictionary<string, TurboSetting>(data, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { /* archivo danado: todo apagado */ }

                loaded = true;
            }
        }

        public static TurboSetting Get(string rulePath)
        {
            EnsureLoaded();
            lock (fileLock)
            {
                return rulePath != null && settings.TryGetValue(rulePath, out TurboSetting s)
                    ? new TurboSetting { Enabled = s.Enabled, Button = s.Button, Rate = Math.Clamp(s.Rate, MIN_RATE, MAX_RATE) }
                    : new TurboSetting();
            }
        }

        public static void SetEnabled(string rulePath, bool value) => Update(rulePath, s => s.Enabled = value);
        public static void SetButton(string rulePath, TurboButton value) => Update(rulePath, s => s.Button = value);
        public static void SetRate(string rulePath, int value) => Update(rulePath, s => s.Rate = Math.Clamp(value, MIN_RATE, MAX_RATE));

        private static void Update(string rulePath, Action<TurboSetting> change)
        {
            if (string.IsNullOrEmpty(rulePath)) return;
            EnsureLoaded();
            lock (fileLock)
            {
                if (!settings.TryGetValue(rulePath, out TurboSetting s))
                {
                    s = new TurboSetting();
                    settings[rulePath] = s;
                }

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

            // Por seguridad se vuelve a revisar el anti-trampas (el juego pudo actualizarse)
            if (rulePath != null && Get(rulePath).Enabled)
            {
                Task.Run(() =>
                {
                    string found = FindAntiCheat(rulePath);
                    if (found == null) return;
                    SetEnabled(rulePath, false);
                    AppLogger.LogToGui(PPText.T("PPC_TurboBlocked",
                        "Turbo was turned off: this game has an anti-cheat ({0}). Turbo is only for offline games.", found), true);
                });
            }
        }

        private static void Recalculate()
        {
            string game = activeGamePath;
            TurboSetting s = Get(game);
            active = game != null && s.Enabled ? s : null;
        }

        /// <summary>Si el boton del turbo esta apretado en el mando fisico (antes de traducirlo).</summary>
        public static bool IsHeld(DS4State raw)
        {
            TurboSetting s = active;
            if (s == null || raw == null) return false;
            switch (s.Button)
            {
                case TurboButton.R2: return raw.R2 > 100;
                case TurboButton.L2: return raw.L2 > 100;
                case TurboButton.R1: return raw.R1;
                case TurboButton.Cross: return raw.Cross;
                case TurboButton.Square: return raw.Square;
                case TurboButton.Circle: return raw.Circle;
                case TurboButton.Triangle: return raw.Triangle;
                default: return false;
            }
        }

        /// <summary>
        /// Se llama en cada lectura del mando, justo antes de enviarla al mando virtual: mientras se
        /// mantiene el boton, la mitad del tiempo se envia pulsado y la otra mitad suelto.
        /// </summary>
        public static void Apply(bool held, DS4State output)
        {
            TurboSetting s = active;
            if (s == null || !held || output == null) return;

            long periodMs = 1000 / s.Rate;
            bool pressed = clock.ElapsedMilliseconds % periodMs < periodMs / 2;
            if (pressed) return; // en la mitad "pulsada" se deja tal cual

            switch (s.Button)
            {
                case TurboButton.R2: output.R2 = 0; output.R2Btn = false; break;
                case TurboButton.L2: output.L2 = 0; output.L2Btn = false; break;
                case TurboButton.R1: output.R1 = false; break;
                case TurboButton.Cross: output.Cross = false; break;
                case TurboButton.Square: output.Square = false; break;
                case TurboButton.Circle: output.Circle = false; break;
                case TurboButton.Triangle: output.Triangle = false; break;
            }
        }

        // ===================== Deteccion de anti-trampas =====================

        /// <summary>Palabras que delatan un anti-trampas en archivos o carpetas del juego.</summary>
        private static readonly string[] AntiCheatMarkers =
        {
            "easyanticheat", "anticheat", "anti-cheat", "anti_cheat", "battleye", "beservice",
            "pnkbstr", "punkbuster", "xigncode", "gameguard", "nprotect", "vgk.sys",
            "equ8", "faceit",
        };

        /// <summary>
        /// Busca un anti-trampas en la carpeta del juego (hasta 4 niveles). Devuelve lo encontrado
        /// (nombre del archivo o carpeta) o null si no hay.
        /// </summary>
        public static string FindAntiCheat(string rulePath)
        {
            try
            {
                string folder = rulePath?.TrimStart('^');
                if (string.IsNullOrEmpty(folder)) return null;
                if (File.Exists(folder)) folder = Path.GetDirectoryName(folder);
                if (!Directory.Exists(folder)) return null;
                return Search(new DirectoryInfo(folder), 0, new int[1]);
            }
            catch
            {
                return null;
            }
        }

        private static string Search(DirectoryInfo dir, int depth, int[] counter)
        {
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos().ToList(); }
            catch { return null; } // sin permiso para leer esa carpeta

            foreach (FileSystemInfo entry in entries)
            {
                if (++counter[0] > 50000) return null; // carpeta enorme: se deja de buscar
                string name = entry.Name.ToLowerInvariant();
                foreach (string marker in AntiCheatMarkers)
                {
                    if (name.Contains(marker)) return entry.Name;
                }
            }

            if (depth >= 4) return null;
            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    string found = Search(sub, depth + 1, counter);
                    if (found != null) return found;
                }
            }

            return null;
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
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
                    else File.Move(temp, FilePath);
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_TurboSaveFailed", "Could not save the turbo settings: {0}", e.Message), false);
                }
            }
        }
    }
}
