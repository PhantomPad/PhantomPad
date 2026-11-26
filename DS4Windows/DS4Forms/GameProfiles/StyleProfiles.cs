/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>Valores de sticks de un estilo de control.</summary>
    public class StyleValues
    {
        public int DeadZonePct { get; set; }
        public int AntiDeadZonePct { get; set; }
        public int MaxZonePct { get; set; }
        public string Curve { get; set; } // nombre interno: linear, quadratic, enhanced-precision...
    }

    /// <summary>Valores leidos de un perfil (para la tabla "Valor actual").</summary>
    public class ProfileValues
    {
        public int DeadZonePct { get; set; } = -1;
        public int AntiDeadZonePct { get; set; } = -1;
        public string Curve { get; set; } = "";
        public GyroAim Gyro { get; set; } = GyroAim.Off;
    }

    /// <summary>
    /// PhantomPad: perfiles compartidos por estilo y gyro aim
    /// ("PhantomPad Precise", "PhantomPad Precise Gyro L2", "PhantomPad Precise Gyro"...).
    /// Se crean copiando el perfil base del usuario (sus botones) y cambiando solo sticks y giroscopio.
    /// Las variantes con gyro se crean solo cuando se necesitan.
    /// </summary>
    public static class StyleProfiles
    {
        // Valores del gyro aim (modo "joystick tipo raton" de DS4Windows)
        public const int GYRO_BASE_MAXZONE = 830;   // valor por defecto de DS4Windows = sensibilidad 100 %
        public const string GYRO_TRIGGER_L2 = "5";  // indice de L2 en la lista de activadores
        public const string GYRO_TRIGGER_ALWAYS = "-1";

        private static string BaseName(ControlStyle style) => style switch
        {
            ControlStyle.Smooth => "PhantomPad Smooth",
            ControlStyle.Precise => "PhantomPad Precise",
            ControlStyle.Competitive => "PhantomPad Competitive",
            _ => "PhantomPad Balanced",
        };

        public static string ProfileName(ControlStyle style, GyroAim gyro = GyroAim.Off) => gyro switch
        {
            GyroAim.WhileAiming => BaseName(style) + " Gyro L2",
            GyroAim.Always => BaseName(style) + " Gyro",
            _ => BaseName(style),
        };

        public static StyleValues Values(ControlStyle style) => style switch
        {
            ControlStyle.Smooth => new StyleValues { DeadZonePct = 10, AntiDeadZonePct = 15, MaxZonePct = 100, Curve = "quadratic" },
            ControlStyle.Precise => new StyleValues { DeadZonePct = 6, AntiDeadZonePct = 15, MaxZonePct = 100, Curve = "enhanced-precision" },
            ControlStyle.Competitive => new StyleValues { DeadZonePct = 4, AntiDeadZonePct = 25, MaxZonePct = 95, Curve = "linear" },
            _ => new StyleValues { DeadZonePct = 8, AntiDeadZonePct = 20, MaxZonePct = 100, Curve = "linear" },
        };

        /// <summary>Si el nombre es de un perfil de estilo, devuelve su estilo y su gyro.</summary>
        public static bool TryGetStyle(string profileName, out ControlStyle style, out GyroAim gyro)
        {
            foreach (ControlStyle s in Enum.GetValues(typeof(ControlStyle)))
            {
                foreach (GyroAim g in Enum.GetValues(typeof(GyroAim)))
                {
                    if (ProfileName(s, g) == profileName) { style = s; gyro = g; return true; }
                }
            }

            style = ControlStyle.Balanced; gyro = GyroAim.Off;
            return false;
        }

        public static bool TryGetStyle(string profileName, out ControlStyle style) => TryGetStyle(profileName, out style, out _);

        private static string ProfilesDir => Path.Combine(Global.appdatapath, "Profiles");

        /// <summary>Crea los 4 perfiles base que falten. Con regenerate=true rehace todos los de estilo existentes.</summary>
        public static void EnsureProfiles(ProfileList profileList, bool regenerate = false)
        {
            foreach (ControlStyle style in Enum.GetValues(typeof(ControlStyle)))
            {
                foreach (GyroAim gyro in Enum.GetValues(typeof(GyroAim)))
                {
                    string path = Path.Combine(ProfilesDir, ProfileName(style, gyro) + ".xml");
                    bool isBase = gyro == GyroAim.Off;
                    if (isBase || (regenerate && File.Exists(path)))
                    {
                        EnsureProfile(profileList, style, gyro, regenerate);
                    }
                }
            }
        }

        /// <summary>Crea (si falta) el perfil de un estilo + gyro y devuelve su nombre.</summary>
        public static string EnsureProfile(ProfileList profileList, ControlStyle style, GyroAim gyro, bool regenerate = false)
        {
            string name = ProfileName(style, gyro);
            string path = Path.Combine(ProfilesDir, name + ".xml");
            if (File.Exists(path) && !regenerate) { AddToList(profileList, name); return name; }

            string baseFile = FindBaseProfile();
            if (baseFile == null) return name;
            try
            {
                var doc = new XmlDocument();
                doc.Load(baseFile);
                ApplyStyle(doc, Values(style));
                ApplyGyro(doc, gyro, 100, true);
                doc.Save(path);
                AddToList(profileList, name);
            }
            catch (Exception e)
            {
                AppLogger.LogToGui(PPText.T("PPC_StyleProfileFailed", "Could not create profile {0}: {1}", name, e.Message), false);
            }

            return name;
        }

        /// <summary>Cambia el gyro aim dentro de un perfil guardado (perfiles propios / personalizados).</summary>
        public static void SetGyroInProfileFile(string profileName, GyroAim gyro)
        {
            string path = Path.Combine(ProfilesDir, profileName + ".xml");
            if (!File.Exists(path)) return;
            var doc = new XmlDocument();
            doc.Load(path);
            ApplyGyro(doc, gyro, ReadGyroSensitivity(doc), true);
            doc.Save(path);
        }

        private static void AddToList(ProfileList list, string name)
        {
            if (list != null && !list.ProfileListCol.Any(p => p.Name == name)) list.AddProfileSort(name);
        }

        /// <summary>Perfil base: "Default" si existe; si no, el primero que no sea de estilo.</summary>
        private static string FindBaseProfile()
        {
            if (!Directory.Exists(ProfilesDir)) return null;
            string def = Path.Combine(ProfilesDir, "Default.xml");
            if (File.Exists(def)) return def;
            return Directory.GetFiles(ProfilesDir, "*.xml")
                .FirstOrDefault(f => !TryGetStyle(Path.GetFileNameWithoutExtension(f), out _));
        }

        private static void ApplyStyle(XmlDocument doc, StyleValues v)
        {
            string dzRaw = ((int)Math.Round(v.DeadZonePct / 100.0 * 127.0)).ToString();
            SetNode(doc, "LSDeadZone", dzRaw); SetNode(doc, "RSDeadZone", dzRaw);
            SetNode(doc, "LSAntiDeadZone", v.AntiDeadZonePct.ToString()); SetNode(doc, "RSAntiDeadZone", v.AntiDeadZonePct.ToString());
            SetNode(doc, "LSMaxZone", v.MaxZonePct.ToString()); SetNode(doc, "RSMaxZone", v.MaxZonePct.ToString());
            SetNode(doc, "LSOutputCurveMode", v.Curve); SetNode(doc, "RSOutputCurveMode", v.Curve);
        }

        /// <summary>Gyro aim en un perfil: modo joystick tipo raton sobre el stick derecho.</summary>
        private static void ApplyGyro(XmlDocument doc, GyroAim gyro, int sensitivityPct, bool smoothing)
        {
            if (gyro == GyroAim.Off)
            {
                SetNode(doc, "GyroOutputMode", "Controls"); // valor por defecto de DS4Windows (sin gyro aim)
                return;
            }

            SetNode(doc, "GyroOutputMode", "MouseJoystick");
            SetNode(doc, "GyroMouseStickTriggers", gyro == GyroAim.WhileAiming ? GYRO_TRIGGER_L2 : GYRO_TRIGGER_ALWAYS);
            SetNode(doc, "GyroMouseStickTriggerCond", "and");
            SetNode(doc, "GyroMouseStickTriggerTurns", "True");
            SetNode(doc, "GyroMouseStickOutputStick", "RightStick");
            SetNode(doc, "GyroMouseStickMaxZone", SensitivityToMaxZone(sensitivityPct).ToString(CultureInfo.InvariantCulture));
            SetNode(doc, "GyroMouseStickSmoothingSettings/UseSmoothing", smoothing ? "True" : "False");
            SetNode(doc, "GyroMouseStickSmoothingSettings/SmoothingMethod", smoothing ? "one-euro" : "none");
        }

        public static int SensitivityToMaxZone(int sensitivityPct) =>
            Math.Max(60, (int)Math.Round(GYRO_BASE_MAXZONE * 100.0 / Math.Max(10, sensitivityPct)));

        public static int MaxZoneToSensitivity(int maxZone) =>
            maxZone <= 0 ? 100 : (int)Math.Round(GYRO_BASE_MAXZONE * 100.0 / maxZone);

        private static int ReadGyroSensitivity(XmlDocument doc) =>
            int.TryParse(doc.DocumentElement.SelectSingleNode("GyroMouseStickMaxZone")?.InnerText, out int mz) ? MaxZoneToSensitivity(mz) : 100;

        private static void SetNode(XmlDocument doc, string path, string value)
        {
            XmlElement parent = doc.DocumentElement;
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                XmlNode node = parent.SelectSingleNode(parts[i]);
                if (node == null)
                {
                    node = doc.CreateElement(parts[i]);
                    parent.AppendChild(node);
                }

                if (i == parts.Length - 1) node.InnerText = value;
                else parent = (XmlElement)node;
            }
        }

        /// <summary>Lee los valores principales de un perfil guardado.</summary>
        public static ProfileValues ReadValues(string profileName)
        {
            var result = new ProfileValues();
            try
            {
                string path = Path.Combine(ProfilesDir, profileName + ".xml");
                if (!File.Exists(path)) return result;
                var doc = new XmlDocument();
                doc.Load(path);
                XmlElement root = doc.DocumentElement;
                if (int.TryParse(root.SelectSingleNode("LSDeadZone")?.InnerText, out int dz)) result.DeadZonePct = (int)Math.Round(dz / 127.0 * 100.0);
                if (int.TryParse(root.SelectSingleNode("LSAntiDeadZone")?.InnerText, out int anti)) result.AntiDeadZonePct = anti;
                result.Curve = root.SelectSingleNode("LSOutputCurveMode")?.InnerText ?? "linear";
                result.Gyro = ReadGyro(root);
            }
            catch { }

            return result;
        }

        private static GyroAim ReadGyro(XmlElement root)
        {
            if (root.SelectSingleNode("GyroOutputMode")?.InnerText != "MouseJoystick") return GyroAim.Off;
            string triggers = root.SelectSingleNode("GyroMouseStickTriggers")?.InnerText ?? GYRO_TRIGGER_ALWAYS;
            return triggers == GYRO_TRIGGER_ALWAYS ? GyroAim.Always : GyroAim.WhileAiming;
        }

        /// <summary>Nombre visible de una curva interna.</summary>
        public static string CurveDisplayName(string curve) => curve switch
        {
            "enhanced-precision" => Translations.Strings.EnhancedPrecision,
            "quadratic" => Translations.Strings.Quadratic,
            "cubic" => Translations.Strings.Cubic,
            "easeout-quad" => Translations.Strings.EaseoutQuad,
            "easeout-cubic" => Translations.Strings.EaseoutCubic,
            "custom" => Translations.Strings.Custom,
            _ => Translations.Strings.Linear,
        };

        public static string GyroDisplayName(GyroAim gyro) => gyro switch
        {
            GyroAim.WhileAiming => PPText.T("PP_GyroWhileAiming", "While aiming (L2)"),
            GyroAim.Always => PPText.T("PP_GyroAlways", "Always"),
            _ => PPText.T("PP_GyroOff", "Off"),
        };
    }

    /// <summary>Recarga un perfil en los mandos que lo estan usando (tras cambiarlo en disco).</summary>
    public static class ProfileReloader
    {
        public static void ReloadControllersUsing(string profileName)
        {
            for (int i = 0; i < ControlService.CURRENT_DS4_CONTROLLER_LIMIT; i++)
            {
                bool usesAsTemp = Global.useTempProfile[i] && Global.tempprofilename[i] == profileName;
                bool usesAsMain = !Global.useTempProfile[i] && Global.ProfilePath[i] == profileName;
                if (!usesAsTemp && !usesAsMain) continue;

                int index = i;
                DS4Device device = App.rootHub.DS4Controllers[i];
                Action reload = usesAsTemp
                    ? () => Global.LoadTempProfile(index, profileName, false, App.rootHub)
                    : () => Global.LoadProfile(index, false, App.rootHub);
                if (device != null) device.HaltReportingRunAction(reload);
                else reload();
            }
        }
    }
}
