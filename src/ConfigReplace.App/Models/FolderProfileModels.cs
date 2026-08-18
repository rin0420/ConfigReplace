using System.Text.Json.Serialization;

namespace ConfigReplace.Models;

public sealed class ProfilesDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<TargetSlot> Slots { get; set; } = [];
    public List<FolderProfile> Profiles { get; set; } = [];
}

public sealed class TargetSlot
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string TargetPath { get; set; }

    [JsonIgnore]
    public string DisplayName => $"{Name} ({TargetPath})";
}

public sealed class FolderProfile
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public List<ProfileSlotSnapshot> Snapshots { get; set; } = [];
    public List<ProfileFolderGroup> Groups { get; set; } = [];
}

public sealed class ProfileFolderGroup
{
    public required string Id { get; set; }
    public required string TargetRootPath { get; set; }
    public List<ProfileFolderSnapshot> Folders { get; set; } = [];
}

public sealed class ProfileFolderSnapshot
{
    public required string Id { get; set; }
    public required string FolderName { get; set; }
    public required string SnapshotRelativePath { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SourcePath { get; set; } = string.Empty;
    public required string TreeHash { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class ProfileSlotSnapshot
{
    public required string SlotId { get; set; }
    public required string SnapshotRelativePath { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SourcePath { get; set; } = string.Empty;
    public required string TreeHash { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

public sealed class SnapshotManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string SourcePath { get; set; } = string.Empty;
    public required string TreeHash { get; set; }
    public List<string> Directories { get; set; } = [];
    public List<SnapshotFileEntry> Files { get; set; } = [];
}

public sealed class SnapshotFileEntry
{
    public required string RelativePath { get; set; }
    public long Length { get; set; }
    public required string Hash { get; set; }
    public FileAttributes Attributes { get; set; }
}

public sealed class FolderSwitchPlan
{
    public required FolderProfile Profile { get; init; }
    public required IReadOnlyList<FolderSwitchSlotPlan> Slots { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public bool IsValid => ValidationErrors.Count == 0 && Slots.Count > 0;
}

public sealed class FolderSwitchSlotPlan
{
    public required TargetSlot Slot { get; init; }
    public required ProfileSlotSnapshot Snapshot { get; init; }
    public required string SnapshotAbsolutePath { get; init; }
    public bool TargetExists { get; init; }
    public string CurrentTreeHash { get; init; } = string.Empty;
    public int CurrentFileCount { get; init; }
    public int NewFileCount { get; init; }
    public long NewTotalBytes { get; init; }
    public int AddedFileCount { get; init; }
    public int RemovedFileCount { get; init; }
    public int UpdatedFileCount { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FolderSwitchOperationKind
{
    ProfileSwitch,
    Restore
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FolderSwitchStatus
{
    Prepared,
    InProgress,
    Completed,
    RolledBack,
    RollbackFailed,
    Failed
}

public sealed class FolderSwitchManifest
{
    public int SchemaVersion { get; set; } = 1;
    public required string RunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public FolderSwitchOperationKind OperationKind { get; init; }
    public FolderSwitchStatus Status { get; set; }
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public string? SourceManifestPath { get; init; }
    public string? ErrorMessage { get; set; }
    public List<FolderSwitchManifestEntry> Entries { get; init; } = [];
}

public sealed class FolderSwitchManifestEntry
{
    public required string SlotId { get; init; }
    public required string TargetPath { get; init; }
    public required string BackupRelativePath { get; init; }
    public bool TargetExisted { get; init; }
    public required string BeforeTreeHash { get; init; }
    public string AfterTreeHash { get; set; } = string.Empty;
    public bool Applied { get; set; }
}

public sealed class FolderHistoryItem
{
    public required string ManifestPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public FolderSwitchOperationKind OperationKind { get; init; }
    public FolderSwitchStatus Status { get; init; }
    public int SlotCount { get; init; }
    public bool CanRestore { get; init; }
    public string ValidationMessage { get; init; } = string.Empty;

    public string DisplayOperation => OperationKind == FolderSwitchOperationKind.ProfileSwitch ? "プロファイル切替" : "復元";
    public string DisplayStatus => Status switch
    {
        FolderSwitchStatus.Completed => "完了",
        FolderSwitchStatus.RolledBack => "ロールバック済み",
        FolderSwitchStatus.RollbackFailed => "ロールバック失敗",
        FolderSwitchStatus.InProgress => "処理中断",
        FolderSwitchStatus.Prepared => "準備中断",
        _ => "失敗"
    };
}

public sealed class ActiveProfileState
{
    public FolderProfile? Profile { get; init; }
    public bool IsModified { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class FolderSetSwitchPlan
{
    public required FolderProfile Profile { get; init; }
    public required IReadOnlyList<FolderSetGroupPlan> Groups { get; init; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
    public bool IsValid => ValidationErrors.Count == 0 && Groups.Count > 0;
}

public sealed class FolderSetGroupPlan
{
    public required string TargetRootPath { get; init; }
    public required IReadOnlyList<string> ManagedFolderNames { get; init; }
    public required IReadOnlyList<ProfileFolderSnapshot> DesiredFolders { get; init; }
    public required IReadOnlyDictionary<string, string> CurrentHashes { get; init; }
    public int AddedFolderCount { get; init; }
    public int RemovedFolderCount { get; init; }
    public int ReplacedFolderCount { get; init; }
}

public sealed class FolderSetSwitchManifest
{
    public int SchemaVersion { get; set; } = 2;
    public required string RunId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public FolderSwitchOperationKind OperationKind { get; init; }
    public FolderSwitchStatus Status { get; set; }
    public string? ProfileId { get; init; }
    public string? ProfileName { get; init; }
    public string? SourceManifestPath { get; init; }
    public string? ErrorMessage { get; set; }
    public List<FolderSetSwitchManifestEntry> Entries { get; init; } = [];
}

public sealed class FolderSetSwitchManifestEntry
{
    public required string TargetRootPath { get; init; }
    public required string FolderName { get; init; }
    public required string TargetPath { get; init; }
    public required string BackupRelativePath { get; init; }
    public bool BeforeExisted { get; init; }
    public bool DesiredExisted { get; init; }
    public required string BeforeTreeHash { get; init; }
    public string AfterTreeHash { get; set; } = string.Empty;
    public bool Applied { get; set; }
}

public sealed class FolderSetHistoryItem
{
    public required string ManifestPath { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public FolderSwitchOperationKind OperationKind { get; init; }
    public FolderSwitchStatus Status { get; init; }
    public int FolderCount { get; init; }
    public bool CanRestore { get; init; }
    public string ValidationMessage { get; init; } = string.Empty;

    public string DisplayOperation => OperationKind == FolderSwitchOperationKind.ProfileSwitch ? "プロファイル切替" : "復元";
    public string DisplayStatus => Status switch
    {
        FolderSwitchStatus.Completed => "完了",
        FolderSwitchStatus.RolledBack => "ロールバック済み",
        FolderSwitchStatus.RollbackFailed => "ロールバック失敗",
        FolderSwitchStatus.InProgress => "処理中断",
        FolderSwitchStatus.Prepared => "準備中断",
        _ => "失敗"
    };
}
