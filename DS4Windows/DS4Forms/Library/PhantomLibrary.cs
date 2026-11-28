/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

La forma de fusionar los juegos detectados con los que ya estan en la biblioteca
(sin duplicar y sin pisar lo que el usuario cambio a mano) esta inspirada en
Playnite (GameDatabase.ImportGames). Playnite © 2020 Josef Nemec, licencia MIT.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DS4Windows;
using DS4WinWPF.DS4Forms.GameProfiles;
using Microsoft.Win32;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>Un juego de la biblioteca (Escritorio).</summary>
    public class LibraryGame
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; }
        /// <summary>Steam, Epic, GOG o Manual.</summary>
        public string Source { get; set; }
        /// <summary>Id en la tienda (Steam: appid).</summary>
        public string SourceId { get; set; }
        public string InstallDir { get; set; }
        /// <summary>steam://, com.epicgames.launcher:// o ruta del .exe.</summary>
        public string LaunchUri { get; set; }
        public string Arguments { get; set; }
        public bool Installed { get; set; } = true;
        public bool Hidden { get; set; }
        public bool Favorite { get; set; }
        public DateTime Added { get; set; } = DateTime.Now;
        public DateTime? LastPlayed { get; set; }

        // Datos del juego (se pueden editar)
        public string Description { get; set; }
        public string Developer { get; set; }
        public string Publisher { get; set; }
        public string Genres { get; set; }
        public int? ReleaseYear { get; set; }

        // Imagenes elegidas por el usuario o descargadas (carpeta Library\<Id>)
        public string CoverPath { get; set; }
        public string BackgroundPath { get; set; }
        public string IconPath { get; set; }
        public string LogoPath { get; set; }

        /// <summary>El usuario cambio el nombre: al volver a detectar no se pisa.</summary>
        public bool NameEditedByUser { get; set; }

        /// <summary>Juego de la tienda de Steam del que salen los datos (para los que no son de Steam).</summary>
        public string MetadataSteamId { get; set; }
        /// <summary>Ya se busco en Steam automaticamente (encontrado o no): no se vuelve a buscar solo.</summary>
        public bool MetadataChecked { get; set; }
        /// <summary>Version de la busqueda automatica que ya se hizo (2 = tienda propia + Steam + IGDB).</summary>
        public int MetadataVersion { get; set; }
        /// <summary>Numero del juego en su tienda (Epic: CatalogItemId; GOG: id del producto).</summary>
        public string StoreItemId { get; set; }
    }

    /// <summary>
    /// PhantomPad: biblioteca de juegos del apartado Escritorio.
    /// Se guarda en %appdata%\PhantomPad\PhantomPadLibrary.json; las imagenes propias en
    /// %appdata%\PhantomPad\Library\.
    /// </summary>
    public static class PhantomLibrary
    {
        public const string SOURCE_MANUAL = "Manual";
        private const string FILE_NAME = "PhantomPadLibrary.json";

        private class LibraryFile
        {
            /// <summary>Descargar solos los datos e imagenes de los juegos nuevos.</summary>
            public bool AutoMetadata { get; set; } = true;
            /// <summary>IGDB (opcional): usarlo y sus claves de Twitch (el secreto va cifrado).</summary>
            public bool IgdbEnabled { get; set; }
            public string IgdbClientId { get; set; }
            public string IgdbSecretProtected { get; set; }
            public List<LibraryGame> Games { get; set; } = new List<LibraryGame>();
        }

        public static bool AutoMetadata
        {
            get => Data.AutoMetadata;
            set
            {
                if (Data.AutoMetadata == value) return;
                Data.AutoMetadata = value;
                Save();
            }
        }

        public static bool IgdbEnabled => Data.IgdbEnabled;
        public static string IgdbClientId => Data.IgdbClientId;
        public static string IgdbSecret => SecretStore.Unprotect(Data.IgdbSecretProtected);
        public static bool HasIgdbSecret => !string.IsNullOrEmpty(Data.IgdbSecretProtected);

        /// <summary>Guarda los ajustes de IGDB. secret = null deja el que ya habia.</summary>
        public static void SetIgdb(bool enabled, string clientId, string secret)
        {
            Data.IgdbEnabled = enabled;
            Data.IgdbClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId.Trim();
            if (secret != null) Data.IgdbSecretProtected = secret.Length == 0 ? null : SecretStore.Protect(secret.Trim());
            Igdb.ForgetToken();
            Save();
            // Con IGDB recien activado se vuelve a mirar lo que quedo sin datos
            if (enabled) lock (fileLock) foreach (LibraryGame g in Data.Games) if (NeedsData(g)) g.MetadataVersion = 0;
        }

        private static readonly object fileLock = new object();
        private static LibraryFile data;
        private static bool readFailed;

        public static event Action Changed;

        private static string FilePath => Path.Combine(Global.appdatapath, FILE_NAME);
        public static string ImagesDir => Path.Combine(Global.appdatapath, "Library");

        private static LibraryFile Data
        {
            get
            {
                if (data != null) return data;
                lock (fileLock)
                {
                    if (data != null) return data;
                    LibraryFile read = null;
                    try
                    {
                        if (File.Exists(FilePath)) read = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(FilePath));
                    }
                    catch
                    {
                        readFailed = true; // archivo danado: no se sobrescribe
                    }
                    data = read ?? new LibraryFile();
                    data.Games ??= new List<LibraryGame>();
                    return data;
                }
            }
        }

        public static List<LibraryGame> Games
        {
            get { lock (fileLock) return Data.Games.ToList(); }
        }

        // ===================== Deteccion automatica =====================

        /// <summary>
        /// Busca los juegos instalados (Steam, Epic, GOG) y los une con la biblioteca:
        /// los nuevos se anaden, los que ya estaban se actualizan sin pisar lo que el usuario
        /// cambio, y los que ya no estan quedan como "no instalado".
        /// </summary>
        public static void Sync()
        {
            List<DetectedGame> found = GameLibraryScanner.ScanAll();
            lock (fileLock)
            {
                var games = Data.Games;
                var seen = new HashSet<string>();
                foreach (DetectedGame d in found)
                {
                    string sourceId = d.Source == GameLibraryScanner.SOURCE_STEAM ? d.SteamAppId : null;
                    LibraryGame g = games.FirstOrDefault(x => x.Source == d.Source &&
                        (!string.IsNullOrEmpty(sourceId) ? x.SourceId == sourceId
                            : string.Equals(GameLibraryScanner.NormalizeDir(x.InstallDir), GameLibraryScanner.NormalizeDir(d.InstallDir), StringComparison.OrdinalIgnoreCase)));
                    if (g == null)
                    {
                        g = new LibraryGame { Name = d.Name, Source = d.Source, SourceId = sourceId };
                        games.Add(g);
                    }
                    else if (!g.NameEditedByUser)
                    {
                        g.Name = d.Name;
                    }
                    g.InstallDir = d.InstallDir;
                    g.LaunchUri = d.LaunchUri;
                    if (!string.IsNullOrEmpty(d.StoreItemId)) g.StoreItemId = d.StoreItemId;
                    g.Installed = true;
                    seen.Add(g.Id);
                }

                foreach (LibraryGame g in games)
                {
                    if (g.Source == SOURCE_MANUAL)
                        g.Installed = File.Exists(g.LaunchUri) || Directory.Exists(g.InstallDir);
                    else if (!seen.Contains(g.Id))
                        g.Installed = false;
                }
            }
            Save();
            Changed?.Invoke();
        }

        // ===================== Juegos anadidos a mano =====================

        /// <summary>Anade un juego a mano a partir de su .exe (o acceso directo). Devuelve el juego.</summary>
        public static LibraryGame AddManual(string exePath)
        {
            lock (fileLock)
            {
                LibraryGame existing = Data.Games.FirstOrDefault(x => string.Equals(x.LaunchUri, exePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Hidden = false;
                    Save();
                    Changed?.Invoke();
                    return existing;
                }

                var g = new LibraryGame
                {
                    Name = GuessName(exePath),
                    Source = SOURCE_MANUAL,
                    LaunchUri = exePath,
                    InstallDir = Path.GetDirectoryName(exePath),
                    Installed = true,
                };
                Data.Games.Add(g);
                Save();
                Changed?.Invoke();
                return g;
            }
        }

        /// <summary>Palabras de nombres que no son el del juego (motor, sistema, lanzador...).</summary>
        private static readonly string[] GenericNames =
            { "microsoft", "windows", "unity", "unreal", "engine", "launcher", "shipping", "win64", "win32", "bootstrap", "game.exe" };

        private static readonly string[] GenericFolders =
            { "bin", "binaries", "win64", "win32", "x64", "x86", "game", "retail", "release" };

        private static bool GoodName(string s) =>
            !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= 60 &&
            !GenericNames.Any(w => s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// Nombre del juego: el "nombre del producto" del .exe, su descripcion, el nombre de su
        /// carpeta o el del archivo (en ese orden, saltando nombres genericos).
        /// </summary>
        private static string GuessName(string exePath)
        {
            try
            {
                if (exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
                    if (GoodName(info.ProductName)) return info.ProductName.Trim();
                    if (GoodName(info.FileDescription)) return info.FileDescription.Trim();
                }
                DirectoryInfo dir = new FileInfo(exePath).Directory;
                while (dir != null && GenericFolders.Contains(dir.Name.ToLowerInvariant())) dir = dir.Parent;
                if (dir != null && dir.Parent != null && !string.Equals(dir.FullName, Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
                    && !dir.FullName.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
                    return dir.Name;
            }
            catch { }
            return Path.GetFileNameWithoutExtension(exePath);
        }

        public static void Update(LibraryGame game)
        {
            lock (fileLock)
            {
                int i = Data.Games.FindIndex(x => x.Id == game.Id);
                if (i >= 0) Data.Games[i] = game;
            }
            Save();
            Changed?.Invoke();
        }

        /// <summary>Quita el juego de la biblioteca. Los detectados solo se ocultan (si no, volverian a aparecer).</summary>
        public static void Remove(LibraryGame game)
        {
            lock (fileLock)
            {
                LibraryGame g = Data.Games.FirstOrDefault(x => x.Id == game.Id);
                if (g == null) return;
                if (g.Source == SOURCE_MANUAL)
                {
                    Data.Games.Remove(g);
                    // Sus imagenes propias ya no sirven
                    try
                    {
                        string dir = Path.Combine(ImagesDir, g.Id);
                        if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    }
                    catch { }
                }
                else g.Hidden = true;
            }
            Save();
            Changed?.Invoke();
        }

        // ===================== Jugar =====================

        /// <summary>Abre el juego. Si hay un mando conectado, PhantomPad le aplica sus ajustes como siempre.</summary>
        public static string Launch(LibraryGame game)
        {
            try
            {
                string target = game.LaunchUri;
                if (string.IsNullOrWhiteSpace(target)) return PPText.T("PP_LibNoLaunch", "This game has nothing to launch.");
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                if (File.Exists(target))
                {
                    psi.WorkingDirectory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(game.Arguments)) psi.Arguments = game.Arguments;
                }
                Process.Start(psi);

                lock (fileLock)
                {
                    LibraryGame g = Data.Games.FirstOrDefault(x => x.Id == game.Id);
                    if (g != null) g.LastPlayed = DateTime.Now;
                }
                Save();
                Changed?.Invoke();
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        // ===================== Imagenes =====================

        private static string steamCache;

        /// <summary>Carpeta donde Steam guarda las imagenes de cada juego (appcache\librarycache).</summary>
        private static string SteamLibraryCache
        {
            get
            {
                if (steamCache != null) return steamCache;
                try
                {
                    string steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                    steamCache = string.IsNullOrEmpty(steam) ? "" : Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache");
                }
                catch { steamCache = ""; }
                return steamCache;
            }
        }

        /// <summary>Una imagen que Steam ya tiene en el PC (library_600x900.jpg, library_hero.jpg, logo.png...).</summary>
        private static string SteamImage(LibraryGame g, string fileName)
        {
            if (g.Source != GameLibraryScanner.SOURCE_STEAM || string.IsNullOrEmpty(g.SourceId) || string.IsNullOrEmpty(SteamLibraryCache)) return null;
            try
            {
                string dir = Path.Combine(SteamLibraryCache, g.SourceId);
                if (Directory.Exists(dir))
                    return Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                string old = Path.Combine(SteamLibraryCache, $"{g.SourceId}_{fileName}"); // Steam antiguo
                return File.Exists(old) ? old : null;
            }
            catch { return null; }
        }

        private static string Existing(string path) => !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;

        /// <summary>
        /// Copia una imagen elegida por el usuario a %appdata%\PhantomPad\Library\&lt;Id&gt;\ y devuelve la
        /// ruta nueva. Cada copia lleva un nombre distinto para que la anterior se pueda borrar sin
        /// problemas aunque se este mostrando.
        /// </summary>
        public static string StoreImage(LibraryGame g, string kind, string sourceFile)
        {
            string dir = Path.Combine(ImagesDir, g.Id);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, $"{kind}_{DateTime.Now.Ticks}{Path.GetExtension(sourceFile).ToLowerInvariant()}");
            File.Copy(sourceFile, target, true);
            return target;
        }

        /// <summary>Guarda una imagen ya creada (p. ej. el icono sacado de un .exe) como PNG.</summary>
        public static string StoreImage(LibraryGame g, string kind, System.Windows.Media.Imaging.BitmapSource image)
        {
            string dir = Path.Combine(ImagesDir, g.Id);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, $"{kind}_{DateTime.Now.Ticks}.png");
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using (FileStream fs = File.Create(target)) enc.Save(fs);
            return target;
        }

        /// <summary>Borra una imagen propia que ya no se usa (solo dentro de la carpeta Library).</summary>
        public static void DeleteStoredImage(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path) &&
                    Path.GetFullPath(path).StartsWith(Path.GetFullPath(ImagesDir), StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch { }
        }

        public static string CoverFor(LibraryGame g) => Existing(g.CoverPath) ?? SteamImage(g, "library_600x900.jpg");
        public static string IconFileFor(LibraryGame g) => Existing(g.IconPath);
        public static string BackgroundFor(LibraryGame g) => Existing(g.BackgroundPath) ?? SteamImage(g, "library_hero.jpg") ?? SteamImage(g, "header.jpg");
        public static string LogoFor(LibraryGame g) => Existing(g.LogoPath) ?? SteamImage(g, "logo.png");

        // ===================== Datos de Steam =====================

        /// <summary>Numero de Steam del juego: el suyo, el ya emparejado o (si se permite) el que encuentre la busqueda.</summary>
        public static async System.Threading.Tasks.Task<string> FindSteamIdAsync(LibraryGame g, bool search)
        {
            if (g.Source == GameLibraryScanner.SOURCE_STEAM && !string.IsNullOrEmpty(g.SourceId)) return g.SourceId;
            if (!string.IsNullOrEmpty(g.MetadataSteamId)) return g.MetadataSteamId;
            if (!search) return null;
            var results = await SteamMetadata.SearchAsync(g.Name);
            return SteamMetadata.BestMatch(g.Name, results)?.Id;
        }

        /// <summary>Al juego le falta algo que una fuente podria dar.</summary>
        private static bool NeedsData(LibraryGame g) =>
            g.Description == null || g.Genres == null || g.ReleaseYear == null || CoverFor(g) == null || BackgroundFor(g) == null;

        /// <summary>
        /// Reune los datos de un juego, como Playnite: primero su propia tienda (Epic con el catalogo
        /// del PC, GOG con su base de datos), despues Steam y por ultimo IGDB (si tiene claves).
        /// La primera fuente que tenga cada dato gana. steamPicker (opcional) deja elegir en Steam
        /// cuando el nombre no es claro y ninguna otra fuente encontro nada.
        /// </summary>
        public static async System.Threading.Tasks.Task<GameMetadataResult> CollectAsync(LibraryGame g, string name,
            Func<string, List<(string Id, string Name)>, string> steamPicker = null)
        {
            name = string.IsNullOrWhiteSpace(name) ? g.Name : name.Trim();
            var result = new GameMetadataResult();

            // 1. Su propia tienda
            if (g.Source == GameLibraryScanner.SOURCE_EPIC) result.Merge(EpicCatalog.Find(g.StoreItemId, name), "Epic");
            if (g.Source == GameLibraryScanner.SOURCE_GOG)
            {
                try { result.Merge(await GogDb.GetAsync(g.StoreItemId), "GOG"); } catch { }
            }
            // Juegos anadidos a mano: puede que esten en el catalogo de Epic del PC
            if (g.Source == SOURCE_MANUAL) result.Merge(EpicCatalog.Find(null, name), "Epic");

            // 2. Steam (el propio juego de Steam, el ya emparejado o uno con el mismo nombre)
            try
            {
                string id = g.Source == GameLibraryScanner.SOURCE_STEAM && !string.IsNullOrEmpty(g.SourceId) ? g.SourceId : g.MetadataSteamId;
                if (id == null)
                {
                    var results = await SteamMetadata.SearchAsync(name);
                    id = SteamMetadata.BestMatch(name, results)?.Id;
                    if (id == null && steamPicker != null && result.Sources.Count == 0 && !Igdb.Ready) id = steamPicker(name, results);
                }
                if (id != null)
                {
                    SteamGameInfo info = await SteamMetadata.GetDetailsAsync(id);
                    if (info != null)
                    {
                        bool local = g.Source == GameLibraryScanner.SOURCE_STEAM; // sus imagenes ya estan en el PC
                        result.Merge(new GameMetadataResult
                        {
                            Description = info.Description, Genres = info.Genres, Developer = info.Developer,
                            Publisher = info.Publisher, Year = info.Year, SteamAppId = id,
                            CoverUrl = local ? null : SteamMetadata.ImageUrl(id, "library_600x900_2x.jpg"),
                            BackgroundUrl = local ? null : SteamMetadata.ImageUrl(id, "library_hero.jpg"),
                            LogoUrl = local ? null : SteamMetadata.ImageUrl(id, "logo.png"),
                        }, "Steam");
                    }
                }
            }
            catch { /* Steam no responde: siguen las demas fuentes */ }

            // 3. IGDB (opcional)
            if (Igdb.Ready)
            {
                try { result.Merge(await Igdb.SearchAsync(name), "IGDB"); } catch { }
            }
            return result;
        }

        /// <summary>
        /// Completa un juego SIN pisar nada: solo rellena lo que esta vacio y solo pone imagenes
        /// donde no hay ninguna. Devuelve true si alguna fuente tenia datos.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> FillFromSourcesAsync(LibraryGame g)
        {
            GameMetadataResult r = await CollectAsync(g, null);
            g.Description ??= r.Description;
            g.Genres ??= r.Genres;
            g.Developer ??= r.Developer;
            g.Publisher ??= r.Publisher;
            g.ReleaseYear ??= r.Year;
            if (r.SteamAppId != null && g.Source != GameLibraryScanner.SOURCE_STEAM) g.MetadataSteamId ??= r.SteamAppId;

            if (CoverFor(g) == null) g.CoverPath = await StoreDownloaded(g, "cover", MetadataHttp.DownloadImageAsync(r.CoverUrl));
            if (BackgroundFor(g) == null) g.BackgroundPath = await StoreDownloaded(g, "background", MetadataHttp.DownloadImageAsync(r.BackgroundUrl));
            if (LogoFor(g) == null) g.LogoPath = await StoreDownloaded(g, "logo", MetadataHttp.DownloadImageAsync(r.LogoUrl));

            g.MetadataChecked = true;
            g.MetadataVersion = 2;
            Update(g);
            return r.Sources.Count > 0;
        }

        /// <summary>Guarda en la carpeta de la biblioteca una imagen recien descargada (y borra la temporal).</summary>
        private static async System.Threading.Tasks.Task<string> StoreDownloaded(LibraryGame g, string kind, System.Threading.Tasks.Task<string> download)
        {
            string temp = await download;
            if (temp == null) return null;
            try { return StoreImage(g, kind, temp); }
            catch { return null; }
            finally { try { File.Delete(temp); } catch { } }
        }

        private static bool autoRunning;

        /// <summary>Juegos que la busqueda automatica todavia debe mirar.</summary>
        public static List<LibraryGame> PendingData() =>
            Games.Where(x => !x.Hidden && (!x.MetadataChecked || (x.MetadataVersion < 2 && NeedsData(x)))).ToList();

        /// <summary>Busca en Steam los datos de los juegos que aun no se miraron (si el interruptor esta encendido).</summary>
        public static async System.Threading.Tasks.Task AutoFillAsync()
        {
            if (!AutoMetadata || autoRunning) return;
            autoRunning = true;
            try
            {
                // Nuevos, o ya mirados con la version anterior (solo Steam) y a los que les falta algo
                foreach (LibraryGame g in PendingData())
                {
                    if (!AutoMetadata) break;
                    try { await FillFromSourcesAsync(g); }
                    catch { /* sin internet o Steam no responde: se intenta otro dia */ break; }
                    await System.Threading.Tasks.Task.Delay(400); // no saturar la tienda
                }
            }
            finally { autoRunning = false; }
        }

        /// <summary>El .exe del juego (para su icono): el propio o el mas grande de su carpeta.</summary>
        public static string ExeFor(LibraryGame g)
        {
            if (!string.IsNullOrEmpty(g.LaunchUri) && File.Exists(g.LaunchUri) && g.LaunchUri.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return g.LaunchUri;
            return string.IsNullOrEmpty(g.InstallDir) ? null : GameLibraryScanner.FindMainExe(g.InstallDir);
        }

        // ===================== Guardar =====================

        private static void Save()
        {
            lock (fileLock)
            {
                if (readFailed || data == null) return;
                try
                {
                    Directory.CreateDirectory(Global.appdatapath);
                    string temp = FilePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null, true);
                    else File.Move(temp, FilePath);
                }
                catch (Exception e)
                {
                    AppLogger.LogToGui(PPText.T("PP_LibSaveFailed", "Could not save the game library: {0}", e.Message), false);
                }
            }
        }
    }
}
