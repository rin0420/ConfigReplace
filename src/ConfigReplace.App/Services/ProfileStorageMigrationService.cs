using ConfigReplace.Models;

namespace ConfigReplace.Services;

/// <summary>
/// 旧プロファイル保存形式を、プロファイル名／フォルダー名の直接保存へ移行します。
/// 旧manifestは参照せず、実体のcontentだけを移します。
/// </summary>
public sealed class ProfileStorageMigrationService(
    ProfileStore profileStore,
    FolderTreeService treeService)
{
    public async Task<bool> MigrateAsync(
        ProfilesDocument document,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var changed = false;
        foreach (var profile in document.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var legacyProfilePath = profileStore.GetProfileDirectoryPath(profile.Id);
            if (Directory.Exists(legacyProfilePath)) changed = true;
            profileStore.MigrateProfileDirectory(profile);

            var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in profile.Groups.SelectMany(group => group.Folders))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProfileStore.ValidateFolderName(snapshot.FolderName);
                if (!folderNames.Add(snapshot.FolderName))
                {
                    throw new InvalidDataException($"プロファイル内で同じフォルダー名を複数登録できません: {snapshot.FolderName}");
                }

                var directPath = profileStore.GetProfileFolderPath(profile, snapshot.FolderName);
                var legacyPath = profileStore.GetSnapshotPath(profile, snapshot.Id);
                var legacyContentPath = Path.Combine(legacyPath, "content");
                var metadataIsNew = string.Equals(snapshot.Id, snapshot.FolderName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(snapshot.SnapshotRelativePath, snapshot.FolderName, StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(snapshot.SourcePath);
                var legacyLayout = !metadataIsNew && Directory.Exists(legacyContentPath);

                if (legacyLayout)
                {
                    var samePath = string.Equals(directPath, legacyPath, StringComparison.OrdinalIgnoreCase);
                    var migrationPath = directPath + $".configreplace-migrate-{Guid.NewGuid():N}";
                    try
                    {
                        var summary = await treeService.CopyDirectoryContentsAsync(
                            legacyContentPath,
                            migrationPath,
                            progress,
                            cancellationToken);

                        if (Directory.Exists(directPath))
                        {
                            if (!samePath)
                            {
                                FileSystemUtilities.DeleteDirectoryTree(migrationPath);
                            }
                            else
                            {
                                FileSystemUtilities.DeleteDirectoryTree(directPath);
                                Directory.Move(migrationPath, directPath);
                            }
                        }
                        else if (File.Exists(directPath))
                        {
                            throw new IOException($"新形式の保存先がファイルです: {directPath}");
                        }
                        else
                        {
                            Directory.Move(migrationPath, directPath);
                        }

                        if (!samePath && Directory.Exists(legacyPath))
                        {
                            FileSystemUtilities.DeleteDirectoryTree(legacyPath);
                        }

                        snapshot.FileCount = summary.FileCount;
                        snapshot.TotalBytes = summary.TotalBytes;
                        changed = true;
                    }
                    finally
                    {
                        if (Directory.Exists(migrationPath)) FileSystemUtilities.DeleteDirectoryTree(migrationPath);
                    }
                }

                if (!metadataIsNew)
                {
                    snapshot.Id = snapshot.FolderName;
                    snapshot.SnapshotRelativePath = snapshot.FolderName;
                    snapshot.SourcePath = string.Empty;
                    snapshot.TreeHash = string.Empty;
                    changed = true;
                }
            }
        }

        return changed;
    }
}
