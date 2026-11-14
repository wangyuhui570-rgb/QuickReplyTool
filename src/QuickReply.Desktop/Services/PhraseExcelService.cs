using System.IO;
using System.Text;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using QuickReply.Desktop.Models;

namespace QuickReply.Desktop.Services;

public sealed class PhraseExcelService
{
    private const string DefaultCategoryName = "未分类";

    public void Export(string filePath, IEnumerable<ReplyCategory> categories, Func<ReplyPhrase, IReadOnlyList<string>>? resolveImagePaths = null)
    {
        var extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            ExportToCsv(filePath, categories, resolveImagePaths);
            return;
        }

        ExportToXls(filePath, categories, resolveImagePaths);
    }

    public IReadOnlyList<PhraseImportRow> Import(string filePath)
    {
        var extension = Path.GetExtension(filePath);
        return string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
            ? ImportFromCsv(filePath)
            : ImportFromXls(filePath);
    }

    public void ExportToXls(
        string filePath,
        IEnumerable<ReplyCategory> categories,
        Func<ReplyPhrase, IReadOnlyList<string>>? resolveImagePaths = null)
    {
        using var export = new PhraseExport(filePath);
        using var workbook = new HSSFWorkbook();
        var sheet = workbook.CreateSheet("快捷短语");

        var headerRow = sheet.CreateRow(0);
        headerRow.CreateCell(0).SetCellValue("分类");
        headerRow.CreateCell(1).SetCellValue("备注标签");
        headerRow.CreateCell(2).SetCellValue("短语内容");
        headerRow.CreateCell(3).SetCellValue("图片1");
        headerRow.CreateCell(4).SetCellValue("图片2");
        headerRow.CreateCell(5).SetCellValue("图片3");
        headerRow.CreateCell(6).SetCellValue("检索标签");

        var rowIndex = 1;
        foreach (var category in categories.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt))
        {
            foreach (var phrase in category.Phrases.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt))
            {
                var row = sheet.CreateRow(rowIndex);
                row.CreateCell(0).SetCellValue(string.IsNullOrWhiteSpace(category.Name) ? DefaultCategoryName : category.Name);
                row.CreateCell(1).SetCellValue(NormalizeRemark(phrase.Title));
                row.CreateCell(2).SetCellValue(phrase.Content ?? string.Empty);
                row.CreateCell(6).SetCellValue(phrase.Tags);

                var images = export.CopyImages(phrase, rowIndex, resolveImagePaths);
                for (var imageIndex = 0; imageIndex < 3; imageIndex++)
                {
                    row.CreateCell(3 + imageIndex).SetCellValue(imageIndex < images.Count ? images[imageIndex] : string.Empty);
                }

                rowIndex++;
            }
        }

        sheet.SetColumnWidth(0, 18 * 256);
        sheet.SetColumnWidth(1, 24 * 256);
        sheet.SetColumnWidth(2, 72 * 256);
        sheet.SetColumnWidth(3, 28 * 256);
        sheet.SetColumnWidth(4, 28 * 256);
        sheet.SetColumnWidth(5, 28 * 256);
        sheet.SetColumnWidth(6, 24 * 256);

        using (var stream = new FileStream(export.TemporaryPath, FileMode.CreateNew, FileAccess.Write))
        {
            workbook.Write(stream, leaveOpen: true);
            stream.Flush(flushToDisk: true);
        }
        export.Commit();
    }

    public IReadOnlyList<PhraseImportRow> ImportFromXls(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using IWorkbook workbook = new HSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);
        var formatter = new DataFormatter();
        var header = sheet.GetRow(0);
        var tagsIndex = -1;
        for (var index = 0; index < (header?.LastCellNum ?? 0); index++)
        {
            if (formatter.FormatCellValue(header!.GetCell(index)).Trim() == "检索标签")
            {
                tagsIndex = index;
                break;
            }
        }
        var rows = new List<PhraseImportRow>();
        var baseDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;

        for (var rowIndex = 1; rowIndex <= sheet.LastRowNum; rowIndex++)
        {
            var row = sheet.GetRow(rowIndex);
            if (row is null)
            {
                continue;
            }

            var categoryName = formatter.FormatCellValue(row.GetCell(0)).Trim();
            var remark = formatter.FormatCellValue(row.GetCell(1)).Trim();
            var content = formatter.FormatCellValue(row.GetCell(2)).Trim();
            var imagePaths = new List<string>();

            for (var imageColumnIndex = 3; imageColumnIndex <= 5; imageColumnIndex++)
            {
                var imagePathValue = formatter.FormatCellValue(row.GetCell(imageColumnIndex)).Trim();
                var resolvedImagePath = ResolveImportImagePath(baseDirectory, imagePathValue);
                if (!string.IsNullOrWhiteSpace(resolvedImagePath))
                {
                    imagePaths.Add(resolvedImagePath);
                }
            }

            if (string.IsNullOrWhiteSpace(content) && imagePaths.Count == 0)
            {
                continue;
            }

            rows.Add(new PhraseImportRow(
                string.IsNullOrWhiteSpace(categoryName) ? DefaultCategoryName : categoryName,
                remark,
                content,
                imagePaths,
                tagsIndex < 0 ? null : formatter.FormatCellValue(row.GetCell(tagsIndex)).Trim()));
        }

        return rows;
    }

    public void ExportToCsv(
        string filePath,
        IEnumerable<ReplyCategory> categories,
        Func<ReplyPhrase, IReadOnlyList<string>>? resolveImagePaths = null)
    {
        using var export = new PhraseExport(filePath);

        var rows = new List<string[]>
        {
            new[] { "快捷短语编码", "快捷短语", "快捷短语分类", "快捷短语图片", "检索标签" }
        };

        var rowIndex = 1;
        foreach (var category in categories.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt))
        {
            foreach (var phrase in category.Phrases.OrderBy(item => item.SortOrder).ThenBy(item => item.CreatedAt))
            {
                var exportedImageValues = export.CopyImages(phrase, rowIndex, resolveImagePaths);

                rows.Add(
                    new[]
                    {
                        NormalizeRemark(phrase.Title),
                        phrase.Content ?? string.Empty,
                        string.IsNullOrWhiteSpace(category.Name) ? DefaultCategoryName : category.Name,
                        string.Join("|", exportedImageValues),
                        phrase.Tags
                    });
                rowIndex++;
            }
        }

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(EscapeCsvValue)));
        }

        using (var stream = new FileStream(export.TemporaryPath, FileMode.CreateNew, FileAccess.Write))
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
            writer.Write(builder.ToString());
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        export.Commit();
    }

    public IReadOnlyList<PhraseImportRow> ImportFromCsv(string filePath)
    {
        var content = File.ReadAllText(filePath, DetectEncoding(filePath));
        var csvRows = ParseCsv(content);
        if (csvRows.Count == 0)
        {
            return [];
        }

        var header = csvRows[0];
        var headerLookup = header
            .Select((value, index) => new { Key = NormalizeHeader(value), Index = index })
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);

        var remarkIndex = FindHeaderIndex(headerLookup, "备注标签", "快捷短语编码", "标题");
        var tagsIndex = FindHeaderIndex(headerLookup, "检索标签");
        var contentIndex = FindHeaderIndex(headerLookup, "短语内容", "快捷短语", "内容");
        var categoryIndex = FindHeaderIndex(headerLookup, "分类", "快捷短语分类", "快捷短语分组");
        var imageIndexes = header
            .Select((value, index) => new { Key = NormalizeHeader(value), Index = index })
            .Where(item => item.Key is "图片1" or "图片2" or "图片3" or "快捷短语图片" or "图片")
            .Select(item => item.Index)
            .Distinct()
            .ToList();

        var baseDirectory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var rows = new List<PhraseImportRow>();

        foreach (var row in csvRows.Skip(1))
        {
            var remark = GetCell(row, remarkIndex).Trim();
            var body = GetCell(row, contentIndex).Trim();
            var category = GetCell(row, categoryIndex).Trim();
            var imagePaths = new List<string>();

            foreach (var imageIndex in imageIndexes)
            {
                var imageValue = GetCell(row, imageIndex).Trim();
                if (string.IsNullOrWhiteSpace(imageValue))
                {
                    continue;
                }

                foreach (var token in imageValue.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var resolved = ResolveImportImagePath(baseDirectory, token);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        imagePaths.Add(resolved);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(body) && imagePaths.Count == 0)
            {
                continue;
            }

            rows.Add(new PhraseImportRow(
                string.IsNullOrWhiteSpace(category) ? DefaultCategoryName : category,
                remark,
                body,
                imagePaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray(),
                tagsIndex < 0 ? null : GetCell(row, tagsIndex).Trim()));
        }

        return rows;
    }

    // The published workbook is the commit point. Older asset directories remain usable by
    // older exports, and a failed export owns only its new directory and temporary workbook.
    private sealed class PhraseExport : IDisposable
    {
        private readonly string _targetPath;
        private readonly string _assetsFolderName;
        private readonly string _assetsPath;
        private bool _createdAssets;
        private bool _committed;

        public PhraseExport(string filePath)
        {
            _targetPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(_targetPath)!;
            var operationId = Guid.NewGuid().ToString("N");
            TemporaryPath = Path.Combine(directory, $".quickreply-{operationId}.tmp");
            _assetsFolderName = $"{Path.GetFileNameWithoutExtension(filePath)}_assets_{operationId}";
            _assetsPath = Path.Combine(directory, _assetsFolderName);
        }

        public string TemporaryPath { get; }

        public IReadOnlyList<string> CopyImages(
            ReplyPhrase phrase,
            int rowIndex,
            Func<ReplyPhrase, IReadOnlyList<string>>? resolveImagePaths)
        {
            var paths = resolveImagePaths?.Invoke(phrase) ?? [];
            if (paths.Count != phrase.ImageCount)
            {
                throw new InvalidDataException($"短语“{phrase.Title}”的图片不完整，请补全后再导出。");
            }

            var exported = new List<string>();
            for (var index = 0; index < paths.Count; index++)
            {
                var source = paths[index];
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    throw new FileNotFoundException($"短语“{phrase.Title}”的图片不存在：{source}", source);
                }

                Directory.CreateDirectory(_assetsPath);
                _createdAssets = true;
                var name = $"phrase-{rowIndex:D4}-{index + 1}{Path.GetExtension(source)}";
                File.Copy(source, Path.Combine(_assetsPath, name), overwrite: false);
                exported.Add(Path.Combine(_assetsFolderName, name));
            }
            return exported;
        }

        public void Commit()
        {
            File.Move(TemporaryPath, _targetPath, overwrite: true);
            _committed = true;
        }

        public void Dispose()
        {
            if (_committed)
            {
                return;
            }

            File.Delete(TemporaryPath);
            if (_createdAssets)
            {
                Directory.Delete(_assetsPath, recursive: true);
            }
        }
    }

    private static string NormalizeRemark(string? title)
    {
        var normalized = title?.Trim() ?? string.Empty;
        if (string.Equals(normalized, "未命名短语", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "未命名", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static string ResolveImportImagePath(string baseDirectory, string imagePathValue)
    {
        if (string.IsNullOrWhiteSpace(imagePathValue))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(imagePathValue))
        {
            return File.Exists(imagePathValue) ? imagePathValue : string.Empty;
        }

        var combinedPath = Path.Combine(baseDirectory, imagePathValue);
        return File.Exists(combinedPath) ? combinedPath : string.Empty;
    }

    private static string EscapeCsvValue(string value)
    {
        var normalized = value ?? string.Empty;
        return normalized.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{normalized.Replace("\"", "\"\"")}\""
            : normalized;
    }

    private static IReadOnlyList<string[]> ParseCsv(string content)
    {
        var rows = new List<string[]>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < content.Length; index++)
        {
            var ch = content[index];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        currentField.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    currentField.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    break;
                case '\r':
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow.ToArray());
                    currentRow.Clear();
                    if (index + 1 < content.Length && content[index + 1] == '\n')
                    {
                        index++;
                    }
                    break;
                case '\n':
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    rows.Add(currentRow.ToArray());
                    currentRow.Clear();
                    break;
                default:
                    currentField.Append(ch);
                    break;
            }
        }

        if (currentField.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(currentField.ToString());
            rows.Add(currentRow.ToArray());
        }

        return rows;
    }

    private static Encoding DetectEncoding(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        Span<byte> bom = stackalloc byte[4];
        var read = stream.Read(bom);

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        try
        {
            return Encoding.GetEncoding("GB18030");
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static string NormalizeHeader(string? header)
    {
        return (header ?? string.Empty).Trim();
    }

    private static int FindHeaderIndex(IReadOnlyDictionary<string, int> headerLookup, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headerLookup.TryGetValue(candidate, out var index))
            {
                return index;
            }
        }

        return -1;
    }

    private static string GetCell(IReadOnlyList<string> row, int index)
    {
        return index >= 0 && index < row.Count ? row[index] : string.Empty;
    }
}

public readonly record struct PhraseImportRow(string CategoryName, string Remark, string Content, IReadOnlyList<string> ImagePaths, string? Tags = null);
