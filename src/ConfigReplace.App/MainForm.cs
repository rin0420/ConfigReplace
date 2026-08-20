using System.Collections.Specialized;
using System.ComponentModel;
using ConfigReplace.Models;
using ConfigReplace.ViewModels;

namespace ConfigReplace;

public sealed class MainForm : Form
{
    private readonly MainViewModel _viewModel = new();
    private readonly ComboBox _profiles = new();
    private readonly DataGridView _folders = CreateGrid();
    private readonly TextBox _profileDetail = CreateReadOnlyTextBox();
    private readonly TextBox _plan = CreateReadOnlyTextBox();
    private readonly Button _newButton = CreateButton("新規", 62);
    private readonly Button _editButton = CreateButton("編集", 62);
    private readonly Button _duplicateButton = CreateButton("複製", 62);
    private readonly Button _deleteButton = CreateButton("削除", 62);
    private readonly Button _previewButton = CreateButton("内容確認", 112);
    private readonly Button _switchButton = CreateButton("上書き実行", 112);
    private readonly Button _cancelButton = CreateButton("キャンセル", 112);
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Width = 120, Minimum = 0, Maximum = 100 };
    private readonly ToolStripStatusLabel _detail = new() { AutoSize = false, Width = 250, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm()
    {
        Text = "ConfigReplace - フォルダー上書きツール";
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(852, 480);
        MinimumSize = new Size(760, 450);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.Sizable;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
        tabs.TabPages.Add(BuildSwitchPage());

        var statusStrip = new StatusStrip { SizingGrip = true };
        statusStrip.Items.AddRange([_status, _progress, _detail]);
        Controls.Add(tabs);
        Controls.Add(statusStrip);

        WireEvents();
        // 初期読み込みがイベント購読より先に完了していても、一覧を画面へ反映する。
        RefreshProfiles();
        RefreshFolderRows();
        RefreshAll();
    }

    private TabPage BuildSwitchPage()
    {
        var page = new TabPage("プロファイル上書き") { Padding = new Padding(8), UseVisualStyleBackColor = true };
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 61, ColumnCount = 7, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        top.Controls.Add(new Label { Text = "プロファイル:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _profiles.DropDownStyle = ComboBoxStyle.DropDownList;
        _profiles.Dock = DockStyle.Fill;
        _profiles.Margin = new Padding(0, 2, 6, 2);
        top.Controls.Add(_profiles, 1, 0);
        top.Controls.Add(_newButton, 3, 0);
        top.Controls.Add(_editButton, 4, 0);
        top.Controls.Add(_duplicateButton, 5, 0);
        top.Controls.Add(_deleteButton, 6, 0);
        top.Controls.Add(new Label { Text = "処理内容:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        var state = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        state.DataBindings.Add(nameof(Label.Text), _viewModel, nameof(MainViewModel.ActiveStateText), false, DataSourceUpdateMode.Never);
        top.Controls.Add(state, 1, 1);
        top.SetColumnSpan(state, 6);

        ConfigureFolderGrid();
        var folderCaption = new Label { Text = "対象フォルダー一覧", Dock = DockStyle.Top, Height = 20, TextAlign = ContentAlignment.BottomLeft };
        _folders.Dock = DockStyle.Top;
        _folders.Height = 180;
        _profileDetail.Dock = DockStyle.Top;
        _profileDetail.Height = 25;
        _profileDetail.Multiline = false;

        var operation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 5, 0, 0) };
        operation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        operation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122));
        operation.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        operation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        operation.Controls.Add(new Label { Text = "上書き内容", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 0);
        _plan.Dock = DockStyle.Fill;
        _plan.Multiline = true;
        _plan.ScrollBars = ScrollBars.Vertical;
        operation.Controls.Add(_plan, 0, 1);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(6, 0, 0, 0), Margin = Padding.Empty
        };
        actions.Controls.AddRange([_previewButton, _switchButton, _cancelButton]);
        operation.Controls.Add(actions, 1, 1);

        page.Controls.Add(operation);
        page.Controls.Add(_profileDetail);
        page.Controls.Add(_folders);
        page.Controls.Add(folderCaption);
        page.Controls.Add(top);
        return page;
    }

    private void ConfigureFolderGrid()
    {
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "配置先", DataPropertyName = nameof(ProfileFolderDisplayRow.TargetRootPath), Width = 250 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "配置するフォルダー", DataPropertyName = nameof(ProfileFolderDisplayRow.FolderName), Width = 190 });
        _folders.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "上書き先", DataPropertyName = nameof(ProfileFolderDisplayRow.TargetPath), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 220 });
    }

    private void WireEvents()
    {
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        ((INotifyCollectionChanged)_viewModel.Profiles).CollectionChanged += (_, _) => RunOnUiThread(RefreshProfiles);
        ((INotifyCollectionChanged)_viewModel.SelectedProfileFolders).CollectionChanged += (_, _) => RunOnUiThread(RefreshFolderRows);
        _profiles.SelectedIndexChanged += (_, _) => _viewModel.SelectedProfile = _profiles.SelectedItem as FolderProfile;
        _newButton.Click += (_, _) => Execute(_viewModel.CreateProfileCommand);
        _editButton.Click += (_, _) => Execute(_viewModel.EditProfileCommand);
        _duplicateButton.Click += (_, _) => Execute(_viewModel.DuplicateProfileCommand);
        _deleteButton.Click += (_, _) => Execute(_viewModel.DeleteProfileCommand);
        _previewButton.Click += (_, _) => Execute(_viewModel.PreviewSwitchCommand);
        _switchButton.Click += (_, _) => Execute(_viewModel.SwitchCommand);
        _cancelButton.Click += (_, _) => Execute(_viewModel.CancelCommand);
        ObserveCommand(_viewModel.CreateProfileCommand);
        ObserveCommand(_viewModel.EditProfileCommand);
        ObserveCommand(_viewModel.DuplicateProfileCommand);
        ObserveCommand(_viewModel.DeleteProfileCommand);
        ObserveCommand(_viewModel.PreviewSwitchCommand);
        ObserveCommand(_viewModel.SwitchCommand);
        ObserveCommand(_viewModel.CancelCommand);
    }

    private static void Execute(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void ObserveCommand(System.Windows.Input.ICommand command)
        => command.CanExecuteChanged += (_, _) => RunOnUiThread(RefreshButtonStates);

    private void RunOnUiThread(Action action)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(() => ViewModelOnPropertyChanged(sender, e)); return; }
        RefreshAll();
    }

    private void RefreshAll()
    {
        _profileDetail.Text = _viewModel.SelectedProfileDetail.Replace("\n", "  |  ");
        _plan.Text = _viewModel.PlanSummary;
        _status.Text = _viewModel.StatusText;
        _detail.Text = _viewModel.DetailText;
        _detail.ToolTipText = _viewModel.DetailText;
        _progress.Value = Math.Clamp(_viewModel.ProgressPercent, 0, 100);
        RefreshButtonStates();
    }

    private void RefreshProfiles()
    {
        if (_profiles.Items.Count != _viewModel.Profiles.Count || !_profiles.Items.Cast<FolderProfile>().SequenceEqual(_viewModel.Profiles))
        {
            _profiles.BeginUpdate();
            _profiles.Items.Clear();
            foreach (var profile in _viewModel.Profiles) _profiles.Items.Add(profile);
            _profiles.DisplayMember = nameof(FolderProfile.Name);
            _profiles.EndUpdate();
        }
        if (!ReferenceEquals(_profiles.SelectedItem, _viewModel.SelectedProfile)) _profiles.SelectedItem = _viewModel.SelectedProfile;
    }

    private void RefreshFolderRows()
    {
        var selected = _folders.CurrentCell?.RowIndex ?? -1;
        _folders.DataSource = _viewModel.SelectedProfileFolders.ToList();
        if (selected >= 0 && selected < _folders.Rows.Count) _folders.CurrentCell = _folders.Rows[selected].Cells[0];
    }

    private void RefreshButtonStates()
    {
        _newButton.Enabled = _viewModel.CreateProfileCommand.CanExecute(null);
        _editButton.Enabled = _viewModel.EditProfileCommand.CanExecute(null);
        _duplicateButton.Enabled = _viewModel.DuplicateProfileCommand.CanExecute(null);
        _deleteButton.Enabled = _viewModel.DeleteProfileCommand.CanExecute(null);
        _previewButton.Enabled = _viewModel.PreviewSwitchCommand.CanExecute(null);
        _switchButton.Enabled = _viewModel.SwitchCommand.CanExecute(null);
        _cancelButton.Enabled = _viewModel.CancelCommand.CanExecute(null);
    }

    private static Button CreateButton(string text, int width) => new()
    {
        Text = text, Width = width, Height = 25, Margin = new Padding(2, 1, 2, 2), UseVisualStyleBackColor = true
    };

    private static TextBox CreateReadOnlyTextBox() => new()
    {
        ReadOnly = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D
    };

    private static DataGridView CreateGrid() => new()
    {
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AutoGenerateColumns = false, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.Fixed3D,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersHeight = 24, RowHeadersVisible = false, RowTemplate = { Height = 22 },
        ReadOnly = true, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
    };
}
