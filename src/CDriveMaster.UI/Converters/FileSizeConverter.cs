using System;
using System.Globalization;
using System.Windows.Data;

namespace CDriveMaster.UI.Converters;

public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "0 B";

        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;

        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }

        return $"{number:n2} {suffixes[counter]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
