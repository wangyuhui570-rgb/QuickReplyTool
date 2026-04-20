using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class UsageReportExcelService
{
    public void ExportToXls(string filePath, UsageDashboardStats dashboardStats, IReadOnlyList<PhraseUsageReportItem> items)
    {
        var workbook = new HSSFWorkbook();

        BuildSummarySheet(workbook, dashboardStats);
        BuildDetailSheet(workbook, items);

        using var stream = File.Create(filePath);
        workbook.Write(stream, leaveOpen: false);
    }

    private static void BuildSummarySheet(HSSFWorkbook workbook, UsageDashboardStats dashboardStats)
    {
        var sheet = workbook.CreateSheet("使用概览");
        var rowIndex = 0;

        WriteKeyValueRow(sheet, ref rowIndex, "累计启动次数", dashboardStats.LaunchCount.ToString());
        WriteKeyValueRow(sheet, ref rowIndex, "累计运行时长", FormatDuration(dashboardStats.TotalRuntimeSeconds));
        rowIndex++;

        sheet.CreateRow(rowIndex++).CreateCell(0).SetCellValue("最高频短语");
        WriteRankingHeader(sheet.CreateRow(rowIndex++));
        foreach (var item in dashboardStats.MostUsedPhrases.Select((value, index) => new { value, index }))
        {
            WriteUsageRow(sheet.CreateRow(rowIndex++), item.index + 1, item.value);
        }

        rowIndex++;
        sheet.CreateRow(rowIndex++).CreateCell(0).SetCellValue("低频 / 久未用");
        WriteRankingHeader(sheet.CreateRow(rowIndex++));
        foreach (var item in dashboardStats.LeastUsedPhrases.Select((value, index) => new { value, index }))
        {
            WriteUsageRow(sheet.CreateRow(rowIndex++), item.index + 1, item.value);
        }

        SetSummaryColumns(sheet);
    }

    private static void BuildDetailSheet(HSSFWorkbook workbook, IReadOnlyList<PhraseUsageReportItem> items)
    {
        var sheet = workbook.CreateSheet("短语统计明细");
        var header = sheet.CreateRow(0);
        header.CreateCell(0).SetCellValue("排名");
        header.CreateCell(1).SetCellValue("分类");
        header.CreateCell(2).SetCellValue("备注标签");
        header.CreateCell(3).SetCellValue("短语内容");
        header.CreateCell(4).SetCellValue("发送次数");
        header.CreateCell(5).SetCellValue("最近发送时间");

        var rowIndex = 1;
        foreach (var item in items)
        {
            var row = sheet.CreateRow(rowIndex++);
            row.CreateCell(0).SetCellValue(item.Rank);
            row.CreateCell(1).SetCellValue(item.CategoryName);
            row.CreateCell(2).SetCellValue(item.Title);
            row.CreateCell(3).SetCellValue(item.ContentPreview);
            row.CreateCell(4).SetCellValue(item.SendCount);
            row.CreateCell(5).SetCellValue(item.LastSentAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "从未发送");
        }

        sheet.SetColumnWidth(0, 10 * 256);
        sheet.SetColumnWidth(1, 18 * 256);
        sheet.SetColumnWidth(2, 24 * 256);
        sheet.SetColumnWidth(3, 72 * 256);
        sheet.SetColumnWidth(4, 14 * 256);
        sheet.SetColumnWidth(5, 24 * 256);
    }

    private static void WriteKeyValueRow(ISheet sheet, ref int rowIndex, string key, string value)
    {
        var row = sheet.CreateRow(rowIndex++);
        row.CreateCell(0).SetCellValue(key);
        row.CreateCell(1).SetCellValue(value);
    }

    private static void WriteRankingHeader(IRow row)
    {
        row.CreateCell(0).SetCellValue("排名");
        row.CreateCell(1).SetCellValue("分类");
        row.CreateCell(2).SetCellValue("备注标签");
        row.CreateCell(3).SetCellValue("短语内容");
        row.CreateCell(4).SetCellValue("发送次数");
        row.CreateCell(5).SetCellValue("最近发送时间");
    }

    private static void WriteUsageRow(IRow row, int rank, PhraseUsageDashboardItem item)
    {
        row.CreateCell(0).SetCellValue(rank);
        row.CreateCell(1).SetCellValue(item.CategoryName);
        row.CreateCell(2).SetCellValue(item.Title);
        row.CreateCell(3).SetCellValue(item.ContentPreview);
        row.CreateCell(4).SetCellValue(item.SendCount);
        row.CreateCell(5).SetCellValue(item.LastSentAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "从未发送");
    }

    private static void SetSummaryColumns(ISheet sheet)
    {
        sheet.SetColumnWidth(0, 14 * 256);
        sheet.SetColumnWidth(1, 16 * 256);
        sheet.SetColumnWidth(2, 18 * 256);
        sheet.SetColumnWidth(3, 60 * 256);
        sheet.SetColumnWidth(4, 14 * 256);
        sheet.SetColumnWidth(5, 24 * 256);
    }

    private static string FormatDuration(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0 分钟";
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes} 分钟";
        }

        return $"{Math.Max(1, (int)span.TotalSeconds)} 秒";
    }
}
