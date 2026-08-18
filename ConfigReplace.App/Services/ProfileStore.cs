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
    {
        ValidateSegment(profileId, nameof(profileId));
        ValidateSegment(slotId, nameof(slotId));
        var path = Path.GetFullPath(Path.Combine(ProfilesRoot, profileId, slotId));
        if (!FileSystemUtilities.IsSameOrChildPath(path, ProfilesRoot))
        {
            throw new InvalidDataException("プロファイルの保存先がProfilesフォルダー外を参照しています。");
        }

        return path;
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
}
