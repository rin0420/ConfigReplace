using System.Collections.ObjectModel;
using System.Windows.Forms;
using ConfigReplace.Models;
using ConfigReplace.Services;
using ConfigReplace.Views;

namespace ConfigReplace.ViewModels;

public sealed class ProfileFolderDisplayRow
{
    public required string TargetRootPath { get; init; }
    public required string FolderName { get; init; }
    public string TargetPath => Path.Combine(TargetRootPath, FolderName);
}

public sealed class MainViewModel : ObservableObject
{
    private readonly ProfileStore _profileStore;
    private readonly FolderTreeService _treeService;
    private readonly IProfileFolderSetSwitchService _switchService;
    private readonly string _backupRoot;
    private ProfilesDocument _document = new();
    private FolderSetSwitchPlan? _switchPlan;
    private CancellationTokenSource? _cancellation;
    private FolderProfile? _selectedProfile;
    private FolderSetHistoryItem? _selectedHistory;
    private string _activeStateText = "読み込み中...";
    private string _statusText = "プロファイルを読み込んでいます。";
    private string _detailText = string.Empty;
    private string _planSummary = "切替プレビューを作成してください。";
    private int _progressPercent;
    private bool _isBusy;

    public MainViewModel()
    {
        var appRoot = AppContext.BaseDirectory;
        _profileStore = new ProfileStore(Path.Combine(appRoot, "Profiles"));
        _treeService = new FolderTreeService();
        _backupRoot = Path.Combine(appRoot, "Backups");
        _switchService = new ProfileFolderSetSwitchService(_profileStore, _treeService, _backupRoot);

        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync, () => !IsBusy);
        EditProfileCommand = new AsyncRelayCommand(EditProfileAsync, () => !IsBusy && SelectedProfile is not null);
        DuplicateProfileCommand = new AsyncRelayCommand(DuplicateProfileAsync, () => !IsBusy && SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => !IsBusy && SelectedProfile is not null);
        PreviewSwitchCommand = new AsyncRelayCommand(PreviewSwitchAsync, () => !IsBusy && SelectedProfile is not null);
        SwitchCommand = new AsyncRelayCommand(SwitchAsync, () => !IsBusy && _switchPlan?.IsValid == true);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync, () => !IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedHistory?.CanRestore == true);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        _ = InitializeAsync();
    }

    public ObservableCollection<FolderProfile> Profiles { get; } = [];
    public ObservableCollection<ProfileFolderDisplayRow> SelectedProfileFolders { get; } = [];
    public ObservableCollection<FolderSetHistoryItem> HistoryItems { get; } = [];

    public AsyncRelayCommand CreateProfileCommand { get; }
    public AsyncRelayCommand EditProfileCommand { get; }
    public AsyncRelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public AsyncRelayCommand PreviewSwitchCommand { get; }
    public AsyncRelayCommand SwitchCommand { get; }
    public AsyncRelayCommand RefreshHistoryCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }

    public FolderProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                RefreshSelectedProfileFolders();
                InvalidatePlan();
                OnPropertyChanged(nameof(SelectedProfileDetail));
                RaiseCommandStates();
            }
        }
    }

    public FolderSetHistoryItem? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (SetProperty(ref _selectedHistory, value))
            {
                OnPropertyChanged(nameof(HistoryDetail));
                RaiseCommandStates();
            }
        }
    }

    public string ActiveStateText
    {
        get => _activeStateText;
        private set => SetProperty(ref _activeStateText, value);
    }

    public string SelectedProfileDetail
        => SelectedProfile is null
            ? "プロファイルを選択してください。"
            : $"{SelectedProfile.Name}\n配置フォルダー数: {SelectedProfile.Groups.Sum(group => group.Folders.Count):N0}\n更新日時: {SelectedProfile.UpdatedAt:yyyy/MM/dd HH:mm:ss}";

    public string HistoryDetail
        => SelectedHistory is null
            ? "履歴を選択してください。"
            : $"種類: {SelectedHistory.DisplayOperation}\nプロファイル: {SelectedHistory.ProfileName}\nフォルダー数: {SelectedHistory.FolderCount:N0}\n{SelectedHistory.ValidationMessage}";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string PlanSummary
    {
        get => _planSummary;
        private set => SetProperty(ref _planSummary, value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) RaiseCommandStates();
        }
    }

    private async Task InitializeAsync()
    {
        BeginOperation("プロファイルを読み込んでいます。");
        try
        {
            _document = await _profileStore.LoadAsync();
            var migrated = MigrateLegacySlotProfiles();
            var sanitized = await SanitizeStoredSourceMetadataAsync();
            if (migrated || sanitized) await _profileStore.SaveAsync(_document);
            SyncCollections();
            await RefreshActiveStateAsync();
            await RefreshHistoryCoreAsync();
            StatusText = Profiles.Count == 0
                ? "［新規］から、配置先を指定してフォルダーを取り込んでください。"
                : "プロファイルを選択してください。";
        }
        catch (Exception exception)
        {
            StatusText = "プロファイルを読み込めませんでした。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task CreateProfileAsync()
    {
        using var dialog = new FolderProfileEditorWindow();
        if (dialog.ShowDialog(Form.ActiveForm) != DialogResult.OK || dialog.Result is null) return;

        BeginOperation("プロファイルを作成しています。");
        var profile = new FolderProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = dialog.Result.Name,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        try
        {
            profile.Groups = await CaptureProfileFoldersAsync(profile.Id, dialog.Result.Folders);
            _document.SchemaVersion = 2;
            _document.Profiles.Add(profile);
            await _profileStore.SaveAsync(_document, _cancellation!.Token);
            SyncCollections(profile);
            StatusText = $"プロファイル「{profile.Name}」を作成しました。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "プロファイル作成をキャンセルしました。";
        }
        catch (Exception exception)
        {
            StatusText = "プロファイルを作成できませんでした。";
            DetailText = exception.Message;
            DeleteProfileDirectory(profile.Id);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task EditProfileAsync()
    {
        if (SelectedProfile is null) return;
        var edited = SelectedProfile;
        using var dialog = new FolderProfileEditorWindow(edited, "プロファイル編集");
        if (dialog.ShowDialog(Form.ActiveForm) != DialogResult.OK || dialog.Result is null) return;

        BeginOperation("プロファイルを更新しています。");
        try
        {
            var groups = await CaptureProfileFoldersAsync(edited.Id, dialog.Result.Folders);
            edited.Name = dialog.Result.Name;
            edited.UpdatedAt = DateTimeOffset.Now;
            edited.Groups = groups;
            edited.Snapshots.Clear();
            _document.SchemaVersion = 2;
            await _profileStore.SaveAsync(_document, _cancellation!.Token);
            SyncCollections(edited);
            StatusText = $"プロファイル「{edited.Name}」を更新しました。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "プロファイル更新をキャンセルしました。";
        }
        catch (Exception exception)
        {
            StatusText = "プロファイルを更新できませんでした。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task DuplicateProfileAsync()
    {
        if (SelectedProfile is null) return;
        var name = TextInputDialog.Show(Form.ActiveForm, "プロファイル複製", "新しいプロファイル名:", SelectedProfile.Name + " コピー");
        if (string.IsNullOrWhiteSpace(name)) return;

        BeginOperation("プロファイルを複製しています。");
        var source = SelectedProfile;
        var copy = new FolderProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        try
        {
            var tempId = copy.Id + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                foreach (var group in source.Groups)
                {
                    var groupCopy = new ProfileFolderGroup { Id = Guid.NewGuid().ToString("N"), TargetRootPath = group.TargetRootPath };
                    foreach (var snapshot in group.Folders)
                    {
                        var destination = _profileStore.GetSnapshotPath(tempId, snapshot.Id);
                        var sourcePath = _profileStore.GetSnapshotPath(source.Id, snapshot.Id);
                        await _treeService.CloneSnapshotAsync(sourcePath, destination, _cancellation!.Token);
                        await _treeService.RemoveSourceMetadataAsync(destination, _cancellation!.Token);
                        groupCopy.Folders.Add(new ProfileFolderSnapshot
                        {
                            Id = snapshot.Id,
                            FolderName = snapshot.FolderName,
                            SnapshotRelativePath = Path.Combine(copy.Id, snapshot.Id),
                            SourcePath = string.Empty,
                            TreeHash = snapshot.TreeHash,
                            FileCount = snapshot.FileCount,
                            TotalBytes = snapshot.TotalBytes
                        });
                    }
                    copy.Groups.Add(groupCopy);
                }
                MoveProfileDirectory(tempId, copy.Id);
            }
            finally
            {
                DeleteProfileDirectory(tempId);
            }

            _document.SchemaVersion = 2;
            _document.Profiles.Add(copy);
            await _profileStore.SaveAsync(_document, _cancellation!.Token);
            SyncCollections(copy);
            StatusText = $"プロファイル「{copy.Name}」を複製しました。";
        }
        catch (Exception exception)
        {
            StatusText = "プロファイルを複製できませんでした。";
            DetailText = exception.Message;
            DeleteProfileDirectory(copy.Id);
        }
        finally
        {
            EndOperation();
        }
    }

    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var result = MessageBox.Show(Form.ActiveForm,
            $"プロファイル「{SelectedProfile.Name}」を削除しますか？\n保存したスナップショットも削除されます。",
            "削除確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;
        var deleted = SelectedProfile;
        _document.Profiles.Remove(deleted);
        DeleteProfileDirectory(deleted.Id);
        SyncCollections();
        _ = SaveDocumentAsync("プロファイルを削除しました。", true);
    }

    private async Task PreviewSwitchAsync()
    {
        if (SelectedProfile is null) return;
        BeginOperation("切替内容を確認しています。");
        try
        {
            _switchPlan = await Task.Run(() => _switchService.CreatePlanAsync(SelectedProfile, _document, _cancellation!.Token));
            PlanSummary = BuildPlanSummary(_switchPlan);
            StatusText = _switchPlan.IsValid ? "切替内容を確認してください。" : "切替前の検証に失敗しました。";
            DetailText = _switchPlan.ValidationErrors.Count == 0 ? string.Empty : string.Join(Environment.NewLine, _switchPlan.ValidationErrors);
        }
        catch (Exception exception)
        {
            _switchPlan = null;
            StatusText = "切替プレビューを作成できませんでした。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task SwitchAsync()
    {
        if (_switchPlan is null) return;
        BeginOperation("プロファイル切替を開始します。");
        try
        {
            var result = await Task.Run(() => _switchService.ExecuteAsync(_switchPlan, CreateProgress(), _cancellation!.Token));
            ShowResult(result);
            if (result.Success)
            {
                _switchPlan = null;
                await RefreshActiveStateAsync();
                await RefreshHistoryCoreAsync();
                PlanSummary = "切替済みです。次の切替時は新しいプレビューを作成してください。";
            }
        }
        catch (Exception exception)
        {
            StatusText = "切替処理で予期しないエラーが発生しました。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RefreshHistoryAsync()
    {
        BeginOperation("切替履歴を読み込んでいます。");
        try
        {
            await RefreshHistoryCoreAsync();
            StatusText = $"切替履歴を{HistoryItems.Count:N0}件読み込みました。";
        }
        catch (Exception exception)
        {
            StatusText = "切替履歴を読み込めませんでした。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RestoreAsync()
    {
        if (SelectedHistory is null) return;
        BeginOperation("フォルダー復元を開始します。");
        try
        {
            var result = await Task.Run(() => _switchService.RestoreAsync(SelectedHistory, CreateProgress(), _cancellation!.Token));
            ShowResult(result);
            if (result.Success)
            {
                await RefreshActiveStateAsync();
                await RefreshHistoryCoreAsync();
            }
        }
        catch (Exception exception)
        {
            StatusText = "復元処理で予期しないエラーが発生しました。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<List<ProfileFolderGroup>> CaptureProfileFoldersAsync(string profileId, IReadOnlyList<ProfileFolderInput> inputs)
    {
        var tempId = profileId + ".staging-" + Guid.NewGuid().ToString("N");
        var groups = new Dictionary<string, ProfileFolderGroup>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var input in inputs)
            {
                var targetRoot = Path.GetFullPath(input.TargetRootPath);
                if (!groups.TryGetValue(targetRoot, out var group))
                {
                    group = new ProfileFolderGroup { Id = Guid.NewGuid().ToString("N"), TargetRootPath = targetRoot };
                    groups[targetRoot] = group;
                }

                var snapshotId = Guid.NewGuid().ToString("N");
                var snapshotPath = _profileStore.GetSnapshotPath(tempId, snapshotId);
                if (input.ExistingSnapshot is not null)
                {
                    var existingPath = _profileStore.GetSnapshotPath(profileId, input.ExistingSnapshot.Id);
                    await _treeService.CloneSnapshotAsync(existingPath, snapshotPath, _cancellation!.Token);
                }
                else if (!string.IsNullOrWhiteSpace(input.StagedSnapshotPath))
                {
                    await _treeService.CloneSnapshotAsync(input.StagedSnapshotPath, snapshotPath, _cancellation!.Token);
                }
                else
                {
                    throw new InvalidOperationException($"配置するフォルダーが取り込まれていません: {input.FolderName}");
                }
                await _treeService.RemoveSourceMetadataAsync(snapshotPath, _cancellation!.Token);
                var manifest = await _treeService.LoadAndValidateSnapshotAsync(snapshotPath, _cancellation!.Token);
                group.Folders.Add(new ProfileFolderSnapshot
                {
                    Id = snapshotId,
                    FolderName = input.FolderName,
                    SnapshotRelativePath = Path.Combine(profileId, snapshotId),
                    SourcePath = string.Empty,
                    TreeHash = manifest.TreeHash,
                    FileCount = manifest.Files.Count,
                    TotalBytes = manifest.Files.Sum(file => file.Length)
                });
            }

            MoveProfileDirectory(tempId, profileId);
            return groups.Values.ToList();
        }
        finally
        {
            DeleteProfileDirectory(tempId);
        }
    }

    private async Task SaveDocumentAsync(string message, bool invalidatePlan = false)
    {
        try
        {
            await _profileStore.SaveAsync(_document);
            if (invalidatePlan) InvalidatePlan();
            StatusText = message;
            await RefreshActiveStateAsync();
        }
        catch (Exception exception)
        {
            StatusText = "設定を保存できませんでした。";
            DetailText = exception.Message;
        }
    }

    private async Task RefreshActiveStateAsync()
    {
        var state = await Task.Run(() => _switchService.DetectActiveProfileAsync(_document, _cancellation?.Token ?? CancellationToken.None));
        ActiveStateText = state.Message;
    }

    private async Task RefreshHistoryCoreAsync()
    {
        var items = await Task.Run(() => _switchService.GetHistoryAsync(_cancellation?.Token ?? CancellationToken.None));
        HistoryItems.Clear();
        foreach (var item in items) HistoryItems.Add(item);
        SelectedHistory = HistoryItems.FirstOrDefault();
    }

    private void SyncCollections(FolderProfile? profile = null)
    {
        Profiles.Clear();
        foreach (var item in _document.Profiles) Profiles.Add(item);
        SelectedProfile = profile ?? (SelectedProfile is not null && Profiles.Contains(SelectedProfile) ? SelectedProfile : Profiles.FirstOrDefault());
        RefreshSelectedProfileFolders();
        OnPropertyChanged(nameof(SelectedProfileDetail));
        RaiseCommandStates();
    }

    private void RefreshSelectedProfileFolders()
    {
        SelectedProfileFolders.Clear();
        if (SelectedProfile is null) return;
        foreach (var group in SelectedProfile.Groups.OrderBy(group => group.TargetRootPath, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var folder in group.Folders.OrderBy(folder => folder.FolderName, StringComparer.OrdinalIgnoreCase))
            {
                SelectedProfileFolders.Add(new ProfileFolderDisplayRow
                {
                    TargetRootPath = group.TargetRootPath,
                    FolderName = folder.FolderName
                });
            }
        }
    }

    private bool MigrateLegacySlotProfiles()
    {
        var changed = false;
        foreach (var profile in _document.Profiles)
        {
            foreach (var folder in profile.Groups.SelectMany(group => group.Folders))
            {
                if (string.IsNullOrEmpty(folder.SourcePath)) continue;
                folder.SourcePath = string.Empty;
                changed = true;
            }
        }
        if (_document.Slots.Count == 0 || _document.Profiles.All(profile => profile.Groups.Count > 0 || profile.Snapshots.Count == 0)) return changed;
        foreach (var profile in _document.Profiles)
        {
            if (profile.Groups.Count > 0 || profile.Snapshots.Count == 0) continue;
            foreach (var snapshot in profile.Snapshots)
            {
                var slot = _document.Slots.FirstOrDefault(value => string.Equals(value.Id, snapshot.SlotId, StringComparison.OrdinalIgnoreCase));
                if (slot is null) continue;
                var targetPath = Path.GetFullPath(slot.TargetPath);
                var root = Directory.GetParent(targetPath)?.FullName;
                var folderName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(folderName)) continue;
                var group = profile.Groups.FirstOrDefault(value => string.Equals(value.TargetRootPath, root, StringComparison.OrdinalIgnoreCase));
                if (group is null)
                {
                    group = new ProfileFolderGroup { Id = Guid.NewGuid().ToString("N"), TargetRootPath = root };
                    profile.Groups.Add(group);
                }
                group.Folders.Add(new ProfileFolderSnapshot
                {
                    Id = snapshot.SlotId,
                    FolderName = folderName,
                    SnapshotRelativePath = Path.Combine(profile.Id, snapshot.SlotId),
                    SourcePath = string.Empty,
                    TreeHash = snapshot.TreeHash,
                    FileCount = snapshot.FileCount,
                    TotalBytes = snapshot.TotalBytes
                });
            }
            profile.Snapshots.Clear();
        }
        _document.Slots.Clear();
        _document.SchemaVersion = 2;
        return true;
    }

    private async Task<bool> SanitizeStoredSourceMetadataAsync()
    {
        var changed = false;
        foreach (var profile in _document.Profiles)
        {
            foreach (var snapshot in profile.Groups.SelectMany(group => group.Folders))
            {
                var path = _profileStore.GetSnapshotPath(profile.Id, snapshot.Id);
                if (!Directory.Exists(path)) continue;
                changed |= await _treeService.RemoveSourceMetadataAsync(path);
            }
        }
        return changed;
    }

    private void BeginOperation(string message)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        ProgressPercent = 0;
        DetailText = string.Empty;
        StatusText = message;
        IsBusy = true;
    }

    private void EndOperation()
    {
        IsBusy = false;
        _cancellation?.Dispose();
        _cancellation = null;
        RaiseCommandStates();
    }

    private IProgress<OperationProgress> CreateProgress()
        => new Progress<OperationProgress>(value =>
        {
            ProgressPercent = value.Percent;
            StatusText = string.IsNullOrEmpty(value.CurrentFile) ? value.Phase : $"{value.Phase}: {value.CurrentFile}";
        });

    private void ShowResult(OperationResult result)
    {
        StatusText = result.Message;
        DetailText = result.Errors is { Count: > 0 }
            ? string.Join(Environment.NewLine, result.Errors)
            : result.ManifestPath is null ? string.Empty : $"履歴: {result.ManifestPath}";
    }

    private static string BuildPlanSummary(FolderSetSwitchPlan plan)
    {
        if (plan.ValidationErrors.Count > 0) return string.Join(Environment.NewLine, plan.ValidationErrors);
        var lines = new List<string> { $"切替先プロファイル: {plan.Profile.Name}", $"配置先グループ数: {plan.Groups.Count:N0}" };
        foreach (var group in plan.Groups)
        {
            lines.Add($"{group.TargetRootPath}: 追加 {group.AddedFolderCount:N0} / 置換 {group.ReplacedFolderCount:N0} / 削除 {group.RemovedFolderCount:N0} フォルダー");
        }
        lines.Add("登録済みプロファイル間で管理しているフォルダーだけを、バックアップ後に切り替えます。");
        return string.Join(Environment.NewLine, lines);
    }

    private void Cancel() => _cancellation?.Cancel();

    private void InvalidatePlan()
    {
        _switchPlan = null;
        PlanSummary = "切替プレビューを作成してください。";
        SwitchCommand.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        CreateProfileCommand.RaiseCanExecuteChanged();
        EditProfileCommand.RaiseCanExecuteChanged();
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        PreviewSwitchCommand.RaiseCanExecuteChanged();
        SwitchCommand.RaiseCanExecuteChanged();
        RefreshHistoryCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private void MoveProfileDirectory(string sourceId, string destinationId)
    {
        var source = Path.Combine(_profileStore.ProfilesRoot, sourceId);
        var destination = Path.Combine(_profileStore.ProfilesRoot, destinationId);
        var old = destination + ".old-" + Guid.NewGuid().ToString("N");
        try
        {
            if (Directory.Exists(destination)) Directory.Move(destination, old);
            Directory.Move(source, destination);
            DeleteProfileDirectory(old);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(old)) Directory.Move(old, destination);
            throw;
        }
    }

    private void DeleteProfileDirectory(string id)
    {
        var path = Path.Combine(_profileStore.ProfilesRoot, id);
        if (!Directory.Exists(path)) return;
        FileSystemUtilities.DeleteDirectoryTree(path);
    }
}
