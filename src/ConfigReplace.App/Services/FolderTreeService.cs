using System.Buffers;
using ConfigReplace.Models;

namespace ConfigReplace.Services;

public sealed class FolderTreeService
{
    /// <summary>
    /// フォルダーの内容だけをコピーします。コピー先に同じ相対パスのファイルがあれば上書きし、
    /// コピー元にないコピー先のファイルやフォルダーは残します。
    /// </summary>
    public async Task<(int FileCount, long TotalBytes)> CopyDirectoryContentsAsync(
        string sourceRootPath,
        string destinationRootPath,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sourceRoot = Path.GetFullPath(sourceRootPath);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"コピー元フォルダーがありません: {sourceRootPath}");
        }
        EnsureDirectoryIsSafe(sourceRoot);

        var destinationRoot = Path.GetFullPath(destinationRootPath);
        if (FileSystemUtilities.IsSameOrChildPath(destinationRoot, sourceRoot))
        {
            throw new InvalidOperationException("コピー先をコピー元フォルダーの内側には作成できません。");
        }
        if (File.Exists(destinationRoot))
        {
            throw new IOException($"コピー先がファイルです: {destinationRoot}");
        }
        if (Directory.Exists(destinationRoot)) EnsureDirectoryIsSafe(destinationRoot);

        var directories = new List<string>();
        var files = new List<(string FullPath, string RelativePath)>();
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
                files.Add((file, FileSystemUtilities.SafeRelativePath(sourceRoot, file)));
            }
        }

        directories.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        var totalWork = directories.Count + files.Count + 1;
        var completedWork = 0;
        Directory.CreateDirectory(destinationRoot);

        foreach (var relativeDirectory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, relativeDirectory);
            if (File.Exists(destination))
            {
                File.SetAttributes(destination, FileAttributes.Normal);
                File.Delete(destination);
            }
            Directory.CreateDirectory(destination);
            completedWork++;
            progress?.Report(new OperationProgress(completedWork, totalWork, relativeDirectory, "フォルダーを準備中"));
        }

        long totalBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, file.RelativePath);
            if (Directory.Exists(destination)) FileSystemUtilities.DeleteDirectoryTree(destination);
            if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            totalBytes += await CopyFileContentsAsync(file.FullPath, destination, cancellationToken);
            completedWork++;
            progress?.Report(new OperationProgress(completedWork, totalWork, file.RelativePath, "フォルダーを上書き中"));
        }

        progress?.Report(new OperationProgress(totalWork, totalWork, string.Empty, "フォルダーの上書き完了"));
        return (files.Count, totalBytes);
    }

    private static async Task<long> CopyFileContentsAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var expectedLength = new FileInfo(source).Length;
        var attributes = File.GetAttributes(source);
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
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long copiedLength = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
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
        return copiedLength;
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
