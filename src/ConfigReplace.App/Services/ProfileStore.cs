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

    public void CommitStagedSnapshots(FolderProfile profile, string stagingId, string? previousProfileName = null)
    {
        var stagingPath = GetLegacyProfileDirectoryPath(stagingId);
        var destination = GetProfileDirectoryPath(profile.Name);

        if (!string.IsNullOrWhiteSpace(previousProfileName)
            && !string.Equals(previousProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            var previousPath = GetProfileDirectoryPath(previousProfileName);
            if (Directory.Exists(previousPath))
            {
                if (!Directory.Exists(destination)) Directory.Move(previousPath, destination);
                else MergeProfileDirectories(previousPath, destination);
            }
        }

        if (!Directory.Exists(stagingPath)) return;
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(stagingPath))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new IOException($"プロファイル保存先に同名のスナップショットがあります: {target}");
            }
            if (Directory.Exists(entry)) Directory.Move(entry, target);
            else File.Move(entry, target);
        }
        try { Directory.Delete(stagingPath); } catch { }
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

    private string GetContainedPath(string segment)
    {
        var path = Path.GetFullPath(Path.Combine(ProfilesRoot, segment));
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

    private static void MergeProfileDirectories(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target)) continue;
            if (Directory.Exists(entry)) Directory.Move(entry, target);
            else File.Move(entry, target);
        }
        try { Directory.Delete(source); } catch { }
    }
}
