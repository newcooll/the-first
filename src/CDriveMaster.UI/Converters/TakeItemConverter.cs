using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace CDriveMaster.UI.Converters;

public sealed class TakeItemConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IEnumerable source)
        {
            return Array.Empty<object>();
        }

        int takeCount = 5;
        if (parameter is not null && int.TryParse(parameter.ToString(), out int parsed) && parsed > 0)
        {
            takeCount = parsed;
        }

        return source.Cast<object>().Take(takeCount).ToList();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
