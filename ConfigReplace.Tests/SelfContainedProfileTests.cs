namespace ConfigReplace.Tests;

[TestClass]
public sealed class SelfContainedProfileTests
{
    [TestMethod]
    public async Task SelfContainedSnapshot_DoesNotDependOnDroppedFolder()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "Desktop", "TestFolder");
        Directory.CreateDirectory(Path.Combine(source, "subfolder"));
        await File.WriteAllTextAsync(Path.Combine(source, "file1.txt"), "original");
        await File.WriteAllTextAsync(Path.Combine(source, "subfolder", "file2.ini"), "value=1");
        var snapshot = Path.Combine(workspace.Root, "Profiles", "profile-a", "snapshot-a");
        var service = new FolderTreeService();

        var manifest = await service.CaptureSelfContainedAsync(source, snapshot);

        Assert.AreEqual(string.Empty, manifest.SourcePath);
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(snapshot, "snapshot.json"));
        Assert.IsFalse(manifestJson.Contains(source, StringComparison.OrdinalIgnoreCase));

        Directory.Delete(Path.Combine(workspace.Root, "Desktop"), true);
        var deployed = Path.Combine(workspace.Target, "TestFolder");
        await service.CopySnapshotContentAsync(snapshot, deployed);

        Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Combine(deployed, "file1.txt")));
        Assert.AreEqual("value=1", await File.ReadAllTextAsync(Path.Combine(deployed, "subfolder", "file2.ini")));
    }

    [TestMethod]
    public async Task RemoveSourceMetadata_ClearsLegacyManifestPath()
    {
        using var workspace = new TemporaryWorkspace();
        var source = Path.Combine(workspace.Root, "legacy-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "config.ini"), "legacy");
        var snapshot = Path.Combine(workspace.Root, "Profiles", "profile-a", "snapshot-a");
        var service = new FolderTreeService();
        var legacy = await service.CaptureAsync(source, snapshot);
        Assert.AreEqual(Path.GetFullPath(source), legacy.SourcePath);

        var changed = await service.RemoveSourceMetadataAsync(snapshot);
        var sanitized = await service.LoadAndValidateSnapshotAsync(snapshot);

        Assert.IsTrue(changed);
        Assert.AreEqual(string.Empty, sanitized.SourcePath);
        Assert.IsFalse(await service.RemoveSourceMetadataAsync(snapshot));
    }

    [TestMethod]
    public void DeleteDirectoryTree_RemovesReadOnlyProfileData()
    {
        using var workspace = new TemporaryWorkspace();
        var profileDirectory = Path.Combine(workspace.Root, "Profiles", "profile-a", "snapshot-a", "content");
        Directory.CreateDirectory(profileDirectory);
        var file = Path.Combine(profileDirectory, "readonly.ini");
        File.WriteAllText(file, "value=1");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        FileSystemUtilities.DeleteDirectoryTree(Path.Combine(workspace.Root, "Profiles", "profile-a"));

        Assert.IsFalse(Directory.Exists(Path.Combine(workspace.Root, "Profiles", "profile-a")));
    }
}
