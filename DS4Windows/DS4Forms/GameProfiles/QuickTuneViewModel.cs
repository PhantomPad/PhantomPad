/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DS4Windows;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>
    /// PhantomPad: ajustes rapidos de un perfil (zonas muertas y curvas de respuesta).
    /// Carga el perfil en la ranura de edicion (TEST_PROFILE_INDEX, la misma que usa el editor
    /// de perfiles), cambia solo estos valores y lo guarda con el formato original.
    /// </summary>
    public class QuickTuneViewModel
    {
        private const int SLOT = Global.TEST_PROFILE_INDEX;
        public const double GRAPH_SIZE = 150.0;

        public string ProfileName { get; private set; }
        public string GameName { get; }

        /// <summary>Nombres de las curvas en el orden de los ids internos (0..6).</summary>
        public List<string> CurveNames { get; } = new List<string>
        {
            Translations.Strings.Linear,
            Translations.Strings.EnhancedPrecision,
            Translations.Strings.Quadratic,
            Translations.Strings.Cubic,
            Translations.Strings.EaseoutQuad,
            Translations.Strings.EaseoutCubic,
            Translations.Strings.Custom,
        };

        public QuickTuneViewModel(string profileName, string gameName)
        {
            GameName = gameName;
            LoadProfile(profileName);
        }

        public void LoadProfile(string profileName)
        {
            ProfileName = profileName;
            Global.ProfilePath[SLOT] = profileName;
            Global.LoadProfile(SLOT, false, App.rootHub, false);
            ProfileNameChanged?.Invoke(this, EventArgs.Empty);
            RaiseAll();
        }
        public event EventHandler ProfileNameChanged;

        /// <summary>Cambia el perfil de destino sin perder los valores ya ajustados.</summary>
        public void RetargetProfile(string newProfileName)
        {
            ProfileName = newProfileName;
            Global.ProfilePath[SLOT] = newProfileName;
            ProfileNameChanged?.Invoke(this, EventArgs.Empty);
        }

        // ===== Stick izquierdo =====
        public double LSDeadZone
        {
            get => Math.Round(Global.LSModInfo[SLOT].deadZone / 127.0 * 100.0);
            set { Global.LSModInfo[SLOT].deadZone = (int)Math.Round(Clamp(value, 0, 70) / 100.0 * 127.0); Raise(nameof(LSDeadZone)); }
        }
        public double LSAntiDeadZone
        {
            get => Global.LSModInfo[SLOT].antiDeadZone;
            set { Global.LSModInfo[SLOT].antiDeadZone = (int)Math.Round(Clamp(value, 0, 70)); Raise(nameof(LSAntiDeadZone)); }
        }
        public double LSMaxZone
        {
            get => Global.LSModInfo[SLOT].maxZone;
            set { Global.LSModInfo[SLOT].maxZone = (int)Math.Round(Clamp(value, 50, 100)); Raise(nameof(LSMaxZone)); }
        }
        public int LSCurve
        {
            get => Global.getLsOutCurveMode(SLOT);
            set { if (value >= 0 && value <= 5) { Global.setLsOutCurveMode(SLOT, value); } Raise(nameof(LSCurve)); }
        }
        public PointCollection LSCurvePoints => BuildCurve(LSDeadZone, LSAntiDeadZone, LSMaxZone, LSCurve);

        // ===== Stick derecho =====
        public double RSDeadZone
        {
            get => Math.Round(Global.RSModInfo[SLOT].deadZone / 127.0 * 100.0);
            set { Global.RSModInfo[SLOT].deadZone = (int)Math.Round(Clamp(value, 0, 70) / 100.0 * 127.0); Raise(nameof(RSDeadZone)); }
        }
        public double RSAntiDeadZone
        {
            get => Global.RSModInfo[SLOT].antiDeadZone;
            set { Global.RSModInfo[SLOT].antiDeadZone = (int)Math.Round(Clamp(value, 0, 70)); Raise(nameof(RSAntiDeadZone)); }
        }
        public double RSMaxZone
        {
            get => Global.RSModInfo[SLOT].maxZone;
            set { Global.RSModInfo[SLOT].maxZone = (int)Math.Round(Clamp(value, 50, 100)); Raise(nameof(RSMaxZone)); }
        }
        public int RSCurve
        {
            get => Global.getRsOutCurveMode(SLOT);
            set { if (value >= 0 && value <= 5) { Global.setRsOutCurveMode(SLOT, value); } Raise(nameof(RSCurve)); }
        }
        public PointCollection RSCurvePoints => BuildCurve(RSDeadZone, RSAntiDeadZone, RSMaxZone, RSCurve);

        // ===== Gatillos =====
        public double L2DeadZone
        {
            get => Math.Round(Global.L2ModInfo[SLOT].deadZone / 255.0 * 100.0);
            set { Global.L2ModInfo[SLOT].deadZone = (byte)Math.Round(Clamp(value, 0, 70) / 100.0 * 255.0); Raise(nameof(L2DeadZone)); }
        }
        public double R2DeadZone
        {
            get => Math.Round(Global.R2ModInfo[SLOT].deadZone / 255.0 * 100.0);
            set { Global.R2ModInfo[SLOT].deadZone = (byte)Math.Round(Clamp(value, 0, 70) / 100.0 * 255.0); Raise(nameof(R2DeadZone)); }
        }
        public int L2Curve
        {
            get => Global.getL2OutCurveMode(SLOT);
            set { if (value >= 0 && value <= 5) { Global.setL2OutCurveMode(SLOT, value); } Raise(nameof(L2Curve)); }
        }
        public int R2Curve
        {
            get => Global.getR2OutCurveMode(SLOT);
            set { if (value >= 0 && value <= 5) { Global.setR2OutCurveMode(SLOT, value); } Raise(nameof(R2Curve)); }
        }

        // ===== Preajustes (se aplican a los dos sticks) =====
        public void ApplyPreset(string preset)
        {
            switch (preset)
            {
                case "precision": SetSticks(6, 15, 100, 1); break;   // precision mejorada: apuntado fino
                case "fast":      SetSticks(4, 25, 95, 0); break;    // lineal y reactivo
                case "smooth":    SetSticks(10, 15, 100, 2); break;  // cuadratica: suave al centro
                default:          SetSticks(StickDeadZoneInfo.DEFAULT_DEADZONE / 127.0 * 100.0, StickDeadZoneInfo.DEFAULT_ANTIDEADZONE,
                                            StickDeadZoneInfo.DEFAULT_MAXZONE, 0); break; // valores de fabrica
            }
        }

        private void SetSticks(double dz, double anti, double max, int curve)
        {
            LSDeadZone = dz; LSAntiDeadZone = anti; LSMaxZone = max; LSCurve = curve;
            RSDeadZone = dz; RSAntiDeadZone = anti; RSMaxZone = max; RSCurve = curve;
        }

        /// <summary>Guarda el perfil y lo recarga en los mandos que lo esten usando.</summary>
        public void Save()
        {
            Global.SaveProfile(SLOT, ProfileName);
            ProfileReloader.ReloadControllersUsing(ProfileName);
        }

        // ===== Grafico aproximado de la respuesta (entrada -> salida) =====
        private static PointCollection BuildCurve(double deadZonePct, double antiPct, double maxPct, int curve)
        {
            var points = new PointCollection();
            double dz = deadZonePct / 100.0, anti = antiPct / 100.0, max = Math.Max(maxPct / 100.0, dz + 0.01);
            for (int i = 0; i <= 60; i++)
            {
                double x = i / 60.0;
                double y;
                if (x <= dz) y = 0;
                else
                {
                    double t = Math.Min(1.0, (x - dz) / (max - dz));
                    t = ApplyCurve(t, curve);
                    y = anti + (1.0 - anti) * t;
                }

                points.Add(new Point(x * GRAPH_SIZE, GRAPH_SIZE - y * GRAPH_SIZE));
            }

            points.Freeze();
            return points;
        }

        private static double ApplyCurve(double t, int curve)
        {
            switch (curve)
            {
                case 1: // precision mejorada (aprox. a la de DS4Windows)
                    if (t <= 0.4) return 0.8 * t;
                    if (t <= 0.75) return t - 0.08;
                    return 1.32 * t - 0.32;
                case 2: return t * t;
                case 3: return t * t * t;
                case 4: return 1 - (1 - t) * (1 - t);
                case 5: return 1 - Math.Pow(1 - t, 3);
                default: return t;
            }
        }

        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

        // ===== Notificaciones de cambio (patron XChanged que entiende WPF) =====
        public event EventHandler LSDeadZoneChanged, LSAntiDeadZoneChanged, LSMaxZoneChanged, LSCurveChanged, LSCurvePointsChanged;
        public event EventHandler RSDeadZoneChanged, RSAntiDeadZoneChanged, RSMaxZoneChanged, RSCurveChanged, RSCurvePointsChanged;
        public event EventHandler L2DeadZoneChanged, R2DeadZoneChanged, L2CurveChanged, R2CurveChanged;

        private void Raise(string name)
        {
            switch (name)
            {
                case nameof(LSDeadZone): LSDeadZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(LSAntiDeadZone): LSAntiDeadZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(LSMaxZone): LSMaxZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(LSCurve): LSCurveChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(RSDeadZone): RSDeadZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(RSAntiDeadZone): RSAntiDeadZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(RSMaxZone): RSMaxZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(RSCurve): RSCurveChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(L2DeadZone): L2DeadZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(R2DeadZone): R2DeadZoneChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(L2Curve): L2CurveChanged?.Invoke(this, EventArgs.Empty); break;
                case nameof(R2Curve): R2CurveChanged?.Invoke(this, EventArgs.Empty); break;
            }

            if (name.StartsWith("LS")) LSCurvePointsChanged?.Invoke(this, EventArgs.Empty);
            if (name.StartsWith("RS")) RSCurvePointsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RaiseAll()
        {
            foreach (string n in new[] { nameof(LSDeadZone), nameof(LSAntiDeadZone), nameof(LSMaxZone), nameof(LSCurve),
                nameof(RSDeadZone), nameof(RSAntiDeadZone), nameof(RSMaxZone), nameof(RSCurve),
                nameof(L2DeadZone), nameof(R2DeadZone), nameof(L2Curve), nameof(R2Curve) })
            {
                Raise(n);
            }
        }
    }
}
