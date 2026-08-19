using ConfigReplace.Models;
using ConfigReplace.Services;

namespace ConfigReplace.Views;

public sealed class HistoryDiffWindow : Form
{
    private readonly HistoryDiffService _service;
    private readonly FolderSetHistoryItem _history;
    private readonly ComboBox _folders = new();
    private readonly CheckBox _changedOnly = new() { Text = "差分のみ", Checked = true, AutoSize = true };
    private readonly ListView _files = new();
    private readonly RichTextBox _before = CreateViewer();
    private readonly RichTextBox _current = CreateViewer();
    private readonly Label _beforeTitle = new();
    private readonly Label _currentTitle = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly CancellationTokenSource _closed = new();
    private IReadOnlyList<HistoryFolderComparison> _folderItems = [];
    private HistoryFolderDiff? _folderDiff;
    private bool _loadingFolder;

    public HistoryDiffWindow(HistoryDiffService service, FolderSetHistoryItem history)
    {
        _service = service;
        _history = history;
        Text = $"履歴のファイル差分 - {history.ProfileName}";
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1180, 720);
        MinimumSize = new Size(900, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 38, ColumnCount = 3, Padding = new Padding(0, 4, 0, 4)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        header.Controls.Add(new Label { Text = "フォルダー:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _folders.DropDownStyle = ComboBoxStyle.DropDownList;
        _folders.DisplayMember = nameof(HistoryFolderComparison.DisplayName);
        _folders.Dock = DockStyle.Fill;
        header.Controls.Add(_folders, 1, 0);
        header.Controls.Add(_changedOnly, 2, 0);

        _files.View = View.Details;
        _files.FullRowSelect = true;
        _files.HideSelection = false;
        _files.MultiSelect = false;
        _files.GridLines = true;
        _files.Columns.Add("状態", 64);
        _files.Columns.Add("相対パス", 440);
        _files.Columns.Add("履歴", 95);
        _files.Columns.Add("現在", 95);
        _files.Dock = DockStyle.Fill;

        var filePanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 5, 0) };
        filePanel.Controls.Add(_files);
        var diffPanel = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 310,
            FixedPanel = FixedPanel.Panel1
        };
        diffPanel.Panel1.Controls.Add(CreateViewerPanel(_beforeTitle, _before));
        diffPanel.Panel2.Controls.Add(CreateViewerPanel(_currentTitle, _current));
        var main = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 500 };
        main.Panel1.Padding = new Padding(0, 0, 5, 0);
        main.Panel2.Padding = new Padding(5, 0, 0, 0);
        main.Panel1.Controls.Add(filePanel);
        main.Panel2.Controls.Add(diffPanel);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(0, 5, 0, 0) };
        footer.Controls.Add(_status);
        Controls.Add(main);
        Controls.Add(footer);
        Controls.Add(header);

        _folders.SelectedIndexChanged += FoldersOnSelectedIndexChanged;
        _changedOnly.CheckedChanged += (_, _) => RefreshFileRows();
        _files.SelectedIndexChanged += FilesOnSelectedIndexChanged;
        Load += async (_, _) => await LoadFoldersAsync();
        FormClosed += (_, _) => _closed.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _closed.Dispose();
        base.Dispose(disposing);
    }

    private static Panel CreateViewerPanel(Label title, RichTextBox viewer)
    {
        title.Dock = DockStyle.Top;
        title.Height = 24;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(viewer);
        panel.Controls.Add(title);
        return panel;
    }

    private static RichTextBox CreateViewer() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = SystemColors.Window,
        BorderStyle = BorderStyle.Fixed3D,
        DetectUrls = false,
        Font = new Font("Consolas", 9F),
        Multiline = true,
        ScrollBars = RichTextBoxScrollBars.Both,
        WordWrap = false
    };

    private async Task LoadFoldersAsync()
    {
        try
        {
            _status.Text = "履歴のフォルダー一覧を読み込んでいます。";
            _folderItems = await _service.GetFoldersAsync(_history, _closed.Token);
            _folders.BeginUpdate();
            _folders.Items.Clear();
            foreach (var folder in _folderItems) _folders.Items.Add(folder);
            _folders.EndUpdate();
            if (_folders.Items.Count > 0) _folders.SelectedIndex = 0;
            else _status.Text = "比較できるフォルダーがありません。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _status.Text = $"フォルダー一覧を読み込めませんでした: {exception.Message}";
        }
    }

    private async void FoldersOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_folders.SelectedItem is not HistoryFolderComparison folder || _loadingFolder) return;
        _loadingFolder = true;
        try
        {
            _status.Text = $"「{folder.FolderName}」のファイルを比較しています。";
            _folderDiff = await _service.CompareFolderAsync(folder, _closed.Token);
            RefreshFileRows();
            _status.Text = $"{_folderDiff.Files.Count:N0}ファイル、差分 {_folderDiff.ChangedFileCount:N0}件。履歴（切替前）と現在の配置先を比較しています。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _folderDiff = null;
            RefreshFileRows();
            _status.Text = $"ファイル比較に失敗しました: {exception.Message}";
        }
        finally
        {
            _loadingFolder = false;
        }
    }

    private void RefreshFileRows()
    {
        _files.BeginUpdate();
        try
        {
            _files.Items.Clear();
            var files = _folderDiff?.Files
                .Where(file => !_changedOnly.Checked || file.ChangeKind != HistoryFileChangeKind.Unchanged)
                .ToArray() ?? [];
            foreach (var file in files)
            {
                var item = new ListViewItem(file.DisplayChange);
                item.SubItems.Add(file.RelativePath);
                item.SubItems.Add(file.DisplayBeforeLength);
                item.SubItems.Add(file.DisplayCurrentLength);
                item.Tag = file;
                if (file.ChangeKind == HistoryFileChangeKind.Added) item.ForeColor = Color.DarkGreen;
                else if (file.ChangeKind == HistoryFileChangeKind.Removed) item.ForeColor = Color.DarkRed;
                else if (file.ChangeKind == HistoryFileChangeKind.Modified) item.ForeColor = Color.DarkBlue;
                _files.Items.Add(item);
            }
        }
        finally
        {
            _files.EndUpdate();
        }

        if (_files.Items.Count > 0) _files.Items[0].Selected = true;
        else
        {
            _beforeTitle.Text = "履歴（切替前）";
            _currentTitle.Text = "現在の配置先";
            _before.Clear();
            _current.Clear();
        }
    }

    private async void FilesOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_files.SelectedItems.Count == 0 || _files.SelectedItems[0].Tag is not HistoryFileDifference file) return;
        try
        {
            var beforeTask = _service.ReadBeforeFileAsync(file, _closed.Token);
            var currentTask = _service.ReadCurrentFileAsync(file, _closed.Token);
            await Task.WhenAll(beforeTask, currentTask);
            var before = await beforeTask;
            var current = await currentTask;
            _beforeTitle.Text = $"履歴（切替前）: {file.RelativePath}";
            _currentTitle.Text = $"現在の配置先: {file.RelativePath}";
            _before.Text = FormatContent(before);
            _current.Text = FormatContent(current);
            _status.Text = $"{file.DisplayChange}: {file.RelativePath}";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _status.Text = $"ファイルを読み込めませんでした: {exception.Message}";
        }
    }

    private static string FormatContent(HistoryFileContent content)
    {
        if (!content.Exists) return $"[ファイルなし] {content.Message}";
        if (content.IsBinary || content.IsTooLarge) return $"[{content.Message}]";
        return content.Text;
    }
}
