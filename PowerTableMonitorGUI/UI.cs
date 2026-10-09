using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using PowerTableMonitorGUI.Core;
using PowerTableMonitorGUI.Telemetry;
using System.Configuration;

namespace PowerTableMonitorGUI;

public partial class UI : Form
{
    private static void EnableDoubleBuffering(Control control)
    {
        if (control == null)
            return;

        var property = typeof(Control).GetProperty(
            "DoubleBuffered",
            BindingFlags.Instance | BindingFlags.NonPublic);

        property?.SetValue(control, true);
    }

    private static void SetTextIfChanged(Label label, string text)
    {
        if (!string.Equals(label.Text, text, StringComparison.Ordinal))
            label.Text = text;
    }

    private readonly SmuMonitor _monitor = new();
    private readonly TelemetryRecorder _recorder = new();

    private readonly Label _lblCpu = new();
    private readonly Label _lblTable = new();
    private readonly Label _lblBase = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblSamples = new();
    private readonly Button _btnRecord = new();
    private readonly DataGridView _grid = new();
    private readonly TabControl _tabs = new();
    private readonly FlowLayoutPanel _candidatePanel = new();

    private readonly Dictionary<int, List<(DataGridView Grid, int Row)>> _valueCells = new();
    private readonly Dictionary<int, List<(DataGridView Grid, int Row)>> _deltaCells = new();
    private readonly List<DataGridView> _dashboardTables = new();
    private float[]? _previous;
    private TelemetrySample? _latestSample;
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private sealed record DashboardField(int Offset, string Name, string Unit = "", string Format = "F3", string Formula = "none");

    private static readonly DashboardField[] PowerFields =
    {
        new(0x054, "APU Package Power", "W", "F4"),
    };

    private static readonly DashboardField[] SecondaryFields =
    {
        new(0x21C, "0x21C"),
        new(0x294, "0x294"),
        new(0x284, "0x284"),
    };

    private static readonly DashboardField[] PerformanceFields =
    {
        new(0x1D4, "0x1D4 - CPU DCLK"),
        new(0x220, "0x220", "%", "F1"),
        new(0x26C, "0x26C", "%", "F1"),
        new(0x204, "0x204 - NB Clock", "MHz", "F1"),
        new(0x08C, "0x08C - VDD NB", "V", "F3"),
    };

    private static readonly DashboardField[] DynamicFields =
    {
        new(0x1D4, "0x1D4 - CPU DCLK"),
        new(0x1DC, "0x1DC", "%", "F2"),
        new(0x1E4, "0x1E4", "%", "F2"),
        new(0x1EC, "0x1EC", "%", "F2"),
        new(0x21C, "0x21C"),
        new(0x228, "0x228"),
        new(0x22C, "0x22C"),
        new(0x230, "0x230"),
        new(0x234, "0x234"),
        new(0x284, "0x284"),
        new(0x290, "0x290"),
        new(0x294, "0x294"),
    };

    private static readonly DashboardField[] SmuConfigFields =
    {
        new(0x288, "FAST PPT", "W", "F2"),
        new(0x208, "SLOW PPT", "W", "F2"),
        //new(0x298, "STAPM limit", "W", "F2"),
        new(0x29C, "STAPM time", "s", "F1"),
    };

    public UI()
    {
        InitializeComponent();
        BuildUi();

        // UI updates are driven by a WinForms timer on the UI thread.
        // The SMU worker only publishes the newest sample; it never touches controls.
        _uiTimer.Interval = 50;
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        _monitor.SampleReady += Monitor_SampleReady;
        _monitor.Error += Monitor_Error;

        FormClosing += Form1_FormClosing;
        Shown += (_, _) => StartMonitor();
    }

    private void BuildUi()
    {
        Text = "PowerTable Monitor — Stoney Ridge / SMU";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 500);
        Size = new Size(980, 600);
        Font = new Font("Segoe UI", 8.25F);
        DoubleBuffered = true;
        EnableDoubleBuffering(this);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(2)
        };
        for (int i = 0; i < 4; i++)
            info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        _lblCpu.Text = "CPU: —";
        _lblTable.Text = "PM Table: —";
        _lblBase.Text = "DRAM: —";
        _lblStatus.Text = "Status: starting…";
        _lblSamples.Text = "Samples: 0";

        AddInfo(info, _lblCpu, 0, 0);
        AddInfo(info, _lblTable, 1, 0);
        AddInfo(info, _lblBase, 2, 0);
        AddInfo(info, _lblStatus, 3, 0);
        AddInfo(info, _lblSamples, 0, 1);

        _btnRecord.Text = "●  Start recording";
        _btnRecord.AutoSize = true;
        _btnRecord.Click += (_, _) => ToggleRecording();
        info.Controls.Add(_btnRecord, 3, 1);

        root.Controls.Add(info, 0, 0);

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        var btnRestart = new Button { Text = "Restart monitor", AutoSize = true };
        btnRestart.Click += (_, _) => RestartMonitor();
        toolbar.Controls.Add(btnRestart);

        var btnInterval = new Button { Text = "500 ms", AutoSize = true };
        btnInterval.Click += (_, _) =>
        {
            _monitor.IntervalMs = _monitor.IntervalMs switch
            {
                250 => 500,
                500 => 1000,
                _ => 250
            };
            btnInterval.Text = $"{_monitor.IntervalMs} ms";
        };
        toolbar.Controls.Add(btnInterval);

        var lblHint = new Label
        {
            Text = "  Live telemetry • 180 float fields • raw IEEE-754 HEX",
            AutoSize = true,
            Padding = new Padding(8, 7, 0, 0)
        };
        toolbar.Controls.Add(lblHint);

        root.Controls.Add(toolbar, 0, 1);

        _tabs.Dock = DockStyle.Fill;
        root.Controls.Add(_tabs, 0, 2);

        BuildDashboardTab();
        BuildTableTab();
    }

    private static void AddInfo(TableLayoutPanel panel, Control control, int col, int row)
    {
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(3);
        panel.Controls.Add(control, col, row);
    }

    private void BuildDashboardTab()
    {
        var page = new TabPage("Dashboard");
        _tabs.TabPages.Add(page);

        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        page.Controls.Add(scroll);

        var groups = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            BackColor = Color.White,
            AutoSize = true,
            Padding = new Padding(5),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        groups.Controls.Add(CreateDashboardGroup("Power-related", PowerFields));
        groups.Controls.Add(CreateDashboardGroup("Secondary", SecondaryFields));
        groups.Controls.Add(CreateDashboardGroup("Performance", PerformanceFields));
        groups.Controls.Add(CreateDashboardGroup("SMU configuration", SmuConfigFields));
        groups.Controls.Add(CreateDynamicGroup());

        scroll.Controls.Add(groups);
    }

    private GroupBox CreateDashboardGroup(string title, DashboardField[] fields)
    {
        var box = new GroupBox
        {
            Text = title,
            Width = 260,
            Height = 170,
            Margin = new Padding(4),
            Padding = new Padding(7, 17, 7, 5)
        };

        var grid = CreateDashboardGrid(3, fields.Length);
        grid.Columns.Add("Name", "");
        grid.Columns.Add("Value", "");
        grid.Columns.Add("Unit", "");
        grid.Columns[0].FillWeight = 65;
        grid.Columns[1].FillWeight = 45;
        grid.Columns[2].FillWeight = 28;

        for (int i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            grid.Rows.Add(field.Name, "—", field.Unit);
            grid.Rows[i].Cells[0].Style.Font = new Font("Segoe UI", 8.25F);
            grid.Rows[i].Cells[1].Style.Font = new Font("Consolas", 10.5F, FontStyle.Bold);
            grid.Rows[i].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Rows[i].Cells[2].Style.Alignment = DataGridViewContentAlignment.MiddleLeft;
            AddCellTarget(_valueCells, field.Offset, grid, i);
        }

        // Добавляем кастомные строки
        if (title == "Power-related")
        {
            int r = grid.Rows.Count;
            grid.Rows.Add("APU Temperature", "—", " C");
            grid.Rows[r].Cells[1].Style.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            grid.Rows[r].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            AddCellTarget(_valueCells, -2, grid, r, 1);

            r = grid.Rows.Count;
            grid.Rows.Add("STAPM package", "—", "W*min");
            grid.Rows[r].Cells[1].Style.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            grid.Rows[r].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            AddCellTarget(_valueCells, -3, grid, r, 1);

            r = grid.Rows.Count;
            grid.Rows.Add("STAPM estimated", "—", "min");
            grid.Rows[r].Cells[1].Style.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            grid.Rows[r].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            AddCellTarget(_valueCells, -4, grid, r, 1);
        }
        if (title == "SMU configuration")
        {
            int r = grid.Rows.Count;
            grid.Rows.Add("STAPM limit", "—", " W");
            grid.Rows[r].Cells[1].Style.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            grid.Rows[r].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            AddCellTarget(_valueCells, -5, grid, r, 1);
        }

        box.Controls.Add(grid);
        return box;
    }

    private GroupBox CreateDynamicGroup()
    {
        var box = new GroupBox
        {
            Text = "Dynamic / Load-dependent",
            Width = 462,
            Height = 382,
            Margin = new Padding(4),
            Padding = new Padding(7, 17, 7, 5)
        };

        var grid = CreateDashboardGrid(4, DynamicFields.Length + 1);
        grid.Columns.Add("Name", "");
        grid.Columns.Add("Value", "");
        grid.Columns.Add("Delta", "");
        grid.Columns.Add("Unit", "");
        grid.Columns[0].FillWeight = 24;
        grid.Columns[1].FillWeight = 31;
        grid.Columns[2].FillWeight = 31;
        grid.Columns[3].FillWeight = 14;

        for (int i = 0; i < DynamicFields.Length; i++)
        {
            var field = DynamicFields[i];
            grid.Rows.Add(field.Name, "—", "Δ —", field.Unit);
            grid.Rows[i].Cells[1].Style.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
            grid.Rows[i].Cells[2].Style.Font = new Font("Consolas", 8.5F);
            grid.Rows[i].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Rows[i].Cells[2].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            AddCellTarget(_valueCells, field.Offset, grid, i);
            AddCellTarget(_deltaCells, field.Offset, grid, i, 2);
        }

        int r = DynamicFields.Length;
        grid.Rows.Add("1DC + 1EC", "—", "Δ —", "%");
        grid.Rows[r].Cells[1].Style.Font = new Font("Consolas", 9.5F, FontStyle.Bold);
        grid.Rows[r].Cells[2].Style.Font = new Font("Consolas", 8.5F);
        grid.Rows[r].Cells[1].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        grid.Rows[r].Cells[2].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        // Synthetic row is kept separately; offsets are 0x1DC and 0x1EC.
        AddCellTarget(_valueCells, -1, grid, r, 1);
        AddCellTarget(_deltaCells, -1, grid, r, 2);

        box.Controls.Add(grid);
        return box;
    }

    private DataGridView CreateDashboardGrid(int columns, int rows)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackgroundColor = Color.White,
            GridColor = SystemColors.ControlLight,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersVisible = false,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToResizeColumns = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowTemplate = { Height = 22 },
            ScrollBars = ScrollBars.None,
            EnableHeadersVisualStyles = false,
            TabStop = false
        };

        EnableDoubleBuffering(grid);
        grid.DefaultCellStyle.SelectionBackColor = grid.DefaultCellStyle.BackColor;
        grid.DefaultCellStyle.SelectionForeColor = grid.DefaultCellStyle.ForeColor;
        grid.DefaultCellStyle.BackColor = Color.White;
        grid.DefaultCellStyle.ForeColor = SystemColors.ControlText;
        grid.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        grid.Font = new Font("Segoe UI", 8.25F);
        grid.RowCount = 0;
        _dashboardTables.Add(grid);
        return grid;
    }

    private static void AddCellTarget(
        Dictionary<int, List<(DataGridView Grid, int Row)>> map,
        int key, DataGridView grid, int row, int column = 1)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<(DataGridView Grid, int Row)>();
            map[key] = list;
        }
        // Store the column in the tuple by using a synthetic row index encoding.
        // Normal value cells use column 1; delta cells use column 2.
        list.Add((grid, row * 10 + column));
    }

    private static void SetGridCellIfChanged(DataGridView grid, int encodedRow, string text)
    {
        int row = encodedRow / 10;
        int column = encodedRow % 10;
        if (row < 0 || row >= grid.Rows.Count)
            return;
        var cell = grid.Rows[row].Cells[column];
        if (!Equals(cell.Value, text))
            cell.Value = text;
    }

    private static IEnumerable<DashboardField> EnumerateDashboardFields()
    {
        foreach (var field in PowerFields) yield return field;
        foreach (var field in SecondaryFields) yield return field;
        foreach (var field in PerformanceFields) yield return field;
        foreach (var field in SmuConfigFields) yield return field;
    }

    private void BuildTableTab()
    {
        var page = new TabPage("PM Table");
        _tabs.TabPages.Add(page);

        _grid.Dock = DockStyle.Fill;
        _grid.Font = new Font("Consolas", 8F);
        _grid.RowTemplate.Height = 20;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.VirtualMode = false;
        EnableDoubleBuffering(_grid);
        _grid.EnableHeadersVisualStyles = false;

        _grid.Columns.Add("Offset", "Offset");
        _grid.Columns.Add("Float", "Float");
        _grid.Columns.Add("Hex", "Raw HEX");
        _grid.Columns.Add("Delta", "Δ");

        _grid.Columns[0].FillWeight = 20;
        _grid.Columns[1].FillWeight = 35;
        _grid.Columns[2].FillWeight = 30;
        _grid.Columns[3].FillWeight = 25;

        for (int i = 0; i < 180; i++)
            _grid.Rows.Add($"0x{i * 4:X3}", "—", "—", "—");

        page.Controls.Add(_grid);
    }

    private void StartMonitor()
    {
        try
        {
            _monitor.Start();
            _lblCpu.Text = $"CPU: {_monitor.CpuName}";
            _lblTable.Text = $"PM Table: {_monitor.TableSize} bytes";
            _lblBase.Text = $"DRAM: 0x{_monitor.DramBaseAddress:X}";
            _lblStatus.Text = $"Status: monitoring ({_monitor.CodeName})";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Status: ERROR";
            MessageBox.Show(this, ex.ToString(), "SMU initialization failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartMonitor()
    {
        _monitor.Stop();
        _previous = null;
        StartMonitor();
    }

    private void Monitor_SampleReady(object? sender, TelemetrySample sample)
    {
        // This event is raised by the background SMU worker. Do not update WinForms
        // controls here. Just keep the newest sample; the UI timer consumes it.
        Interlocked.Exchange(ref _latestSample, sample);
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        var sample = Interlocked.Exchange(ref _latestSample, null);
        if (sample != null)
            ApplySample(sample);
    }

    private void Monitor_Error(object? sender, Exception ex)
    {
        if (IsDisposed)
            return;

        BeginInvoke(new Action(() =>
        {
            _lblStatus.Text = $"Status: ERROR — {ex.Message}";
        }));
    }

    private void ApplySample(TelemetrySample sample)
    {
        if (IsDisposed)
            return;

        // Update only the currently visible page. No WM_SETREDRAW calls are used,
        // so switching tabs remains entirely under the normal WinForms paint system.
        var selectedPage = _tabs.SelectedTab;
        bool dashboardVisible = selectedPage?.Text == "Dashboard";
        bool tableVisible = selectedPage?.Text == "PM Table";

            SetTextIfChanged(_lblStatus, $"Status: monitoring • {sample.Timestamp:HH:mm:ss.fff}");

            if (_recorder.IsRecording)
            {
                _recorder.Write(sample);
                SetTextIfChanged(_lblSamples, $"Samples: {_recorder.SampleCount:N0}");
            }

            // Dashboard uses compact DataGridViews, like the PM Table itself.
            // Updating cells in a buffered DataGridView avoids Label erase/repaint flicker.
            if (dashboardVisible)
            {
                foreach (var field in EnumerateDashboardFields())
                {
                    int index = field.Offset / 4;
                    if (index >= sample.Values.Length)
                        continue;

                    string text = sample.Values[index].ToString(field.Format, CultureInfo.InvariantCulture);
                    if (_valueCells.TryGetValue(field.Offset, out var targets))
                        foreach (var target in targets)
                            SetGridCellIfChanged(target.Grid, target.Row, text);
                }

                foreach (var field in DynamicFields)
                {
                    int index = field.Offset / 4;
                    if (index >= sample.Values.Length)
                        continue;

                    float value = sample.Values[index];
                    string valueText = value.ToString(field.Format, CultureInfo.InvariantCulture);
                    if (_valueCells.TryGetValue(field.Offset, out var valueTargets))
                        foreach (var target in valueTargets)
                            SetGridCellIfChanged(target.Grid, target.Row, valueText);

                    string deltaText = "Δ —";
                    if (_previous != null && index < _previous.Length)
                    {
                        float delta = value - _previous[index];
                        deltaText = $"Δ {delta:+0.000;-0.000;0}";
                    }

                    if (_deltaCells.TryGetValue(field.Offset, out var deltaTargets))
                        foreach (var target in deltaTargets)
                            SetGridCellIfChanged(target.Grid, target.Row, deltaText);
                }

                int i1 = 0x1DC / 4;
                int i2 = 0x1EC / 4;

                int index_stapm_limit = 0x298 / 4;
                int index_stapm_time = 0x29C / 4;
                int index_package_power = 0x054 / 4;
                int index_apu_temperature = 0x01C / 4;
                float apu_temperature = sample.Values[index_apu_temperature];
                float stapm_limit = sample.Values[index_stapm_limit] / 10;
                float stapm_time = sample.Values[index_stapm_time];
                float stapm_package = (stapm_limit * stapm_time) / 60;
                float package_power = sample.Values[index_package_power];
                float stapm_estimated_time = stapm_package / package_power;


                if (_valueCells.TryGetValue(-2, out var apu_temperature_targets))
                    foreach (var target in apu_temperature_targets)
                        SetGridCellIfChanged(target.Grid, target.Row, apu_temperature.ToString("F1", CultureInfo.InvariantCulture));
                if (_valueCells.TryGetValue(-3, out var stapm_package_targets))
                        foreach (var target in stapm_package_targets)
                            SetGridCellIfChanged(target.Grid, target.Row, stapm_package.ToString("F1", CultureInfo.InvariantCulture));
                string corrected_estimated_time = "-";
                if (package_power >= stapm_limit) corrected_estimated_time = stapm_estimated_time.ToString("F1", CultureInfo.InvariantCulture);
                if (_valueCells.TryGetValue(-4, out var stapm_estimated_time_targets))
                        foreach (var target in stapm_estimated_time_targets)
                            SetGridCellIfChanged(target.Grid, target.Row, corrected_estimated_time);
                if (_valueCells.TryGetValue(-5, out var stapm_limit_targets))
                    foreach (var target in stapm_limit_targets)
                        SetGridCellIfChanged(target.Grid, target.Row, stapm_limit.ToString("F1", CultureInfo.InvariantCulture));

            if (i1 < sample.Values.Length && i2 < sample.Values.Length)
                {
                    float sum = sample.Values[i1] + sample.Values[i2];
                    string sumText = sum.ToString("F2", CultureInfo.InvariantCulture);
                    if (_valueCells.TryGetValue(-1, out var sumTargets))
                        foreach (var target in sumTargets)
                            SetGridCellIfChanged(target.Grid, target.Row, sumText);

                    string deltaText = "Δ —";
                    if (_previous != null && i1 < _previous.Length && i2 < _previous.Length)
                    {
                        float previousSum = _previous[i1] + _previous[i2];
                        float delta = sum - previousSum;
                        deltaText = $"Δ {delta:+0.000;-0.000;0}";
                    }

                    if (_deltaCells.TryGetValue(-1, out var sumDeltaTargets))
                        foreach (var target in sumDeltaTargets)
                            SetGridCellIfChanged(target.Grid, target.Row, deltaText);
                }
            }

            // Updating 180 DataGridView rows is expensive. Only touch the grid when
            // its page is actually visible. DataGridView has its own double buffering.
            if (tableVisible)
            {
                for (int i = 0; i < sample.Values.Length && i < _grid.Rows.Count; i++)
                {
                    float value = sample.Values[i];
                    string floatText = value.ToString("F9", CultureInfo.InvariantCulture);
                    string hexText = $"0x{sample.RawValues[i]:X8}";

                    if (!Equals(_grid.Rows[i].Cells[1].Value, floatText))
                        _grid.Rows[i].Cells[1].Value = floatText;
                    if (!Equals(_grid.Rows[i].Cells[2].Value, hexText))
                        _grid.Rows[i].Cells[2].Value = hexText;

                    if (_previous != null && i < _previous.Length)
                    {
                        float delta = value - _previous[i];
                        string deltaText = delta.ToString("+0.000000;-0.000000;0", CultureInfo.InvariantCulture);
                        if (!Equals(_grid.Rows[i].Cells[3].Value, deltaText))
                            _grid.Rows[i].Cells[3].Value = deltaText;
                    }
                }
            }

            _previous = sample.Values;
        }

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _btnRecord.Text = "●  Start recording";
            _lblStatus.Text = "Status: monitoring";
            return;
        }

        string directory = Path.Combine(AppContext.BaseDirectory, "Telemetry");
        _recorder.Start(directory);

        _btnRecord.Text = "■  Stop recording";
        _lblSamples.Text = "Samples: 0";
        _lblStatus.Text = $"Recording: {Path.GetFileName(_recorder.FilePath)}";
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        _uiTimer.Dispose();
        _recorder.Stop();
        _monitor.Stop();
    }
}
