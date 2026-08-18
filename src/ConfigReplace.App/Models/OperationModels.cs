namespace ConfigReplace.Models;

public sealed record OperationProgress(int Processed, int Total, string CurrentFile, string Phase)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Round(Processed * 100d / Total);
}

public sealed record OperationResult(
    bool Success,
    string Message,
    string? ManifestPath = null,
    IReadOnlyList<string>? Errors = null);
