using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;

namespace CDriveMaster.UI.ViewModels;

public partial class HelpManualViewModel : ObservableObject
{
    internal const string ManualResourceName = "CDriveMaster.UI.Resources.user-manual.md";

    public string Title => "帮助文档";

    public string Subtitle => "内置使用说明，适合首次上手和发布版用户自助排障。";

    [ObservableProperty]
    private string manualText = LoadManualText(typeof(HelpManualViewModel).Assembly);

    internal static string LoadManualText(Assembly assembly)
    {
        using Stream? stream = assembly.GetManifestResourceStream(ManualResourceName);
        if (stream is null)
        {
            return "未能加载内置帮助文档。请重新安装程序或联系开发者。";
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
