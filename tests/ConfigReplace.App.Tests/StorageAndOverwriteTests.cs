using ConfigReplace.Models;
using ConfigReplace.Services;
using Xunit;

namespace ConfigReplace.App.Tests;

public sealed class StorageAndOverwriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ConfigReplaceTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProfileFolderUsesSimpleNameAndPreservesEmptyDirectories()
    {
        var source = Path.Combine(_root, "source");
        var profiles = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(Path.Combine(source, "empty", "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "app.json"), "{\"version\":1}");
        await File.WriteAllTextAsync(Path.Combine(source, "empty", "settings.txt"), "設定");

        var store = new ProfileStore(profiles);
        var profile = new FolderProfile { Id = "profile-id", Name = "開発" };
        var destination = store.GetProfileFolderPath(profile, "sample");
        var progress = new List<OperationProgress>();
        var summary = await new FolderTreeService().CopyDirectoryContentsAsync(
            source,
            destination,
            new Progress<OperationProgress>(progress.Add));

        Assert.Equal(2, summary.FileCount);
        Assert.True(Directory.Exists(Path.Combine(destination, "empty", "nested")));
        Assert.True(File.Exists(Path.Combine(destination, "app.json")));
        Assert.True(Directory.Exists(Path.Combine(profiles, "開発", "sample")));
        Assert.False(File.Exists(Path.Combine(profiles, "開発", "sample", "snapshot.json")));
        Assert.False(Directory.Exists(Path.Combine(profiles, "開発", "sample", "content")));
        Assert.Equal(100, progress[^1].Percent);
    }

    [Fact]
    public void StagedFoldersAreCommittedUnderProfileName()
    {
        var store = new ProfileStore(Path.Combine(_root, "Profiles"));
        var profile = new FolderProfile { Id = "profile-id", Name = "本番" };
        var stagingId = "profile-id.staging-test";
        var staged = store.GetProfileFolderPath(stagingId, "sample");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "settings.ini"), "new");

        store.CommitStagedFolders(profile, stagingId);

        Assert.Equal("new", File.ReadAllText(Path.Combine(store.GetProfileFolderPath(profile, "sample"), "settings.ini")));
        Assert.False(Directory.Exists(store.GetProfileDirectoryPath(stagingId)));
        Assert.False(File.Exists(Path.Combine(store.GetProfileDirectoryPath(profile.Name), "sample", "snapshot.json")));
    }

    [Fact]
    public void RenamingProfileMovesDirectFoldersWithoutSessionDirectories()
    {
        var store = new ProfileStore(Path.Combine(_root, "Profiles"));
        var oldProfile = new FolderProfile { Id = "profile-id", Name = "旧名" };
        var newProfile = new FolderProfile { Id = oldProfile.Id, Name = "新名" };
        var oldFolder = store.GetProfileFolderPath(oldProfile, "sample");
        Directory.CreateDirectory(oldFolder);
        File.WriteAllText(Path.Combine(oldFolder, "old.txt"), "old");

        var stagingId = "profile-id.staging-rename";
        var staged = store.GetProfileFolderPath(stagingId, "sample");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");

        store.CommitStagedFolders(newProfile, stagingId, oldProfile.Name);

        Assert.False(Directory.Exists(store.GetProfileDirectoryPath(oldProfile.Name)));
        Assert.True(File.Exists(Path.Combine(store.GetProfileFolderPath(newProfile, "sample"), "new.txt")));
        var storedFolders = Directory.EnumerateDirectories(store.GetProfileDirectoryPath(newProfile.Name))
            .Select(path => Path.GetFileName(path) ?? string.Empty)
            .ToArray();
        Assert.Equal(["sample"], storedFolders);
    }

    [Fact]
    public async Task OverwriteCopiesRegisteredContentAndLeavesUnregisteredContent()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var targetRoot = Path.Combine(_root, "target-root");
        var targetFolder = Path.Combine(targetRoot, "sample");
        var extraTargetFolder = Path.Combine(targetRoot, "do-not-touch");
        Directory.CreateDirectory(targetFolder);
        Directory.CreateDirectory(extraTargetFolder);
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "value.txt"), "before");
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "keep.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(extraTargetFolder, "untouched.txt"), "untouched");

        var store = new ProfileStore(profiles);
        var tree = new FolderTreeService();
        var profile = new FolderProfile { Id = "profile-id", Name = "切替" };
        var sourceFolder = store.GetProfileFolderPath(profile, "sample");
        Directory.CreateDirectory(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "value.txt"), "after");
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "new.txt"), "new");
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "group-id",
            TargetRootPath = targetRoot,
            Folders =
            [
                new ProfileFolderSnapshot
                {
                    Id = "sample",
                    FolderName = "sample",
                    SnapshotRelativePath = "sample",
                    TreeHash = string.Empty
                }
            ]
        });

        var service = new ProfileFolderSetSwitchService(store, tree);
        var plan = await service.CreatePlanAsync(profile, new ProfilesDocument { Profiles = [profile] });
        var result = await service.ExecuteAsync(plan);

        Assert.True(result.Success, result.Message);
        Assert.Equal("after", await File.ReadAllTextAsync(Path.Combine(targetFolder, "value.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(targetFolder, "new.txt")));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(targetFolder, "keep.txt")));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(extraTargetFolder, "untouched.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Backups")));
    }

    [Fact]
    public async Task LegacySnapshotContentIsFlattenedEvenWhenManifestIsBroken()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var store = new ProfileStore(profiles);
        var tree = new FolderTreeService();
        var profile = new FolderProfile { Id = "profile-id", Name = "旧形式" };
        var legacySnapshot = store.GetSnapshotPath(profile, "legacy-session");
        var legacyContent = Path.Combine(legacySnapshot, "content");
        Directory.CreateDirectory(Path.Combine(legacyContent, "empty", "nested"));
        await File.WriteAllTextAsync(Path.Combine(legacyContent, "settings.ini"), "legacy");
        await File.WriteAllTextAsync(Path.Combine(legacySnapshot, "snapshot.json"), "壊れたmanifest");
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "group-id",
            TargetRootPath = Path.Combine(_root, "target"),
            Folders =
            [
                new ProfileFolderSnapshot
                {
                    Id = "legacy-session",
                    FolderName = "sample",
                    SnapshotRelativePath = Path.Combine(profile.Name, "legacy-session"),
                    TreeHash = "old-hash"
                }
            ]
        });

        var changed = await new ProfileStorageMigrationService(store, tree)
            .MigrateAsync(new ProfilesDocument { Profiles = [profile] });
        var direct = store.GetProfileFolderPath(profile, "sample");

        Assert.True(changed);
        Assert.Equal("sample", profile.Groups[0].Folders[0].Id);
        Assert.Equal("sample", profile.Groups[0].Folders[0].SnapshotRelativePath);
        Assert.Equal(string.Empty, profile.Groups[0].Folders[0].TreeHash);
        Assert.Equal("legacy", await File.ReadAllTextAsync(Path.Combine(direct, "settings.ini")));
        Assert.True(Directory.Exists(Path.Combine(direct, "empty", "nested")));
        Assert.False(Directory.Exists(legacySnapshot));
        Assert.False(File.Exists(Path.Combine(direct, "snapshot.json")));
        Assert.False(Directory.Exists(Path.Combine(direct, "content")));
    }

    [Fact]
    public async Task OverwriteReplacesFileDirectoryConflictsAndReadOnlyFiles()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var targetRoot = Path.Combine(_root, "target-root");
        var targetFolder = Path.Combine(targetRoot, "sample");
        var sourceFolder = Path.Combine(profiles, "切替", "sample");
        Directory.CreateDirectory(Path.Combine(sourceFolder, "as-directory"));
        Directory.CreateDirectory(Path.Combine(targetFolder, "as-file"));
        Directory.CreateDirectory(targetFolder);
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "as-directory", "value.txt"), "directory");
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "as-file"), "file");
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "as-directory"), "old-file");
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "as-file", "old.txt"), "old-directory");
        File.SetAttributes(Path.Combine(targetFolder, "as-directory"), FileAttributes.ReadOnly);

        var store = new ProfileStore(profiles);
        var profile = new FolderProfile { Id = "profile-id", Name = "切替" };
        Directory.CreateDirectory(sourceFolder);
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "group-id",
            TargetRootPath = targetRoot,
            Folders = [CreateStoredFolder("sample")]
        });

        var service = new ProfileFolderSetSwitchService(store, new FolderTreeService());
        var plan = await service.CreatePlanAsync(profile, new ProfilesDocument { Profiles = [profile] });
        var result = await service.ExecuteAsync(plan);

        Assert.True(result.Success, result.Message);
        Assert.Equal("directory", await File.ReadAllTextAsync(Path.Combine(targetFolder, "as-directory", "value.txt")));
        Assert.Equal("file", await File.ReadAllTextAsync(Path.Combine(targetFolder, "as-file")));
        Assert.False(File.Exists(Path.Combine(targetFolder, "as-file", "old.txt")));
    }

    [Fact]
    public async Task PlanRejectsInternalTargetsAndDuplicateFolderNames()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var store = new ProfileStore(profiles);
        var profile = new FolderProfile { Id = "profile-id", Name = "検証" };
        Directory.CreateDirectory(store.GetProfileFolderPath(profile, "sample"));
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "valid",
            TargetRootPath = Path.Combine(_root, "valid-target"),
            Folders = [CreateStoredFolder("sample")]
        });
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "duplicate",
            TargetRootPath = Path.Combine(_root, "other-target"),
            Folders = [CreateStoredFolder("sample")]
        });
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "internal",
            TargetRootPath = profiles,
            Folders = [CreateStoredFolder("other")]
        });

        var plan = await new ProfileFolderSetSwitchService(store, new FolderTreeService())
            .CreatePlanAsync(profile, new ProfilesDocument { Profiles = [profile] });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.ValidationErrors, error => error.Contains("Profiles配下", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.ValidationErrors, error => error.Contains("同じフォルダー名", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VolumeRootIsAcceptedAsTargetRoot()
    {
        var profiles = Path.Combine(_root, "Profiles");
        var store = new ProfileStore(profiles);
        var profile = new FolderProfile { Id = "profile-id", Name = "ルート確認" };
        var sourceFolder = store.GetProfileFolderPath(profile, "ConfigReplaceTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceFolder);
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "file.txt"), "data");
        var folderName = Path.GetFileName(sourceFolder);
        profile.Groups.Add(new ProfileFolderGroup
        {
            Id = "group-id",
            TargetRootPath = Path.GetPathRoot(Environment.SystemDirectory)!,
            Folders =
            [
                new ProfileFolderSnapshot
                {
                    Id = folderName,
                    FolderName = folderName,
                    SnapshotRelativePath = folderName,
                    TreeHash = string.Empty
                }
            ]
        });

        var plan = await new ProfileFolderSetSwitchService(store, new FolderTreeService())
            .CreatePlanAsync(profile, new ProfilesDocument { Profiles = [profile] });

        Assert.True(plan.IsValid, string.Join(Environment.NewLine, plan.ValidationErrors));
    }

    private static ProfileFolderSnapshot CreateStoredFolder(string folderName)
        => new()
        {
            Id = folderName,
            FolderName = folderName,
            SnapshotRelativePath = folderName,
            TreeHash = string.Empty
        };

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
