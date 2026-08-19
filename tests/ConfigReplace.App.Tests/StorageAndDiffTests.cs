using System.Text.Json;
using System.Text.Json.Serialization;
using ConfigReplace.Models;
using ConfigReplace.Services;
using Xunit;

namespace ConfigReplace.App.Tests;

public sealed class StorageAndDiffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ConfigReplaceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CaptureStoresNamedProfileAndCanBeValidated()
    {
        var source = Path.Combine(_root, "source");
        var profiles = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        await File.WriteAllTextAsync(Path.Combine(source, "app.json"), "{\"version\":1}");
        await File.WriteAllTextAsync(Path.Combine(source, "sub", "settings.txt"), "設定");

        var store = new ProfileStore(profiles);
        var profile = new FolderProfile { Id = "profile-id", Name = "開発" };
        var snapshotPath = store.GetSnapshotPath(profile, "snapshot-id");
        var tree = new FolderTreeService();
        var manifest = await tree.CaptureSelfContainedAsync(source, snapshotPath);

        Assert.Equal(string.Empty, manifest.SourcePath);
        Assert.True(Directory.Exists(Path.Combine(profiles, "開発", "snapshot-id")));
        var loaded = await tree.LoadAndValidateSnapshotAsync(snapshotPath);
        Assert.Equal(manifest.TreeHash, loaded.TreeHash);
        Assert.Equal(2, loaded.Files.Count);
    }

    [Fact]
    public async Task CapturePreservesEmptyDirectoriesAndDoesNotReach100BeforeFinalization()
    {
        var source = Path.Combine(_root, "source-with-empty-directory");
        var snapshot = Path.Combine(_root, "snapshot");
        Directory.CreateDirectory(Path.Combine(source, "empty", "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "data");
        var progress = new List<OperationProgress>();
        var tree = new FolderTreeService();

        var manifest = await tree.CaptureSelfContainedAsync(
            source,
            snapshot,
            new Progress<OperationProgress>(progress.Add));

        var loaded = await tree.LoadAndValidateSnapshotAsync(snapshot);
        Assert.Equal(manifest.TreeHash, loaded.TreeHash);
        Assert.Contains("empty", loaded.Directories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("empty", "nested"), loaded.Directories, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(progress);
        Assert.All(progress.Take(progress.Count - 1), item => Assert.True(item.Percent < 100));
        Assert.Equal(100, progress[^1].Percent);
    }

    [Fact]
    public void StagedSnapshotIsCommittedUnderProfileName()
    {
        var store = new ProfileStore(Path.Combine(_root, "Profiles"));
        var profile = new FolderProfile { Id = "profile-id", Name = "本番" };
        var stagingId = "profile-id.staging-test";
        var staged = store.GetSnapshotPath(stagingId, "snapshot-id");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "snapshot.json"), "{}");

        store.CommitStagedSnapshots(profile, stagingId);

        Assert.True(File.Exists(Path.Combine(store.ProfilesRoot, "本番", "snapshot-id", "snapshot.json")));
        Assert.False(Directory.Exists(Path.Combine(store.ProfilesRoot, stagingId)));
    }

    [Fact]
    public void RenamingProfileMovesNamedSnapshotDirectoryWithoutMerging()
    {
        var store = new ProfileStore(Path.Combine(_root, "Profiles"));
        var oldProfile = new FolderProfile { Id = "profile-id", Name = "旧名" };
        var newProfile = new FolderProfile { Id = oldProfile.Id, Name = "新名" };
        var oldSnapshot = store.GetSnapshotPath(oldProfile, "snapshot-id");
        Directory.CreateDirectory(oldSnapshot);
        File.WriteAllText(Path.Combine(oldSnapshot, "snapshot.json"), "{}");

        var stagingId = "profile-id.staging-rename";
        var staged = store.GetSnapshotPath(stagingId, "new-snapshot");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "snapshot.json"), "{}");

        store.CommitStagedSnapshots(newProfile, stagingId, oldProfile.Name);

        Assert.False(Directory.Exists(store.GetProfileDirectoryPath(oldProfile.Name)));
        Assert.True(File.Exists(Path.Combine(store.GetProfileDirectoryPath(newProfile.Name), "snapshot-id", "snapshot.json")));
        Assert.True(File.Exists(Path.Combine(store.GetProfileDirectoryPath(newProfile.Name), "new-snapshot", "snapshot.json")));
    }

    [Fact]
    public async Task RemovingLegacySourceMetadataDoesNotRescanOrLockManifest()
    {
        var source = Path.Combine(_root, "legacy-source");
        var snapshot = Path.Combine(_root, "legacy-snapshot");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "data");
        var tree = new FolderTreeService();
        var captured = await tree.CaptureAsync(source, snapshot);

        Assert.Equal(Path.GetFullPath(source), captured.SourcePath);
        Assert.True(await tree.RemoveSourceMetadataAsync(snapshot));
        Assert.Equal(string.Empty, (await tree.LoadAndValidateSnapshotAsync(snapshot)).SourcePath);
    }

    [Fact]
    public async Task VolumeRootIsAcceptedAsTargetRoot()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var backups = Path.Combine(_root, "Backups");
        var store = new ProfileStore(profiles);
        var tree = new FolderTreeService();
        var profile = new FolderProfile { Id = "profile-id", Name = "ルート確認" };
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "data");
        var snapshotPath = store.GetSnapshotPath(profile, "snapshot-id");
        var manifest = await tree.CaptureSelfContainedAsync(source, snapshotPath);
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "group-id",
            TargetRootPath = Path.GetPathRoot(Environment.SystemDirectory)!,
            Folders =
            [
                new ProfileFolderSnapshot
                {
                    Id = "snapshot-id",
                    FolderName = "ConfigReplaceTest-" + Guid.NewGuid().ToString("N"),
                    SnapshotRelativePath = Path.Combine(profile.Name, "snapshot-id"),
                    TreeHash = manifest.TreeHash,
                    FileCount = manifest.Files.Count,
                    TotalBytes = manifest.Files.Sum(file => file.Length)
                }
            ]
        });

        var document = new ProfilesDocument { Profiles = [profile] };
        var service = new ProfileFolderSetSwitchService(store, tree, backups);
        var plan = await service.CreatePlanAsync(profile, document);

        Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.ValidationErrors));
    }

    [Fact]
    public async Task FastPreviewPlanIsRevalidatedBeforeSwitch()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var backups = Path.Combine(_root, "Backups");
        var targetRoot = Path.Combine(_root, "target-root");
        var targetFolder = Path.Combine(targetRoot, "sample");
        var source = Path.Combine(_root, "desired-source");
        Directory.CreateDirectory(targetFolder);
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "value.txt"), "before");
        await File.WriteAllTextAsync(Path.Combine(source, "value.txt"), "after");

        var store = new ProfileStore(profiles);
        var tree = new FolderTreeService();
        var profile = new FolderProfile { Id = "profile-id", Name = "切替" };
        var snapshotPath = store.GetSnapshotPath(profile, "snapshot-id");
        var manifest = await tree.CaptureSelfContainedAsync(source, snapshotPath);
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "group-id",
            TargetRootPath = targetRoot,
            Folders =
            [
                new ProfileFolderSnapshot
                {
                    Id = "snapshot-id",
                    FolderName = "sample",
                    SnapshotRelativePath = Path.Combine(profile.Name, "snapshot-id"),
                    TreeHash = manifest.TreeHash,
                    FileCount = manifest.Files.Count,
                    TotalBytes = manifest.Files.Sum(file => file.Length)
                }
            ]
        });

        var service = new ProfileFolderSetSwitchService(store, tree, backups);
        var plan = await service.CreatePlanAsync(profile, new ProfilesDocument { Profiles = [profile] });
        var result = await service.ExecuteAsync(plan);

        Assert.True(result.Success, result.Message);
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(targetFolder, "value.txt")));
    }

    [Fact]
    public async Task HistoryDiffListsAddedRemovedAndModifiedFiles()
    {
        var backupRoot = Path.Combine(_root, "Backups");
        var runDirectory = Path.Combine(backupRoot, "run");
        var backupFolder = Path.Combine(runDirectory, "folders", "0000", "sample");
        var targetFolder = Path.Combine(_root, "target", "sample");
        Directory.CreateDirectory(backupFolder);
        Directory.CreateDirectory(targetFolder);
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "changed.txt"), "before");
        await File.WriteAllTextAsync(Path.Combine(backupFolder, "removed.txt"), "removed");
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "changed.txt"), "after");
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "added.txt"), "added");

        var manifest = new FolderSetSwitchManifest
        {
            RunId = "run-id",
            OperationKind = FolderSwitchOperationKind.ProfileSwitch,
            Status = FolderSwitchStatus.Completed,
            ProfileName = "テスト",
            Entries =
            [
                new FolderSetSwitchManifestEntry
                {
                    TargetRootPath = Path.GetDirectoryName(targetFolder)!,
                    FolderName = "sample",
                    TargetPath = targetFolder,
                    BackupRelativePath = Path.Combine("folders", "0000", "sample"),
                    BeforeExisted = true,
                    DesiredExisted = true,
                    BeforeTreeHash = "before",
                    AfterTreeHash = "after"
                }
            ]
        };
        Directory.CreateDirectory(runDirectory);
        await using (var stream = File.Create(Path.Combine(runDirectory, "manifest.json")))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            });
        }

        var history = new FolderSetHistoryItem
        {
            ManifestPath = Path.Combine(runDirectory, "manifest.json"),
            CreatedAt = DateTimeOffset.Now,
            ProfileName = "テスト",
            OperationKind = FolderSwitchOperationKind.ProfileSwitch,
            Status = FolderSwitchStatus.Completed,
            FolderCount = 1,
            CanRestore = true
        };
        var service = new HistoryDiffService(new FolderTreeService(), backupRoot);
        var folders = await service.GetFoldersAsync(history);
        var diff = await service.CompareFolderAsync(folders[0]);

        Assert.Equal(3, diff.ChangedFileCount);
        Assert.Equal(HistoryFileChangeKind.Modified, diff.Files.Single(file => file.RelativePath == "changed.txt").ChangeKind);
        Assert.Equal(HistoryFileChangeKind.Removed, diff.Files.Single(file => file.RelativePath == "removed.txt").ChangeKind);
        Assert.Equal(HistoryFileChangeKind.Added, diff.Files.Single(file => file.RelativePath == "added.txt").ChangeKind);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) FileSystemUtilities.DeleteDirectoryTree(_root);
        }
        catch
        {
            // テスト後の一時ファイル削除失敗は、次回の一意なルート利用を妨げません。
        }
    }
}
