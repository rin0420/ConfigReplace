namespace ConfigReplace.Models;

public sealed class HistoryFolderComparison
{
    public required int EntryIndex { get; init; }
    public required string FolderName { get; init; }
    public required string TargetPath { get; init; }
    public string? BackupPath { get; init; }
    public bool BeforeExisted { get; init; }
    public bool CurrentExists { get; init; }

    public string DisplayName => $"{FolderName}  ({TargetPath})";
}

public enum HistoryFileChangeKind
{
    Unchanged,
    Added,
    Removed,
    Modified
}

public sealed class HistoryFileDifference
{
    public required string RelativePath { get; init; }
    public HistoryFileChangeKind ChangeKind { get; init; }
    public string? BeforePath { get; init; }
    public string? CurrentPath { get; init; }
    public long? BeforeLength { get; init; }
    public long? CurrentLength { get; init; }

    public string DisplayChange => ChangeKind switch
    {
        HistoryFileChangeKind.Added => "追加",
        HistoryFileChangeKind.Removed => "削除",
        HistoryFileChangeKind.Modified => "変更",
        _ => "同一"
    };

    public string DisplayBeforeLength => BeforeLength is long length ? FormatLength(length) : "—";
    public string DisplayCurrentLength => CurrentLength is long length ? FormatLength(length) : "—";

    private static string FormatLength(long length)
        => length < 1024 ? $"{length:N0} B" : $"{length / 1024d:N1} KB";
}

public sealed class HistoryFolderDiff
{
    public required HistoryFolderComparison Folder { get; init; }
    public required IReadOnlyList<HistoryFileDifference> Files { get; init; }
    public int ChangedFileCount => Files.Count(file => file.ChangeKind != HistoryFileChangeKind.Unchanged);
}

public sealed class HistoryFileContent
{
    public bool Exists { get; init; }
    public bool IsBinary { get; init; }
    public bool IsTooLarge { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}
