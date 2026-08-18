namespace ConfigReplace.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Root = Path.Combine(Path.GetTempPath(), "ConfigReplace.Tests", Guid.NewGuid().ToString("N"));
        Target = Path.Combine(Root, "target");
        Backups = Path.Combine(Root, "app", "Backups");
        Directory.CreateDirectory(Target);
    }

    public string Root { get; }
    public string Target { get; }
    public string Backups { get; }

    public string CreateTargetFile(string relativePath, byte[] content)
    {
        var path = Path.Combine(Target, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, true);
    }
}
