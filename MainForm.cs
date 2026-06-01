using System;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VSCode_C_;

public sealed partial class MainForm : Form
{
    // ── Constants ──
    // 移除了写死的行高，改用最小高度保证美观，布局交由 AutoSize 处理
    private const int MinRowHeight_Buttons = 40; 
    private const int SplitBarWidth = 8;
    private const int Panel1Min = 300;
    private const int Panel2Min = 200;
    private const int FormDefaultW = 1400;
    private const int FormDefaultH = 860;
    private const int FormMinW = 1000;
    private const int FormMinH = 650;
    private const int MarginSize = 4;
    private const int PadSize = 2;

    // ── Controls ──
    private SplitContainer _splitter = null!;
    private DataGridView _grid = null!;
    private RichTextBox _outputBox = null!;
    private Button _btnRun = null!;
    private Button _btnCancel = null!;
    private ProgressBar _progress = null!;
    private Label _lblSummary = null!;
    private Label _lblDuration = null!;
    private TextBox _txtToolsPath = null!;
    private NumericUpDown _numFontSize = null!; // 新增：字体大小调节器
    
    private CancellationTokenSource? _cts;
    private int _totalChecks;
    private bool _splitterDistanceSet;
    private float _currentFontSize = 10f; // 默认字体大小

    public MainForm()
    {
        InitializeForm();
    }

    private void InitializeForm()
    {
        SuspendLayout();

        // ── Form ──
        Text = "Preload Process Diagnostic Tool";
        Size = new Size(FormDefaultW, FormDefaultH);
        MinimumSize = new Size(FormMinW, FormMinH);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", _currentFontSize);
        BackColor = SystemColors.Control;

        // ═══════════════════════════════════════════════════════════
        //  Main table: 4 rows
        //  0=tools (AutoSize)  1=splitter(100%)  2=buttons (AutoSize)  3=summary (AutoSize)
        // ═══════════════════════════════════════════════════════════
        var mainTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(PadSize)
        };
        mainTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        
        // 关键修复：改为 AutoSize，让行高根据内容自适应，不再遮挡
        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize)); 
        mainTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // ── Row 0: Tools path ──
        mainTable.Controls.Add(BuildToolsRow(), 0, 0);

        // ── Row 1: SplitContainer ──
        _splitter = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = SplitBarWidth,
            Margin = new Padding(MarginSize)
        };
        _splitter.Panel1.Controls.Add(BuildLeftPanel());
        _splitter.Panel2.Controls.Add(BuildRightPanel());
        mainTable.Controls.Add(_splitter, 0, 1);

        // ── Row 2: Buttons + Progress ──
        mainTable.Controls.Add(BuildButtonsRow(), 0, 2);

        // ── Row 3: Summary + Duration ──
        mainTable.Controls.Add(BuildSummaryRow(), 0, 3);

        Controls.Add(mainTable);

        Shown += OnFirstShown;
        ResumeLayout(false);
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnFirstShown(object? sender, EventArgs e)
    {
        if (_splitterDistanceSet) return;
        _splitterDistanceSet = true;

        _splitter.Panel1MinSize = Panel1Min;
        _splitter.Panel2MinSize = Panel2Min;

        var half = (_splitter.Width - _splitter.SplitterWidth) / 2;
        if (half >= Panel1Min && (_splitter.Width - half - _splitter.SplitterWidth) >= Panel2Min)
            _splitter.SplitterDistance = half;
    }

    // ═══════════════════════════════════════════════════════════
    //  Row builders
    // ═══════════════════════════════════════════════════════════

    private TableLayoutPanel BuildToolsRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true, // 关键修复：自适应高度
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 6, // 增加列以容纳字体调节器
            RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Tools Dir Label
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // TextBox
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Browse Btn
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // CheckBox
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Font Label
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // Font NUD

        var lbl = new Label
        {
            Text = "Tools Dir:",
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            Margin = new Padding(MarginSize)
        };

        _txtToolsPath = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = PreloadProcessChecker.ResolveDefaultToolsPath(), // 请确保此类存在或替换为你自己的逻辑
            Margin = new Padding(MarginSize)
        };

        var btnBrowse = new Button
        {
            Text = "Browse...",
            AutoSize = true,
            Margin = new Padding(0, MarginSize, MarginSize, MarginSize),
            Padding = new Padding(10, 0, 10, 0)
        };
        btnBrowse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select directory containing SACVT.exe, DCVT.exe, MDAVT.exe"
            };
            if (!string.IsNullOrEmpty(_txtToolsPath.Text) && Directory.Exists(_txtToolsPath.Text))
                dlg.SelectedPath = _txtToolsPath.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _txtToolsPath.Text = dlg.SelectedPath;
        };

        bool isAdmin = IsRunningAsAdmin();
        var lblAdmin = new Label
        {
            Text = isAdmin ? "Administrator" : "NOT Admin (limited)",
            AutoSize = true,
            ForeColor = isAdmin ? Color.ForestGreen : Color.DarkOrange,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(MarginSize)
        };

        var lblFont = new Label
        {
            Text = "Font Size:",
            Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = true,
            Margin = new Padding(MarginSize * 4, MarginSize, MarginSize, MarginSize)
        };

        // 字体调节器
        _numFontSize = new NumericUpDown
        {
            Minimum = 8,
            Maximum = 32,
            Value = (decimal)_currentFontSize,
            Width = 60,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(MarginSize)
        };
        _numFontSize.ValueChanged += NumFontSize_ValueChanged;

        row.Controls.Add(lbl, 0, 0);
        row.Controls.Add(_txtToolsPath, 1, 0);
        row.Controls.Add(btnBrowse, 2, 0);
        row.Controls.Add(lblAdmin, 3, 0);
        row.Controls.Add(lblFont, 4, 0);
        row.Controls.Add(_numFontSize, 5, 0);
        
        return row;
    }

    private TableLayoutPanel BuildLeftPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            GridColor = Color.FromArgb(230, 230, 230),
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            // 关键修复：改为 AllCells 自动适应行高
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, 
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            Margin = new Padding(0)
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "#",
            Name = "colIdx",
            FillWeight = 4,
            MinimumWidth = 40,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Check Item",
            Name = "colName",
            FillWeight = 28,
            MinimumWidth = 120
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Status",
            Name = "colStatus",
            FillWeight = 9,
            MinimumWidth = 60,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter, Font = new Font("Segoe UI", _currentFontSize, FontStyle.Bold) }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Message / Detail",
            Name = "colMessage",
            FillWeight = 59,
            MinimumWidth = 140,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } // 允许长文本换行
        });

        panel.Controls.Add(_grid, 0, 0);
        return panel;
    }

    private TableLayoutPanel BuildRightPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _outputBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.White,
            ForeColor = Color.Black,
            Font = new Font("Consolas", _currentFontSize), // 使用等宽字体
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap = true,
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            Margin = new Padding(0)
        };

        panel.Controls.Add(_outputBox, 0, 0);
        return panel;
    }

    private TableLayoutPanel BuildButtonsRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true, // 关键修复：自适应高度
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, PadSize * 3));

        _btnRun = new Button
        {
            Text = "Run All Checks",
            AutoSize = true,
            MinimumSize = new Size(150, MinRowHeight_Buttons),
            BackColor = Color.FromArgb(0, 120, 212),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", _currentFontSize, FontStyle.Bold),
            Margin = new Padding(MarginSize)
        };
        _btnRun.FlatAppearance.BorderSize = 0;
        _btnRun.Click += async (_, _) => await RunChecksAsync();

        _btnCancel = new Button
        {
            Text = "Cancel",
            AutoSize = true,
            MinimumSize = new Size(100, MinRowHeight_Buttons),
            Enabled = false,
            Margin = new Padding(0, MarginSize, MarginSize, MarginSize)
        };
        _btnCancel.Click += (_, _) => _cts?.Cancel();

        _progress = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100,
            Height = MinRowHeight_Buttons - (MarginSize * 2),
            Anchor = AnchorStyles.Left | AnchorStyles.Right, // 让进度条居中拉伸
            Margin = new Padding(MarginSize)
        };

        row.Controls.Add(_btnRun, 0, 0);
        row.Controls.Add(_btnCancel, 1, 0);
        row.Controls.Add(_progress, 2, 0);
        return row;
    }

    private TableLayoutPanel BuildSummaryRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true, // 关键修复：自适应高度
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lblSummary = new Label
        {
            Text = "Pass: 0  |  Fail: 0  |  Warn: 0  |  Skip: 0",
            AutoSize = true,
            Font = new Font("Segoe UI", _currentFontSize + 1, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(MarginSize, PadSize, MarginSize, PadSize)
        };

        _lblDuration = new Label
        {
            Text = "Duration: --",
            AutoSize = true,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(MarginSize, PadSize, MarginSize, PadSize)
        };

        row.Controls.Add(_lblSummary, 0, 0);
        row.Controls.Add(new Label { AutoSize = false, Margin = new Padding(0) }, 1, 0); // spacer
        row.Controls.Add(_lblDuration, 2, 0);
        return row;
    }

    // ═══════════════════════════════════════════════════════════
    //  字体改变事件处理
    // ═══════════════════════════════════════════════════════════
    private void NumFontSize_ValueChanged(object? sender, EventArgs e)
    {
        float newSize = (float)_numFontSize.Value;
        if (newSize == _currentFontSize) return;
        _currentFontSize = newSize;

        // 1. 更新主窗体基础字体（会自动下发给大部分标准控件）
        this.Font = new Font("Segoe UI", _currentFontSize);

        // 2. 特殊字体的控件单独更新
        _btnRun.Font = new Font("Segoe UI", _currentFontSize, FontStyle.Bold);
        _lblSummary.Font = new Font("Segoe UI", _currentFontSize + 1, FontStyle.Bold);
        
        // 3. 右侧日志框使用等宽字体
        _outputBox.Font = new Font("Consolas", _currentFontSize);

        // 4. DataGridView 强制刷新字体
        _grid.DefaultCellStyle.Font = new Font("Segoe UI", _currentFontSize);
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", _currentFontSize);
        
        // 由于 DataGridView 的 Status 列加粗了，需单独维持
        if (_grid?.Columns != null && _grid.Columns.Contains("colStatus"))
        {
            var statusColumn = _grid.Columns["colStatus"];
            if (statusColumn?.DefaultCellStyle != null)
            {
                statusColumn.DefaultCellStyle.Font = new Font("Segoe UI", _currentFontSize, FontStyle.Bold);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  Business logic (保持不变，已保留你的核心逻辑结构)
    // ═══════════════════════════════════════════════════════════

    private async Task RunChecksAsync()
    {
        _btnRun.Enabled = false;
        _btnCancel.Enabled = true;
        _progress.Value = 0;
        _grid.Rows.Clear();
        _outputBox.Clear();
        _totalChecks = 0;
        Text = "Running... — Preload Process Diagnostic Tool";
        _lblSummary.Text = "Pass: 0  |  Fail: 0  |  Warn: 0  |  Skip: 0";
        _lblDuration.Text = "Duration: --";

        _cts = new CancellationTokenSource();
        var logger = new FormLogger(this);

        var toolsPath = _txtToolsPath.Text.Trim();
        if (string.IsNullOrEmpty(toolsPath) || !Directory.Exists(toolsPath))
            toolsPath = AppContext.BaseDirectory;

        try
        {
            LogSection($"Hardware Diagnostic — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            LogInfo($"OS: {Environment.OSVersion}");
            LogInfo($"Machine: {Environment.MachineName}");
            LogInfo($"Tools path: {toolsPath}");

            var engine = new DiagnosticEngine(logger);
            engine.RegisterAll(new PreloadProcessChecker(toolsPath));

            var startTime = DateTime.Now;
            var report = await Task.Run(() => engine.RunAsync(_cts.Token), _cts.Token);

            int pass = report.Results.Sum(r => r.PassCount);
            int fail = report.Results.Sum(r => r.FailCount);
            int warn = report.Results.Sum(r => r.WarningCount);
            int skip = report.Results.Sum(r => r.SubChecks.Count(s => s.Status == CheckStatus.Skipped));

            _progress.Value = 100;
            var dur = DateTime.Now - startTime;

            // Populate grid rows from final results (exactly one row per sub-check)
            int idx = 0;
            foreach (var r in report.Results)
            {
                foreach (var sc in r.SubChecks)
                {
                    idx++;
                    var name = sc.Name;
                    var st = sc.Status;
                    var msg = st == CheckStatus.Pass ? sc.Message : $"{sc.Message} | {sc.Detail ?? ""}";
                    AddResult(name, st, msg);
                }
            }

            Update(() =>
            {
                _lblSummary.Text = $"Pass: {pass}  |  Fail: {fail}  |  Warn: {warn}  |  Skip: {skip}";
                _lblDuration.Text = $"Duration: {dur.TotalSeconds:F1}s";
                Text = fail > 0 ? $"FAIL — {fail} check(s) failed — Preload Process Diagnostic Tool"
                    : warn > 0 ? $"WARNING — {warn} warning(s) — Preload Process Diagnostic Tool"
                    : pass > 0 ? "PASS — Preload Process Diagnostic Tool"
                    : "NO DATA — Preload Process Diagnostic Tool";
            });
        }
        catch (OperationCanceledException)
        {
            LogInfo("Diagnostic cancelled by user.");
            Text = "Cancelled — Preload Process Diagnostic Tool";
        }
        catch (Exception ex)
        {
            LogInfo($"FATAL ERROR: {ex}");
            Text = "FATAL ERROR — Preload Process Diagnostic Tool";
        }
        finally
        {
            Update(() => { _btnRun.Enabled = true; _btnCancel.Enabled = false; });
            _cts.Dispose();
            _cts = null;
        }
    }

    public void LogSection(string title) => Update(() =>
    {
        _outputBox.AppendText($"\n{new string('═', 52)}\n  {title}\n{new string('═', 52)}\n\n");
        ScrollOutput();
    });


    public void AddResult(string checkName, CheckStatus status, string detail) => Update(() =>
    {
        _totalChecks++;
        var rowIdx = _grid.Rows.Add(_totalChecks.ToString(), checkName, StatusText(status), detail);
        var row = _grid.Rows[rowIdx];

        row.DefaultCellStyle.BackColor = status switch
        {
            CheckStatus.Pass => Color.FromArgb(228, 248, 228),
            CheckStatus.Fail => Color.FromArgb(255, 212, 212),
            CheckStatus.Warning => Color.FromArgb(255, 250, 200),
            _ => Color.FromArgb(240, 240, 240)
        };
        row.DefaultCellStyle.ForeColor = status switch
        {
            CheckStatus.Pass => Color.FromArgb(0, 100, 0),
            CheckStatus.Fail => Color.FromArgb(180, 0, 0),
            CheckStatus.Warning => Color.FromArgb(140, 120, 0),
            _ => Color.Gray
        };

        if (_grid.Rows.Count > 0 && _grid.DisplayedRowCount(false) > 0)
            _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;

        _progress.Value = Math.Min(100, (int)(_totalChecks / 15.0 * 100));
    });

    public void LogInfo(string message) => Update(() =>
    {
        _outputBox.AppendText($"[INFO] {message}\n");
        ScrollOutput();
    });

    public void LogRaw(string line) => Update(() =>
    {
        _outputBox.AppendText($"       {line}\n");
        ScrollOutput();
    });

    private void ScrollOutput()
    {
        _outputBox.SelectionStart = _outputBox.TextLength;
        _outputBox.ScrollToCaret();
    }

    private static string StatusText(CheckStatus s) => s switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Fail => "FAIL",
        CheckStatus.Warning => "WARN",
        _ => "SKIP"
    };

    private void Update(Action action)
    {
        if (InvokeRequired) BeginInvoke(action);
        else action();
    }
}