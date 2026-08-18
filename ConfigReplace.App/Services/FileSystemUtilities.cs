using System.Security.Cryptography;

namespace ConfigReplace.Services;

public static class FileSystemUtilities
{
    public static string ComputeHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static bool IsSameOrChildPath(string path, string root)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException($"対象ルート外のパスです: {path}");
        }

        return relative;
    }

    public static async Task AtomicWriteAsync(string targetPath, byte[] content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("対象ファイルのフォルダーを取得できません。");
        var tempPath = Path.Combine(directory, $".configreplace-{Guid.NewGuid():N}.tmp");
        var attributes = File.Exists(targetPath) ? File.GetAttributes(targetPath) : FileAttributes.Normal;

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            try
            {
                File.Replace(tempPath, targetPath, null, true);
            }
            catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
            {
                File.Move(tempPath, targetPath, true);
            }

            File.SetAttributes(targetPath, attributes);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // 一時ファイルの削除失敗は元の処理結果を上書きしません。
                }
            }
        }
    }

    public static void DeleteDirectoryTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(value => value.Length))
        {
            File.SetAttributes(directory, FileAttributes.Directory);
        }
        File.SetAttributes(path, FileAttributes.Directory);
        Directory.Delete(path, true);
    }
}
