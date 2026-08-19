using System.Text.Json;
using System.Text.Json.Serialization;
using ConfigReplace.Models;

namespace ConfigReplace.Services;

/// <summary>
/// 「プロファイル1 = C:\\ に A/B」「プロファイル2 = C:\\ に C/D」のような
/// フォルダーセットの完全切替を担当します。共通の配置先に登録されたフォルダー名の
/// みを管理し、それ以外の利用者フォルダーには触れません。
/// </summary>
public sealed class ProfileFolderSetSwitchService(
    ProfileStore profileStore,
    FolderTreeService treeService,
    string backupRoot) : IProfileFolderSetSwitchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private const string ExistingFolderMarker = "__EXISTS__";

    private readonly string _backupRoot = Path.GetFullPath(backupRoot);

    public async Task<FolderSetSwitchPlan> CreatePlanAsync(
        FolderProfile profile,
        ProfilesDocument document,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var selectedGroups = NormalizeProfileGroups(profile, errors);
        var managedByRoot = BuildManagedFolderNames(document, errors);
        var plans = new List<FolderSetGroupPlan>();

        if (selectedGroups.Count == 0)
        {
            errors.Add("プロファイルに配置するフォルダーが登録されていません。");
        }

        foreach (var rootEntry in managedByRoot.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rootPath = rootEntry.Key;
            var managedNames = rootEntry.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            var desired = selectedGroups.TryGetValue(rootPath, out var selected)
                ? selected
                : new Dictionary<string, ProfileFolderSnapshot>(StringComparer.OrdinalIgnoreCase);

            try
            {
                progress?.Report(new OperationProgress(0, 1, rootPath, "切替内容を確認中"));
                ValidateTargetRoot(rootPath);
                ValidateTargetParent(rootPath);
                var currentHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var folderName in managedNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var targetPath = Path.Combine(rootPath, folderName);
                    if (File.Exists(targetPath))
                    {
                        errors.Add($"配置先に同名のファイルがあります: {targetPath}");
                        continue;
                    }

                    currentHashes[folderName] = Directory.Exists(targetPath) ? ExistingFolderMarker : "MISSING";
                }

                foreach (var snapshot in desired.Values)
                {
                    var snapshotPath = GetSnapshotAbsolutePath(profile, snapshot);
                    await treeService.ValidateSnapshotLayoutAsync(snapshotPath, cancellationToken);
                }

                var added = desired.Keys.Count(folder => !currentHashes.TryGetValue(folder, out var hash) || hash == "MISSING");
                var removed = managedNames.Count(folder => !desired.ContainsKey(folder)
                    && currentHashes.TryGetValue(folder, out var hash)
                    && hash != "MISSING");
                var replaced = desired.Keys.Count(folder => currentHashes.TryGetValue(folder, out var hash)
                    && hash != "MISSING");
                plans.Add(new FolderSetGroupPlan
                {
                    TargetRootPath = rootPath,
                    ManagedFolderNames = managedNames,
                    DesiredFolders = desired.Values.OrderBy(value => value.FolderName, StringComparer.OrdinalIgnoreCase).ToArray(),
                    CurrentHashes = currentHashes,
                    AddedFolderCount = added,
                    RemovedFolderCount = removed,
                    ReplacedFolderCount = replaced
                });
                progress?.Report(new OperationProgress(1, 1, rootPath, "切替内容の確認完了"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                errors.Add($"{rootPath}: {exception.Message}");
            }
        }

        return new FolderSetSwitchPlan
        {
            Profile = profile,
            Groups = plans,
            ValidationErrors = errors
        };
    }

    public async Task<OperationResult> ExecuteAsync(
        FolderSetSwitchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!plan.IsValid)
        {
            return new OperationResult(false, "切替前の検証に失敗しました。", Errors: plan.ValidationErrors);
        }

        FolderSetSwitchManifest? manifest = null;
        string? runDirectory = null;
        string? manifestPath = null;
        var oldPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnsureBackupWritable();
            var currentHashes = await VerifyPlanIsUnchangedAsync(plan, cancellationToken);

            var runId = Guid.NewGuid().ToString("N");
            runDirectory = CreateRunDirectory(runId, DateTimeOffset.Now);
            manifestPath = Path.Combine(runDirectory, "manifest.json");
            manifest = new FolderSetSwitchManifest
            {
                SchemaVersion = 2,
                RunId = runId,
                CreatedAt = DateTimeOffset.Now,
                OperationKind = FolderSwitchOperationKind.ProfileSwitch,
                Status = FolderSwitchStatus.Prepared,
                ProfileId = plan.Profile.Id,
                ProfileName = plan.Profile.Name
            };

            var entries = FlattenEntries(plan, currentHashes);
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = entries[index];
                progress?.Report(new OperationProgress(index, entries.Count, item.TargetPath, "切替前バックアップ中"));
                var backupRelative = Path.Combine("folders", index.ToString("D4"), item.FolderName);
                if (item.BeforeExisted)
                {
                    await CopyDirectoryAsync(item.TargetPath, Path.Combine(runDirectory, backupRelative), cancellationToken);
                }

                manifest.Entries.Add(new FolderSetSwitchManifestEntry
                {
                    TargetRootPath = item.TargetRootPath,
                    FolderName = item.FolderName,
                    TargetPath = item.TargetPath,
                    BackupRelativePath = backupRelative,
                    BeforeExisted = item.BeforeExisted,
                    DesiredExisted = item.Desired is not null,
                    BeforeTreeHash = item.BeforeHash,
                    AfterTreeHash = item.Desired?.TreeHash ?? "MISSING"
                });
            }

            await SaveManifestAsync(manifestPath, manifest, cancellationToken);
            manifest.Status = FolderSwitchStatus.InProgress;
            await SaveManifestAsync(manifestPath, manifest, cancellationToken);

            foreach (var entry in manifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.DesiredExisted) continue;
                var desired = entries.First(item => string.Equals(item.TargetPath, entry.TargetPath, StringComparison.OrdinalIgnoreCase)).Desired!;
                var stagePath = CreateUniqueSiblingPath(entry.TargetPath, $".configreplace-stage-{runId}");
                stagePaths[entry.TargetPath] = stagePath;
                    await treeService.CopySnapshotContentAsync(GetSnapshotAbsolutePath(plan.Profile, desired), stagePath, null, cancellationToken);
            }

            for (var index = 0; index < manifest.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = manifest.Entries[index];
                progress?.Report(new OperationProgress(index, manifest.Entries.Count, entry.TargetPath, "フォルダーを切替中"));
                await EnsureCurrentTreeHashAsync(entry.TargetPath, entry.BeforeTreeHash, cancellationToken);
                var oldPath = CreateUniqueSiblingPath(entry.TargetPath, $".configreplace-old-{runId}");
                oldPaths[entry.TargetPath] = oldPath;
                if (Directory.Exists(entry.TargetPath)) Directory.Move(entry.TargetPath, oldPath);
                if (entry.DesiredExisted)
                {
                    Directory.Move(stagePaths[entry.TargetPath], entry.TargetPath);
                }

                entry.Applied = true;
                await SaveManifestAsync(manifestPath, manifest, cancellationToken);
            }

            manifest.Status = FolderSwitchStatus.Completed;
            await SaveManifestAsync(manifestPath, manifest, cancellationToken);
            foreach (var oldPath in oldPaths.Values) TryDeleteDirectory(oldPath);
            foreach (var stagePath in stagePaths.Values) TryDeleteDirectory(stagePath);
            progress?.Report(new OperationProgress(manifest.Entries.Count, manifest.Entries.Count, string.Empty, "プロファイル切替完了"));
            return new OperationResult(true, $"プロファイル「{plan.Profile.Name}」へ切り替えました。", manifestPath);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            var rollbackErrors = await RollbackAsync(manifest, manifestPath, oldPaths, stagePaths, exception);
            var message = rollbackErrors.Count == 0
                ? exception is OperationCanceledException
                    ? "切替をキャンセルしました。変更済みフォルダーは元に戻しました。"
                    : $"切替に失敗したため、変更済みフォルダーを元に戻しました: {exception.Message}"
                : $"切替とロールバックに失敗しました。バックアップを確認してください: {runDirectory}";
            return new OperationResult(false, message, manifestPath, rollbackErrors);
        }
    }

    public async Task<IReadOnlyList<FolderSetHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_backupRoot)) return [];
        var items = new List<FolderSetHistoryItem>();
        foreach (var directory in Directory.EnumerateDirectories(_backupRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
            {
                items.Add(InvalidHistory(path, directory, "manifest.jsonがありません。"));
                continue;
            }

            try
            {
                var manifest = await LoadManifestAsync(path, cancellationToken);
                if (manifest.SchemaVersion < 2 || manifest.Entries.Count == 0)
                {
                    items.Add(InvalidHistory(path, directory, "旧形式の履歴、またはフォルダー履歴ではありません。"));
                    continue;
                }

                items.Add(new FolderSetHistoryItem
                {
                    ManifestPath = path,
                    CreatedAt = manifest.CreatedAt,
                    ProfileName = manifest.ProfileName ?? string.Empty,
                    OperationKind = manifest.OperationKind,
                    Status = manifest.Status,
                    FolderCount = manifest.Entries.Count,
                    CanRestore = manifest.Status == FolderSwitchStatus.Completed,
                    ValidationMessage = manifest.Status == FolderSwitchStatus.Completed
                        ? "復元時に現在の配置先とバックアップを再検証します。"
                        : string.Empty
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                items.Add(InvalidHistory(path, directory, $"履歴を読み取れません: {exception.Message}"));
            }
        }

        return items.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<OperationResult> RestoreAsync(
        FolderSetHistoryItem history,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!history.CanRestore) return new OperationResult(false, "この履歴は復元できません。", Errors: [history.ValidationMessage]);

        FolderSetSwitchManifest? restoreManifest = null;
        string? restoreDirectory = null;
        string? restoreManifestPath = null;
        var oldPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sourceManifest = await LoadManifestAsync(history.ManifestPath, cancellationToken);
            if (sourceManifest.Status != FolderSwitchStatus.Completed || sourceManifest.SchemaVersion < 2)
            {
                return new OperationResult(false, "完了した新形式の履歴だけを復元できます。");
            }

            var sourceDirectory = Path.GetDirectoryName(history.ManifestPath)!;
            var conflicts = new List<string>();
            foreach (var entry in sourceManifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureCurrentTreeHashAsync(entry.TargetPath, entry.AfterTreeHash, cancellationToken, conflicts);
                if (entry.BeforeExisted)
                {
                    var backupPath = GetBackupPath(sourceDirectory, entry.BackupRelativePath);
                    if (!Directory.Exists(backupPath))
                    {
                        conflicts.Add($"バックアップがありません: {entry.TargetPath}");
                    }
                    else
                    {
                        var backupTree = await treeService.ScanAsync(backupPath, cancellationToken);
                        if (!string.Equals(backupTree.TreeHash, entry.BeforeTreeHash, StringComparison.Ordinal))
                        {
                            conflicts.Add($"バックアップが破損しています: {entry.TargetPath}");
                        }
                    }
                }
            }

            if (conflicts.Count > 0)
            {
                return new OperationResult(false, "外部変更または不足ファイルがあるため、復元を中止しました。", history.ManifestPath, conflicts);
            }

            EnsureBackupWritable();
            var runId = Guid.NewGuid().ToString("N");
            restoreDirectory = CreateRunDirectory(runId, DateTimeOffset.Now);
            restoreManifestPath = Path.Combine(restoreDirectory, "manifest.json");
            restoreManifest = new FolderSetSwitchManifest
            {
                SchemaVersion = 2,
                RunId = runId,
                CreatedAt = DateTimeOffset.Now,
                OperationKind = FolderSwitchOperationKind.Restore,
                Status = FolderSwitchStatus.Prepared,
                ProfileName = sourceManifest.ProfileName,
                SourceManifestPath = history.ManifestPath
            };

            for (var index = 0; index < sourceManifest.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceEntry = sourceManifest.Entries[index];
                var current = await treeService.ScanAsync(sourceEntry.TargetPath, cancellationToken);
                var backupRelative = Path.Combine("folders", index.ToString("D4"), sourceEntry.FolderName);
                if (current.Exists)
                {
                    await CopyDirectoryAsync(sourceEntry.TargetPath, Path.Combine(restoreDirectory, backupRelative), cancellationToken);
                }

                restoreManifest.Entries.Add(new FolderSetSwitchManifestEntry
                {
                    TargetRootPath = sourceEntry.TargetRootPath,
                    FolderName = sourceEntry.FolderName,
                    TargetPath = sourceEntry.TargetPath,
                    BackupRelativePath = backupRelative,
                    BeforeExisted = current.Exists,
                    DesiredExisted = sourceEntry.BeforeExisted,
                    BeforeTreeHash = current.TreeHash,
                    AfterTreeHash = sourceEntry.BeforeTreeHash
                });
            }

            await SaveManifestAsync(restoreManifestPath, restoreManifest, cancellationToken);
            restoreManifest.Status = FolderSwitchStatus.InProgress;
            await SaveManifestAsync(restoreManifestPath, restoreManifest, cancellationToken);

            for (var index = 0; index < sourceManifest.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceEntry = sourceManifest.Entries[index];
                if (!sourceEntry.BeforeExisted) continue;
                var stagePath = CreateUniqueSiblingPath(sourceEntry.TargetPath, $".configreplace-stage-{runId}");
                stagePaths[sourceEntry.TargetPath] = stagePath;
                await CopyDirectoryAsync(GetBackupPath(sourceDirectory, sourceEntry.BackupRelativePath), stagePath, cancellationToken);
            }

            for (var index = 0; index < restoreManifest.Entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = restoreManifest.Entries[index];
                progress?.Report(new OperationProgress(index, restoreManifest.Entries.Count, entry.TargetPath, "履歴を復元中"));
                await EnsureCurrentTreeHashAsync(entry.TargetPath, entry.BeforeTreeHash, cancellationToken);
                var oldPath = CreateUniqueSiblingPath(entry.TargetPath, $".configreplace-old-{runId}");
                oldPaths[entry.TargetPath] = oldPath;
                if (Directory.Exists(entry.TargetPath)) Directory.Move(entry.TargetPath, oldPath);
                if (entry.DesiredExisted) Directory.Move(stagePaths[entry.TargetPath], entry.TargetPath);
                entry.Applied = true;
                await SaveManifestAsync(restoreManifestPath, restoreManifest, cancellationToken);
            }

            restoreManifest.Status = FolderSwitchStatus.Completed;
            await SaveManifestAsync(restoreManifestPath, restoreManifest, cancellationToken);
            foreach (var oldPath in oldPaths.Values) TryDeleteDirectory(oldPath);
            foreach (var stagePath in stagePaths.Values) TryDeleteDirectory(stagePath);
            return new OperationResult(true, "履歴からフォルダーを復元しました。", restoreManifestPath);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            var rollbackErrors = await RollbackAsync(restoreManifest, restoreManifestPath, oldPaths, stagePaths, exception);
            return new OperationResult(false,
                rollbackErrors.Count == 0
                    ? $"復元に失敗したため、変更済みフォルダーを元に戻しました: {exception.Message}"
                    : $"復元とロールバックに失敗しました。バックアップを確認してください: {restoreDirectory}",
                restoreManifestPath,
                rollbackErrors);
        }
    }

    public async Task<ActiveProfileState> DetectActiveProfileAsync(
        ProfilesDocument document,
        CancellationToken cancellationToken = default)
    {
        if (document.Profiles.Count == 0)
        {
            return new ActiveProfileState { Message = "プロファイルがありません。［新規］から作成してください。" };
        }

        var managedByRoot = BuildManagedFolderNames(document, null);
        var currentByRoot = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootEntry in managedByRoot)
        {
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folderName in rootEntry.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tree = await treeService.ScanAsync(Path.Combine(rootEntry.Key, folderName), cancellationToken);
                current[folderName] = tree.TreeHash;
            }
            currentByRoot[rootEntry.Key] = current;
        }

        foreach (var profile in document.Profiles)
        {
            var desiredByRoot = NormalizeProfileGroups(profile, null);
            var matched = true;
            foreach (var rootEntry in managedByRoot)
            {
                var desired = desiredByRoot.TryGetValue(rootEntry.Key, out var values)
                    ? values
                    : new Dictionary<string, ProfileFolderSnapshot>(StringComparer.OrdinalIgnoreCase);
                foreach (var folderName in rootEntry.Value)
                {
                    var currentHash = currentByRoot[rootEntry.Key][folderName];
                    var expected = desired.TryGetValue(folderName, out var snapshot) ? snapshot.TreeHash : "MISSING";
                    if (!string.Equals(currentHash, expected, StringComparison.Ordinal))
                    {
                        matched = false;
                        break;
                    }
                }
                if (!matched) break;
            }

            if (matched)
            {
                return new ActiveProfileState { Profile = profile, Message = $"現在のプロファイル: {profile.Name}" };
            }
        }

        return new ActiveProfileState
        {
            IsModified = true,
            Message = "現在の配置先は登録済みプロファイルと一致しません。"
        };
    }

    private async Task<Dictionary<string, string>> VerifyPlanIsUnchangedAsync(FolderSetSwitchPlan plan, CancellationToken cancellationToken)
    {
        var currentHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.Groups)
        {
            foreach (var folderName in group.ManagedFolderNames)
            {
                var targetPath = Path.Combine(group.TargetRootPath, folderName);
                var tree = await treeService.ScanAsync(targetPath, cancellationToken);
                currentHashes[targetPath] = tree.TreeHash;
                var expected = group.CurrentHashes[folderName];
                if (string.Equals(expected, "MISSING", StringComparison.Ordinal))
                {
                    if (tree.Exists) throw new InvalidOperationException($"プレビュー後に配置先が変更されました: {targetPath}");
                }
                else if (string.Equals(expected, ExistingFolderMarker, StringComparison.Ordinal))
                {
                    if (!tree.Exists) throw new InvalidOperationException($"プレビュー後に配置先が変更されました: {targetPath}");
                }
                else if (!string.Equals(tree.TreeHash, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"プレビュー後に配置先が変更されました: {targetPath}");
                }
            }

            foreach (var snapshot in group.DesiredFolders)
            {
                await treeService.LoadAndValidateSnapshotAsync(GetSnapshotAbsolutePath(plan.Profile, snapshot), cancellationToken);
            }
        }

        return currentHashes;
    }

    private Dictionary<string, Dictionary<string, ProfileFolderSnapshot>> NormalizeProfileGroups(FolderProfile profile, List<string>? errors)
    {
        var result = new Dictionary<string, Dictionary<string, ProfileFolderSnapshot>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in profile.Groups)
        {
            string root;
            try { root = Path.GetFullPath(group.TargetRootPath); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                errors?.Add($"プロファイル「{profile.Name}」の配置先が不正です: {exception.Message}");
                continue;
            }

            if (!result.TryGetValue(root, out var folders))
            {
                folders = new Dictionary<string, ProfileFolderSnapshot>(StringComparer.OrdinalIgnoreCase);
                result[root] = folders;
            }

            foreach (var snapshot in group.Folders)
            {
                if (!IsSafeFolderName(snapshot.FolderName))
                {
                    errors?.Add($"フォルダー名が不正です: {snapshot.FolderName}");
                    continue;
                }
                if (!folders.TryAdd(snapshot.FolderName, snapshot))
                {
                    errors?.Add($"同じ配置先に同名フォルダーが重複しています: {Path.Combine(root, snapshot.FolderName)}");
                }
            }
        }

        return result;
    }

    private Dictionary<string, HashSet<string>> BuildManagedFolderNames(ProfilesDocument document, List<string>? errors)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in document.Profiles)
        {
            foreach (var group in NormalizeProfileGroups(profile, errors))
            {
                if (!result.TryGetValue(group.Key, out var names))
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[group.Key] = names;
                }
                foreach (var folderName in group.Value.Keys) names.Add(folderName);
            }
        }
        return result;
    }

    private List<PlanEntry> FlattenEntries(FolderSetSwitchPlan plan, IReadOnlyDictionary<string, string> currentHashes)
    {
        var result = new List<PlanEntry>();
        foreach (var group in plan.Groups)
        {
            var desired = group.DesiredFolders.ToDictionary(value => value.FolderName, StringComparer.OrdinalIgnoreCase);
            foreach (var folderName in group.ManagedFolderNames)
            {
                var targetPath = Path.Combine(group.TargetRootPath, folderName);
                var hash = currentHashes.TryGetValue(targetPath, out var current) ? current : "MISSING";
                result.Add(new PlanEntry
                {
                    TargetRootPath = group.TargetRootPath,
                    FolderName = folderName,
                    TargetPath = targetPath,
                    BeforeHash = hash,
                    BeforeExisted = hash != "MISSING",
                    Desired = desired.TryGetValue(folderName, out var snapshot) ? snapshot : null
                });
            }
        }
        return result;
    }

    private string GetSnapshotAbsolutePath(FolderProfile profile, ProfileFolderSnapshot snapshot)
        => profileStore.GetSnapshotPath(profile, snapshot.Id);

    private async Task EnsureCurrentTreeHashAsync(string path, string expectedHash, CancellationToken cancellationToken, List<string>? conflicts = null)
    {
        var tree = await treeService.ScanAsync(path, cancellationToken);
        if (string.Equals(tree.TreeHash, expectedHash, StringComparison.Ordinal)) return;
        if (conflicts is null) throw new InvalidOperationException($"プレビュー後に配置先が変更されました: {path}");
        conflicts.Add($"履歴作成後に変更されています: {path}");
    }

    private async Task<List<string>> RollbackAsync(
        FolderSetSwitchManifest? manifest,
        string? manifestPath,
        IReadOnlyDictionary<string, string> oldPaths,
        IReadOnlyDictionary<string, string> stagePaths,
        Exception exception)
    {
        var errors = new List<string>();
        if (manifest is not null)
        {
            foreach (var entry in manifest.Entries.AsEnumerable().Reverse())
            {
                try
                {
                    if (oldPaths.TryGetValue(entry.TargetPath, out var oldPath) && Directory.Exists(oldPath))
                    {
                        TryDeleteDirectory(entry.TargetPath);
                        Directory.Move(oldPath, entry.TargetPath);
                    }
                    else if (entry.Applied)
                    {
                        TryDeleteDirectory(entry.TargetPath);
                    }
                }
                catch (Exception rollbackException)
                {
                    errors.Add($"{entry.TargetPath}: {rollbackException.Message}");
                }
            }
            manifest.ErrorMessage = exception.Message;
            manifest.Status = errors.Count == 0
                ? manifest.Entries.Any(entry => entry.Applied) ? FolderSwitchStatus.RolledBack : FolderSwitchStatus.Failed
                : FolderSwitchStatus.RollbackFailed;
            if (manifestPath is not null)
            {
                try { await SaveManifestAsync(manifestPath, manifest, CancellationToken.None); }
                catch (Exception saveException) { errors.Add($"履歴保存: {saveException.Message}"); }
            }
        }

        foreach (var stagePath in stagePaths.Values) TryDeleteDirectory(stagePath);
        return errors;
    }

    private async Task CopyDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var tree = await treeService.ScanAsync(source, cancellationToken);
        if (!tree.Exists) throw new DirectoryNotFoundException($"フォルダーがありません: {source}");
        Directory.CreateDirectory(destination);
        foreach (var directory in tree.Directories) Directory.CreateDirectory(Path.Combine(destination, directory));
        foreach (var file in tree.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(source, file.RelativePath), target, true);
            File.SetAttributes(target, file.Attributes);
        }
    }

    private async Task<List<string>> ValidateManifestBackupsAsync(FolderSetSwitchManifest manifest, string runDirectory, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        foreach (var entry in manifest.Entries)
        {
            if (!entry.BeforeExisted) continue;
            var path = GetBackupPath(runDirectory, entry.BackupRelativePath);
            if (!Directory.Exists(path))
            {
                errors.Add($"バックアップがありません: {entry.TargetPath}");
                continue;
            }
            var tree = await treeService.ScanAsync(path, cancellationToken);
            if (!string.Equals(tree.TreeHash, entry.BeforeTreeHash, StringComparison.Ordinal)) errors.Add($"バックアップが破損しています: {entry.TargetPath}");
        }
        return errors;
    }

    private string CreateRunDirectory(string runId, DateTimeOffset createdAt)
    {
        Directory.CreateDirectory(_backupRoot);
        var directory = Path.Combine(_backupRoot, $"{createdAt:yyyyMMdd-HHmmss}-{runId[..8]}");
        Directory.CreateDirectory(Path.Combine(directory, "folders"));
        return directory;
    }

    private void EnsureBackupWritable()
    {
        Directory.CreateDirectory(_backupRoot);
        var probe = Path.Combine(_backupRoot, $".write-test-{Guid.NewGuid():N}.tmp");
        try { using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.WriteByte(0); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    private async Task SaveManifestAsync(string path, FolderSetSwitchManifest manifest, CancellationToken cancellationToken)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch { } } }
    }

    private static async Task<FolderSetSwitchManifest> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await JsonSerializer.DeserializeAsync<FolderSetSwitchManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("履歴が空です。");
    }

    private string GetBackupPath(string runDirectory, string relativePath)
    {
        var root = Path.GetFullPath(runDirectory);
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidDataException("履歴のバックアップパスが空です。");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || !FileSystemUtilities.IsSameOrChildPath(path, root)) throw new InvalidDataException("履歴のバックアップパスが不正です。");
        return path;
    }

    private void ValidateTargetRoot(string rootPath)
    {
        if (IsInternalPath(rootPath)) throw new InvalidDataException("ProfilesまたはBackups配下は配置先にできません。");
        if (File.Exists(rootPath)) throw new InvalidDataException($"配置先がファイルです: {rootPath}");
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

        if (!HasSafeExistingParent(normalized)) throw new InvalidDataException("配置先の親フォルダーを安全に確認できません。");
        if (!Directory.Exists(parent))
        {
            try { Directory.CreateDirectory(parent); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new IOException("配置先の親フォルダーを作成できません。", exception); }
        }
    }

    private bool IsInternalPath(string path)
        => FileSystemUtilities.IsSameOrChildPath(path, profileStore.ProfilesRoot)
            || FileSystemUtilities.IsSameOrChildPath(path, _backupRoot);

    private static bool IsSafeFolderName(string value)
        => !string.IsNullOrWhiteSpace(value)
            && value is not "." and not ".."
            && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !value.Contains(Path.DirectorySeparatorChar)
            && !value.Contains(Path.AltDirectorySeparatorChar);

    private static bool HasSafeExistingParent(string targetPath)
    {
        var current = Directory.GetParent(targetPath)?.FullName;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current)) current = Directory.GetParent(current)?.FullName;
        while (!string.IsNullOrEmpty(current))
        {
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return true;
    }

    private static bool HasSafeDirectory(string path)
    {
        try
        {
            return !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string CreateUniqueSiblingPath(string targetPath, string suffix) => targetPath + suffix;

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch { }
    }

    private static FolderSetHistoryItem InvalidHistory(string path, string directory, string message)
        => new()
        {
            ManifestPath = path,
            CreatedAt = Directory.Exists(directory) ? Directory.GetCreationTimeUtc(directory) : DateTimeOffset.MinValue,
            Status = FolderSwitchStatus.Failed,
            CanRestore = false,
            ValidationMessage = message
        };

    private sealed class PlanEntry
    {
        public required string TargetRootPath { get; init; }
        public required string FolderName { get; init; }
        public required string TargetPath { get; init; }
        public required string BeforeHash { get; init; }
        public bool BeforeExisted { get; init; }
        public ProfileFolderSnapshot? Desired { get; init; }
    }
}
