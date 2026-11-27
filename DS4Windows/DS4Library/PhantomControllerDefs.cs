/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using DS4Windows.InputDevices;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: definiciones de mandos en un archivo (%appdata%\PhantomPad\Mandos.json).
    /// Los mandos del archivo se suman a los incluidos de fabrica sin tener que recompilar.
    /// Una entrada con el mismo VID/PID que uno incluido lo reemplaza, y con "Activo": false lo desactiva.
    /// </summary>
    public static class PhantomControllerDefs
    {
        private const string FILE_NAME = "Mandos.json";

        /// <summary>Tipos de mando que entiende el programa (nombre en el archivo → tipo interno).</summary>
        public static readonly Dictionary<string, InputDeviceType> Types = new Dictionary<string, InputDeviceType>(StringComparer.OrdinalIgnoreCase)
        {
            ["DS4"] = InputDeviceType.DS4,
            ["DualSense"] = InputDeviceType.DualSense,
            ["SwitchPro"] = InputDeviceType.SwitchPro,
            ["JoyConL"] = InputDeviceType.JoyConL,
            ["JoyConR"] = InputDeviceType.JoyConR,
            ["JoyConGrip"] = InputDeviceType.JoyConGrip,
            ["DS3"] = InputDeviceType.DS3,
        };

        /// <summary>Opciones especiales (nombre en el archivo → bandera interna).</summary>
        public static readonly Dictionary<string, VidPidFeatureSet> Options = new Dictionary<string, VidPidFeatureSet>(StringComparer.OrdinalIgnoreCase)
        {
            ["SinLuzNiVibracion"] = VidPidFeatureSet.NoOutputData,
            ["SinBateria"] = VidPidFeatureSet.NoBatteryReading,
            ["SinCalibracionGiroscopio"] = VidPidFeatureSet.NoGyroCalib,
            ["SoloEntrada0x01"] = VidPidFeatureSet.OnlyInputData0x01,
            ["SoloSalida0x05"] = VidPidFeatureSet.OnlyOutputData0x05,
            ["Audio"] = VidPidFeatureSet.MonitorAudio,
            ["DispositivoDelFabricante"] = VidPidFeatureSet.VendorDefinedDevice,
        };

        public class Def
        {
            public string Nombre { get; set; } = "Mando";
            public string VID { get; set; }
            public string PID { get; set; }
            public string Tipo { get; set; } = "DS4";
            public List<string> Opciones { get; set; } = new List<string>();
            public bool Activo { get; set; } = true;
        }

        private class FileData
        {
            public string Ayuda { get; set; }
            public List<string> TiposValidos { get; set; }
            public List<string> OpcionesValidas { get; set; }
            public List<Def> Mandos { get; set; } = new List<Def>();
        }

        private static readonly object fileLock = new object();
        private static List<Def> defs = new List<Def>();
        private static bool applied;
        private static string lastError;

        /// <summary>Error del archivo (si lo hay), para mostrarlo en la pantalla.</summary>
        public static string LastError => lastError;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);

        /// <summary>Mandos del archivo (copia).</summary>
        public static List<Def> Defs { get { lock (fileLock) return defs.Select(Clone).ToList(); } }

        /// <summary>Se llama antes de buscar mandos: la primera vez lee el archivo y prepara la lista.</summary>
        public static void EnsureApplied()
        {
            if (applied) return;
            Reload();
        }

        /// <summary>Vuelve a leer el archivo (lo crea con instrucciones si no existe) y aplica la lista.</summary>
        public static void Reload()
        {
            lock (fileLock)
            {
                lastError = null;
                try
                {
                    if (!File.Exists(FilePath))
                    {
                        defs = new List<Def>();
                        Save();
                    }
                    else
                    {
                        FileData data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath), JsonOptions);
                        defs = data?.Mandos ?? new List<Def>();
                    }
                }
                catch (Exception e)
                {
                    // Archivo con un error: se siguen usando los mandos incluidos y se avisa
                    lastError = e.Message;
                    defs = new List<Def>();
                    AppLogger.LogToGui(PPText.T("PPC_ControllerDefsError", "The controllers file (Mandos.json) has an error and was ignored: {0}", e.Message), true);
                }

                Apply();
            }
        }

        /// <summary>Junta los mandos incluidos con los del archivo y se los pasa al buscador de mandos.</summary>
        private static void Apply()
        {
            var result = new List<VidPidInfo>();
            var overridden = new HashSet<(int, int)>();
            foreach (Def d in defs)
            {
                if (!TryParseId(d.VID, out int vid) || !TryParseId(d.PID, out int pid)) continue;
                overridden.Add((vid, pid));
                if (!d.Activo) continue;
                result.Add(ToInfo(d, vid, pid));
            }

            foreach (VidPidInfo info in DS4Devices.BuiltInDevices)
            {
                if (!overridden.Contains((info.vid, info.pid))) result.Add(info);
            }

            // Sin repetidos (la busqueda de mandos necesita un solo registro por VID/PID)
            VidPidInfo[] unique = result.GroupBy(i => (i.vid, i.pid)).Select(g => g.First()).ToArray();
            DS4Devices.SetKnownDevices(unique);
            applied = true;
        }

        private static VidPidInfo ToInfo(Def d, int vid, int pid)
        {
            InputDeviceType type = Types.TryGetValue(d.Tipo ?? "DS4", out InputDeviceType t) ? t : InputDeviceType.DS4;
            VidPidFeatureSet features = VidPidFeatureSet.DefaultDS4;
            foreach (string o in d.Opciones ?? new List<string>())
            {
                if (Options.TryGetValue(o, out VidPidFeatureSet f)) features |= f;
            }

            CheckConnectionDelegate check = type switch
            {
                InputDeviceType.DualSense => DualSenseDevice.DetermineConnectionType,
                InputDeviceType.SwitchPro => SwitchProDevice.DetermineConnectionType,
                InputDeviceType.JoyConL or InputDeviceType.JoyConR or InputDeviceType.JoyConGrip => JoyConDevice.DetermineConnectionType,
                InputDeviceType.DS3 => DS3Device.DetermineConnectionType,
                _ => null,
            };

            return new VidPidInfo(vid, pid, string.IsNullOrWhiteSpace(d.Nombre) ? "Mando" : d.Nombre.Trim(), type, features, check);
        }

        // ===================== Cambios desde la pantalla =====================

        /// <summary>Anade o reemplaza (mismo VID/PID) un mando del archivo y lo aplica al momento.</summary>
        public static void AddOrUpdate(Def def)
        {
            if (!TryParseId(def.VID, out int vid) || !TryParseId(def.PID, out int pid)) return;
            def.VID = FormatId(vid);
            def.PID = FormatId(pid);
            lock (fileLock)
            {
                defs.RemoveAll(d => TryParseId(d.VID, out int v) && TryParseId(d.PID, out int p) && v == vid && p == pid);
                defs.Add(Clone(def));
                Save();
                Apply();
            }
        }

        /// <summary>Quita un mando del archivo (si era un incluido desactivado, vuelve a activarse).</summary>
        public static void Remove(int vid, int pid)
        {
            lock (fileLock)
            {
                defs.RemoveAll(d => TryParseId(d.VID, out int v) && TryParseId(d.PID, out int p) && v == vid && p == pid);
                Save();
                Apply();
            }
        }

        /// <summary>Activa o desactiva un mando incluido de fabrica (se guarda como una entrada del archivo).</summary>
        public static void SetBuiltInActive(VidPidInfo info, bool active)
        {
            if (active)
            {
                Remove(info.vid, info.pid);
                return;
            }

            AddOrUpdate(new Def
            {
                Nombre = info.name, VID = FormatId(info.vid), PID = FormatId(info.pid), Activo = false,
                Tipo = Types.FirstOrDefault(kv => kv.Value == info.inputDevType).Key ?? "DS4",
            });
        }

        public static bool IsBuiltInActive(VidPidInfo info)
        {
            lock (fileLock)
            {
                Def d = defs.FirstOrDefault(x => TryParseId(x.VID, out int v) && TryParseId(x.PID, out int p) && v == info.vid && p == info.pid);
                return d == null || d.Activo;
            }
        }

        public static bool IsKnown(int vid, int pid) => DS4Devices.KnownDevices.Any(i => i.vid == vid && i.pid == pid);

        // ===================== Archivo =====================

        private static void Save()
        {
            // Si el archivo tiene un error, no se sobrescribe: el usuario perderia lo que escribio a mano
            if (lastError != null)
            {
                AppLogger.LogToGui(PPText.T("PPC_ControllerDefsNotSaved", "The change was not saved because Mandos.json has an error. Fix the file and press \"Read the file again\"."), true);
                return;
            }

            try
            {
                var data = new FileData
                {
                    Ayuda = "Mandos que PhantomPad reconoce ademas de los incluidos. VID y PID en hexadecimal (por ejemplo 054C). " +
                            "Una entrada con el mismo VID/PID que un mando incluido lo reemplaza; con \"Activo\": false lo desactiva. " +
                            "Los cambios se usan al volver a conectar el mando.",
                    TiposValidos = Types.Keys.ToList(),
                    OpcionesValidas = Options.Keys.ToList(),
                    Mandos = defs,
                };
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
                else File.Move(temp, FilePath);
            }
            catch (Exception e)
            {
                AppLogger.LogToGui(PPText.T("PPC_ControllerDefsSaveFailed", "Could not save the controllers file: {0}", e.Message), false);
            }
        }

        public static bool TryParseId(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = text.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
            return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) && value > 0 && value <= 0xFFFF;
        }

        public static string FormatId(int value) => value.ToString("X4");

        private static Def Clone(Def d) => new Def
        {
            Nombre = d.Nombre, VID = d.VID, PID = d.PID, Tipo = d.Tipo, Activo = d.Activo,
            Opciones = new List<string>(d.Opciones ?? new List<string>()),
        };
    }
}
