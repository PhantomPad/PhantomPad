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
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>Un juego instalado encontrado en Steam, Epic o GOG.</summary>
    public class DetectedGame
    {
        public string Name { get; set; }
        public string InstallDir { get; set; }
        public string Source { get; set; }
        /// <summary>Id de Steam (para generos y para lanzar el juego). Vacio si no es de Steam.</summary>
        public string SteamAppId { get; set; }
        /// <summary>Direccion para abrir el juego (steam://, com.epicgames.launcher:// o ruta del .exe).</summary>
        public string LaunchUri { get; set; }
    }

    /// <summary>
    /// PhantomPad: busca juegos instalados leyendo los archivos de las tiendas (sin red).
    /// Steam: libraryfolders.vdf + appmanifest_*.acf. Epic: manifiestos .item. GOG: registro.
    /// </summary>
    public static class GameLibraryScanner
    {
        public const string SOURCE_STEAM = "Steam";
        public const string SOURCE_EPIC = "Epic";
        public const string SOURCE_GOG = "GOG";
        public const string SOURCE_MANUAL = "Manual";

        // Herramientas de Steam que no son juegos
        private static readonly string[] steamSkipNames =
        {
            "Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "SteamVR",
            "Steam Controller Configs", "Wallpaper Engine",
        };

        public static List<DetectedGame> ScanAll()
        {
            var result = new List<DetectedGame>();
            result.AddRange(SafeScan(ScanSteam));
            result.AddRange(SafeScan(ScanEpic));
            result.AddRange(SafeScan(ScanGog));

            return result
                .Where(g => !string.IsNullOrWhiteSpace(g.Name) && Directory.Exists(g.InstallDir))
                .GroupBy(g => NormalizeDir(g.InstallDir), StringComparer.OrdinalIgnoreCase)
                .Select(grp => grp.First())
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Ruta de carpeta en minusculas con "\" final, para comparar.</summary>
        public static string NormalizeDir(string dir)
        {
            string full = Path.GetFullPath(dir).Replace('/', '\\');
            if (!full.EndsWith("\\")) full += "\\";
            return full;
        }

        private static IEnumerable<DetectedGame> SafeScan(Func<IEnumerable<DetectedGame>> scan)
        {
            try { return scan().ToList(); }
            catch (Exception) { return Enumerable.Empty<DetectedGame>(); }
        }

        private static IEnumerable<DetectedGame> ScanSteam()
        {
            string steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (string.IsNullOrEmpty(steamPath)) yield break;
            steamPath = steamPath.Replace('/', '\\');

            var libraries = new List<string> { steamPath };
            string libFile = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libFile))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(libFile), "\"path\"\\s+\"([^\"]+)\""))
                {
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
            }

            foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string text = File.ReadAllText(manifest);
                    string appId = Regex.Match(text, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
                    string name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    string installDir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"").Groups[1].Value;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) continue;
                    if (steamSkipNames.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;

                    yield return new DetectedGame
                    {
                        Name = name,
                        InstallDir = Path.Combine(apps, "common", installDir),
                        Source = SOURCE_STEAM,
                        SteamAppId = appId,
                        LaunchUri = string.IsNullOrEmpty(appId) ? null : $"steam://rungameid/{appId}",
                    };
                }
            }
        }

        private static IEnumerable<DetectedGame> ScanEpic()
        {
            string manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifests)) yield break;

            foreach (string file in Directory.GetFiles(manifests, "*.item"))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement root = doc.RootElement;
                string name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                string dir = root.TryGetProperty("InstallLocation", out var d) ? d.GetString() : null;
                bool isGame = true;
                if (root.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array)
                {
                    isGame = cats.EnumerateArray().Any(c => string.Equals(c.GetString(), "games", StringComparison.OrdinalIgnoreCase));
                }

                if (isGame && !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dir))
                {
                    string ns = root.TryGetProperty("CatalogNamespace", out var a) ? a.GetString() : null;
                    string item = root.TryGetProperty("CatalogItemId", out var b) ? b.GetString() : null;
                    string appName = root.TryGetProperty("AppName", out var c) ? c.GetString() : null;
                    string launch = (!string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(item) && !string.IsNullOrEmpty(appName))
                        ? $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{appName}?action=launch&silent=true"
                        : null;
                    yield return new DetectedGame { Name = name, InstallDir = dir, Source = SOURCE_EPIC, LaunchUri = launch };
                }
            }
        }

        private static IEnumerable<DetectedGame> ScanGog()
        {
            using RegistryKey games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (games == null) yield break;

            foreach (string id in games.GetSubKeyNames())
            {
                using RegistryKey key = games.OpenSubKey(id);
                string name = key?.GetValue("gameName") as string;
                string dir = key?.GetValue("path") as string;
                string exe = key?.GetValue("exe") as string;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(dir))
                {
                    yield return new DetectedGame { Name = name, InstallDir = dir, Source = SOURCE_GOG, LaunchUri = exe };
                }
            }
        }

        /// <summary>
        /// Busca el .exe mas grande de la carpeta del juego (hasta 2 niveles) para mostrar su icono.
        /// Solo se usa para el icono; la deteccion del juego usa la carpeta completa.
        /// </summary>
        public static string FindMainExe(string installDir)
        {
            try
            {
                if (File.Exists(installDir)) return installDir;
                if (!Directory.Exists(installDir)) return null;
                string[] skip = { "unins", "setup", "redist", "crash", "report", "vc_", "dxsetup", "dotnet", "install", "helper", "anticheat", "easyanticheat", "be_service" };
                var candidates = new List<FileInfo>();
                var dirInfo = new DirectoryInfo(installDir);
                candidates.AddRange(dirInfo.GetFiles("*.exe", SearchOption.TopDirectoryOnly));
                foreach (DirectoryInfo sub in dirInfo.GetDirectories())
                {
                    try { candidates.AddRange(sub.GetFiles("*.exe", SearchOption.TopDirectoryOnly)); } catch { }
                }

                return candidates
                    .Where(f => !skip.Any(s => f.Name.ToLowerInvariant().Contains(s)))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault()?.FullName;
            }
            catch
            {
                return null;
            }
        }
    }
}
