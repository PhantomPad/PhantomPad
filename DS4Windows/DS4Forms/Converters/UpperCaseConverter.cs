/*
PhantomPad (based on DS4Windows)
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
*/

using System;
using System.Globalization;
using System.Windows.Data;

namespace DS4WinWPF.DS4Forms.Converters
{
    /// <summary>
    /// PhantomPad: muestra un texto en MAYUSCULAS (etiquetas de la barra de iconos y titulos de seccion).
    /// </summary>
    public class UpperCaseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString()?.ToUpper(CultureInfo.CurrentUICulture) ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Binding.DoNothing;
        }
    }
}
