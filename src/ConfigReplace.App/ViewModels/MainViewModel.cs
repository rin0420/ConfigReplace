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
    private readonly ProfileStorageMigrationService _storageMigration;
    private readonly IProfileFolderSetSwitchService _switchService;
    private ProfilesDocument _document = new();
    private FolderSetSwitchPlan? _switchPlan;
    private CancellationTokenSource? _cancellation;
    private FolderProfile? _selectedProfile;
    private string _activeStateText = "選択したプロファイルの登録フォルダーを配置先へ上書きします。";
    private string _statusText = "プロファイルを読み込んでいます。";
    private string _detailText = string.Empty;
    private string _planSummary = "内容確認を実行してください。";
    private int _progressPercent;
    private bool _isBusy;

    public MainViewModel()
    {
        var appRoot = AppContext.BaseDirectory;
        _profileStore = new ProfileStore(Path.Combine(appRoot, "Profiles"));
        _treeService = new FolderTreeService();
        _storageMigration = new ProfileStorageMigrationService(_profileStore, _treeService);
        _switchService = new ProfileFolderSetSwitchService(_profileStore, _treeService);

        CreateProfileCommand = new AsyncRelayCommand(CreateProfileAsync, () => !IsBusy);
        EditProfileCommand = new AsyncRelayCommand(EditProfileAsync, () => !IsBusy && SelectedProfile is not null);
        DuplicateProfileCommand = new AsyncRelayCommand(DuplicateProfileAsync, () => !IsBusy && SelectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(DeleteProfile, () => !IsBusy && SelectedProfile is not null);
        PreviewSwitchCommand = new AsyncRelayCommand(PreviewSwitchAsync, () => !IsBusy && SelectedProfile is not null);
        SwitchCommand = new AsyncRelayCommand(SwitchAsync, () => !IsBusy && _switchPlan?.IsValid == true);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        _ = InitializeAsync();
    }

    public ObservableCollection<FolderProfile> Profiles { get; } = [];
    public ObservableCollection<ProfileFolderDisplayRow> SelectedProfileFolders { get; } = [];

    public AsyncRelayCommand CreateProfileCommand { get; }
    public AsyncRelayCommand EditProfileCommand { get; }
    public AsyncRelayCommand DuplicateProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public AsyncRelayCommand PreviewSwitchCommand { get; }
    public AsyncRelayCommand SwitchCommand { get; }
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

    public string ActiveStateText
    {
        get => _activeStateText;
        private set => SetProperty(ref _activeStateText, value);
    }

    public string SelectedProfileDetail
        => SelectedProfile is null
            ? "プロファイルを選択してください。"
            : $"{SelectedProfile.Name}\n対象フォルダー数: {SelectedProfile.Groups.Sum(group => group.Folders.Count):N0}\n更新日時: {SelectedProfile.UpdatedAt:yyyy/MM/dd HH:mm:ss}";

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
            var changed = MigrateLegacySlotProfiles();
            changed |= await _storageMigration.MigrateAsync(_document, CreateProgress(), _cancellation!.Token);
            if (changed)
            {
                _document.SchemaVersion = 2;
                await _profileStore.SaveAsync(_document, _cancellation.Token);
            }

            SyncCollections();
            StatusText = Profiles.Count == 0
                ? "［新規］から、配置先を指定してフォルダーを取り込んでください。"
                : "プロファイルを選択し、内容確認後に上書き実行してください。";
        }
        catch (Exception exception)
        {
            StatusText = "プロファイルを読み込めませんでした。";
            DetailText = exception.Message;
            SyncCollections();
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

        string profileName;
        try
        {
            profileName = CreateUniqueProfileName(dialog.Result.Name);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(Form.ActiveForm, exception.Message, "プロファイル名を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        BeginOperation("プロファイルを作成しています。");
        var profile = new FolderProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = profileName,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        var storageCommitted = false;
        try
        {
            profile.Groups = await CaptureProfileFoldersAsync(profile, dialog.Result.Folders);
            storageCommitted = true;
            _document.SchemaVersion = 2;
            _document.Profiles.Add(profile);
            SetProgressPhase(0, 1, "プロファイル設定を保存中");
            await _profileStore.SaveAsync(_document, _cancellation!.Token);
            SetProgressPhase(1, 1, "プロファイル作成完了");
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
            if (storageCommitted) DeleteProfileDirectory(profile);
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

        var newName = dialog.Result.Name.Trim();
        if (!TryValidateEditedProfileName(edited, newName)) return;

        BeginOperation("プロファイルを更新しています。");
        var previousName = edited.Name;
        try
        {
            var groups = await CaptureProfileFoldersAsync(edited, dialog.Result.Folders, previousName, newName);
            edited.Name = newName;
            edited.UpdatedAt = DateTimeOffset.Now;
            edited.Groups = groups;
            edited.Snapshots.Clear();
            _document.SchemaVersion = 2;
            SetProgressPhase(0, 1, "プロファイル設定を保存中");
            await _profileStore.SaveAsync(_document, _cancellation!.Token);
            SetProgressPhase(1, 1, "プロファイル更新完了");
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
        name = name.Trim();
        try { _profileStore.GetProfileDirectoryPath(name); }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(Form.ActiveForm, exception.Message, "プロファイル名を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (_document.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(Form.ActiveForm, "同じ名前のプロファイルが既にあります。別の名前を指定してください。", "プロファイル名を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        BeginOperation("プロファイルを複製しています。");
        var source = SelectedProfile;
        var copy = new FolderProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        try
        {
            var tempId = copy.Id + ".staging-" + Guid.NewGuid().ToString("N");
            try
            {
                var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in source.Groups)
                {
                    var groupCopy = new ProfileFolderGroup
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        TargetRootPath = group.TargetRootPath
                    };
                    foreach (var snapshot in group.Folders)
                    {
                        if (!folderNames.Add(snapshot.FolderName))
                        {
                            throw new InvalidDataException($"プロファイル内で同じフォルダー名を複数登録できません: {snapshot.FolderName}");
                        }
                        var sourcePath = _profileStore.GetProfileFolderPath(source, snapshot.FolderName);
                        var destination = _profileStore.GetProfileFolderPath(tempId, snapshot.FolderName);
                        var summary = await _treeService.CopyDirectoryContentsAsync(sourcePath, destination, CreateProgress(), _cancellation!.Token);
                        groupCopy.Folders.Add(CreateStoredFolder(snapshot.FolderName, summary.FileCount, summary.TotalBytes));
                    }
                    copy.Groups.Add(groupCopy);
                }

                _profileStore.CommitStagedFolders(copy, tempId);
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
            DeleteProfileDirectory(copy);
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
            $"プロファイル「{SelectedProfile.Name}」を削除しますか？\n保存したフォルダーも削除されます。",
            "削除確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;
        var deleted = SelectedProfile;
        _document.Profiles.Remove(deleted);
        DeleteProfileDirectory(deleted);
        SyncCollections();
        _ = SaveDocumentAsync("プロファイルを削除しました。", true);
    }

    private async Task PreviewSwitchAsync()
    {
        if (SelectedProfile is null) return;
        BeginOperation("上書き対象を確認しています。");
        try
        {
            _switchPlan = await Task.Run(() => _switchService.CreatePlanAsync(
                SelectedProfile,
                _document,
                CreateProgress(),
                _cancellation!.Token));
            PlanSummary = BuildPlanSummary(_switchPlan);
            StatusText = _switchPlan.IsValid ? "上書き内容を確認してください。" : "上書き前の確認に失敗しました。";
            DetailText = _switchPlan.ValidationErrors.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, _switchPlan.ValidationErrors);
        }
        catch (Exception exception)
        {
            _switchPlan = null;
            StatusText = "上書き対象を確認できませんでした。";
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
        BeginOperation("プロファイルの上書きを開始します。");
        try
        {
            var result = await Task.Run(() => _switchService.ExecuteAsync(
                _switchPlan,
                CreateProgress(),
                _cancellation!.Token));
            ShowResult(result);
            if (result.Success)
            {
                _switchPlan = null;
                PlanSummary = "上書き済みです。次回は新しい内容確認を実行してください。";
            }
        }
        catch (Exception exception)
        {
            StatusText = "上書き処理で予期しないエラーが発生しました。";
            DetailText = exception.Message;
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<List<ProfileFolderGroup>> CaptureProfileFoldersAsync(
        FolderProfile profile,
        IReadOnlyList<ProfileFolderInput> inputs,
        string? previousProfileName = null,
        string? destinationProfileName = null)
    {
        var destinationName = destinationProfileName ?? profile.Name;
        var tempId = profile.Id + ".staging-" + Guid.NewGuid().ToString("N");
        var groups = new Dictionary<string, ProfileFolderGroup>(StringComparer.OrdinalIgnoreCase);
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var input in inputs)
            {
                _cancellation!.Token.ThrowIfCancellationRequested();
                ProfileStore.ValidateFolderName(input.FolderName);
                if (!folderNames.Add(input.FolderName))
                {
                    throw new InvalidDataException($"プロファイル内で同じフォルダー名を複数登録できません: {input.FolderName}");
                }

                var targetRoot = Path.GetFullPath(input.TargetRootPath);
                if (!groups.TryGetValue(targetRoot, out var group))
                {
                    group = new ProfileFolderGroup { Id = Guid.NewGuid().ToString("N"), TargetRootPath = targetRoot };
                    groups[targetRoot] = group;
                }

                var sourcePath = ResolveInputSourcePath(profile, input);
                var destination = _profileStore.GetProfileFolderPath(tempId, input.FolderName);
                var summary = await _treeService.CopyDirectoryContentsAsync(
                    sourcePath,
                    destination,
                    CreateProgress(),
                    _cancellation!.Token);
                group.Folders.Add(CreateStoredFolder(input.FolderName, summary.FileCount, summary.TotalBytes));
            }

            var destinationProfile = new FolderProfile
            {
                Id = profile.Id,
                Name = destinationName,
                CreatedAt = profile.CreatedAt,
                UpdatedAt = profile.UpdatedAt
            };
            SetProgressPhase(0, 1, "Profilesへ保存中");
            _profileStore.CommitStagedFolders(destinationProfile, tempId, previousProfileName);
            SetProgressPhase(1, 1, "Profilesへの保存完了");
            return groups.Values.ToList();
        }
        finally
        {
            DeleteProfileDirectory(tempId);
        }
    }

    private string ResolveInputSourcePath(FolderProfile profile, ProfileFolderInput input)
    {
        if (input.ExistingSnapshot is not null)
        {
            return _profileStore.GetProfileFolderPath(profile, input.ExistingSnapshot.FolderName);
        }
        if (!string.IsNullOrWhiteSpace(input.SourcePath))
        {
            return Path.GetFullPath(input.SourcePath);
        }
        if (!string.IsNullOrWhiteSpace(input.StagedSnapshotPath))
        {
            var staged = Path.GetFullPath(input.StagedSnapshotPath);
            var content = Path.Combine(staged, "content");
            return Directory.Exists(content) ? content : staged;
        }

        throw new InvalidOperationException($"配置するフォルダーが取り込まれていません: {input.FolderName}");
    }

    private static ProfileFolderSnapshot CreateStoredFolder(string folderName, int fileCount, long totalBytes)
        => new()
        {
            Id = folderName,
            FolderName = folderName,
            SnapshotRelativePath = folderName,
            SourcePath = string.Empty,
            TreeHash = string.Empty,
            FileCount = fileCount,
            TotalBytes = totalBytes
        };

    private async Task SaveDocumentAsync(string message, bool invalidatePlan = false)
    {
        try
        {
            await _profileStore.SaveAsync(_document);
            if (invalidatePlan) InvalidatePlan();
            StatusText = message;
        }
        catch (Exception exception)
        {
            StatusText = "設定を保存できませんでした。";
            DetailText = exception.Message;
        }
    }

    private void SyncCollections(FolderProfile? profile = null)
    {
        Profiles.Clear();
        foreach (var item in _document.Profiles) Profiles.Add(item);
        SelectedProfile = profile ?? (SelectedProfile is not null && Profiles.Contains(SelectedProfile)
            ? SelectedProfile
            : Profiles.FirstOrDefault());
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

        if (_document.Slots.Count == 0 || _document.Profiles.All(profile => profile.Groups.Count > 0 || profile.Snapshots.Count == 0))
        {
            return changed;
        }

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
                    SnapshotRelativePath = Path.Combine(profile.Name, snapshot.SlotId),
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

    private void SetProgressPhase(int processed, int total, string phase, string currentFile = "")
    {
        var progress = new OperationProgress(processed, total, currentFile, phase);
        ProgressPercent = progress.Percent;
        StatusText = string.IsNullOrEmpty(progress.CurrentFile) ? progress.Phase : $"{progress.Phase}: {progress.CurrentFile}";
    }

    private void ShowResult(OperationResult result)
    {
        StatusText = result.Message;
        DetailText = result.Errors is { Count: > 0 }
            ? string.Join(Environment.NewLine, result.Errors)
            : string.Empty;
    }

    private static string BuildPlanSummary(FolderSetSwitchPlan plan)
    {
        if (plan.ValidationErrors.Count > 0) return string.Join(Environment.NewLine, plan.ValidationErrors);
        var lines = new List<string>
        {
            $"対象プロファイル: {plan.Profile.Name}",
            $"配置先グループ数: {plan.Groups.Count:N0}"
        };
        foreach (var group in plan.Groups)
        {
            lines.Add($"{group.TargetRootPath}: 対象 {group.DesiredFolders.Count:N0} / 新規 {group.AddedFolderCount:N0} / 上書き {group.ReplacedFolderCount:N0}");
        }
        lines.Add("登録フォルダーの内容だけを配置先へコピーします。配置先にしかないファイルやフォルダーは削除しません。履歴・バックアップは作成しません。");
        return string.Join(Environment.NewLine, lines);
    }

    private void Cancel() => _cancellation?.Cancel();

    private void InvalidatePlan()
    {
        _switchPlan = null;
        PlanSummary = "内容確認を実行してください。";
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
        CancelCommand.RaiseCanExecuteChanged();
    }

    private string CreateUniqueProfileName(string requestedName)
    {
        var baseName = requestedName.Trim();
        _profileStore.GetProfileDirectoryPath(baseName);
        var candidate = baseName;
        var suffix = 2;
        while (_document.Profiles.Any(profile => string.Equals(profile.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseName} ({suffix++})";
            _profileStore.GetProfileDirectoryPath(candidate);
        }
        return candidate;
    }

    private bool TryValidateEditedProfileName(FolderProfile profile, string requestedName)
    {
        string newPath;
        try
        {
            newPath = _profileStore.GetProfileDirectoryPath(requestedName);
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(Form.ActiveForm, exception.Message, "プロファイル名を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (_document.Profiles.Any(other => !ReferenceEquals(other, profile)
            && string.Equals(other.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(Form.ActiveForm, "同じ名前のプロファイルが既にあります。別の名前を指定してください。", "プロファイル名を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        string oldPath;
        try { oldPath = _profileStore.GetProfileDirectoryPath(profile.Name); }
        catch (InvalidDataException) { oldPath = string.Empty; }
        if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(newPath) || File.Exists(newPath)))
        {
            MessageBox.Show(Form.ActiveForm, "同名のProfiles保存先が既にあります。別の名前を指定してください。", "プロファイル名を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    private void DeleteProfileDirectory(string id)
    {
        string path;
        try { path = _profileStore.GetProfileDirectoryPath(id); }
        catch (InvalidDataException) { return; }
        if (Directory.Exists(path)) FileSystemUtilities.DeleteDirectoryTree(path);
    }

    private void DeleteProfileDirectory(FolderProfile profile)
    {
        try
        {
            var namedPath = _profileStore.GetProfileDirectoryPath(profile.Name);
            if (Directory.Exists(namedPath)) FileSystemUtilities.DeleteDirectoryTree(namedPath);
        }
        catch (InvalidDataException)
        {
            // 壊れたプロファイル名でも、識別子ベースの旧保存先は後始末します。
        }

        DeleteProfileDirectory(profile.Id);
    }
}
