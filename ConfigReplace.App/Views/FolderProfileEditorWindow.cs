using ConfigReplace.Models;
using ConfigReplace.Services;

namespace ConfigReplace.Views;

public sealed class ProfileFolderInput
{
    public required string TargetRootPath { get; init; }
    public required string FolderName { get; init; }
    public string? StagedSnapshotPath { get; init; }
    public ProfileFolderSnapshot? ExistingSnapshot { get; init; }
}

public sealed class FolderProfileEditResult
{
    public required string Name { get; init; }
    public required IReadOnlyList<ProfileFolderInput> Folders { get; init; }
}

public sealed class FolderProfileEditorWindow : Form
{
    private readonly DataGridView _rows = new();
    private readonly Label _instruction = new();
    private readonly FolderTreeService _treeService = new();
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "ConfigReplace", "ProfileImports", Guid.NewGuid().ToString("N"));
    private readonly string? _existingProfileName;
    private DataGridViewCell? _dropCell;
    private bool _importing;

    public FolderProfileEditorWindow(FolderProfile? profile = null, string title = "プロファイル作成")
    {
        _existingProfileName = profile?.Name;
        Text = title;
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 286);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _instruction.Text = "配置先を入力し、エクスプローラーからフォルダを「配置するフォルダ」セルへドロップしてください。";
        _instruction.AutoSize = true;
        _instruction.Location = new Point(10, 12);
        _instruction.ForeColor = SystemColors.GrayText;
        Controls.Add(_instruction);

        _rows.SetBounds(10, 36, 700, 194);
        _rows.AllowDrop = true;
        _rows.AllowUserToAddRows = true;
        _rows.AllowUserToDeleteRows = true;
        _rows.AllowUserToResizeRows = false;
        _rows.BackgroundColor = SystemColors.Window;
        _rows.BorderStyle = BorderStyle.Fixed3D;
        _rows.RowHeadersVisible = false;
        _rows.RowTemplate.Height = 23;
        _rows.ColumnHeadersHeight = 24;
        _rows.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _rows.MultiSelect = false;
        _rows.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "配置先", Width = 340, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _rows.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "配置するフォルダ", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _rows.DragEnter += RowsOnDragOver;
        _rows.DragOver += RowsOnDragOver;
        _rows.DragLeave += (_, _) => ClearDropHighlight();
        _rows.DragDrop += RowsOnDragDrop;
        Controls.Add(_rows);

        var add = MakeButton("行を追加", 10, 246, 82);
        var remove = MakeButton("行を削除", 98, 246, 82);
        var browse = MakeButton("配置先を参照...", 186, 246, 112);
        var cancel = MakeButton("キャンセル", 526, 246, 88);
        var save = MakeButton("保存", 622, 246, 88);
        cancel.DialogResult = DialogResult.Cancel;
        add.Click += (_, _) => _rows.Rows.Add();
        remove.Click += (_, _) => RemoveSelectedRow();
        browse.Click += (_, _) => BrowseTarget();
        save.Click += Save;
        AcceptButton = save;
        CancelButton = cancel;
        Controls.AddRange([add, remove, browse, cancel, save]);

        if (profile is not null)
        {
            foreach (var group in profile.Groups)
            {
                foreach (var folder in group.Folders)
                {
                    var index = _rows.Rows.Add(group.TargetRootPath, folder.FolderName);
                    _rows.Rows[index].Tag = FolderImportReference.FromExisting(folder);
                }
            }
        }
        if (_rows.Rows.Count == 1) _rows.Rows.Add();
    }

    public FolderProfileEditResult? Result { get; private set; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DeleteTemporaryImports();
        base.Dispose(disposing);
    }

    private static Button MakeButton(string text, int x, int y, int width) => new()
    {
        Text = text, Bounds = new Rectangle(x, y, width, 25), UseVisualStyleBackColor = true
    };

    private void BrowseTarget()
    {
        var row = GetOrCreateSelectedRow();
        using var dialog = new FolderBrowserDialog
        {
            Description = "配置先フォルダーを選択してください",
            UseDescriptionForTitle = true
        };
        var current = Convert.ToString(row.Cells[0].Value)?.Trim();
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == DialogResult.OK) row.Cells[0].Value = dialog.SelectedPath;
    }

    private void RemoveSelectedRow()
    {
        if (_rows.CurrentRow is not { IsNewRow: false } row) return;
        DeleteStagedImport(row.Tag as FolderImportReference);
        _rows.Rows.Remove(row);
    }

    private void RowsOnDragOver(object? sender, DragEventArgs e)
    {
        var cell = GetDropCell(e);
        if (cell is null || !TryGetSingleFolder(e.Data, out _))
        {
            e.Effect = DragDropEffects.None;
            ClearDropHighlight();
            return;
        }
        e.Effect = DragDropEffects.Copy;
        SetDropHighlight(cell);
    }

    private async void RowsOnDragDrop(object? sender, DragEventArgs e)
    {
        var cell = GetDropCell(e);
        ClearDropHighlight();
        if (_importing || cell is null || !TryGetSingleFolder(e.Data, out var sourcePath)) return;

        var row = _rows.Rows[cell.RowIndex];
        if (row.IsNewRow)
        {
            var index = _rows.Rows.Add();
            row = _rows.Rows[index];
        }

        var snapshotPath = Path.Combine(_temporaryRoot, Guid.NewGuid().ToString("N"));
        _importing = true;
        _rows.Enabled = false;
        _instruction.Text = $"「{Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath))}」を取り込んでいます...";
        try
        {
            Directory.CreateDirectory(_temporaryRoot);
            await _treeService.CaptureSelfContainedAsync(sourcePath, snapshotPath);
            DeleteStagedImport(row.Tag as FolderImportReference);
            row.Tag = FolderImportReference.FromStaged(snapshotPath);
            row.Cells[1].Value = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
            _instruction.Text = "取り込みました。元フォルダを変更しても、保存するプロファイルには影響しません。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            DeleteDirectory(snapshotPath);
            _instruction.Text = "フォルダを「配置するフォルダ」セルへドロップしてください。";
            MessageBox.Show(this, $"フォルダを取り込めませんでした。\n\n{exception.Message}", "取り込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _rows.Enabled = true;
            _importing = false;
        }
    }

    private DataGridViewCell? GetDropCell(DragEventArgs e)
    {
        var point = _rows.PointToClient(new Point(e.X, e.Y));
        var hit = _rows.HitTest(point.X, point.Y);
        if (hit.RowIndex < 0 || hit.ColumnIndex != 1) return null;
        return _rows.Rows[hit.RowIndex].Cells[1];
    }

    private static bool TryGetSingleFolder(IDataObject? data, out string path)
    {
        path = string.Empty;
        if (data?.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return false;
        if (!Directory.Exists(paths[0]) || File.Exists(paths[0])) return false;
        path = Path.GetFullPath(paths[0]);
        return true;
    }

    private void SetDropHighlight(DataGridViewCell cell)
    {
        if (ReferenceEquals(_dropCell, cell)) return;
        ClearDropHighlight();
        _dropCell = cell;
        cell.Style.BackColor = SystemColors.Info;
        cell.Style.ForeColor = SystemColors.InfoText;
    }

    private void ClearDropHighlight()
    {
        if (_dropCell is null) return;
        _dropCell.Style.BackColor = Color.Empty;
        _dropCell.Style.ForeColor = Color.Empty;
        _dropCell = null;
    }

    private DataGridViewRow GetOrCreateSelectedRow()
    {
        if (_rows.CurrentRow is { } current && !current.IsNewRow) return current;
        return _rows.Rows[_rows.Rows.Add()];
    }

    private void Save(object? sender, EventArgs e)
    {
        if (_importing) return;
        var folders = new List<ProfileFolderInput>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in _rows.Rows)
        {
            if (row.IsNewRow) continue;
            var target = Convert.ToString(row.Cells[0].Value)?.Trim() ?? string.Empty;
            var folderName = Convert.ToString(row.Cells[1].Value)?.Trim() ?? string.Empty;
            if (target.Length == 0 && folderName.Length == 0) continue;
            if (target.Length == 0 || folderName.Length == 0 || row.Tag is not FolderImportReference source)
            {
                ShowInputError("配置先を指定し、配置するフォルダをドラッグ＆ドロップしてください。");
                return;
            }
            string fullTarget;
            try { fullTarget = Path.GetFullPath(target); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                ShowInputError($"配置先が不正です: {exception.Message}");
                return;
            }
            if (!keys.Add($"{fullTarget}\0{folderName}"))
            {
                ShowInputError($"同じ配置先に同名フォルダが重複しています: {Path.Combine(fullTarget, folderName)}");
                return;
            }
            folders.Add(new ProfileFolderInput
            {
                TargetRootPath = fullTarget,
                FolderName = folderName,
                StagedSnapshotPath = source.StagedSnapshotPath,
                ExistingSnapshot = source.ExistingSnapshot
            });
        }
        if (folders.Count == 0)
        {
            ShowInputError("少なくとも1つのフォルダを登録してください。");
            return;
        }

        var generatedName = folders.Count == 1 ? folders[0].FolderName : $"{folders[0].FolderName} ほか{folders.Count - 1}件";
        Result = new FolderProfileEditResult { Name = _existingProfileName ?? generatedName, Folders = folders };
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowInputError(string message)
        => MessageBox.Show(this, message, "入力確認", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void DeleteTemporaryImports() => DeleteDirectory(_temporaryRoot);

    private static void DeleteStagedImport(FolderImportReference? source)
    {
        if (source?.StagedSnapshotPath is not null) DeleteDirectory(source.StagedSnapshotPath);
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ConfigReplace", "ProfileImports"));
        var fullPath = Path.GetFullPath(path);
        if (!FileSystemUtilities.IsSameOrChildPath(fullPath, allowedRoot)) return;
        try { FileSystemUtilities.DeleteDirectoryTree(fullPath); } catch { }
    }

    private sealed class FolderImportReference
    {
        public string? StagedSnapshotPath { get; init; }
        public ProfileFolderSnapshot? ExistingSnapshot { get; init; }
        public static FolderImportReference FromStaged(string path) => new() { StagedSnapshotPath = path };
        public static FolderImportReference FromExisting(ProfileFolderSnapshot snapshot) => new() { ExistingSnapshot = snapshot };
    }
}

public sealed class TextInputDialog : Form
{
    private readonly TextBox _input = new();
    private TextInputDialog(string title, string label, string initialValue)
    {
        Text = title; Font = new Font("Segoe UI", 9F); FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(420, 116);
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(10, 13) });
        _input.SetBounds(10, 34, 400, 23); _input.Text = initialValue; _input.SelectAll();
        var ok = new Button { Text = "OK", Bounds = new Rectangle(242, 76, 80, 25), DialogResult = DialogResult.OK, UseVisualStyleBackColor = true };
        var cancel = new Button { Text = "キャンセル", Bounds = new Rectangle(330, 76, 80, 25), DialogResult = DialogResult.Cancel, UseVisualStyleBackColor = true };
        Controls.AddRange([_input, ok, cancel]); AcceptButton = ok; CancelButton = cancel;
    }
    public static string? Show(IWin32Window? owner, string title, string label, string initialValue)
    {
        using var dialog = new TextInputDialog(title, label, initialValue);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input.Text.Trim() : null;
    }
}
