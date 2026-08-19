using ConfigReplace.Models;

namespace ConfigReplace.Services;

/// <summary>
/// プロファイルに登録されたフォルダーの内容を、配置先へ上書きするサービスです。
/// </summary>
public interface IProfileFolderSetSwitchService
{
    Task<FolderSetSwitchPlan> CreatePlanAsync(
        FolderProfile profile,
        ProfilesDocument document,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<OperationResult> ExecuteAsync(
        FolderSetSwitchPlan plan,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
