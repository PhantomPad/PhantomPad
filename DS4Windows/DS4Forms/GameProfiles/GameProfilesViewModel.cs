/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DS4Windows;
using DS4WinWPF.DS4Forms.ViewModels.Util;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>
    /// PhantomPad: seccion "Juegos" (estilo app de NVIDIA).
    /// - Detecta los juegos instalados y los anade solos con la configuracion recomendada
    ///   segun su tipo (lista incluida + generos de Steam opcionales).
    /// - Usa el motor y el archivo de perfiles automaticos originales (Auto Profiles.xml).
    /// - Datos extra (nombre, tipo, como lanzar, juegos quitados) en PhantomPadGames.json.
    /// </summary>
    public class GameProfilesViewModel
    {
        private const string DATA_FILE = "PhantomPadGames.json";

        private readonly AutoProfileHolder holder;
        private readonly ProfileList profileList;
        private GamesData data = new GamesData();
        private bool scanning;

        public ObservableCollection<GameEntry> Games { get; } = new ObservableCollection<GameEntry>();
        public ObservableCollection<ProfileEntity> Profiles => profileList.ProfileListCol;
        public GameEntry GeneralEntry { get; private set; }

        public event EventHandler GamesCountChanged;
        public int GamesCount => Games.Count(g => !g.IsGeneral);

        private GameEntry selectedGame;
        public GameEntry SelectedGame
        {
            get => selectedGame;
            set { selectedGame = value; selectedGame?.RefreshValues(); SelectedGameChanged?.Invoke(this, EventArgs.Empty); }
        }
        public event EventHandler SelectedGameChanged;

        public event EventHandler IsScanningChanged;
        public bool IsScanning => scanning;

        /// <summary>El perfil normal ("Todos los juegos") cambio: la ventana principal lo aplica a los mandos.</summary>
        public event EventHandler<string> NormalProfileChangeRequested;

        public bool UseSteamGenres
        {
            get => data.UseSteamGenres;
            set { data.UseSteamGenres = value; SaveData(); if (value) _ = ClassifyWithSteamAsync(); }
        }

        public bool RevertOnClose
        {
            get => Global.AutoProfileRevertDefaultProfile;
            set { Global.AutoProfileRevertDefaultProfile = value; Global.Save(); }
        }

        public List<EnumChoiceSelection<AutoProfileDisplayProfileSwitchChoices>> NotifyChoices { get; } =
            new List<EnumChoiceSelection<AutoProfileDisplayProfileSwitchChoices>>()
            {
                new EnumChoiceSelection<AutoProfileDisplayProfileSwitchChoices>(Translations.Strings.None, AutoProfileDisplayProfileSwitchChoices.None),
                new EnumChoiceSelection<AutoProfileDisplayProfileSwitchChoices>(Translations.Strings.Log, AutoProfileDisplayProfileSwitchChoices.Log),
                new EnumChoiceSelection<AutoProfileDisplayProfileSwitchChoices>(Translations.Strings.Notification, AutoProfileDisplayProfileSwitchChoices.Notification),
                new EnumChoiceSelection<AutoProfileDisplayProfileSwitchChoices>(Translations.Strings.AutoProfiles_LogAndNotification, AutoProfileDisplayProfileSwitchChoices.LogAndNotification),
            };

        public AutoProfileDisplayProfileSwitchChoices NotifyChoice
        {
            get => Global.autoProfileSwitchNotifyChoice;
            set { Global.autoProfileSwitchNotifyChoice = value; Global.Save(); }
        }

        public GameProfilesViewModel(AutoProfileHolder holder, ProfileList profileList)
        {
            this.holder = holder;
            this.profileList = profileList;
            LoadData();
            StyleProfiles.EnsureProfiles(profileList);
            RemoveGyroVariants();
            RebuildGames();
            holder.AutoProfileColl.CollectionChanged += AutoProfileColl_CollectionChanged;

            // El gyro aim ahora es global por mando (tarjeta del mando). Aqui solo se le dice
            // al motor que juegos son de carreras, para poder pausarlo en ellos.
            PhantomGyro.IsRacingGame = path => path != null && data.Games.TryGetValue(path, out GameData gd) && gd.Category == GameCategory.Racing;
        }

        private void AutoProfileColl_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Application.Current?.Dispatcher.BeginInvoke((Action)RebuildGames);
        }

        /// <summary>
        /// El gyro por juego se reemplazo por el gyro global del mando: los juegos que usaban una
        /// variante con gyro ("... Gyro L2" / "... Gyro") vuelven a su perfil de estilo normal.
        /// </summary>
        private void RemoveGyroVariants()
        {
            bool changed = false;
            foreach (AutoProfileEntity e in holder.AutoProfileColl)
            {
                if (StyleProfiles.TryGetStyle(e.ProfileNames[0], out ControlStyle style, out GyroAim gyro) && gyro != GyroAim.Off)
                {
                    SetEntityProfile(e, StyleProfiles.EnsureProfile(profileList, style, GyroAim.Off));
                    changed = true;
                }
            }

            if (changed) SaveRules();

            foreach (ControlStyle s in Enum.GetValues(typeof(ControlStyle)))
            {
                foreach (GyroAim g in new[] { GyroAim.WhileAiming, GyroAim.Always })
                {
                    string name = StyleProfiles.ProfileName(s, g);
                    string path = Path.Combine(Global.appdatapath, "Profiles", name + ".xml");
                    bool inUse = Enumerable.Range(0, ControlService.CURRENT_DS4_CONTROLLER_LIMIT)
                        .Any(i => Global.ProfilePath[i] == name || Global.tempprofilename[i] == name);
                    if (File.Exists(path) && !inUse)
                    {
                        try { File.Delete(path); profileList.RemoveProfile(name); } catch { }
                    }
                }
            }
        }

        public void RebuildGames()
        {
            string selectedPath = selectedGame?.Entity?.Path;
            bool generalSelected = selectedGame?.IsGeneral == true;
            Games.Clear();
            GeneralEntry = new GameEntry(null, null, this);
            Games.Add(GeneralEntry);
            foreach (AutoProfileEntity entity in holder.AutoProfileColl.OrderBy(e => InfoFor(e.Path)?.Name ?? e.Path))
            {
                Games.Add(new GameEntry(entity, InfoFor(entity.Path), this));
            }

            GamesCountChanged?.Invoke(this, EventArgs.Empty);
            SelectedGame = generalSelected ? GeneralEntry
                : Games.FirstOrDefault(g => g.Entity?.Path == selectedPath) ?? Games.FirstOrDefault(g => !g.IsGeneral) ?? GeneralEntry;
        }

        internal GameData InfoFor(string path) => path != null && data.Games.TryGetValue(path, out GameData d) ? d : null;

        // ================= Deteccion y optimizacion automatica =================

        /// <summary>Busca juegos instalados y anade los nuevos con su configuracion recomendada.</summary>
        public async Task ScanAndAutoAddAsync()
        {
            if (scanning) return;
            scanning = true; IsScanningChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                List<DetectedGame> found = await Task.Run(GameLibraryScanner.ScanAll);
                int added = 0;
                foreach (DetectedGame g in found)
                {
                    string rule = PathRuleForFolder(g.InstallDir);
                    if (data.Ignored.Contains(rule)) continue;

                    AutoProfileEntity existing = holder.AutoProfileColl.FirstOrDefault(e => SamePath(e.Path, rule));
                    if (existing != null && data.Games.ContainsKey(rule)) continue; // ya conocido

                    GameCategory? local = GameCategories.FromLocalDatabase(g.Name);
                    GameCategory category = local ?? GameCategory.General;
                    data.Games[rule] = new GameData
                    {
                        Name = g.Name, Source = g.Source, SteamAppId = g.SteamAppId, LaunchUri = g.LaunchUri,
                        Category = category, CategoryKnownLocally = local.HasValue,
                    };

                    string recommended = RecommendedProfileFor(category);
                    if (existing == null)
                    {
                        AddRuleEntity(rule, recommended);
                    }
                    else if (existing.ProfileNames[0] == Global.ProfilePath[0] || string.IsNullOrEmpty(existing.ProfileNames[0])
                        || existing.ProfileNames[0] == AutoProfileEntity.NONE_STRING)
                    {
                        // Regla que ya existia sin configuracion propia: se adopta y se optimiza
                        SetEntityProfile(existing, recommended);
                    }

                    added++;
                }

                SaveData();
                if (added > 0) { SaveRules(); RebuildGames(); }
                if (data.UseSteamGenres) await ClassifyWithSteamAsync();
            }
            finally
            {
                scanning = false; IsScanningChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Para juegos de Steam que la lista no reconoce, pide sus generos a Steam una sola vez.</summary>
        private async Task ClassifyWithSteamAsync()
        {
            var pending = data.Games.Where(kv => !string.IsNullOrEmpty(kv.Value.SteamAppId) && !kv.Value.CategoryKnownLocally
                && !kv.Value.CategoryChosenByUser && !kv.Value.SteamChecked).ToList();
            bool changed = false;
            foreach (var kv in pending)
            {
                GameCategory? steamCat = await GameCategories.FromSteamAsync(App.requestClient, kv.Value.SteamAppId);
                kv.Value.SteamChecked = true;
                changed = true;
                if (steamCat.HasValue && steamCat.Value != kv.Value.Category)
                {
                    GameCategory oldCategory = kv.Value.Category;
                    kv.Value.Category = steamCat.Value;
                    AutoProfileEntity entity = holder.AutoProfileColl.FirstOrDefault(e => SamePath(e.Path, kv.Key));
                    // Solo se cambia si sigue con el estilo recomendado de antes: si el usuario eligio
                    // otro estilo para este juego, se respeta su eleccion
                    if (entity != null && entity.ProfileNames[0] == RecommendedProfileFor(oldCategory))
                    {
                        SetEntityProfile(entity, RecommendedProfileFor(steamCat.Value));
                    }
                }
            }

            if (changed)
            {
                SaveData();
                SaveRules();
                Application.Current?.Dispatcher.BeginInvoke((Action)RebuildGames);
            }
        }

        public static string PathRuleForFolder(string installDir) => "^" + GameLibraryScanner.NormalizeDir(installDir);
        private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        public void AddManualExe(string exePath)
        {
            data.Ignored.Remove(exePath);
            GameCategory category = GameCategories.FromLocalDatabase(Path.GetFileNameWithoutExtension(exePath)) ?? GameCategory.General;
            data.Games[exePath] = new GameData
            {
                Name = Path.GetFileNameWithoutExtension(exePath), Source = GameLibraryScanner.SOURCE_MANUAL,
                LaunchUri = exePath, Category = category, CategoryKnownLocally = true,
            };
            SaveData();
            if (!holder.AutoProfileColl.Any(e => SamePath(e.Path, exePath)))
            {
                AddRuleEntity(exePath, RecommendedProfileFor(category));
                SaveRules();
            }
        }

        private void AddRuleEntity(string pathRule, string profile)
        {
            var entity = new AutoProfileEntity(pathRule, string.Empty);
            for (int i = 0; i < entity.ProfileNames.Length; i++) entity.ProfileNames[i] = profile;
            holder.AutoProfileColl.Add(entity);
        }

        // ================= Acciones de cada juego =================

        public void Remove(GameEntry entry)
        {
            if (entry.IsGeneral) return;
            data.Ignored.Add(entry.Entity.Path); // no volver a anadirlo solo
            SaveData();
            holder.Remove(entry.Entity);
            SaveRules();
        }

        public void SetCategory(GameEntry entry, GameCategory category)
        {
            if (entry.IsGeneral) return;
            GameData info = data.Games.TryGetValue(entry.Entity.Path, out GameData d) ? d : (data.Games[entry.Entity.Path] = new GameData { Name = entry.Name });
            info.Category = category;
            info.CategoryChosenByUser = true;
            SaveData();
            if (!entry.IsCustom) SetEntityProfile(entry.Entity, RecommendedProfileFor(category));
            SaveRules();
            entry.Reload(info);
        }

        /// <summary>Perfil recomendado para un tipo de juego; lo crea si falta.</summary>
        private string RecommendedProfileFor(GameCategory category) =>
            StyleProfiles.EnsureProfile(profileList, GameCategories.RecommendedStyle(category), GyroAim.Off);

        /// <summary>Cambia el estilo (barra).</summary>
        public void ApplyStyle(GameEntry entry, ControlStyle style)
        {
            AssignStyleProfile(entry, style);
        }

        private void AssignStyleProfile(GameEntry entry, ControlStyle style)
        {
            string profile = StyleProfiles.EnsureProfile(profileList, style, GyroAim.Off);
            if (entry.IsGeneral) { NormalProfileChangeRequested?.Invoke(this, profile); entry.Reload(null); return; }
            SetEntityProfile(entry.Entity, profile);
            SaveRules();
            entry.Reload(InfoFor(entry.Entity.Path));
        }

        public void ResetToRecommended(GameEntry entry)
        {
            StyleProfiles.EnsureProfiles(profileList, regenerate: true);
            AssignStyleProfile(entry, entry.RecommendedStyle);
            // Los perfiles de estilo se rehicieron: recargar los que esten en uso
            foreach (var p in profileList.ProfileListCol.Select(x => x.Name).Where(n => StyleProfiles.TryGetStyle(n, out _)).ToList())
            {
                ProfileReloader.ReloadControllersUsing(p);
            }
        }

        public void Launch(GameEntry entry)
        {
            string target = entry.LaunchUri;
            if (string.IsNullOrEmpty(target)) return;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception e) { AppLogger.LogToGui(PPText.T("PPC_GameLaunchFailed", "Could not open the game: {0}", e.Message), true); }
        }

        internal void SetEntityProfile(AutoProfileEntity entity, string profile)
        {
            for (int i = 0; i < entity.ProfileNames.Length; i++) entity.ProfileNames[i] = profile;
        }

        /// <summary>Copia el perfil del juego a uno propio con su nombre (para personalizar a mano).</summary>
        public string CreateProfileForGame(GameEntry entry)
        {
            string profilesDir = Path.Combine(Global.appdatapath, "Profiles");
            string source = Path.Combine(profilesDir, entry.SelectedProfileName + ".xml");
            if (!File.Exists(source)) return null;

            string baseName = string.Concat(entry.Name.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrEmpty(baseName)) baseName = "Game";
            string newName = baseName;
            for (int n = 2; File.Exists(Path.Combine(profilesDir, newName + ".xml")); n++) newName = $"{baseName} {n}";

            File.Copy(source, Path.Combine(profilesDir, newName + ".xml"));
            profileList.AddProfileSort(newName);
            entry.SelectedProfileName = newName;
            return newName;
        }

        public int CountGamesUsing(string profileName) =>
            holder.AutoProfileColl.Count(e => e.ProfileNames[0] == profileName);

        public void SaveRules() => holder.Save(Path.Combine(Global.appdatapath, "Auto Profiles.xml"));

        // ================= Datos guardados =================

        private string DataPath => Path.Combine(Global.appdatapath, DATA_FILE);

        // Tipos de juego guardados como texto ("Racing") para que el archivo sea legible y editable
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        private void LoadData()
        {
            try
            {
                if (File.Exists(DataPath))
                {
                    data = JsonSerializer.Deserialize<GamesData>(File.ReadAllText(DataPath), JsonOptions) ?? new GamesData();
                    data.Games = new Dictionary<string, GameData>(data.Games ?? new Dictionary<string, GameData>(), StringComparer.OrdinalIgnoreCase);
                    data.Ignored = new HashSet<string>(data.Ignored ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { data = new GamesData(); }
        }

        internal void SaveData()
        {
            try { File.WriteAllText(DataPath, JsonSerializer.Serialize(data, JsonOptions)); }
            catch { }
        }
    }

    public class GamesData
    {
        public bool UseSteamGenres { get; set; } = true;
        public Dictionary<string, GameData> Games { get; set; } = new Dictionary<string, GameData>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Ignored { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public class GameData
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string SteamAppId { get; set; }
        public string LaunchUri { get; set; }
        public GameCategory Category { get; set; }
        public bool CategoryKnownLocally { get; set; }
        public bool CategoryChosenByUser { get; set; }
        public bool SteamChecked { get; set; }
    }

    /// <summary>Fila de la tabla "Configuracion del mando".</summary>
    public class SettingRow
    {
        public string Name { get; set; }
        public string Current { get; set; }
        public string Recommended { get; set; }
        public bool Differs => Current != Recommended;
    }

    /// <summary>Un juego de la lista (o la entrada general "Todos los juegos").</summary>
    public class GameEntry
    {
        private readonly GameProfilesViewModel owner;

        public AutoProfileEntity Entity { get; }
        public bool IsGeneral => Entity == null;
        public string Name { get; private set; }
        public string Source { get; private set; }
        public string PathText { get; }
        public string LaunchUri { get; private set; }
        public bool CanLaunch => !string.IsNullOrEmpty(LaunchUri);
        public GameCategory Category { get; private set; }
        public GameProfilesViewModel Owner => owner;

        public event EventHandler IconChanged;
        public ImageSource Icon { get; private set; }

        public GameEntry(AutoProfileEntity entity, GameData info, GameProfilesViewModel owner)
        {
            this.owner = owner;
            Entity = entity;
            PathText = entity?.Path.TrimStart('^', '*').TrimEnd('$') ?? "";
            Reload(info, raise: false);
            if (!IsGeneral) LoadIconAsync();
        }

        public void Reload(GameData info, bool raise = true)
        {
            if (IsGeneral)
            {
                Name = PPText.T("PP_HubAllGames", "All games");
                Source = "";
                Category = GameCategory.General;
            }
            else
            {
                Name = info?.Name ?? DeriveName(Entity.Path);
                Source = info?.Source ?? GameLibraryScanner.SOURCE_MANUAL;
                LaunchUri = info?.LaunchUri ?? (PathText.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? PathText : null);
                Category = info?.Category ?? GameCategory.General;
            }

            if (raise)
            {
                NameChanged?.Invoke(this, EventArgs.Empty);
                CategoryChanged?.Invoke(this, EventArgs.Empty);
                RefreshValues();
            }
        }
        public event EventHandler NameChanged, CategoryChanged;

        /// <summary>Perfil asignado (el mismo para todos los mandos).</summary>
        public string SelectedProfileName
        {
            get => IsGeneral ? Global.ProfilePath[0] : Entity.ProfileNames[0];
            set
            {
                if (IsGeneral || string.IsNullOrEmpty(value) || value == Entity.ProfileNames[0]) return;
                owner.SetEntityProfile(Entity, value);
                owner.SaveRules();
                SelectedProfileNameChanged?.Invoke(this, EventArgs.Empty);
                RefreshValues();
            }
        }
        public event EventHandler SelectedProfileNameChanged;

        public bool IsCustom => !StyleProfiles.TryGetStyle(SelectedProfileName, out _);
        public ControlStyle RecommendedStyle => IsGeneral ? ControlStyle.Balanced : GameCategories.RecommendedStyle(Category);
        public int RecommendedStyleIndex => (int)RecommendedStyle;

        private bool IsRecommended => StyleIndex == RecommendedStyleIndex;

        /// <summary>Posicion de la barra (0..3); -1 si es personalizado.</summary>
        public int StyleIndex
        {
            get => StyleProfiles.TryGetStyle(SelectedProfileName, out ControlStyle s) ? (int)s : -1;
            set { if (value >= 0 && value <= 3 && value != StyleIndex) owner.ApplyStyle(this, (ControlStyle)value); }
        }
        public event EventHandler StyleIndexChanged, IsCustomChanged, StatusTextChanged, SubtitleChanged, RowsChanged;

        public string CategoryName => CategoryDisplay(Category);

        public static string CategoryDisplay(GameCategory c) => c switch
        {
            GameCategory.Shooter => PPText.T("PP_CatShooter", "Shooter"),
            GameCategory.Racing => PPText.T("PP_CatRacing", "Racing"),
            GameCategory.Action => PPText.T("PP_CatAction", "Action / Adventure"),
            GameCategory.Fighting => PPText.T("PP_CatFighting", "Fighting / Platformer"),
            _ => PPText.T("PP_CatGeneral", "General"),
        };

        public static string StyleDisplay(ControlStyle s) => s switch
        {
            ControlStyle.Smooth => PPText.T("PP_StyleSmooth", "Smooth"),
            ControlStyle.Precise => PPText.T("PP_StylePrecise", "Precise"),
            ControlStyle.Competitive => PPText.T("PP_StyleCompetitive", "Competitive"),
            _ => PPText.T("PP_StyleBalanced", "Balanced"),
        };

        /// <summary>Linea de estado bajo el nombre ("Optimizado para Accion" / "Personalizado").</summary>
        public string StatusText => IsGeneral
            ? PPText.T("PP_HubGeneralStatus", "Used in any game without its own configuration (profile \"{0}\")", SelectedProfileName)
            : IsCustom
            ? PPText.T("PP_HubStatusCustom", "Customized by you (profile \"{0}\")", SelectedProfileName)
            : (IsRecommended
                ? PPText.T("PP_HubStatusOptimized", "Optimized for {0}", CategoryName)
                : PPText.T("PP_HubStatusStyle", "Style: {0} (recommended: {1})", StyleDisplay((ControlStyle)StyleIndex), StyleDisplay(RecommendedStyle)));

        /// <summary>Texto pequeno en la lista de la izquierda.</summary>
        public string Subtitle => IsGeneral
            ? PPText.T("PP_HubAllGamesSub", "Normal profile: {0}", SelectedProfileName)
            : $"{CategoryName} · " + (IsCustom ? PPText.T("PP_HubCustomShort", "custom")
                : IsRecommended ? PPText.T("PP_HubOptimizedShort", "optimized")
                : StyleDisplay((ControlStyle)StyleIndex).ToLowerInvariant());

        public List<SettingRow> Rows { get; private set; } = new List<SettingRow>();

        public void RefreshValues()
        {
            ProfileValues cur = StyleProfiles.ReadValues(SelectedProfileName);
            StyleValues rec = StyleProfiles.Values(RecommendedStyle);
            Rows = new List<SettingRow>
            {
                new SettingRow { Name = PPText.T("PP_RowDeadZone", "Stick dead zone"), Current = cur.DeadZonePct >= 0 ? $"{cur.DeadZonePct} %" : "-", Recommended = $"{rec.DeadZonePct} %" },
                new SettingRow { Name = PPText.T("PP_RowAntiDeadZone", "Anti-dead zone"), Current = cur.AntiDeadZonePct >= 0 ? $"{cur.AntiDeadZonePct} %" : "-", Recommended = $"{rec.AntiDeadZonePct} %" },
                new SettingRow { Name = PPText.T("PP_RowCurve", "Response curve"), Current = StyleProfiles.CurveDisplayName(cur.Curve), Recommended = StyleProfiles.CurveDisplayName(rec.Curve) },
            };
            RowsChanged?.Invoke(this, EventArgs.Empty);
            StyleIndexChanged?.Invoke(this, EventArgs.Empty);
            IsCustomChanged?.Invoke(this, EventArgs.Empty);
            StatusTextChanged?.Invoke(this, EventArgs.Empty);
            SubtitleChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string DeriveName(string path)
        {
            string clean = path.TrimStart('^', '*').TrimEnd('$').TrimEnd('\\');
            return Path.GetExtension(clean).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(clean) : Path.GetFileName(clean);
        }

        private async void LoadIconAsync()
        {
            string exe = await Task.Run(() => GameLibraryScanner.FindMainExe(PathText));
            if (string.IsNullOrEmpty(exe)) return;
            try
            {
                using Icon ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico == null) return;
                BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                Icon = src;
                IconChanged?.Invoke(this, EventArgs.Empty);
            }
            catch { }
        }
    }
}
