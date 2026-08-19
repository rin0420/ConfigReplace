using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConfigReplace.Models;

namespace ConfigReplace.Services;

public sealed class HistoryDiffService(
    FolderTreeService treeService,
    string backupRoot)
{
    private const long MaxTextFileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _backupRoot = Path.GetFullPath(backupRoot);

    public async Task<IReadOnlyList<HistoryFolderComparison>> GetFoldersAsync(
        FolderSetHistoryItem history,
        CancellationToken cancellationToken = default)
    {
        var (manifest, runDirectory) = await LoadManifestAsync(history, cancellationToken);
        var result = new List<HistoryFolderComparison>(manifest.Entries.Count);
        for (var index = 0; index < manifest.Entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Entries[index];
            var targetPath = GetAbsoluteTargetPath(entry.TargetPath);
            var backupPath = entry.BeforeExisted
                ? GetBackupPath(runDirectory, entry.BackupRelativePath)
                : null;
            result.Add(new HistoryFolderComparison
            {
                EntryIndex = index,
                FolderName = entry.FolderName,
                TargetPath = targetPath,
                BackupPath = backupPath,
                BeforeExisted = entry.BeforeExisted,
                CurrentExists = Directory.Exists(targetPath)
            });
        }

        return result;
    }

    public async Task<HistoryFolderDiff> CompareFolderAsync(
        HistoryFolderComparison folder,
        CancellationToken cancellationToken = default)
    {
        var beforeTask = folder.BackupPath is null
            ? Task.FromResult(FolderTree.Missing(folder.BackupPath ?? string.Empty))
            : treeService.ScanAsync(folder.BackupPath, cancellationToken);
        var currentTask = treeService.ScanAsync(folder.TargetPath, cancellationToken);
        await Task.WhenAll(beforeTask, currentTask);
        var before = await beforeTask;
        var current = await currentTask;

        var beforeFiles = before.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var currentFiles = current.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var differences = new List<HistoryFileDifference>(beforeFiles.Count + currentFiles.Count);
        foreach (var relativePath in beforeFiles.Keys.Concat(currentFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            beforeFiles.TryGetValue(relativePath, out var beforeFile);
            currentFiles.TryGetValue(relativePath, out var currentFile);
            var kind = beforeFile is null
                ? HistoryFileChangeKind.Added
                : currentFile is null
                    ? HistoryFileChangeKind.Removed
                    : beforeFile.Length == currentFile.Length
                        && string.Equals(beforeFile.Hash, currentFile.Hash, StringComparison.Ordinal)
                        ? HistoryFileChangeKind.Unchanged
                        : HistoryFileChangeKind.Modified;
            differences.Add(new HistoryFileDifference
            {
                RelativePath = relativePath,
                ChangeKind = kind,
                BeforePath = beforeFile is null ? null : Path.Combine(before.RootPath, relativePath),
                CurrentPath = currentFile is null ? null : Path.Combine(current.RootPath, relativePath),
                BeforeLength = beforeFile?.Length,
                CurrentLength = currentFile?.Length
            });
        }

        return new HistoryFolderDiff { Folder = folder, Files = differences };
    }

    public async Task<HistoryFileContent> ReadBeforeFileAsync(
        HistoryFileDifference difference,
        CancellationToken cancellationToken = default)
        => await ReadFileAsync(difference.BeforePath, cancellationToken);

    public async Task<HistoryFileContent> ReadCurrentFileAsync(
        HistoryFileDifference difference,
        CancellationToken cancellationToken = default)
        => await ReadFileAsync(difference.CurrentPath, cancellationToken);

    private async Task<(FolderSetSwitchManifest Manifest, string RunDirectory)> LoadManifestAsync(
        FolderSetHistoryItem history,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.GetFullPath(history.ManifestPath);
        if (!FileSystemUtilities.IsSameOrChildPath(manifestPath, _backupRoot))
        {
            throw new InvalidDataException("履歴のmanifestがBackupsフォルダー外を参照しています。");
        }

        var runDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("履歴の保存先を取得できません。");
        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var manifest = await JsonSerializer.DeserializeAsync<FolderSetSwitchManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("履歴のmanifestが空です。");
        if (manifest.SchemaVersion < 2 || manifest.Entries.Count == 0)
        {
            throw new InvalidDataException("ファイル比較に対応していない履歴です。");
        }

        return (manifest, runDirectory);
    }

    private static string GetAbsoluteTargetPath(string path)
    {
        if (!Path.IsPathRooted(path)) throw new InvalidDataException("履歴の配置先パスが不正です。");
        return Path.GetFullPath(path);
    }

    private string GetBackupPath(string runDirectory, string relativePath)
    {
        var root = Path.GetFullPath(runDirectory);
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidDataException("履歴のバックアップパスが空です。");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || !FileSystemUtilities.IsSameOrChildPath(path, root)
            || !FileSystemUtilities.IsSameOrChildPath(root, _backupRoot))
        {
            throw new InvalidDataException("履歴のバックアップパスが不正です。");
        }

        return path;
    }

    private static async Task<HistoryFileContent> ReadFileAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new HistoryFileContent { Message = "この側にはファイルがありません。" };
        }

        var info = new FileInfo(path);
        if (info.Length > MaxTextFileBytes)
        {
            return new HistoryFileContent
            {
                Exists = true,
                IsTooLarge = true,
                Message = $"4 MBを超えるため、テキスト表示を省略しました（{info.Length / 1024d:N1} KB）。"
            };
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (LooksBinary(bytes, out var encoding, out var offset))
        {
            return new HistoryFileContent { Exists = true, IsBinary = true, Message = "バイナリファイルのため、テキスト差分を表示できません。" };
        }

        try
        {
            return new HistoryFileContent { Exists = true, Text = encoding.GetString(bytes, offset, bytes.Length - offset) };
        }
        catch (DecoderFallbackException)
        {
            return new HistoryFileContent { Exists = true, IsBinary = true, Message = "テキストとして解釈できないファイルです。" };
        }
    }

    private static bool LooksBinary(byte[] bytes, out Encoding encoding, out int offset)
    {
        offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            encoding = new UTF8Encoding(false, true);
            offset = 3;
            return false;
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
        {
            encoding = new UTF32Encoding(false, false, true);
            offset = 4;
            return false;
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
        {
            encoding = new UTF32Encoding(true, false, true);
            offset = 4;
            return false;
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            encoding = new UnicodeEncoding(false, false, true);
            offset = 2;
            return false;
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            encoding = new UnicodeEncoding(true, false, true);
            offset = 2;
            return false;
        }

        if (bytes.Contains((byte)0))
        {
            encoding = Encoding.UTF8;
            return true;
        }

        encoding = new UTF8Encoding(false, true);
        return false;
    }
}
