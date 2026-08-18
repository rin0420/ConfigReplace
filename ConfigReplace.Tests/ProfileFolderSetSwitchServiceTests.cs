using ConfigReplace.Models;
using ConfigReplace.Services;

namespace ConfigReplace.Tests;

[TestClass]
public sealed class ProfileFolderSetSwitchServiceTests
{
    [TestMethod]
    public async Task SwitchingProfilesReplacesTheWholeManagedFolderSet()
    {
        using var workspace = new TemporaryWorkspace();
        var targetRoot = Path.Combine(workspace.Root, "target-root");
        var sourcesRoot = Path.Combine(workspace.Root, "sources");
        Directory.CreateDirectory(targetRoot);
        var profile1 = new FolderProfile { Id = "profile-one", Name = "接続先1" };
        var profile2 = new FolderProfile { Id = "profile-two", Name = "接続先2" };
        var store = new ProfileStore(Path.Combine(workspace.Root, "app", "Profiles"));
        var tree = new FolderTreeService();

        await CreateFolderAsync(Path.Combine(targetRoot, "A"), "A現在");
        await CreateFolderAsync(Path.Combine(targetRoot, "B"), "B現在");
        var p1Inputs = await CaptureAsync(store, tree, profile1, sourcesRoot, targetRoot, ("A", "A1"), ("B", "B1"));
        var p2Inputs = await CaptureAsync(store, tree, profile2, sourcesRoot, targetRoot, ("C", "C2"), ("D", "D2"));
        profile1.Groups = p1Inputs;
        profile2.Groups = p2Inputs;
        var document = new ProfilesDocument { SchemaVersion = 2, Profiles = [profile1, profile2] };
        await store.SaveAsync(document);
        var service = new ProfileFolderSetSwitchService(store, tree, workspace.Backups);

        var firstPlan = await service.CreatePlanAsync(profile1, document);
        Assert.IsTrue(firstPlan.IsValid, string.Join(Environment.NewLine, firstPlan.ValidationErrors));
        Assert.IsTrue((await service.ExecuteAsync(firstPlan)).Success);
        Assert.AreEqual("A1", await File.ReadAllTextAsync(Path.Combine(targetRoot, "A", "value.txt")));
        Assert.AreEqual("B1", await File.ReadAllTextAsync(Path.Combine(targetRoot, "B", "value.txt")));

        var secondPlan = await service.CreatePlanAsync(profile2, document);
        Assert.IsTrue(secondPlan.IsValid, string.Join(Environment.NewLine, secondPlan.ValidationErrors));
        Assert.IsTrue((await service.ExecuteAsync(secondPlan)).Success);
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "A")));
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "B")));
        Assert.AreEqual("C2", await File.ReadAllTextAsync(Path.Combine(targetRoot, "C", "value.txt")));
        Assert.AreEqual("D2", await File.ReadAllTextAsync(Path.Combine(targetRoot, "D", "value.txt")));

        var history = await service.GetHistoryAsync();
        Assert.AreEqual(2, history.Count);
        Assert.IsTrue(history[0].CanRestore);
        var restore = await service.RestoreAsync(history[0]);
        Assert.IsTrue(restore.Success, restore.Message);
        Assert.AreEqual("A1", await File.ReadAllTextAsync(Path.Combine(targetRoot, "A", "value.txt")));
        Assert.AreEqual("B1", await File.ReadAllTextAsync(Path.Combine(targetRoot, "B", "value.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "C")));
        Assert.IsFalse(Directory.Exists(Path.Combine(targetRoot, "D")));
    }

    private static async Task<List<ProfileFolderGroup>> CaptureAsync(
        ProfileStore store,
        FolderTreeService tree,
        FolderProfile profile,
        string sourcesRoot,
        string targetRoot,
        params (string Name, string Value)[] folders)
    {
        var group = new ProfileFolderGroup { Id = Guid.NewGuid().ToString("N"), TargetRootPath = targetRoot };
        foreach (var (name, value) in folders)
        {
            var source = Path.Combine(sourcesRoot, profile.Id, name);
            await CreateFolderAsync(source, value);
            var id = Guid.NewGuid().ToString("N");
            var manifest = await tree.CaptureAsync(source, store.GetSnapshotPath(profile.Id, id));
            group.Folders.Add(new ProfileFolderSnapshot
            {
                Id = id,
                FolderName = name,
                SnapshotRelativePath = Path.Combine(profile.Id, id),
                SourcePath = source,
                TreeHash = manifest.TreeHash,
                FileCount = manifest.Files.Count,
                TotalBytes = manifest.Files.Sum(file => file.Length)
            });
        }
        return [group];
    }

    private static async Task CreateFolderAsync(string path, string value)
    {
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "value.txt"), value);
    }
}
