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
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DS4WinWPF.DS4Forms.GameProfiles
{
    /// <summary>Tipo de juego: decide la configuracion recomendada.</summary>
    public enum GameCategory { General, Shooter, Racing, Action, Fighting }

    /// <summary>Estilo de control (la "barra" tipo NVIDIA).</summary>
    public enum ControlStyle { Smooth = 0, Balanced = 1, Precise = 2, Competitive = 3 }

    /// <summary>Gyro aim: el giroscopio mueve el stick derecho virtual (modo "joystick tipo raton").</summary>
    public enum GyroAim { Off = 0, WhileAiming = 1, Always = 2 }

    /// <summary>
    /// PhantomPad: averigua el tipo de un juego.
    /// 1) Lista incluida GameCategories.json (editable, sin internet).
    /// 2) Opcional: generos publicos de la tienda de Steam (solo se envia el numero del juego).
    /// </summary>
    public static class GameCategories
    {
        private const string DB_FILE = "GameCategories.json";

        private static Dictionary<string, GameCategory> names = new Dictionary<string, GameCategory>();
        private static Dictionary<string, GameCategory> keywords = new Dictionary<string, GameCategory>();
        private static Dictionary<string, GameCategory> steamGenres = new Dictionary<string, GameCategory>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        /// <summary>Estilo recomendado para cada tipo de juego.</summary>
        public static ControlStyle RecommendedStyle(GameCategory category) => category switch
        {
            GameCategory.Shooter => ControlStyle.Precise,
            GameCategory.Racing => ControlStyle.Balanced,
            GameCategory.Fighting => ControlStyle.Competitive,
            GameCategory.Action => ControlStyle.Smooth,
            _ => ControlStyle.Balanced,
        };

        private static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, DB_FILE);
                if (!File.Exists(path)) return;
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                names = ReadMap(doc.RootElement, "names");
                keywords = ReadMap(doc.RootElement, "keywords");
                steamGenres = new Dictionary<string, GameCategory>(ReadMap(doc.RootElement, "steamGenres"), StringComparer.OrdinalIgnoreCase);
            }
            catch { /* lista danada: todo queda como General */ }
        }

        private static Dictionary<string, GameCategory> ReadMap(JsonElement root, string prop)
        {
            var map = new Dictionary<string, GameCategory>();
            if (root.TryGetProperty(prop, out JsonElement obj) && obj.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in obj.EnumerateObject())
                {
                    if (Enum.TryParse(p.Value.GetString(), true, out GameCategory c)) map[p.Name.ToLowerInvariant()] = c;
                }
            }

            return map;
        }

        /// <summary>Tipo segun la lista incluida. Devuelve null si no lo reconoce.</summary>
        public static GameCategory? FromLocalDatabase(string gameName)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(gameName)) return null;
            string n = gameName.ToLowerInvariant();
            foreach (var kv in names.OrderByDescending(k => k.Key.Length))
            {
                if (n.Contains(kv.Key)) return kv.Value;
            }

            foreach (var kv in keywords)
            {
                if (n.Contains(kv.Key)) return kv.Value;
            }

            return null;
        }

        /// <summary>Consulta los generos publicos de un juego en la tienda de Steam.</summary>
        public static async Task<GameCategory?> FromSteamAsync(HttpClient client, string appId)
        {
            EnsureLoaded();
            if (client == null || string.IsNullOrEmpty(appId) || !appId.All(char.IsDigit)) return null;
            try
            {
                string url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=genres&l=english";
                using HttpResponseMessage resp = await client.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null;
                using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty(appId, out JsonElement app) ||
                    !app.TryGetProperty("data", out JsonElement data) ||
                    !data.TryGetProperty("genres", out JsonElement genres)) return null;

                var found = genres.EnumerateArray()
                    .Select(g => g.TryGetProperty("description", out var d) ? d.GetString() : null)
                    .Where(d => d != null && steamGenres.ContainsKey(d))
                    .Select(d => steamGenres[d])
                    .ToList();

                // Prioridad: carreras > accion > general
                if (found.Contains(GameCategory.Racing)) return GameCategory.Racing;
                if (found.Contains(GameCategory.Action)) return GameCategory.Action;
                return found.Count > 0 ? found[0] : (GameCategory?)null;
            }
            catch
            {
                return null; // sin internet o Steam no responde: se queda con lo local
            }
        }
    }
}
