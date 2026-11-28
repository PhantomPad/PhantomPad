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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>Datos de un juego sacados de la tienda de Steam.</summary>
    public class SteamGameInfo
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Genres { get; set; }
        public string Developer { get; set; }
        public string Publisher { get; set; }
        public int? Year { get; set; }
    }

    /// <summary>
    /// PhantomPad: descripciones e imagenes desde la tienda publica de Steam (sin cuenta ni clave).
    /// Solo se envia a Steam el nombre o el numero del juego.
    /// </summary>
    public static class SteamMetadata
    {
        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhantomPad");
            return c;
        }

        /// <summary>Idioma de la tienda segun el idioma de PhantomPad.</summary>
        private static string Lang => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "spanish" : "english";

        // ===================== Buscar =====================

        /// <summary>Juegos de la tienda de Steam con ese nombre (id, nombre).</summary>
        public static async Task<List<(string Id, string Name)>> SearchAsync(string name)
        {
            var result = new List<(string, string)>();
            string url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(CleanForSearch(name))}&l={Lang}&cc=US";
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(url));
            if (doc.RootElement.TryGetProperty("items", out JsonElement items))
            {
                foreach (JsonElement it in items.EnumerateArray())
                {
                    string id = it.GetProperty("id").ToString();
                    string n = it.TryGetProperty("name", out JsonElement nn) ? nn.GetString() : "";
                    if (!string.IsNullOrEmpty(id)) result.Add((id, n));
                }
            }
            return result;
        }

        /// <summary>El resultado que es claramente el mismo juego (o null si no hay uno seguro).</summary>
        public static (string Id, string Name)? BestMatch(string gameName, List<(string Id, string Name)> results)
        {
            string target = Normalize(gameName);
            if (target.Length == 0) return null;
            foreach (var r in results)
            {
                if (Normalize(r.Name) == target) return r;
            }
            // Sin coincidencia exacta: se acepta el primero solo si su nombre es casi igual
            var first = results.FirstOrDefault();
            if (first.Id != null)
            {
                string n = Normalize(first.Name);
                if (n.Length > 0 && (n.StartsWith(target) || target.StartsWith(n)) && Math.Abs(n.Length - target.Length) <= 12) return first;
            }
            return null;
        }

        /// <summary>Nombre sin simbolos ni mayusculas, para comparar ("The Callisto Protocol™" = "the callisto protocol").</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (char c in d)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (char.IsWhiteSpace(c) || c == '-' || c == ':' || c == '_') sb.Append(' ');
            }
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        private static string CleanForSearch(string s) => Regex.Replace(s ?? "", @"[™®©]", "").Trim();

        // ===================== Datos =====================

        public static async Task<SteamGameInfo> GetDetailsAsync(string appId)
        {
            string url = $"https://store.steampowered.com/api/appdetails?appids={Uri.EscapeDataString(appId)}&l={Lang}";
            using JsonDocument doc = JsonDocument.Parse(await http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty(appId, out JsonElement root) ||
                !root.TryGetProperty("success", out JsonElement ok) || !ok.GetBoolean() ||
                !root.TryGetProperty("data", out JsonElement data))
                return null;

            string Str(string prop) => data.TryGetProperty(prop, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            string List(string prop, string inner = null)
            {
                if (!data.TryGetProperty(prop, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return null;
                var parts = arr.EnumerateArray()
                    .Select(x => inner == null ? x.GetString() : (x.TryGetProperty(inner, out JsonElement d) ? d.GetString() : null))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                return parts.Count == 0 ? null : string.Join(", ", parts);
            }

            var info = new SteamGameInfo
            {
                AppId = appId,
                Name = CleanForSearch(Str("name")),
                Description = HtmlToText(Str("about_the_game")) ?? HtmlToText(Str("short_description")),
                Genres = List("genres", "description"),
                Developer = List("developers"),
                Publisher = List("publishers"),
            };
            if (data.TryGetProperty("release_date", out JsonElement rd) && rd.TryGetProperty("date", out JsonElement date))
            {
                Match m = Regex.Match(date.GetString() ?? "", @"\b(19|20)\d{2}\b");
                if (m.Success) info.Year = int.Parse(m.Value);
            }
            return info;
        }

        /// <summary>Texto limpio a partir del HTML de la tienda (saltos de linea, sin etiquetas, maximo ~3000 letras).</summary>
        public static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;
            string t = Regex.Replace(html, @"<\s*br\s*/?>|</\s*p\s*>|</\s*h\d\s*>|</\s*li\s*>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<\s*li[^>]*>", "• ", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", "");
            t = WebUtility.HtmlDecode(t);
            t = Regex.Replace(t, @"[ \t]+", " ");
            t = Regex.Replace(t, @"\n\s*\n\s*(\n\s*)+", "\n\n").Trim();
            if (t.Length > 3000) t = t.Substring(0, 3000).TrimEnd() + "...";
            return t.Length == 0 ? null : t;
        }

        // ===================== Imagenes =====================

        private static IEnumerable<string> ImageUrls(string appId, string file)
        {
            yield return $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/{file}";
            yield return $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{file}";
        }

        /// <summary>Descarga una imagen de Steam a un archivo temporal. Devuelve la ruta o null.</summary>
        public static async Task<string> DownloadImageAsync(string appId, params string[] fileNames)
        {
            foreach (string file in fileNames)
            {
                foreach (string url in ImageUrls(appId, file))
                {
                    try
                    {
                        using HttpResponseMessage resp = await http.GetAsync(url);
                        if (!resp.IsSuccessStatusCode) continue;
                        string type = resp.Content.Headers.ContentType?.MediaType ?? "";
                        if (!type.StartsWith("image/")) continue;
                        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                        if (bytes.Length < 1024) continue;
                        string temp = Path.Combine(Path.GetTempPath(), $"phantompad_{appId}_{Guid.NewGuid():N}{Path.GetExtension(file)}");
                        await File.WriteAllBytesAsync(temp, bytes);
                        return temp;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// "Direccion" de una imagen de Steam para la cadena de fuentes: steam:&lt;appid&gt;:&lt;archivo&gt;.
        /// MetadataHttp la descarga con las alternativas de Steam (otro tamano u otro servidor).
        /// </summary>
        public static string ImageUrl(string appId, string file) => $"steam:{appId}:{file}";

        public static Task<string> DownloadByPseudoUrlAsync(string pseudo)
        {
            string[] p = pseudo.Split(':');
            if (p.Length != 3) return Task.FromResult<string>(null);
            return p[2] switch
            {
                "library_600x900_2x.jpg" => DownloadCoverAsync(p[1]),
                "library_hero.jpg" => DownloadBackgroundAsync(p[1]),
                "logo.png" => DownloadLogoAsync(p[1]),
                _ => DownloadImageAsync(p[1], p[2]),
            };
        }

        public static Task<string> DownloadCoverAsync(string appId) => DownloadImageAsync(appId, "library_600x900_2x.jpg", "library_600x900.jpg");
        public static Task<string> DownloadBackgroundAsync(string appId) => DownloadImageAsync(appId, "library_hero.jpg", "page_bg_raw.jpg", "header.jpg");
        public static Task<string> DownloadLogoAsync(string appId) => DownloadImageAsync(appId, "logo.png");
    }
}
