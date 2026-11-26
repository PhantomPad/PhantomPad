/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System.Globalization;

namespace DS4Windows
{
    /// <summary>
    /// PhantomPad: textos traducibles usados desde el codigo (registro, avisos, notificaciones).
    /// Busca la clave en Translations\Strings(.XX).resx segun el idioma actual; si no existe,
    /// usa el texto en ingles que se pasa como respaldo.
    /// </summary>
    public static class PPText
    {
        /// <summary>
        /// Traduce un nombre corto para mostrar (botones, ejes, opciones). La clave se deriva del
        /// texto en ingles: "Left X-Axis-" -> PPN_LeftXAxisMinus. Si no hay traduccion, devuelve el ingles.
        /// IMPORTANTE: usar solo al mostrar en pantalla, nunca en datos que se guardan en perfiles.
        /// </summary>
        public static string Name(string english)
        {
            if (string.IsNullOrEmpty(english))
            {
                return english;
            }

            return T(NameKey(english), english);
        }

        public static string NameKey(string english)
        {
            var sb = new System.Text.StringBuilder("PPN_");
            bool upper = true;
            for (int i = 0; i < english.Length; i++)
            {
                char c = english[i];
                bool last = i == english.Length - 1;
                if (c == '+') { sb.Append("Plus"); upper = true; }
                else if (c == '-' && last) { sb.Append("Minus"); upper = true; }
                else if (char.IsLetterOrDigit(c)) { sb.Append(upper ? char.ToUpperInvariant(c) : c); upper = false; }
                else { upper = true; }
            }

            return sb.ToString();
        }

        public static string T(string key, string englishFallback, params object[] args)
        {
            string format = null;
            try
            {
                format = DS4WinWPF.Translations.Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);
            }
            catch
            {
                // Si los recursos no estan disponibles, usar el texto en ingles
            }

            format ??= englishFallback;
            if (args == null || args.Length == 0)
            {
                return format;
            }

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (System.FormatException)
            {
                // Traduccion con marcadores incorrectos: volver al ingles
                return string.Format(CultureInfo.CurrentCulture, englishFallback, args);
            }
        }
    }
}
