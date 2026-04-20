$ErrorActionPreference = "Stop"

$root = Join-Path $env:APPDATA "QuickReplyTool"
$dbPath = Join-Path $root "quick-reply.db"
$imagesRoot = Join-Path $root "phrase-images"
$logDate = Get-Date -Format "yyyyMMdd"
$logPath = Join-Path $root "logs\search-overlay\$logDate.jsonl"
$outputRoot = Join-Path (Resolve-Path ".\artifacts").Path "phrase-image-diagnostics"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDir = Join-Path $outputRoot $stamp

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$summaryPath = Join-Path $outputDir "summary.txt"
$dbRowsPath = Join-Path $outputDir "db-phrases.txt"
$fileListPath = Join-Path $outputDir "phrase-image-files.txt"
$logTailPath = Join-Path $outputDir "phrase-image-log-tail.jsonl"

"Database: $dbPath" | Out-File -FilePath $summaryPath -Encoding utf8
"ImagesRoot: $imagesRoot" | Out-File -FilePath $summaryPath -Encoding utf8 -Append
"LogPath: $logPath" | Out-File -FilePath $summaryPath -Encoding utf8 -Append
"GeneratedAt: $(Get-Date -Format o)" | Out-File -FilePath $summaryPath -Encoding utf8 -Append

@'
import os, sqlite3, sys
db_path, output_path = sys.argv[1], sys.argv[2]
conn = sqlite3.connect(db_path)
cur = conn.cursor()
rows = list(cur.execute("select id, title, ifnull(image_relative_path, ''), updated_at from phrases order by updated_at desc limit 50"))
with open(output_path, "w", encoding="utf-8") as f:
    for row in rows:
        f.write("\t".join(map(str, row)) + "\n")
conn.close()
'@ | python - $dbPath $dbRowsPath

Get-ChildItem $imagesRoot -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object FullName, Length, LastWriteTime |
    Format-Table -AutoSize |
    Out-String |
    Out-File -FilePath $fileListPath -Encoding utf8

if (Test-Path $logPath)
{
    Get-Content $logPath |
        Select-String '"source":"phrase_images"' |
        Select-Object -Last 200 |
        ForEach-Object { $_.Line } |
        Out-File -FilePath $logTailPath -Encoding utf8
}
else
{
    "No log file for today." | Out-File -FilePath $logTailPath -Encoding utf8
}

$dbRows = if (Test-Path $dbRowsPath) { (Get-Content $dbRowsPath).Count } else { 0 }
$imageFileCount = (Get-ChildItem $imagesRoot -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
$logCount = if (Test-Path $logTailPath) { (Get-Content $logTailPath).Count } else { 0 }

"RecentDbRows: $dbRows" | Out-File -FilePath $summaryPath -Encoding utf8 -Append
"ImageFileCount: $imageFileCount" | Out-File -FilePath $summaryPath -Encoding utf8 -Append
"PhraseImageLogLines: $logCount" | Out-File -FilePath $summaryPath -Encoding utf8 -Append
"OutputDir: $outputDir" | Out-File -FilePath $summaryPath -Encoding utf8 -Append

Write-Output $outputDir
