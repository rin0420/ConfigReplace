using ConfigReplace.Models;

namespace ConfigReplace.Services;

/// <summary>
/// プロファイルに登録されたフォルダー群を、配置先ごとにまとめて切り替えるサービスです。
/// </summary>
public interface IProfileFolderSetSwitchService
{
    Task<FolderSetSwitchPlan> CreatePlanAsync(
        FolderProfile profile,
        ProfilesDocument document,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ExecuteAsync(
        FolderSetSwitchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FolderSetHistoryItem>> GetHistoryAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> RestoreAsync(
        FolderSetHistoryItem history,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ActiveProfileState> DetectActiveProfileAsync(
        ProfilesDocument document,
        CancellationToken cancellationToken = default);
}
