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
    /// PhantomPad: intensidad de la vibracion por juego (100 % = la del perfil, 0 % = sin
    /// vibracion, 200 % = el doble). Se multiplica sobre la intensidad del perfil mientras ese
    /// juego esta en primer plano. Se guarda en %appdata%\PhantomPad\PhantomPadGameRumble.json.
    /// </summary>
    public static class PhantomGameRumble
    {
        private const string FILE_NAME = "PhantomPadGameRumble.json";
        public const int DEFAULT_PCT = 100;
        public const int MAX_PCT = 200;

        private static readonly object fileLock = new object();
        private static Dictionary<string, int> pctByGame = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;
        private static Timer saveTimer;
        private static string activeGamePath;

        // Intensidad del perfil de cada mando (antes de aplicar la del juego)
        private static readonly byte[] baseBoost = new byte[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] baseKnown = new bool[Global.MAX_DS4_CONTROLLER_COUNT];

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
                        var data = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath));
                        if (data != null) pctByGame = new Dictionary<string, int>(data, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch { /* archivo danado: todo al 100 % */ }

                loaded = true;
            }
        }

        public static int Get(string rulePath)
        {
            if (string.IsNullOrEmpty(rulePath)) return DEFAULT_PCT;
            EnsureLoaded();
            lock (fileLock)
            {
                return pctByGame.TryGetValue(rulePath, out int pct) ? Math.Clamp(pct, 0, MAX_PCT) : DEFAULT_PCT;
            }
        }

        public static void Set(string rulePath, int pct)
        {
            if (string.IsNullOrEmpty(rulePath)) return;
            EnsureLoaded();
            lock (fileLock)
            {
                pct = Math.Clamp(pct, 0, MAX_PCT);
                if (pct == DEFAULT_PCT) pctByGame.Remove(rulePath);
                else pctByGame[rulePath] = pct;
            }

            ScheduleSave();
            if (string.Equals(rulePath, activeGamePath, StringComparison.OrdinalIgnoreCase)) ApplyAll();
        }

        /// <summary>Lo llama el cambio automatico de perfil al entrar o salir de un juego.</summary>
        public static void SetActiveGame(string rulePath)
        {
            if (string.Equals(activeGamePath, rulePath, StringComparison.OrdinalIgnoreCase)) return;
            activeGamePath = rulePath;
            ApplyAll();
        }

        /// <summary>Se llama al cargar el perfil de un mando: guarda su intensidad y aplica la del juego.</summary>
        public static void OnProfileLoaded(int slot)
        {
            if (slot < 0 || slot >= baseBoost.Length) return;
            baseBoost[slot] = Global.RumbleBoost[slot];
            baseKnown[slot] = true;
            Apply(slot);
        }

        private static void ApplyAll()
        {
            for (int i = 0; i < baseBoost.Length; i++) Apply(i);
        }

        private static void Apply(int slot)
        {
            if (!baseKnown[slot]) return;
            int pct = activeGamePath != null ? Get(activeGamePath) : DEFAULT_PCT;
            Global.RumbleBoost[slot] = (byte)Math.Clamp(baseBoost[slot] * pct / 100, 0, 255);
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
                    File.WriteAllText(temp, JsonSerializer.Serialize(pctByGame, new JsonSerializerOptions { WriteIndented = true }));
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
                    else File.Move(temp, FilePath);
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_RumbleSaveFailed", "Could not save the vibration settings: {0}", e.Message), false);
                }
            }
        }
    }
}
