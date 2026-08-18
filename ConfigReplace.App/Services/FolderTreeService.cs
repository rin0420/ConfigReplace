using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConfigReplace.Models;

namespace ConfigReplace.Services;

public sealed class FolderTreeService
{
    public async Task<FolderTree> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
        {
            return FolderTree.Missing(root);
        }

        EnsureDirectoryIsSafe(root);
        var directories = new List<string>();
        var files = new List<SnapshotFileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> childFiles;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
                childFiles = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"フォルダーを読み取れません: {directory}", exception);
            }

            foreach (var childDirectory in childDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureDirectoryIsSafe(childDirectory);
                directories.Add(FileSystemUtilities.SafeRelativePath(root, childDirectory));
                pending.Push(childDirectory);
            }

            foreach (var file in childFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureFileIsSafe(file);
                var bytes = new FileInfo(file).Length;
                files.Add(new SnapshotFileEntry
                {
                    RelativePath = FileSystemUtilities.SafeRelativePath(root, file),
                    Length = bytes,
                    Hash = await FileSystemUtilities.ComputeFileHashAsync(file, cancellationToken),
                    Attributes = File.GetAttributes(file)
                });
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new FolderTree
        {
            RootPath = root,
            Exists = true,
            Directories = directories,
            Files = files,
            TreeHash = ComputeTreeHash(directories, files)
        };
    }

    public async Task<SnapshotManifest> CaptureAsync(
        string sourcePath,
        string snapshotRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await CaptureCoreAsync(sourcePath, snapshotRoot, true, progress, cancellationToken);

    public async Task<SnapshotManifest> CaptureSelfContainedAsync(
        string sourcePath,
        string snapshotRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await CaptureCoreAsync(sourcePath, snapshotRoot, false, progress, cancellationToken);

    private async Task<SnapshotManifest> CaptureCoreAsync(
        string sourcePath,
        string snapshotRoot,
        bool includeSourceMetadata,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = await ScanAsync(sourcePath, cancellationToken);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"コピー元フォルダーがありません: {sourcePath}");
        }

        var root = Path.GetFullPath(snapshotRoot);
        var contentRoot = Path.Combine(root, "content");
        Directory.CreateDirectory(contentRoot);
        foreach (var relativeDirectory in source.Directories)
        {
            Directory.CreateDirectory(Path.Combine(contentRoot, relativeDirectory));
        }

        for (var index = 0; index < source.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = source.Files[index];
            progress?.Report(new OperationProgress(index, source.Files.Count, entry.RelativePath, "スナップショット作成中"));
            var destination = Path.Combine(contentRoot, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source.RootPath, entry.RelativePath), destination, true);
            File.SetAttributes(destination, entry.Attributes);
        }

        var manifest = new SnapshotManifest
        {
            CapturedAt = DateTimeOffset.Now,
            SourcePath = includeSourceMetadata ? source.RootPath : string.Empty,
            TreeHash = source.TreeHash,
            Directories = source.Directories,
            Files = source.Files
        };
        await SaveManifestAsync(Path.Combine(root, "snapshot.json"), manifest, cancellationToken);
        var copied = await ScanAsync(contentRoot, cancellationToken);
        if (!copied.Exists || !string.Equals(copied.TreeHash, source.TreeHash, StringComparison.Ordinal))
        {
            throw new IOException("スナップショット作成中にコピー元が変更されました。");
        }

        progress?.Report(new OperationProgress(source.Files.Count, source.Files.Count, string.Empty, "スナップショット作成完了"));
        return manifest;
    }

    public async Task<SnapshotManifest> LoadAndValidateSnapshotAsync(
        string snapshotRoot,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(snapshotRoot, "snapshot.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"スナップショットのmanifestがありません: {snapshotRoot}");
        }

        await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        }, cancellationToken)
            ?? throw new InvalidDataException("スナップショットのmanifestが空です。");
        var content = await ScanAsync(Path.Combine(snapshotRoot, "content"), cancellationToken);
        if (!content.Exists || !string.Equals(content.TreeHash, manifest.TreeHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"スナップショットの内容が破損しています: {snapshotRoot}");
        }

        return manifest;
    }

    public async Task CopySnapshotContentAsync(
        string snapshotRoot,
        string destinationRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadAndValidateSnapshotAsync(snapshotRoot, cancellationToken);
        var contentRoot = Path.Combine(snapshotRoot, "content");
        Directory.CreateDirectory(destinationRoot);
        foreach (var relativeDirectory in manifest.Directories)
        {
            Directory.CreateDirectory(Path.Combine(destinationRoot, relativeDirectory));
        }

        for (var index = 0; index < manifest.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = manifest.Files[index];
            progress?.Report(new OperationProgress(index, manifest.Files.Count, entry.RelativePath, "展開準備中"));
            var source = Path.Combine(contentRoot, entry.RelativePath);
            var destination = Path.Combine(destinationRoot, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
            File.SetAttributes(destination, entry.Attributes);
        }
    }

    public async Task CloneSnapshotAsync(
        string sourceSnapshotRoot,
        string destinationSnapshotRoot,
        CancellationToken cancellationToken = default)
    {
        await LoadAndValidateSnapshotAsync(sourceSnapshotRoot, cancellationToken);
        var sourceRoot = Path.GetFullPath(sourceSnapshotRoot);
        var destinationRoot = Path.GetFullPath(destinationSnapshotRoot);
        if (FileSystemUtilities.IsSameOrChildPath(destinationRoot, sourceRoot))
        {
            throw new InvalidOperationException("スナップショット自身をコピー先にはできません。");
        }

        Directory.CreateDirectory(destinationRoot);
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
            File.SetAttributes(destination, File.GetAttributes(file));
        }
    }

    public async Task<bool> RemoveSourceMetadataAsync(
        string snapshotRoot,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadAndValidateSnapshotAsync(snapshotRoot, cancellationToken);
        if (string.IsNullOrEmpty(manifest.SourcePath)) return false;
        manifest.SourcePath = string.Empty;
        await SaveManifestAsync(Path.Combine(snapshotRoot, "snapshot.json"), manifest, cancellationToken);
        return true;
    }

    public static string ComputeTreeHash(IEnumerable<string> directories, IEnumerable<SnapshotFileEntry> files)
    {
        var builder = new StringBuilder();
        foreach (var directory in directories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("D|").Append(directory).Append('\n');
        }

        foreach (var file in files.OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("F|").Append(file.RelativePath).Append('|').Append(file.Length).Append('|').Append(file.Hash).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static async Task SaveManifestAsync(string path, SnapshotManifest manifest, CancellationToken cancellationToken)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, manifest, options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void EnsureDirectoryIsSafe(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"再解析ポイントのフォルダーは使用できません: {path}");
        }
    }

    private static void EnsureFileIsSafe(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"再解析ポイントのファイルは使用できません: {path}");
        }
    }
}

public sealed class FolderTree
{
    public required string RootPath { get; init; }
    public bool Exists { get; init; }
    public List<string> Directories { get; init; } = [];
    public List<SnapshotFileEntry> Files { get; init; } = [];
    public string TreeHash { get; init; } = "MISSING";

    public static FolderTree Missing(string path) => new() { RootPath = path, Exists = false, TreeHash = "MISSING" };
}
