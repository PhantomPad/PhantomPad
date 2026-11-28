/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Como en Playnite, cada juego toma primero los datos de su propia tienda y despues los de
bases de datos generales. Playnite © 2020 Josef Nemec, licencia MIT.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DS4WinWPF.DS4Forms.Library
{
    /// <summary>Datos de un juego encontrados en una fuente (lo que no se sabe queda en null).</summary>
    public class GameMetadataResult
    {
        public string Description { get; set; }
        public string Genres { get; set; }
        public string Developer { get; set; }
        public string Publisher { get; set; }
        public int? Year { get; set; }
        public string CoverUrl { get; set; }
        public string BackgroundUrl { get; set; }
        public string LogoUrl { get; set; }
        /// <summary>Juego de Steam del que salieron los datos (si alguno salio de Steam).</summary>
        public string SteamAppId { get; set; }
        /// <summary>Fuentes usadas, para mostrarlas ("Epic, Steam").</summary>
        public List<string> Sources { get; } = new List<string>();

        /// <summary>Completa lo que falte con otra fuente (la primera que lo tenga gana).</summary>
        public void Merge(GameMetadataResult other, string sourceName)
        {
            if (other == null) return;
            bool used = false;
            string Pick(string mine, string theirs) { if (mine == null && theirs != null) { used = true; return theirs; } return mine; }
            Description = Pick(Description, other.Description);
            Genres = Pick(Genres, other.Genres);
            Developer = Pick(Developer, other.Developer);
            Publisher = Pick(Publisher, other.Publisher);
            if (Year == null && other.Year != null) { Year = other.Year; used = true; }
            CoverUrl = Pick(CoverUrl, other.CoverUrl);
            BackgroundUrl = Pick(BackgroundUrl, other.BackgroundUrl);
            LogoUrl = Pick(LogoUrl, other.LogoUrl);
            SteamAppId ??= other.SteamAppId;
            if (used && !Sources.Contains(sourceName)) Sources.Add(sourceName);
        }
    }

    /// <summary>Descarga una imagen de internet a un archivo temporal (o null).</summary>
    public static class MetadataHttp
    {
        public static readonly HttpClient Http = Create();

        private static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("PhantomPad");
            return c;
        }

        public static async Task<string> DownloadImageAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("steam:")) return await SteamMetadata.DownloadByPseudoUrlAsync(url);
            try
            {
                using HttpResponseMessage resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                string type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!type.StartsWith("image/")) return null;
                byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 1024) return null;
                string ext = type.Contains("png") ? ".png" : type.Contains("webp") ? ".webp" : ".jpg";
                if (ext == ".webp") return null; // WPF no muestra WebP sin un codec extra
                string temp = Path.Combine(Path.GetTempPath(), $"phantompad_{Guid.NewGuid():N}{ext}");
                await File.WriteAllBytesAsync(temp, bytes);
                return temp;
            }
            catch { return null; }
        }
    }

    // ===================== Epic: catalogo que el propio lanzador guarda en el PC =====================

    /// <summary>
    /// El lanzador de Epic guarda en %ProgramData%\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin
    /// los datos de los juegos de la cuenta (caratula, fondo, descripcion, estudio). No hace falta
    /// internet ni cuenta para leerlo; las imagenes se descargan de la web de Epic.
    /// </summary>
    public static class EpicCatalog
    {
        private static List<JsonElement> items;
        private static DateTime loadedAt;

        private static List<JsonElement> Items
        {
            get
            {
                if (items != null && (DateTime.Now - loadedAt).TotalMinutes < 10) return items;
                var list = new List<JsonElement>();
                try
                {
                    string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");
                    if (File.Exists(file))
                    {
                        string json = Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(file).Trim()));
                        using JsonDocument doc = JsonDocument.Parse(json);
                        foreach (JsonElement e in doc.RootElement.EnumerateArray()) list.Add(e.Clone());
                    }
                }
                catch { }
                items = list;
                loadedAt = DateTime.Now;
                return items;
            }
        }

        private static string Str(JsonElement e, string p) => e.TryGetProperty(p, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool IsGame(JsonElement e) =>
            e.TryGetProperty("categories", out JsonElement cats) && cats.ValueKind == JsonValueKind.Array &&
            cats.EnumerateArray().Any(c => Str(c, "path") == "games") &&
            !cats.EnumerateArray().Any(c => Str(c, "path") == "hidden");

        /// <summary>Busca por numero de catalogo (juegos de Epic) o por nombre (cualquier juego).</summary>
        public static GameMetadataResult Find(string catalogItemId, string name)
        {
            JsonElement? hit = null;
            if (!string.IsNullOrEmpty(catalogItemId))
                hit = Items.Cast<JsonElement?>().FirstOrDefault(e => Str(e.Value, "id") == catalogItemId);
            if (hit == null && !string.IsNullOrEmpty(name))
            {
                string target = SteamMetadata.Normalize(name);
                hit = Items.Where(IsGame).Cast<JsonElement?>().FirstOrDefault(e => SteamMetadata.Normalize(Str(e.Value, "title")) == target);
            }
            if (hit == null) return null;
            JsonElement it = hit.Value;

            string Image(params string[] types)
            {
                if (!it.TryGetProperty("keyImages", out JsonElement imgs) || imgs.ValueKind != JsonValueKind.Array) return null;
                foreach (string t in types)
                {
                    foreach (JsonElement img in imgs.EnumerateArray())
                        if (Str(img, "type") == t && Str(img, "url") is string u) return u;
                }
                return null;
            }

            string title = Str(it, "title");
            string desc = Str(it, "description");
            // A menudo la "descripcion" de Epic es solo el titulo: entonces no sirve
            if (desc != null && (desc.Trim().Length < 40 || string.Equals(desc.Trim(), title?.Trim(), StringComparison.OrdinalIgnoreCase))) desc = null;
            // El catalogo de Epic a veces guarda las tildes rotas ("está" → "est?"): mejor sin descripcion
            if (desc != null && System.Text.RegularExpressions.Regex.IsMatch(desc, @"\p{L}\?\p{L}|\p{L}\?[ \t]+\p{Ll}")) desc = null;

            return new GameMetadataResult
            {
                Description = desc?.Trim(),
                Developer = Str(it, "developer"),
                CoverUrl = Image("DieselGameBoxTall", "OfferImageTall", "Thumbnail"),
                BackgroundUrl = Image("DieselGameBox", "OfferImageWide", "DieselGameBoxWide"),
                LogoUrl = Image("DieselGameBoxLogo"),
            };
        }
    }

    // ===================== GOG: base de datos publica de juegos de GOG =====================

    public static class GogDb
    {
        public static async Task<GameMetadataResult> GetAsync(string gogId)
        {
            if (string.IsNullOrEmpty(gogId)) return null;
            string json = await MetadataHttp.Http.GetStringAsync($"https://gamesdb.gog.com/platforms/gog/external_releases/{Uri.EscapeDataString(gogId)}");
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("game", out JsonElement game)) return null;

            string Url(string prop) =>
                game.TryGetProperty(prop, out JsonElement img) && img.ValueKind == JsonValueKind.Object &&
                img.TryGetProperty("url_format", out JsonElement f) && f.GetString() is string fmt
                    ? fmt.Replace("{formatter}", "").Replace("{ext}", "jpg") : null;
            string Localized(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("*", out JsonElement s) ? s.GetString() : null;
            string Names(string prop, bool localized)
            {
                if (!game.TryGetProperty(prop, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array) return null;
                var parts = arr.EnumerateArray()
                    .Select(x => localized ? (x.TryGetProperty("name", out JsonElement n) ? Localized(n) : null)
                                           : (x.TryGetProperty("name", out JsonElement n2) ? n2.GetString() : null))
                    .Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                return parts.Count == 0 ? null : string.Join(", ", parts);
            }

            int? year = null;
            if (game.TryGetProperty("first_release_date", out JsonElement rd) && rd.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(rd.GetString(), out DateTime d)) year = d.Year;

            return new GameMetadataResult
            {
                Description = game.TryGetProperty("summary", out JsonElement sum) ? SteamMetadata.HtmlToText(Localized(sum)) : null,
                Genres = Names("genres", true),
                Developer = Names("developers", false),
                Publisher = Names("publishers", false),
                Year = year,
                CoverUrl = Url("vertical_cover"),
                BackgroundUrl = Url("background"),
                LogoUrl = Url("logo"),
            };
        }
    }

    // ===================== IGDB (opcional, con claves gratis de Twitch) =====================

    /// <summary>
    /// IGDB: la base de datos de juegos que usa Playnite. Hace falta una cuenta de desarrollador de
    /// Twitch (gratis) y sus dos claves (Client ID y Client Secret). El secreto se guarda cifrado
    /// con la proteccion de datos de Windows (solo tu usuario de Windows puede leerlo).
    /// </summary>
    public static class Igdb
    {
        private static string token;
        private static DateTime tokenExpires;

        public static bool Ready => PhantomLibrary.IgdbEnabled && !string.IsNullOrWhiteSpace(PhantomLibrary.IgdbClientId) && !string.IsNullOrWhiteSpace(PhantomLibrary.IgdbSecret);

        public static void ForgetToken() => token = null;

        private static async Task<string> TokenAsync(string clientId, string secret)
        {
            if (token != null && DateTime.UtcNow < tokenExpires) return token;
            string url = $"https://id.twitch.tv/oauth2/token?client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}&grant_type=client_credentials";
            using HttpResponseMessage resp = await MetadataHttp.Http.PostAsync(url, null);
            string body = await resp.Content.ReadAsStringAsync();
            if ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                throw new Exception(DS4Windows.PPText.T("PP_SrcBadKeys", "the Client ID or the Client Secret is not correct"));
            if (!resp.IsSuccessStatusCode) throw new Exception($"Twitch: {(int)resp.StatusCode}");
            using JsonDocument doc = JsonDocument.Parse(body);
            token = doc.RootElement.GetProperty("access_token").GetString();
            tokenExpires = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 120);
            return token;
        }

        /// <summary>Comprueba unas claves (sin guardarlas). Devuelve null si funcionan, o el error.</summary>
        public static async Task<string> TestAsync(string clientId, string secret)
        {
            try
            {
                ForgetToken();
                await QueryAsync(clientId, secret, "fields name; limit 1;");
                return null;
            }
            catch (Exception e) { ForgetToken(); return e.Message; }
        }

        private static async Task<JsonDocument> QueryAsync(string clientId, string secret, string body)
        {
            string t = await TokenAsync(clientId, secret);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games") { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
            req.Headers.Add("Client-ID", clientId);
            req.Headers.Add("Authorization", "Bearer " + t);
            using HttpResponseMessage resp = await MetadataHttp.Http.SendAsync(req);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) throw new Exception($"IGDB: {(int)resp.StatusCode}");
            return JsonDocument.Parse(json);
        }

        public static async Task<GameMetadataResult> SearchAsync(string name)
        {
            if (!Ready || string.IsNullOrWhiteSpace(name)) return null;
            string q = name.Replace("\"", "").Replace("™", "").Replace("®", "").Trim();
            using JsonDocument doc = await QueryAsync(PhantomLibrary.IgdbClientId, PhantomLibrary.IgdbSecret,
                $"search \"{q}\"; fields name,summary,first_release_date,genres.name,involved_companies.company.name," +
                "involved_companies.developer,involved_companies.publisher,cover.image_id,artworks.image_id,screenshots.image_id; limit 10;");

            var games = doc.RootElement.EnumerateArray().ToList();
            if (games.Count == 0) return null;
            string target = SteamMetadata.Normalize(name);
            JsonElement g = games.FirstOrDefault(x => x.TryGetProperty("name", out JsonElement n) && SteamMetadata.Normalize(n.GetString()) == target);
            if (g.ValueKind == JsonValueKind.Undefined)
            {
                // Sin nombre exacto: el primero solo si se parece mucho
                JsonElement first = games[0];
                string n = SteamMetadata.Normalize(first.TryGetProperty("name", out JsonElement nn) ? nn.GetString() : "");
                if (n.Length == 0 || !(n.StartsWith(target) || target.StartsWith(n)) || Math.Abs(n.Length - target.Length) > 12) return null;
                g = first;
            }

            string ImageId(string prop)
            {
                if (!g.TryGetProperty(prop, out JsonElement v)) return null;
                if (v.ValueKind == JsonValueKind.Object) return v.TryGetProperty("image_id", out JsonElement i) ? i.GetString() : null;
                if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(x => x.TryGetProperty("image_id", out JsonElement i) ? i.GetString() : null).FirstOrDefault(x => x != null);
                return null;
            }
            string Companies(string role)
            {
                if (!g.TryGetProperty("involved_companies", out JsonElement arr)) return null;
                var parts = arr.EnumerateArray()
                    .Where(c => c.TryGetProperty(role, out JsonElement r) && r.ValueKind == JsonValueKind.True)
                    .Select(c => c.TryGetProperty("company", out JsonElement co) && co.TryGetProperty("name", out JsonElement cn) ? cn.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
                return parts.Count == 0 ? null : string.Join(", ", parts);
            }

            string cover = ImageId("cover");
            string art = ImageId("artworks") ?? ImageId("screenshots");
            int? year = g.TryGetProperty("first_release_date", out JsonElement frd) && frd.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(frd.GetInt64()).Year : null;

            return new GameMetadataResult
            {
                Description = g.TryGetProperty("summary", out JsonElement s) ? s.GetString() : null,
                Genres = g.TryGetProperty("genres", out JsonElement gs) ? string.Join(", ", gs.EnumerateArray().Select(x => x.TryGetProperty("name", out JsonElement gn) ? gn.GetString() : null).Where(x => x != null)) : null,
                Developer = Companies("developer"),
                Publisher = Companies("publisher"),
                Year = year,
                CoverUrl = cover == null ? null : $"https://images.igdb.com/igdb/image/upload/t_cover_big_2x/{cover}.jpg",
                BackgroundUrl = art == null ? null : $"https://images.igdb.com/igdb/image/upload/t_1080p/{art}.jpg",
            };
        }
    }

    // ===================== Guardar el secreto cifrado (proteccion de datos de Windows) =====================

    public static class SecretStore
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] Run(byte[] data, bool protect)
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
            var output = new DATA_BLOB();
            try
            {
                Marshal.Copy(data, 0, input.pbData, data.Length);
                bool ok = protect
                    ? CryptProtectData(ref input, "PhantomPad", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1 /* sin ventanas */, ref output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref output);
                if (!ok) return null;
                byte[] result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(input.pbData);
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
            }
        }

        public static string Protect(string plain) =>
            string.IsNullOrEmpty(plain) ? null : Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), true) ?? Array.Empty<byte>());

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try
            {
                byte[] b = Run(Convert.FromBase64String(stored), false);
                return b == null ? null : Encoding.UTF8.GetString(b);
            }
            catch { return null; }
        }
    }
}
