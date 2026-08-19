using System.Text.Json.Serialization;

namespace ConfigReplace.Models;

public sealed class ProfilesDocument
{
    public int SchemaVersion { get; set; } = 1;
    public List<TargetSlot> Slots { get; set; } = [];
    public List<FolderProfile> Profiles { get; set; } = [];
}

// 旧形式のprofiles.jsonを読み込んで移行するために残しています。
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

    // 旧形式のスロット情報を読み込むための互換プロパティです。
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

    // 旧形式との互換のため残しています。新形式ではFolderNameと同じ値です。
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

    // 旧形式の計画との互換のため残しています。新形式ではハッシュを計算しません。
    public required IReadOnlyDictionary<string, string> CurrentHashes { get; init; }
    public int AddedFolderCount { get; init; }
    public int RemovedFolderCount { get; init; }
    public int ReplacedFolderCount { get; init; }
}
