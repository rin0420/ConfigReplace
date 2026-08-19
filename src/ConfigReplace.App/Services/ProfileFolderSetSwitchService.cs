using ConfigReplace.Models;

namespace ConfigReplace.Services;

/// <summary>
/// 選択したプロファイルの登録フォルダーを、配置先へそのまま上書きします。
/// コピー元にない配置先のファイルやフォルダーは変更しません。
/// </summary>
public sealed class ProfileFolderSetSwitchService(
    ProfileStore profileStore,
    FolderTreeService treeService) : IProfileFolderSetSwitchService
{
    public Task<FolderSetSwitchPlan> CreatePlanAsync(
        FolderProfile profile,
        ProfilesDocument document,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var groups = new Dictionary<string, Dictionary<string, ProfileFolderSnapshot>>(StringComparer.OrdinalIgnoreCase);
        var allFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new List<FolderSetGroupPlan>();

        foreach (var group in profile.Groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string rootPath;
            try
            {
                rootPath = Path.GetFullPath(group.TargetRootPath);
                ValidateTargetRoot(rootPath);
                ValidateTargetParent(rootPath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                errors.Add($"{group.TargetRootPath}: {exception.Message}");
                continue;
            }

            if (!groups.TryGetValue(rootPath, out var folders))
            {
                folders = new Dictionary<string, ProfileFolderSnapshot>(StringComparer.OrdinalIgnoreCase);
                groups[rootPath] = folders;
            }

            foreach (var snapshot in group.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ProfileStore.ValidateFolderName(snapshot.FolderName);
                    if (!allFolderNames.Add(snapshot.FolderName))
                    {
                        errors.Add($"プロファイル内で同じフォルダー名を複数登録できません: {snapshot.FolderName}");
                        continue;
                    }
                    if (!folders.TryAdd(snapshot.FolderName, snapshot))
                    {
                        errors.Add($"同じ配置先に同名フォルダーが重複しています: {Path.Combine(rootPath, snapshot.FolderName)}");
                        continue;
                    }

                    var sourcePath = profileStore.GetProfileFolderPath(profile, snapshot.FolderName);
                    if (!Directory.Exists(sourcePath))
                    {
                        errors.Add($"プロファイル内に保存フォルダーがありません: {sourcePath}");
                    }
                    else if (IsLegacySnapshotLayout(sourcePath))
                    {
                        errors.Add($"旧スナップショット形式が残っています。アプリを再起動して移行してください: {sourcePath}");
                    }

                    var targetPath = Path.Combine(rootPath, snapshot.FolderName);
                    if (File.Exists(targetPath))
                    {
                        errors.Add($"配置先に同名のファイルがあります: {targetPath}");
                    }

                    progress?.Report(new OperationProgress(0, 1, targetPath, "上書き対象を確認中"));
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
                {
                    errors.Add($"{snapshot.FolderName}: {exception.Message}");
                }
            }
        }

        if (profile.Groups.Count == 0)
        {
            errors.Add("プロファイルに配置するフォルダーが登録されていません。");
        }

        foreach (var rootEntry in groups.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var desired = rootEntry.Value.Values
                .OrderBy(value => value.FolderName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var replaced = desired.Count(folder => Directory.Exists(Path.Combine(rootEntry.Key, folder.FolderName)));
            plans.Add(new FolderSetGroupPlan
            {
                TargetRootPath = rootEntry.Key,
                ManagedFolderNames = desired.Select(value => value.FolderName).ToArray(),
                DesiredFolders = desired,
                CurrentHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                AddedFolderCount = desired.Length - replaced,
                RemovedFolderCount = 0,
                ReplacedFolderCount = replaced
            });
        }

        if (plans.Count == 0 && errors.Count == 0)
        {
            errors.Add("プロファイルに配置するフォルダーが登録されていません。");
        }

        return Task.FromResult(new FolderSetSwitchPlan
        {
            Profile = profile,
            Groups = plans,
            ValidationErrors = errors
        });
    }

    public async Task<OperationResult> ExecuteAsync(
        FolderSetSwitchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.IsValid)
        {
            return new OperationResult(false, "上書き前の確認に失敗しました。", Errors: plan.ValidationErrors);
        }

        var folders = plan.Groups
            .SelectMany(group => group.DesiredFolders.Select(folder => (Group: group, Folder: folder)))
            .ToArray();
        var totalWork = Math.Max(1, folders.Length * 100);

        try
        {
            for (var index = 0; index < folders.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = folders[index];
                var sourcePath = profileStore.GetProfileFolderPath(plan.Profile, item.Folder.FolderName);
                var targetPath = Path.Combine(item.Group.TargetRootPath, item.Folder.FolderName);
                var offset = index * 100;
                var localProgress = new Progress<OperationProgress>(value =>
                {
                    var processed = Math.Min(totalWork, offset + Math.Clamp(value.Percent, 0, 100));
                    progress?.Report(new OperationProgress(processed, totalWork, targetPath, value.Phase));
                });

                await treeService.CopyDirectoryContentsAsync(sourcePath, targetPath, localProgress, cancellationToken);
            }

            progress?.Report(new OperationProgress(totalWork, totalWork, string.Empty, "プロファイルの上書き完了"));
            return new OperationResult(
                true,
                $"プロファイル「{plan.Profile.Name}」の登録フォルダーを配置先へ上書きしました。");
        }
        catch (OperationCanceledException)
        {
            return new OperationResult(false, "上書きをキャンセルしました。途中まで反映された内容は元に戻していません。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new OperationResult(false, $"プロファイルの上書きに失敗しました: {exception.Message}", Errors: [exception.Message]);
        }
    }

    private void ValidateTargetRoot(string rootPath)
    {
        if (FileSystemUtilities.IsSameOrChildPath(rootPath, profileStore.ProfilesRoot))
        {
            throw new InvalidDataException("Profiles配下は配置先にできません。");
        }
        if (File.Exists(rootPath))
        {
            throw new InvalidDataException($"配置先がファイルです: {rootPath}");
        }
        if (Directory.Exists(rootPath) && File.GetAttributes(rootPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException($"再解析ポイントの配置先は使用できません: {rootPath}");
        }
    }

    private static void ValidateTargetParent(string targetRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
        var parent = Directory.GetParent(normalized)?.FullName;
        if (parent is null)
        {
            if (!Directory.Exists(normalized) || !HasSafeDirectory(normalized))
            {
                throw new InvalidDataException("配置先の親フォルダーを安全に確認できません。");
            }
            return;
        }

        if (!HasSafeExistingParent(normalized))
        {
            throw new InvalidDataException("配置先の親フォルダーを安全に確認できません。");
        }
        if (!Directory.Exists(parent))
        {
            try { Directory.CreateDirectory(parent); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new IOException("配置先の親フォルダーを作成できません。", exception);
            }
        }
    }

    private static bool HasSafeExistingParent(string targetPath)
    {
        var current = Directory.GetParent(targetPath)?.FullName;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            current = Directory.GetParent(current)?.FullName;
        }
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return true;
    }

    private static bool HasSafeDirectory(string path)
    {
        try { return !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsLegacySnapshotLayout(string path)
        => File.Exists(Path.Combine(path, "snapshot.json"))
           && Directory.Exists(Path.Combine(path, "content"));
}
