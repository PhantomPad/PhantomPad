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
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DS4WinWPF.DS4Control;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: Configuraciones → Ocultar mandos.
    /// Con HidHide (controlador de Nefarius) los juegos dejan de ver el mando fisico
    /// y solo ven el mando Xbox virtual de PhantomPad: asi no hay doble entrada.
    /// PhantomPad queda en la lista de programas permitidos, asi que sigue leyendo el mando.
    /// Al detener o cerrar PhantomPad, todo lo que se oculto se vuelve a mostrar.
    /// Se guarda en %appdata%\PhantomPad\PhantomPadHide.json.
    /// </summary>
    public static class PhantomHide
    {
        private const string FILE_NAME = "PhantomPadHide.json";

        private class HidePrefs
        {
            /// <summary>Interruptor del usuario.</summary>
            public bool Enabled { get; set; } = true;
            /// <summary>Mandos que PhantomPad agrego a la lista de HidHide (para quitarlos despues).</summary>
            public List<string> Added { get; set; } = new List<string>();
            /// <summary>HidHide estaba apagado y PhantomPad lo encendio.</summary>
            public bool TurnedOn { get; set; }
        }

        private static readonly object fileLock = new object();
        private static readonly object hideLock = new object();
        private static HidePrefs prefs;
        private static bool exitHooked;

        /// <summary>Mandos conectados ahora: id de Windows → nombre.</summary>
        private static readonly Dictionary<string, string> connected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, string> slotIds = new Dictionary<int, string>();

        /// <summary>Se llama antes de ocultar el primero, para asegurar que PhantomPad esta permitido.</summary>
        public static Action EnsureAppAllowed;

        public static event Action Changed;

        /// <summary>Texto del ultimo problema (vacio si todo va bien).</summary>
        public static string LastError { get; private set; } = "";

        public static bool Installed => Global.hidHideInstalled;

        private static HidePrefs Prefs
        {
            get
            {
                if (prefs != null) return prefs;
                lock (fileLock)
                {
                    if (prefs != null) return prefs;
                    HidePrefs read = null;
                    try
                    {
                        if (File.Exists(FilePath)) read = JsonSerializer.Deserialize<HidePrefs>(File.ReadAllText(FilePath));
                    }
                    catch { }

                    prefs = read ?? new HidePrefs();
                    if (prefs.Added == null) prefs.Added = new List<string>();
                    if (!exitHooked)
                    {
                        exitHooked = true;
                        // Si Windows cierra el programa sin pasar por "Detener", tambien se muestran
                        AppDomain.CurrentDomain.ProcessExit += (s, e) => RestoreAll();
                    }
                    return prefs;
                }
            }
        }

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        public static bool Enabled
        {
            get => Prefs.Enabled;
            set
            {
                if (Prefs.Enabled == value) return;
                Prefs.Enabled = value;
                Save();
                if (value) Task.Run(HideAllConnected);
                else Task.Run(RestoreAll);
                Changed?.Invoke();
            }
        }

        /// <summary>Cuantos mandos estan ocultos ahora mismo.</summary>
        public static int HiddenCount
        {
            get { lock (hideLock) return Prefs.Added.Count; }
        }

        /// <summary>Nombres de los mandos conectados y si estan ocultos.</summary>
        public static List<(string Name, bool Hidden)> ConnectedList()
        {
            lock (hideLock)
            {
                return connected.Select(kv => (kv.Value, Prefs.Added.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))).ToList();
            }
        }

        /// <summary>Al abrir PhantomPad: si la vez anterior se cerro de golpe, mostrar lo que quedo oculto.</summary>
        public static void Init()
        {
            if (Prefs.Added.Count > 0 || Prefs.TurnedOn) RestoreAll();
        }

        /// <summary>Un mando se conecto (PhantomPad ya lo abrio): ocultarlo a los juegos.</summary>
        public static void OnConnected(int slot, DS4Device device)
        {
            string id = "";
            try { id = Global.GetInstanceIdFromDevicePath(device.HidDevice.DevicePath); } catch { }
            if (string.IsNullOrEmpty(id)) return;

            lock (hideLock)
            {
                connected[id] = device.DisplayName;
                slotIds[slot] = id;
            }

            if (Enabled && Installed) Task.Run(() => Hide(id));
            Changed?.Invoke();
        }

        /// <summary>Un mando se desconecto. Sigue en la lista: si vuelve, ya esta oculto.</summary>
        public static void OnRemoved(int slot)
        {
            lock (hideLock)
            {
                if (slotIds.TryGetValue(slot, out string id))
                {
                    slotIds.Remove(slot);
                    if (!slotIds.ContainsValue(id)) connected.Remove(id);
                }
            }
            Changed?.Invoke();
        }

        /// <summary>"Detener" o cerrar: los mandos dejan de pasar por PhantomPad, asi que se muestran.</summary>
        public static void OnServiceStopped()
        {
            lock (hideLock)
            {
                connected.Clear();
                slotIds.Clear();
            }
            RestoreAll();
        }

        private static void HideAllConnected()
        {
            List<string> ids;
            lock (hideLock) ids = connected.Keys.ToList();
            foreach (string id in ids) Hide(id);
        }

        private static void Hide(string id)
        {
            if (!Installed) return;
            // Cada vez: si PhantomPad no esta permitido, al reconectar el mando no podria leerlo
            try { EnsureAppAllowed?.Invoke(); } catch { }

            lock (hideLock)
            {
                try
                {
                    using (HidHideAPIDevice hh = OpenHidHide())
                    {
                        if (!hh.IsOpen())
                        {
                            SetError(PPText.T("PP_HideErrOpen", "Could not talk to HidHide. Restart the PC and try again."));
                            return;
                        }

                        if (hh.GetWhiteListInverseState())
                        {
                            SetError(PPText.T("PP_HideErrInverse", "HidHide is in \"inverse\" mode. Turn that option off in the HidHide app so PhantomPad can hide the controllers."));
                            return;
                        }

                        List<string> list = Clean(hh.GetBlacklist());
                        if (!list.Contains(id, StringComparer.OrdinalIgnoreCase))
                        {
                            list.Add(id);
                            hh.SetBlacklist(list);
                            if (!Prefs.Added.Contains(id, StringComparer.OrdinalIgnoreCase)) Prefs.Added.Add(id);
                        }

                        if (!hh.GetActiveState())
                        {
                            hh.SetActiveState(true);
                            Prefs.TurnedOn = true;
                        }
                    }
                    LastError = "";
                    Save();
                    AppLogger.LogToGui(PPText.T("PP_HideLogHidden", "Physical controller hidden from games (they only see the virtual Xbox one)"), false);
                }
                catch (Exception e)
                {
                    SetError(e.Message);
                }
            }
            Changed?.Invoke();
        }

        /// <summary>Volver a mostrar a los juegos todo lo que oculto PhantomPad.</summary>
        public static void RestoreAll()
        {
            if (prefs == null && !File.Exists(FilePath)) return;
            lock (hideLock)
            {
                if (Prefs.Added.Count == 0 && !Prefs.TurnedOn) return;
                try
                {
                    using (HidHideAPIDevice hh = OpenHidHide())
                    {
                        if (!hh.IsOpen()) return; // HidHide ya no esta: no hay nada oculto
                        List<string> list = Clean(hh.GetBlacklist());
                        int before = list.Count;
                        list.RemoveAll(x => Prefs.Added.Contains(x, StringComparer.OrdinalIgnoreCase));
                        if (list.Count != before) hh.SetBlacklist(list);
                        if (Prefs.TurnedOn) hh.SetActiveState(false); // estaba apagado antes de PhantomPad
                    }
                    Prefs.Added.Clear();
                    Prefs.TurnedOn = false;
                    Save();
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                }
            }
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>HidHide solo deja hablar a un programa a la vez: reintentar un poco si esta ocupado.</summary>
        private static HidHideAPIDevice OpenHidHide()
        {
            HidHideAPIDevice hh = new HidHideAPIDevice();
            for (int i = 0; i < 10 && !hh.IsOpen(); i++)
            {
                hh.Dispose();
                System.Threading.Thread.Sleep(150);
                hh = new HidHideAPIDevice();
            }
            return hh;
        }

        private static List<string> Clean(List<string> list) =>
            list.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        private static void SetError(string text)
        {
            LastError = text;
            AppLogger.LogToGui(PPText.T("PP_HideLogError", "Hide controllers: {0}", text), true);
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
