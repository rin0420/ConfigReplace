using System.Buffers;
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
        var sourceRoot = Path.GetFullPath(sourcePath);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"コピー元フォルダーがありません: {sourcePath}");
        }
        EnsureDirectoryIsSafe(sourceRoot);
        progress?.Report(new OperationProgress(0, 1, string.Empty, "対象フォルダーを確認中"));

        var root = Path.GetFullPath(snapshotRoot);
        if (FileSystemUtilities.IsSameOrChildPath(root, sourceRoot))
        {
            throw new InvalidOperationException("コピー先をコピー元フォルダーの内側には作成できません。");
        }

        var contentRoot = Path.Combine(root, "content");
        Directory.CreateDirectory(contentRoot);
        var directories = new List<string>();
        var sourceFiles = new List<(string FullPath, string RelativePath)>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

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
                directories.Add(FileSystemUtilities.SafeRelativePath(sourceRoot, childDirectory));
                pending.Push(childDirectory);
            }

            foreach (var file in childFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureFileIsSafe(file);
                var relativePath = FileSystemUtilities.SafeRelativePath(sourceRoot, file);
                sourceFiles.Add((file, relativePath));
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        sourceFiles.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        var totalWork = directories.Count + sourceFiles.Count + 1;
        var completedWork = 0;
        foreach (var relativeDirectory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(contentRoot, relativeDirectory));
            completedWork++;
            progress?.Report(new OperationProgress(completedWork, totalWork, relativeDirectory, "スナップショットのフォルダーを準備中"));
        }

        var files = new List<SnapshotFileEntry>(sourceFiles.Count);
        for (var index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = sourceFiles[index];
            var destination = Path.Combine(contentRoot, sourceFile.RelativePath);
            files.Add(await CopyFileAndHashAsync(sourceFile.FullPath, sourceFile.RelativePath, destination, cancellationToken));
            completedWork++;
            progress?.Report(new OperationProgress(completedWork, totalWork, sourceFile.RelativePath, "スナップショット作成中"));
        }

        var treeHash = ComputeTreeHash(directories, files);
        var manifest = new SnapshotManifest
        {
            CapturedAt = DateTimeOffset.Now,
            SourcePath = includeSourceMetadata ? sourceRoot : string.Empty,
            TreeHash = treeHash,
            Directories = directories,
            Files = files
        };
        await SaveManifestAsync(Path.Combine(root, "snapshot.json"), manifest, cancellationToken);

        progress?.Report(new OperationProgress(totalWork, totalWork, string.Empty, "スナップショット作成完了"));
        return manifest;
    }

    public async Task<SnapshotManifest> LoadSnapshotManifestAsync(
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
        ValidateManifestPaths(snapshotRoot, manifest);
        return manifest;
    }

    public async Task<SnapshotManifest> ValidateSnapshotLayoutAsync(
        string snapshotRoot,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadSnapshotManifestAsync(snapshotRoot, cancellationToken);
        var contentRoot = Path.Combine(snapshotRoot, "content");
        if (!Directory.Exists(contentRoot))
        {
            throw new InvalidDataException($"スナップショットの内容が破損しています: {snapshotRoot}");
        }

        EnsureManifestDirectories(contentRoot, manifest.Directories);
        var expectedDirectories = manifest.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualDirectories = Directory.EnumerateDirectories(contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => FileSystemUtilities.SafeRelativePath(contentRoot, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedDirectories.SetEquals(actualDirectories))
        {
            throw new InvalidDataException($"スナップショットの内容が破損しています: {snapshotRoot}");
        }

        var expectedFiles = manifest.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actualFiles = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories)
            .Select(path => FileSystemUtilities.SafeRelativePath(contentRoot, path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (expectedFiles.Count != actualFiles.Count
            || actualFiles.Any(path => !expectedFiles.TryGetValue(path, out var expected)
                || new FileInfo(Path.Combine(contentRoot, path)).Length != expected.Length))
        {
            throw new InvalidDataException($"スナップショットの内容が破損しています: {snapshotRoot}");
        }

        return manifest;
    }

    public async Task<SnapshotManifest> LoadAndValidateSnapshotAsync(
        string snapshotRoot,
        CancellationToken cancellationToken = default)
    {
        var manifest = await LoadSnapshotManifestAsync(snapshotRoot, cancellationToken);
        var contentRoot = Path.Combine(snapshotRoot, "content");
        EnsureManifestDirectories(contentRoot, manifest.Directories);
        var content = await ScanAsync(contentRoot, cancellationToken);
        if (!content.Exists || !string.Equals(content.TreeHash, manifest.TreeHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"スナップショットの内容が破損しています: {snapshotRoot}");
        }

        var manifestDirectories = manifest.Directories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var contentDirectories = content.Directories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!manifestDirectories.SequenceEqual(contentDirectories, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"スナップショットのフォルダー一覧が不正です: {snapshotRoot}");
        }

        var manifestFiles = manifest.Files.ToDictionary(value => value.RelativePath, StringComparer.OrdinalIgnoreCase);
        if (manifestFiles.Count != content.Files.Count
            || content.Files.Any(file => !manifestFiles.TryGetValue(file.RelativePath, out var expected)
                || expected.Length != file.Length
                || !string.Equals(expected.Hash, file.Hash, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"スナップショットのファイル一覧が不正です: {snapshotRoot}");
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
        var manifestPath = Path.Combine(snapshotRoot, "snapshot.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"スナップショットのmanifestがありません: {snapshotRoot}");
        }

        SnapshotManifest manifest;
        await using (var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true))
        {
            manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            }, cancellationToken) ?? throw new InvalidDataException("スナップショットのmanifestが空です。");
        }
        if (string.IsNullOrEmpty(manifest.SourcePath)) return false;
        manifest.SourcePath = string.Empty;
        await SaveManifestAsync(Path.Combine(snapshotRoot, "snapshot.json"), manifest, cancellationToken);
        return true;
    }

    private static async Task<SnapshotFileEntry> CopyFileAndHashAsync(
        string source,
        string relativePath,
        string destination,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(source);
        var expectedLength = sourceInfo.Length;
        var attributes = File.GetAttributes(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long copiedLength = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copiedLength += read;
            }
            await output.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (copiedLength != expectedLength || new FileInfo(source).Length != expectedLength)
        {
            throw new IOException($"コピー元がコピー中に変更されました: {source}");
        }

        File.SetAttributes(destination, attributes);
        return new SnapshotFileEntry
        {
            RelativePath = relativePath,
            Length = copiedLength,
            Hash = Convert.ToHexString(hash.GetHashAndReset()),
            Attributes = attributes
        };
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

    private static void ValidateManifestPaths(string snapshotRoot, SnapshotManifest manifest)
    {
        if (manifest.Directories is null || manifest.Files is null)
        {
            throw new InvalidDataException($"スナップショットのmanifestが不完全です: {snapshotRoot}");
        }

        var contentRoot = Path.Combine(snapshotRoot, "content");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativeDirectory in manifest.Directories)
        {
            if (!directories.Add(relativeDirectory))
            {
                throw new InvalidDataException($"スナップショットのフォルダー一覧が不正です: {snapshotRoot}");
            }
            GetContainedSnapshotPath(contentRoot, relativeDirectory, "フォルダー");
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (!files.Add(file.RelativePath))
            {
                throw new InvalidDataException($"スナップショットのファイル一覧が不正です: {snapshotRoot}");
            }
            GetContainedSnapshotPath(contentRoot, file.RelativePath, "ファイル");
        }
    }

    private static void EnsureManifestDirectories(string contentRoot, IEnumerable<string> relativeDirectories)
    {
        foreach (var relativeDirectory in relativeDirectories)
        {
            Directory.CreateDirectory(GetContainedSnapshotPath(contentRoot, relativeDirectory, "フォルダー"));
        }
    }

    private static string GetContainedSnapshotPath(string contentRoot, string relativePath, string kind)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"スナップショットの{kind}パスが不正です: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(contentRoot, relativePath));
        if (string.Equals(fullPath, Path.GetFullPath(contentRoot), StringComparison.OrdinalIgnoreCase)
            || !FileSystemUtilities.IsSameOrChildPath(fullPath, contentRoot))
        {
            throw new InvalidDataException($"スナップショットの{kind}パスが不正です: {relativePath}");
        }

        return fullPath;
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
