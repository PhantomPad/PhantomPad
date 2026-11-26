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
    /// PhantomPad: ajustes propios de CADA mando fisico (por su direccion MAC), para que con
    /// dos mandos cada uno conserve lo suyo aunque se conecten en otro orden:
    /// luz (encendida, brillo, color propio), gyro aim, combo para apagarlo
    /// y frecuencia de lectura por Bluetooth.
    /// Al conectarse un mando se copian sus ajustes a su ranura (1, 2...). Cada cambio desde
    /// la tarjeta se guarda de vuelta en su MAC.
    /// Se guarda en %appdata%\PhantomPad\PhantomPadControllers.json.
    /// </summary>
    public static class PhantomDevice
    {
        private const string FILE_NAME = "PhantomPadControllers.json";
        private const double POWER_HOLD_SECONDS = 0.0; // al instante, como en Steam

        public enum PowerCombo { None, PSCross, PSCircle, PSSquare, PSTriangle, PSOptions, PSShare }

        /// <summary>Frecuencias que se pueden elegir (ms entre lecturas). 0 = automatica.</summary>
        public static readonly int[] PollChoices = { 0, 1, 2, 4, 8 };

        public class Prefs
        {
            public bool LightEnabled { get; set; } = true;
            public int Brightness { get; set; } = 100;
            public bool UseOwnColor { get; set; }
            public string Color { get; set; } = "124,58,237";
            public bool GyroEnabled { get; set; }
            public int GyroSensitivity { get; set; } = 100;
            public int PollMs { get; set; } // 0 = automatica
            /// <summary>Ya se reviso si parece un mando no original (para no repetirlo).</summary>
            public bool CloneChecked { get; set; }

            // Calibracion de sticks (0 = sin calibrar). Zona muerta en la escala 0..127 del programa,
            // alcance en % (si el stick no llega al borde, se le da la salida maxima antes).
            public int CalLSDeadZone { get; set; }
            public int CalRSDeadZone { get; set; }
            public int CalLSMaxZone { get; set; }
            public int CalRSMaxZone { get; set; }
            public string CalDate { get; set; }
        }

        /// <summary>Resultado de una calibracion.</summary>
        public class StickCalibration
        {
            public int LSDeadZone, RSDeadZone, LSMaxZone, RSMaxZone;
            public string Date;
            public bool IsSet => LSDeadZone > 0 || RSDeadZone > 0 || LSMaxZone > 0 || RSMaxZone > 0;
        }

        /// <summary>
        /// Un mando original tiene una MAC de fabricante. Si la MAC esta marcada como
        /// "administrada localmente" (bit 0x02 del primer byte), casi seguro es una copia.
        /// </summary>
        public static bool LooksLikeClone(string mac)
        {
            if (string.IsNullOrEmpty(mac) || mac.Length < 2) return false;
            try { return (Convert.ToByte(mac.Substring(0, 2), 16) & 0x02) != 0; }
            catch { return false; }
        }

        /// <summary>Modo compatible para mandos DS4 no originales (opcion "copycat" del programa original).</summary>
        public static bool? GetCompatMode(DS4Device device) =>
            (device?.optionsStore as DS4ControllerOptions)?.IsCopyCat;

        public static void SetCompatMode(DS4Device device, bool value)
        {
            if (device?.optionsStore is not DS4ControllerOptions opts || opts.IsCopyCat == value) return;
            opts.IsCopyCat = value; // cambia al momento como se envian luz y vibracion
            Global.SaveControllerConfigs(device);
        }

        /// <summary>Colores para distinguir mandos nuevos (el 1 queda con el del perfil).</summary>
        private static readonly DS4Color[] SlotColors =
        {
            new DS4Color(124, 58, 237), new DS4Color(34, 211, 238), new DS4Color(34, 197, 94), new DS4Color(249, 115, 22),
            new DS4Color(236, 72, 153), new DS4Color(250, 204, 21), new DS4Color(37, 99, 235), new DS4Color(255, 255, 255),
        };

        private static readonly object fileLock = new object();
        private static Dictionary<string, Prefs> prefsByMac = new Dictionary<string, Prefs>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] slotMac = new string[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly DateTime[] comboStart = new DateTime[Global.MAX_DS4_CONTROLLER_COUNT];
        private static readonly bool[] comboFired = new bool[Global.MAX_DS4_CONTROLLER_COUNT];
        private static bool loaded;
        private static Timer saveTimer;

        /// <summary>Avisa a la interfaz que cambiaron los ajustes automaticos (p. ej. al conectar otro mando).</summary>
        public static event Action SlotsChanged;

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        /// <summary>
        /// true si el archivo existe pero no se pudo leer: en ese caso NO se guarda nada, para no
        /// borrar las calibraciones y ajustes del usuario con datos vacios.
        /// </summary>
        private static bool readFailed;

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (fileLock)
            {
                if (loaded) return;
                // Si otra copia del programa se esta cerrando, el archivo puede estar ocupado un
                // momento: se reintenta varias veces antes de rendirse.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        if (File.Exists(FilePath))
                        {
                            var data = JsonSerializer.Deserialize<Dictionary<string, Prefs>>(File.ReadAllText(FilePath), JsonOptions);
                            if (data != null) prefsByMac = new Dictionary<string, Prefs>(data, StringComparer.OrdinalIgnoreCase);
                        }

                        readFailed = false;
                        break;
                    }
                    catch (IOException)
                    {
                        readFailed = true;
                        Thread.Sleep(200);
                    }
                    catch (Exception)
                    {
                        // Contenido danado: se guarda una copia y se empieza de cero
                        try { File.Copy(FilePath, FilePath + ".danado", true); } catch { }
                        readFailed = false;
                        break;
                    }
                }

                if (readFailed)
                {
                    AppLogger.LogToGui(PPText.T("PPC_ControllerPrefsReadFailed",
                        "Could not read the controller settings (file in use). They are kept as they are; restart PhantomPad."), true);
                }

                loaded = true;
            }
        }

        private static bool IsValidMac(string mac) =>
            !string.IsNullOrEmpty(mac) && mac != DS4Device.BLANK_SERIAL;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private static bool ValidSlot(int slot) => slot >= 0 && slot < slotMac.Length;

        /// <summary>Ajustes del mando que esta en la ranura (null si no hay mando).</summary>
        public static Prefs Get(int slot)
        {
            if (!ValidSlot(slot)) return null;
            EnsureLoaded();
            string mac = slotMac[slot];
            if (mac == null)
            {
                // El mando pudo conectarse antes de saber su MAC: se enlaza ahora si ya la tiene
                DS4Device dev = Program.rootHub?.DS4Controllers[slot];
                if (dev != null && IsValidMac(dev.getMacAddress()))
                {
                    OnConnected(slot, dev);
                    ReloadProfile(slot);
                    mac = slotMac[slot];
                }

                if (mac == null) return null;
            }
            lock (fileLock)
            {
                return prefsByMac.TryGetValue(mac, out Prefs p) ? p : null;
            }
        }

        // ===================== Conexion y desconexion =====================

        /// <summary>
        /// Se llama al conectar un mando, ANTES de cargar su perfil: copia sus ajustes guardados
        /// a la ranura. Si es un mando nuevo, toma los valores actuales de la ranura.
        /// </summary>
        public static void OnConnected(int slot, DS4Device device)
        {
            if (!ValidSlot(slot) || device == null) return;
            EnsureLoaded();
            string mac = device.getMacAddress();
            if (!IsValidMac(mac))
            {
                // Aun no se sabe que mando es (pasa al reabrir el programa muy rapido): se esperan
                // a tener su MAC para cargar sus ajustes y calibracion
                slotMac[slot] = null;
                void LateSerial(object s, EventArgs e)
                {
                    if (!IsValidMac(device.getMacAddress())) return;
                    device.SerialChange -= LateSerial;
                    int current = device.DeviceSlotNumber;
                    if (!ValidSlot(current) || Program.rootHub?.DS4Controllers[current] != device) return;
                    OnConnected(current, device);
                    ReloadProfile(current);
                }

                device.SerialChange -= LateSerial;
                device.SerialChange += LateSerial;
                return;
            }

            slotMac[slot] = mac;
            comboStart[slot] = default;
            comboFired[slot] = false;

            Prefs p;
            bool isNew;
            lock (fileLock)
            {
                isNew = !prefsByMac.TryGetValue(mac, out p);
                if (isNew)
                {
                    p = new Prefs();
                    prefsByMac[mac] = p;
                }
            }

            LightbarDS4WinInfo light = Global.LightbarSettingsInfo[slot].ds4winSettings;
            if (isNew)
            {
                // Mando nuevo: se quedan los valores que ya tenia esa ranura
                p.LightEnabled = PhantomLight.IsEnabled(slot);
                p.Brightness = PhantomLight.GetBrightness(slot);
                p.GyroEnabled = PhantomGyro.IsEnabled(slot);
                p.GyroSensitivity = PhantomGyro.GetSensitivity(slot);
                if (light.useCustomLed || slot == 0)
                {
                    p.UseOwnColor = light.useCustomLed;
                    p.Color = ColorText(light.m_CustomLed);
                }
                else
                {
                    // Segundo mando (o mas): un color propio distinto para reconocerlo
                    p.UseOwnColor = true;
                    p.Color = ColorText(SlotColors[slot % SlotColors.Length]);
                }

                ScheduleSave();
            }

            // Mando que parece no original: activar el modo compatible una sola vez (luego decide el usuario)
            if (!p.CloneChecked)
            {
                p.CloneChecked = true;
                if (LooksLikeClone(mac) && GetCompatMode(device) == false)
                {
                    SetCompatMode(device, true);
                    AppLogger.LogToGui(PPText.T("PPC_CloneCompatOn",
                        "Controller {0} looks non-original: compatible mode was turned on so the light works.", slot + 1), false);
                }

                ScheduleSave();
            }

            // Pasar los ajustes del mando a su ranura
            PhantomLight.SetRuntime(slot, p.LightEnabled, p.Brightness);
            PhantomGyro.SetRuntime(slot, p.GyroEnabled, p.GyroSensitivity);
            light.useCustomLed = p.UseOwnColor;
            light.m_CustomLed = ParseColor(p.Color, light.m_CustomLed);
            RefreshAllPollRates();
        }

        public static void OnRemoved(int slot)
        {
            if (!ValidSlot(slot)) return;
            slotMac[slot] = null;
            RefreshAllPollRates();

            // Los mandos que quedan suben de puesto (si se apaga el jugador 1, el 2 pasa a ser el 1)
            System.Threading.Tasks.Task.Run(() =>
            {
                Thread.Sleep(800);
                try { CompactXInputSlots(); }
                catch (Exception e) { AppLogger.LogToGui(e.Message, false); }
            });
        }

        /// <summary>
        /// Windows da a cada mando de Xbox virtual un puesto fijo (jugador 1, 2...). Si se apaga el
        /// jugador 1, el otro se queda como jugador 2 y muchos juegos solo escuchan al 1.
        /// Aqui se vuelven a conectar los mandos virtuales que quedan, en orden, para que ocupen
        /// los primeros puestos.
        /// </summary>
        private static void CompactXInputSlots()
        {
            ControlService hub = Program.rootHub;
            if (hub == null || !hub.running) return;

            var pads = new List<(int Slot, DS4Device Device, Xbox360OutDevice Out)>();
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++)
            {
                DS4Device dev = hub.DS4Controllers[i];
                if (dev != null && dev.PrimaryDevice && !dev.IsRemoving && hub.outputDevices[i] is Xbox360OutDevice x)
                    pads.Add((i, dev, x));
            }

            bool needed = false;
            for (int k = 0; k < pads.Count; k++)
            {
                if (pads[k].Out.XinputSlotNum > k) needed = true;
            }

            if (!needed) return;

            foreach (var pad in pads)
            {
                RunHalted(pad.Device, () => hub.UnplugOutDev(pad.Slot, pad.Device, true, true));
            }

            Thread.Sleep(300);
            foreach (var pad in pads)
            {
                RunHalted(pad.Device, () => hub.PluginOutDev(pad.Slot, pad.Device));
                Thread.Sleep(150);
            }

            AppLogger.LogToGui(PPText.T("PPC_PlayersReordered",
                "A controller was turned off: the remaining controllers moved up (player 1 first)."), false);
        }

        /// <summary>Ejecuta una accion con el mando en pausa, reintentando si la pausa no llega a tiempo.</summary>
        private static void RunHalted(DS4Device device, Action action)
        {
            bool ran = false;
            Action wrapped = () => { action(); ran = true; };
            for (int attempt = 0; attempt < 6 && !ran; attempt++)
            {
                device.HaltReportingRunAction(wrapped);
            }
        }

        /// <summary>Guarda en la MAC del mando lo que hay ahora en su ranura (luz, color, gyro).</summary>
        public static void Capture(int slot)
        {
            Prefs p = Get(slot);
            if (p == null) return;
            LightbarDS4WinInfo light = Global.LightbarSettingsInfo[slot].ds4winSettings;
            p.LightEnabled = PhantomLight.IsEnabled(slot);
            p.Brightness = PhantomLight.GetBrightness(slot);
            p.GyroEnabled = PhantomGyro.IsEnabled(slot);
            p.GyroSensitivity = PhantomGyro.GetSensitivity(slot);
            p.UseOwnColor = light.useCustomLed;
            p.Color = ColorText(light.m_CustomLed);
            ScheduleSave();
        }

        /// <summary>Se llama al final de cargar cualquier perfil en una ranura.</summary>
        public static void ApplyAfterProfileLoad(int slot)
        {
            if (!ValidSlot(slot)) return;
            ApplyPollRate(slot);
            ApplyCalibration(slot);
            PhantomGameRumble.OnProfileLoaded(slot); // vibracion por juego
            PhantomBattery.ApplyFlash(slot);         // luz roja con bateria baja
        }

        // ===================== Calibracion de sticks (anti-drift) =====================

        public static StickCalibration GetCalibration(int slot)
        {
            Prefs p = Get(slot);
            if (p == null) return new StickCalibration();
            return new StickCalibration
            {
                LSDeadZone = p.CalLSDeadZone, RSDeadZone = p.CalRSDeadZone,
                LSMaxZone = p.CalLSMaxZone, RSMaxZone = p.CalRSMaxZone, Date = p.CalDate,
            };
        }

        /// <summary>Guarda la calibracion del mando y la aplica al momento (null = quitarla).</summary>
        public static void SetCalibration(int slot, StickCalibration cal)
        {
            Prefs p = Get(slot);
            if (p == null) return;
            p.CalLSDeadZone = cal?.LSDeadZone ?? 0;
            p.CalRSDeadZone = cal?.RSDeadZone ?? 0;
            p.CalLSMaxZone = cal?.LSMaxZone ?? 0;
            p.CalRSMaxZone = cal?.RSMaxZone ?? 0;
            p.CalDate = cal == null ? null : DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            Save(); // la calibracion se guarda al instante (no espera)
            ReloadProfile(slot); // vuelve a cargar el perfil para que se apliquen (o se quiten) los valores
        }

        /// <summary>
        /// La calibracion es un minimo de seguridad encima de cualquier perfil o juego: la zona
        /// muerta nunca baja de lo medido (asi el stick no se mueve solo) y, si el stick no llega
        /// al borde, el alcance se ajusta para que si llegue a la salida maxima.
        /// </summary>
        private static void ApplyCalibration(int slot)
        {
            Prefs p = Get(slot);
            if (p == null) return;
            if (p.CalLSDeadZone > 0) Global.LSModInfo[slot].deadZone = Math.Max(Global.LSModInfo[slot].deadZone, p.CalLSDeadZone);
            if (p.CalRSDeadZone > 0) Global.RSModInfo[slot].deadZone = Math.Max(Global.RSModInfo[slot].deadZone, p.CalRSDeadZone);
            if (p.CalLSMaxZone > 0) Global.LSModInfo[slot].maxZone = Math.Min(Global.LSModInfo[slot].maxZone, p.CalLSMaxZone);
            if (p.CalRSMaxZone > 0) Global.RSModInfo[slot].maxZone = Math.Min(Global.RSModInfo[slot].maxZone, p.CalRSMaxZone);
        }

        /// <summary>Vuelve a cargar el perfil que usa ahora el mando de la ranura (en segundo plano).</summary>
        public static void ReloadProfile(int slot)
        {
            DS4Device dev = Program.rootHub?.DS4Controllers[slot];
            if (dev == null) return;
            System.Threading.Tasks.Task.Run(() => RunHalted(dev, () =>
            {
                if (Global.useTempProfile[slot])
                    Global.LoadTempProfile(slot, Global.tempprofilename[slot], false, Program.rootHub);
                else
                    Global.LoadProfile(slot, false, Program.rootHub);
            }));
        }

        // ===================== Combo para apagar =====================

        public static PowerCombo GetPowerCombo(int slot) => PhantomCommands.PowerOff; // igual para todos los mandos (Opciones → Comandos)

        private static bool IsHeld(PowerCombo combo, DS4State s)
        {
            if (!s.PS) return false;
            switch (combo)
            {
                case PowerCombo.PSCross: return s.Cross;
                case PowerCombo.PSCircle: return s.Circle;
                case PowerCombo.PSSquare: return s.Square;
                case PowerCombo.PSTriangle: return s.Triangle;
                case PowerCombo.PSOptions: return s.Options;
                case PowerCombo.PSShare: return s.Share;
                default: return false;
            }
        }

        private static void Release(PowerCombo combo, DS4State s)
        {
            s.PS = false;
            switch (combo)
            {
                case PowerCombo.PSCross: s.Cross = false; break;
                case PowerCombo.PSCircle: s.Circle = false; break;
                case PowerCombo.PSSquare: s.Square = false; break;
                case PowerCombo.PSTriangle: s.Triangle = false; break;
                case PowerCombo.PSOptions: s.Options = false; break;
                case PowerCombo.PSShare: s.Share = false; break;
            }
        }

        /// <summary>
        /// Se llama en cada lectura del mando (antes de traducirla). Si se mantiene el combo,
        /// esos botones no llegan al juego y el mando se apaga al instante (Bluetooth).
        /// </summary>
        public static void CheckPowerCombo(int slot, DS4Device device, DS4State state)
        {
            if (!ValidSlot(slot) || device == null || state == null) return;
            PowerCombo combo = GetPowerCombo(slot);
            if (combo == PowerCombo.None || !IsHeld(combo, state))
            {
                comboStart[slot] = default;
                comboFired[slot] = false;
                return;
            }

            Release(combo, state); // mientras se mantiene, el juego no ve esos botones
            DateTime now = DateTime.UtcNow;
            if (comboStart[slot] == default) comboStart[slot] = now;
            if (comboFired[slot] || (now - comboStart[slot]).TotalSeconds < POWER_HOLD_SECONDS) return;

            comboFired[slot] = true;
            switch (device.ConnectionType)
            {
                case ConnectionType.BT:
                    AppLogger.LogToGui(PPText.T("PPC_PowerOffCombo", "Controller {0}: turning off (button combo).", slot + 1), false);
                    device.queueEvent(() => device.DisconnectBT());
                    break;
                case ConnectionType.SONYWA:
                    AppLogger.LogToGui(PPText.T("PPC_PowerOffCombo", "Controller {0}: turning off (button combo).", slot + 1), false);
                    device.queueEvent(() => device.DisconnectDongle());
                    break;
                default:
                    AppLogger.LogToGui(PPText.T("PPC_PowerOffUsb", "Controller {0} is connected by USB cable: it cannot be turned off from here. Unplug the cable.", slot + 1), false);
                    break;
            }
        }

        // ===================== Frecuencia de lectura (Bluetooth) =====================

        public static int GetPollChoice(int slot) => Get(slot)?.PollMs ?? 0;

        public static void SetPollChoice(int slot, int ms)
        {
            Prefs p = Get(slot);
            if (p == null) return;
            p.PollMs = Array.IndexOf(PollChoices, ms) >= 0 ? ms : 0;
            ScheduleSave();
            ApplyPollRate(slot);
            SlotsChanged?.Invoke();
        }

        /// <summary>Cuantos mandos hay conectados ahora.</summary>
        public static int ConnectedCount()
        {
            int count = 0;
            for (int i = 0; i < slotMac.Length; i++) if (slotMac[i] != null) count++;
            return count;
        }

        /// <summary>
        /// Solo los DS4 por Bluetooth aceptan que se les pida la frecuencia. Por USB (o en otros
        /// mandos) la frecuencia la decide el propio mando.
        /// </summary>
        public static bool CanSetPollRate(DS4Device device) =>
            device != null && device.ConnectionType == ConnectionType.BT &&
            device.DeviceType == InputDevices.InputDeviceType.DS4;

        /// <summary>ms entre lecturas que se le pide al mando (automatica: 1 mando = 1 ms; 2 = 2 ms; mas = 4 ms).</summary>
        public static int EffectivePollMs(int slot)
        {
            int choice = GetPollChoice(slot);
            if (choice > 0) return choice;
            int count = ConnectedCount();
            return count <= 1 ? 1 : count == 2 ? 2 : 4;
        }

        private static void ApplyPollRate(int slot)
        {
            if (!ValidSlot(slot) || slotMac[slot] == null) return;
            DS4Device device = Program.rootHub?.DS4Controllers[slot];
            if (!CanSetPollRate(device)) return;
            int ms = EffectivePollMs(slot);
            Global.BTPollRate[slot] = ms;
            device.setBTPollRate(ms);
        }

        private static void RefreshAllPollRates()
        {
            for (int i = 0; i < slotMac.Length; i++) ApplyPollRate(i);
            SlotsChanged?.Invoke();
        }

        // ===================== Utilidades =====================

        private static string ColorText(DS4Color c) => $"{c.red},{c.green},{c.blue}";

        private static DS4Color ParseColor(string text, DS4Color fallback)
        {
            try
            {
                string[] parts = text.Split(',');
                return new DS4Color(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
            }
            catch
            {
                return fallback;
            }
        }

        private static bool savePending;

        private static void ScheduleSave()
        {
            lock (fileLock)
            {
                if (saveTimer == null)
                {
                    saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
                    // Si el programa se cierra con un cambio sin guardar, se guarda antes de salir
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => { if (savePending) Save(); };
                }

                savePending = true;
                saveTimer.Change(500, Timeout.Infinite);
            }
        }

        private static void Save()
        {
            lock (fileLock)
            {
                // Si el archivo no se pudo leer al iniciar, no se toca (se perderian las calibraciones)
                if (readFailed) return;
                savePending = false;
                try
                {
                    // Guardado seguro: primero a un archivo temporal y luego se reemplaza de golpe,
                    // asi un cierre a mitad de guardar nunca deja el archivo vacio o roto
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(prefsByMac, JsonOptions));
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak", true);
                    else File.Move(temp, FilePath);
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PPC_ControllerPrefsSaveFailed", "Could not save the controller settings: {0}", e.Message), false);
                }
            }
        }
    }
}
