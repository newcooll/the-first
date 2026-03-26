namespace CDriveMaster.Core.Utilities;

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (bytes >= gb)
        {
            return $"{bytes / gb:F2} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:F2} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:F2} KB";
        }

        return $"{bytes:F2} B";
    }
}
