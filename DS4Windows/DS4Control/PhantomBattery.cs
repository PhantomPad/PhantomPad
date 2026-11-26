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
    /// PhantomPad: aviso de bateria baja (Configuraciones → Bateria).
    /// - Aviso de Windows una vez cuando un mando baja del limite (y no se esta cargando).
    /// - Opcional: la luz del mando parpadea en rojo por debajo del limite (usa el parpadeo de
    ///   bateria que ya trae el programa).
    /// Se guarda en %appdata%\PhantomPad\PhantomPadBattery.json.
    /// </summary>
    public static class PhantomBattery
    {
        private const string FILE_NAME = "PhantomPadBattery.json";
        public static readonly int[] ThresholdChoices = { 10, 15, 20, 25, 30 };

        public class BatteryPrefs
        {
            public bool WarnEnabled { get; set; } = true;
            public int Threshold { get; set; } = 20;
            public bool FlashLight { get; set; } = true;
        }

        private static readonly object fileLock = new object();
        private static BatteryPrefs prefs;
        private static Timer checkTimer;
        private static readonly bool[] warned = new bool[Global.MAX_DS4_CONTROLLER_COUNT];

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        public static BatteryPrefs Prefs
        {
            get
            {
                if (prefs != null) return prefs;
                lock (fileLock)
                {
                    if (prefs != null) return prefs;
                    BatteryPrefs read = null;
                    try
                    {
                        if (File.Exists(FilePath)) read = JsonSerializer.Deserialize<BatteryPrefs>(File.ReadAllText(FilePath));
                    }
                    catch { }

                    prefs = read ?? new BatteryPrefs();
                    if (Array.IndexOf(ThresholdChoices, prefs.Threshold) < 0) prefs.Threshold = 20;
                    return prefs;
                }
            }
        }

        public static void SetWarnEnabled(bool value) { Prefs.WarnEnabled = value; Save(); }

        public static void SetThreshold(int value)
        {
            Prefs.Threshold = Array.IndexOf(ThresholdChoices, value) >= 0 ? value : 20;
            Array.Clear(warned, 0, warned.Length); // volver a avisar con el limite nuevo
            Save();
            ReloadAll();
        }

        public static void SetFlashLight(bool value)
        {
            Prefs.FlashLight = value;
            Save();
            ReloadAll(); // al apagarlo, el perfil vuelve a su propio parpadeo
        }

        /// <summary>Se llama al cargar el perfil de un mando: activa el parpadeo rojo si esta elegido.</summary>
        public static void ApplyFlash(int slot)
        {
            EnsureTimer();
            if (!Prefs.FlashLight || slot < 0 || slot >= Global.MAX_DS4_CONTROLLER_COUNT) return;
            LightbarDS4WinInfo light = Global.LightbarSettingsInfo[slot].ds4winSettings;
            light.flashAt = Prefs.Threshold;
            light.flashType = 0;
            light.m_FlashLed = new DS4Color(255, 0, 0);
        }

        private static void ReloadAll()
        {
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++)
            {
                if (Program.rootHub?.DS4Controllers[i] != null) PhantomDevice.ReloadProfile(i);
            }
        }

        private static void EnsureTimer()
        {
            if (checkTimer != null) return;
            lock (fileLock)
            {
                checkTimer ??= new Timer(_ => Check(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
            }
        }

        /// <summary>Revisa la bateria de cada mando y avisa una vez al bajar del limite.</summary>
        private static void Check()
        {
            try
            {
                int limit = Prefs.Threshold;
                for (int i = 0; i < warned.Length; i++)
                {
                    DS4Device dev = i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT ? Program.rootHub?.DS4Controllers[i] : null;
                    if (dev == null || !dev.PrimaryDevice)
                    {
                        warned[i] = false;
                        continue;
                    }

                    int battery = dev.getBattery();
                    if (dev.isCharging() || battery >= limit + 5)
                    {
                        warned[i] = false; // se cargo: puede volver a avisar
                        continue;
                    }

                    if (Prefs.WarnEnabled && !warned[i] && battery > 0 && battery <= limit)
                    {
                        warned[i] = true;
                        string msg = PPText.T("PPC_LowBattery", "Controller {0}: battery at {1} %. Charge it soon.", i + 1, battery);
                        AppLogger.LogToGui(msg, true);
                        AppLogger.LogToTray(msg, true, true);
                    }
                }
            }
            catch { /* un mando desconectandose justo ahora */ }
        }

        private static void Save()
        {
            lock (fileLock)
            {
                try
                {
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(Prefs, new JsonSerializerOptions { WriteIndented = true }));
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
                    else File.Move(temp, FilePath);
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_BatterySaveFailed", "Could not save the battery settings: {0}", e.Message), false);
                }
            }
        }
    }
}
