/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: actualizaciones desde GitHub Releases (Configuraciones → Actualizaciones).
    /// 1. Al abrir la app o entrar en la seccion se consulta la ultima version publicada.
    /// 2. Si es mas nueva se muestra: imagen, novedades y fecha.
    /// 3. "Descargar" la baja en segundo plano (no interrumpe el juego) y NO se instala sola.
    /// 4. "Reiniciar app" cierra PhantomPad, aplica la actualizacion y lo vuelve a abrir.
    /// La descarga y la instalacion las hace Velopack (solo con PhantomPad instalado con su instalador).
    /// </summary>
    public static class PhantomUpdater
    {
        /// <summary>Cuenta y repositorio de GitHub donde se publican las versiones (se rellena al crearlo).</summary>
        public const string REPO_OWNER = "PhantomPad";
        public const string REPO_NAME = "PhantomPad";

        public enum UpdateState { NotConfigured, Idle, Checking, UpToDate, Available, Downloading, ReadyToRestart, Error }

        public class ReleaseInfo
        {
            public string Version;
            public string Title;
            public DateTime? Date;
            public string Notes;
            public string ImageUrl;
            public string PageUrl;
        }

        public static bool IsConfigured => !string.IsNullOrEmpty(REPO_OWNER);
        public static string RepoUrl => $"https://github.com/{REPO_OWNER}/{REPO_NAME}";

        public static UpdateState State { get; private set; } = IsConfigured ? UpdateState.Idle : UpdateState.NotConfigured;
        public static ReleaseInfo Latest { get; private set; }
        public static int DownloadPercent { get; private set; }
        public static string ErrorText { get; private set; }

        /// <summary>Hora de la ultima comprobacion terminada (para mostrarla).</summary>
        public static DateTime? LastChecked { get; private set; }

        /// <summary>Avisa a la interfaz de cualquier cambio (puede llegar desde otro hilo).</summary>
        public static event Action Changed;

        private static UpdateManager manager;
        private static UpdateInfo pending;
        private static DateTime lastCheck = DateTime.MinValue;

        private static UpdateManager Manager
        {
            get
            {
                if (manager == null && IsConfigured)
                {
                    try { manager = new UpdateManager(new GithubSource(RepoUrl, null, false)); }
                    catch { manager = null; }
                }

                return manager;
            }
        }

        /// <summary>true si PhantomPad se instalo con su instalador (solo asi se puede actualizar solo).</summary>
        public static bool CanSelfUpdate => Manager?.IsInstalled == true;

        public static string CurrentVersion
        {
            get
            {
                try { if (Manager?.IsInstalled == true) return Manager.CurrentVersion.ToString(); } catch { }
                return Global.exeversion;
            }
        }

        private static void Set(UpdateState state)
        {
            State = state;
            Changed?.Invoke();
        }

        /// <summary>Consulta GitHub. Con force=false no repite si se miro hace menos de 10 minutos.</summary>
        public static async Task CheckAsync(bool force = false)
        {
            if (!IsConfigured) { Set(UpdateState.NotConfigured); return; }
            if (State == UpdateState.Checking || State == UpdateState.Downloading || State == UpdateState.ReadyToRestart) return;
            if (!force && (DateTime.UtcNow - lastCheck).TotalMinutes < 10 && State != UpdateState.Idle && State != UpdateState.Error) return;

            lastCheck = DateTime.UtcNow;
            Set(UpdateState.Checking);
            try
            {
                // Un momento visible de "Buscando...", asi se nota que el boton hizo algo
                Task<ReleaseInfo> fetch = FetchLatestAsync();
                await Task.WhenAll(fetch, Task.Delay(800));
                ReleaseInfo info = fetch.Result;
                LastChecked = DateTime.Now;
                Latest = info;
                if (info == null || !IsNewer(info.Version, CurrentVersion))
                {
                    Set(UpdateState.UpToDate);
                    return;
                }

                if (CanSelfUpdate)
                {
                    pending = await Manager.CheckForUpdatesAsync();
                }

                Set(UpdateState.Available);
            }
            catch (Exception e)
            {
                ErrorText = e.Message;
                Set(UpdateState.Error);
            }
        }

        /// <summary>Descarga la actualizacion en segundo plano. No la instala.</summary>
        public static async Task DownloadAsync()
        {
            if (State != UpdateState.Available || !CanSelfUpdate || pending == null) return;
            DownloadPercent = 0;
            Set(UpdateState.Downloading);
            try
            {
                await Task.Run(() => Manager.DownloadUpdatesAsync(pending, p =>
                {
                    DownloadPercent = p;
                    Changed?.Invoke();
                }));
                Set(UpdateState.ReadyToRestart);
                AppLogger.LogToTray(PPText.T("PPC_UpdateReady", "The update is downloaded. Press \"Restart app\" when you want to install it."), false, true);
            }
            catch (Exception e)
            {
                ErrorText = e.Message;
                Set(UpdateState.Error);
            }
        }

        /// <summary>Guarda todo, cierra PhantomPad, aplica la actualizacion y lo vuelve a abrir.</summary>
        public static void RestartAndApply()
        {
            if (State != UpdateState.ReadyToRestart || pending == null) return;
            try { Global.Save(); } catch { }
            Manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
        }

        // ===================== GitHub =====================

        private static async Task<ReleaseInfo> FetchLatestAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PhantomPad-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using HttpResponseMessage resp = await client.GetAsync($"https://api.github.com/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // aun no hay ninguna version publicada
            resp.EnsureSuccessStatusCode();

            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            JsonElement root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            string body = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";
            DateTime? date = root.TryGetProperty("published_at", out JsonElement d) && d.ValueKind == JsonValueKind.String
                ? d.GetDateTime().ToLocalTime() : null;

            return new ReleaseInfo
            {
                Version = (tag ?? "").TrimStart('v', 'V'),
                Title = root.TryGetProperty("name", out JsonElement n) && !string.IsNullOrWhiteSpace(n.GetString()) ? n.GetString() : $"PhantomPad {tag}",
                Date = date,
                ImageUrl = FirstImage(body),
                Notes = MarkdownToText(body),
                PageUrl = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() : RepoUrl + "/releases",
            };
        }

        private static bool IsNewer(string latest, string current)
        {
            return Version.TryParse(Normalize(latest), out Version l) && Version.TryParse(Normalize(current), out Version c) && l > c;

            static string Normalize(string v)
            {
                v = (v ?? "").Split('-', '+')[0].Trim();
                return v.Split('.').Length == 2 ? v + ".0" : v;
            }
        }

        /// <summary>Primera imagen de las novedades: ![texto](url) o &lt;img src="url"&gt;.</summary>
        private static string FirstImage(string markdown)
        {
            Match m = Regex.Match(markdown, @"!\[[^\]]*\]\((?<u>https?://[^)\s]+)");
            if (m.Success) return m.Groups["u"].Value;
            m = Regex.Match(markdown, @"<img[^>]+src=""(?<u>https?://[^""]+)""", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups["u"].Value : null;
        }

        /// <summary>Novedades como texto simple (sin imagenes ni simbolos de formato).</summary>
        private static string MarkdownToText(string markdown)
        {
            string text = Regex.Replace(markdown, @"!\[[^\]]*\]\([^)]*\)", "");
            text = Regex.Replace(text, @"<[^>]+>", ""); // quitar etiquetas web (img, details, summary...)
            text = Regex.Replace(text, @"\[(?<t>[^\]]+)\]\([^)]*\)", "${t}");
            text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
            text = Regex.Replace(text, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
            text = text.Replace("**", "").Replace("__", "").Replace("`", "");
            return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        }
    }
}
