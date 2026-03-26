using System;
using CDriveMaster.Core.Models;

namespace CDriveMaster.Core.Services;

public static class RecommendationEngine
{
    public static CleanupRecommendation GenerateRecommendation(
        string fullPath,
        RiskLevel riskLevel,
        BasicScanActionType actionType)
    {
        string path = fullPath ?? string.Empty;

        if (ContainsAny(path, "WeChat", "Tencent", "Chrome"))
        {
            return new CleanupRecommendation(
                Title: "应用运行时缓存",
                Reason: "应用即使安装在其他盘，运行时仍会在 C 盘产生大量缓存和日志。",
                ActionText: "建议前往【应用清理】模块深度处理");
        }

        if (ContainsAny(path, "Temp", "Cache", "CrashDumps") && riskLevel == RiskLevel.SafeAuto)
        {
            return new CleanupRecommendation(
                Title: "系统临时/缓存文件",
                Reason: "系统或软件运行产生的临时冗余，通常可安全清理。",
                ActionText: "可一键安全清理");
        }

        if (ContainsAny(path, "Downloads", "Desktop", "Documents") && actionType == BasicScanActionType.OpenFolder)
        {
            return new CleanupRecommendation(
                Title: "用户个人文件",
                Reason: "可能包含重要的安装包、视频或文档，不建议自动删除。",
                ActionText: "建议手动打开目录确认");
        }

        if (ContainsAny(path, "hiberfil.sys", "pagefile.sys", "WinSxS"))
        {
            return new CleanupRecommendation(
                Title: "系统核心/保护文件",
                Reason: "涉及系统休眠、虚拟内存或组件更新，强制删除会导致系统崩溃。",
                ActionText: "不可直接删除，建议使用【系统维护】处理");
        }

        return new CleanupRecommendation(
            Title: "未知大文件",
            Reason: "来源不明的占用",
            ActionText: "建议手动甄别");
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
