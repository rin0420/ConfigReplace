using System.Text.Json;
using System.Text.Json.Serialization;
using ConfigReplace.Models;

namespace ConfigReplace.Services;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ProfileStore(string profilesRoot)
    {
        ProfilesRoot = Path.GetFullPath(profilesRoot);
        ProfilesFilePath = Path.Combine(ProfilesRoot, "profiles.json");
    }

    public string ProfilesRoot { get; }
    public string ProfilesFilePath { get; }

    public async Task<ProfilesDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ProfilesFilePath))
        {
            return new ProfilesDocument();
        }

        await using var stream = new FileStream(ProfilesFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await JsonSerializer.DeserializeAsync<ProfilesDocument>(stream, JsonOptions, cancellationToken)
            ?? new ProfilesDocument();
    }

    public async Task SaveAsync(ProfilesDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ProfilesRoot);
        var tempPath = ProfilesFilePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, ProfilesFilePath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public string GetSnapshotPath(string profileId, string slotId)
        => GetLegacySnapshotPath(profileId, slotId);

    public string GetSnapshotPath(FolderProfile profile, string snapshotId)
    {
        ValidateSegment(profile.Id, nameof(profile.Id));
        ValidateSegment(snapshotId, nameof(snapshotId));

        try
        {
            var namedPath = GetSnapshotPathForDirectory(GetProfileDirectoryPath(profile.Name), snapshotId);
            var legacyPath = GetLegacySnapshotPath(profile.Id, snapshotId);
            return Directory.Exists(namedPath) || !Directory.Exists(legacyPath) ? namedPath : legacyPath;
        }
        catch (InvalidDataException)
        {
            return GetLegacySnapshotPath(profile.Id, snapshotId);
        }
    }

    public string GetProfileDirectoryPath(string profileName)
    {
        ValidateProfileName(profileName);
        return GetContainedPath(profileName);
    }

    /// <summary>
    /// 新形式の保存先です。プロファイル配下にはフォルダー名をそのまま配置します。
    /// </summary>
    public string GetProfileFolderPath(FolderProfile profile, string folderName)
        => GetProfileFolderPath(profile.Name, folderName);

    public string GetProfileFolderPath(string profileName, string folderName)
    {
        ValidateProfileName(profileName);
        ValidateFolderName(folderName);
        return GetContainedPath(profileName, folderName);
    }

    public void MigrateProfileDirectory(FolderProfile profile)
    {
        var legacyPath = GetLegacyProfileDirectoryPath(profile.Id);
        if (!Directory.Exists(legacyPath)) return;

        string namedPath;
        try { namedPath = GetProfileDirectoryPath(profile.Name); }
        catch (InvalidDataException) { return; }

        if (string.Equals(legacyPath, namedPath, StringComparison.OrdinalIgnoreCase)) return;
        if (!Directory.Exists(namedPath))
        {
            Directory.Move(legacyPath, namedPath);
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(legacyPath))
        {
            var destination = Path.Combine(namedPath, Path.GetFileName(entry));
            if (File.Exists(destination) || Directory.Exists(destination)) continue;
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
        try { Directory.Delete(legacyPath); } catch { }
    }

    /// <summary>
    /// staging フォルダーを、プロファイル名直下の新形式へ移します。
    /// 編集時は既存のプロファイル保存先を一時的に退避し、保存完了後に削除します。
    /// </summary>
    public void CommitStagedFolders(FolderProfile profile, string stagingId, string? previousProfileName = null)
    {
        var stagingPath = GetProfileDirectoryPath(stagingId);
        var destination = GetProfileDirectoryPath(profile.Name);
        string? previousPath = null;
        string? displacedPath = null;
        var stagedMoved = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(previousProfileName))
            {
                previousPath = GetProfileDirectoryPath(previousProfileName);
                var samePath = string.Equals(previousPath, destination, StringComparison.OrdinalIgnoreCase);
                if (!samePath && (Directory.Exists(destination) || File.Exists(destination)))
                {
                    throw new IOException($"プロファイル名の保存先が既に使用されています: {destination}");
                }

                if (Directory.Exists(previousPath))
                {
                    displacedPath = previousPath + $".configreplace-old-{Guid.NewGuid():N}";
                    Directory.Move(previousPath, displacedPath);
                }
                else if (File.Exists(previousPath))
                {
                    throw new IOException($"プロファイルの保存先がファイルです: {previousPath}");
                }
            }
            else if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException($"プロファイル名の保存先が既に使用されています: {destination}");
            }

            if (!Directory.Exists(stagingPath))
            {
                throw new DirectoryNotFoundException($"プロファイルの一時保存先がありません: {stagingPath}");
            }

            Directory.Move(stagingPath, destination);
            stagedMoved = true;

            if (displacedPath is not null && Directory.Exists(displacedPath))
            {
                FileSystemUtilities.DeleteDirectoryTree(displacedPath);
            }
        }
        catch
        {
            if (stagedMoved && Directory.Exists(destination))
            {
                FileSystemUtilities.DeleteDirectoryTree(destination);
            }

            if (displacedPath is not null && previousPath is not null && Directory.Exists(displacedPath)
                && !Directory.Exists(previousPath) && !File.Exists(previousPath))
            {
                Directory.Move(displacedPath, previousPath);
            }

            throw;
        }
    }
    public void ValidateWritable()
    {
        Directory.CreateDirectory(ProfilesRoot);
        var probe = Path.Combine(ProfilesRoot, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException($"不正なプロファイル識別子です: {parameterName}");
        }
    }

    private string GetLegacySnapshotPath(string profileId, string snapshotId)
    {
        ValidateSegment(profileId, nameof(profileId));
        ValidateSegment(snapshotId, nameof(snapshotId));
        return GetSnapshotPathForDirectory(GetLegacyProfileDirectoryPath(profileId), snapshotId);
    }

    private string GetLegacyProfileDirectoryPath(string profileId)
    {
        ValidateSegment(profileId, nameof(profileId));
        return GetContainedPath(profileId);
    }

    private string GetSnapshotPathForDirectory(string profileDirectory, string snapshotId)
    {
        ValidateSegment(snapshotId, nameof(snapshotId));
        var path = Path.GetFullPath(Path.Combine(profileDirectory, snapshotId));
        if (!FileSystemUtilities.IsSameOrChildPath(path, ProfilesRoot))
        {
            throw new InvalidDataException("プロファイルの保存先がProfilesフォルダー外を参照しています。");
        }

        return path;
    }

    public static void ValidateFolderName(string value)
    {
        ValidateSegment(value, nameof(value));
        if (value.EndsWith(' ') || value.EndsWith('.') || value.Length > 120)
        {
            throw new InvalidDataException("フォルダー名は末尾の空白・ピリオドを含めず、120文字以内で指定してください。");
        }
    }

    private string GetContainedPath(params string[] segments)
    {
        var path = ProfilesRoot;
        foreach (var segment in segments) path = Path.Combine(path, segment);
        path = Path.GetFullPath(path);
        if (!FileSystemUtilities.IsSameOrChildPath(path, ProfilesRoot))
        {
            throw new InvalidDataException("プロファイルの保存先がProfilesフォルダー外を参照しています。");
        }

        return path;
    }

    private static void ValidateProfileName(string value)
    {
        ValidateSegment(value, nameof(value));
        if (value.EndsWith(' ') || value.EndsWith('.') || value.Length > 120)
        {
            throw new InvalidDataException("プロファイル名は末尾の空白・ピリオドを含めず、120文字以内で指定してください。");
        }
    }

}
