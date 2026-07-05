#nullable enable
#pragma warning disable CS8602 // Suppress possible null reference dereference warnings in designer-initialized WinForms partial class

using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Text;
using Npgsql;
using BilvaskRegistrering.Worker.Data;
using BilvaskRegistrering.Worker.Services;
using PlateOfflineQueue = BilvaskRegistrering.Worker.Offline.OfflineWashQueue;
using BilvaskRegistrering.Worker.Models;

namespace BilvaskRegistrering.Worker;

// UI layout is now designer-driven (WorkerForm.Designer.cs).
// Keep runtime logic + event wiring here.
public sealed partial class WorkerForm : Form
{

    // Responsive UI scaling (different resolutions / DPI)
    private float _uiScaleLast = -1f;

    private readonly AppConfig _cfg;
    private readonly WashAuditCsv _audit;

    // NOTE: All WinForms controls (ComboBox, Labels, Panels, etc.) are declared
    // and created in WorkerForm.Designer.cs so you can edit the layout in the designer.
    private bool _suppressDateEvents;

    private PictureBox? _pbPlate;
    private const double PlateLeftInsetRatio = 0.135; // reserved for the blue band in SkiltN.png
    private const double PlateRightInsetRatio = 0.05;  // right margin inside the plate
    private Font? _plateFont;

    private readonly System.Windows.Forms.Timer _dbTimer = new();
    private readonly ToolTip _dbStatusToolTip = new();
    private string? _lastDbError;
    private bool _vehicleLookupSynced;

    // Manual mode: "Kun sjekket" (no camera pass)
    private bool _manualSjekketMode;

    private bool _autoRefreshSuspended;
    private bool _savedFiltersCaptured;
    private DateTime _savedFra;
    private DateTime _savedTil;
    private object? _savedPeriode;
    private bool _savedOnlyUnconfirmed;
    private sealed class VehiclePickItem
    {
        public string Internnr { get; init; } = "";
        public string RegNr { get; init; } = "";
        public override string ToString() => Internnr;
    }
    private readonly List<VehiclePickItem> _vehiclePick = new();
    private readonly Dictionary<string, string> _internToReg = new();
    private readonly HashSet<string> _knownPlates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Color> _ansattAutoColorCache = new(StringComparer.OrdinalIgnoreCase);

    // Preserve user inputs (type/comment) while the grid auto-refreshes
    private long? _selectedWashId;
    private bool _userEditedInputs;
    // When the list refreshes in background, do not let the selected grid row
    // overwrite the operator's current Type vask choice.
    private bool _suppressTypeVaskSyncFromGrid;
    private volatile bool _isClosing;
    // Qualify Timer to avoid ambiguity with System.Threading.Timer (implicit usings)
    private readonly System.Windows.Forms.Timer _timer = new();

    // Plate scanning + offline flush
    private System.Threading.Timer? _flushTimer;
    private WorkerCameraScanner? _scanner;
    private PlateOfflineQueue? _offline;

    // Simple de-dup of repeated plates
    private readonly object _plateLock = new();
    private string? _lastPlate;
    private DateTime _lastPlateAtUtc;
    private readonly PlateDedupeWindow _dedupe30min = new(TimeSpan.FromMinutes(30));

    private static string NormalizePlate(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return string.Empty;
        return plate.Trim().Replace(" ", "").ToUpperInvariant();
    }

    /// <summary>
    /// Canonicalize a plate candidate from the camera/ANPR.
    /// Removes non-alphanumeric chars and fixes common OCR confusions in the numeric part.
    /// </summary>
    private static string CanonicalizePlateCandidate(string? plate)
    {
        var s = NormalizePlate(plate);
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;

        // Some cameras send '-' or '.'
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        s = sb.ToString();

        if (s.Length >= 6 && char.IsLetter(s[0]) && char.IsLetter(s[1]))
        {
            var sb2 = new StringBuilder(s.Length);
            sb2.Append(s[0]);
            sb2.Append(s[1]);
            for (int i = 2; i < s.Length; i++)
            {
                var c = s[i];
                if (c == 'O') c = '0';
                else if (c == 'I') c = '1';
                sb2.Append(c);
            }
            s = sb2.ToString();
        }

        return s;
    }

    /// <summary>
    /// Filter: accept only typical Norwegian plate formats used in this system.
    /// This suppresses logo reads like SKYSS (e.g. SKUS40, SKVFG, 0SS00...).
    /// </summary>
    private static bool IsLikelyNorwegianPlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return false;

        // suppress common logo reads like SKYSS (even if OCR confuses characters)
        if (plate.Contains("SKYSS", StringComparison.OrdinalIgnoreCase)) return false;

        // We only accept the normal format used in this system: 2 letters + 5 digits (7 chars).
        // This blocks shortened/misread plates like EB7569 / CB7574 etc.
        if (plate.Length != 7) return false;

        if (!char.IsLetter(plate[0]) || !char.IsLetter(plate[1])) return false;
        for (int i = 2; i < 7; i++)
            if (!char.IsDigit(plate[i])) return false;

        return true;
    }


    private static string FormatPlateForDisplay(string? plate)
    {
        var norm = NormalizePlate(plate);
        if (string.IsNullOrWhiteSpace(norm)) return "-";

        // Typical Norwegian plates in this system: 2 letters + 5 digits, show with a space: "EC 50316"
        if (norm.Length == 7 && char.IsLetter(norm[0]) && char.IsLetter(norm[1]) && norm.Skip(2).All(char.IsDigit))
            return norm.Substring(0, 2) + " " + norm.Substring(2);

        return norm;
    }

    private static IReadOnlyDictionary<string, string> GetDefaultTypeVaskColorMap()
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Innvendig/utvendig vask"] = "#2DB34A",
            ["Innvendig/utvendig vask/fylt diesel"] = "#111111",
            ["Kun utvendig vask"] = "#A6D608",
            ["Kun innvendig vask"] = "#7F7F7F",
            ["Heldag-nedvask"] = "#B97A57",
            ["Fylt diesel"] = "#111111",
            ["Kun sjekket"] = "#FFE300",
            ["Kun gjennom kjøring"] = "#1E9FE6"
        };

    private Color GetTypeVaskColor(string? typeVask)
    {
        if (string.IsNullOrWhiteSpace(typeVask))
            return Color.Empty;

        if (_cfg.TypeVaskColorMap.TryGetValue(typeVask.Trim(), out var configured)
            && AppConfig.TryParseColor(configured, out var configuredColor))
        {
            return configuredColor;
        }

        foreach (var kv in GetDefaultTypeVaskColorMap())
        {
            if (string.Equals(kv.Key, typeVask.Trim(), StringComparison.OrdinalIgnoreCase)
                && AppConfig.TryParseColor(kv.Value, out var fallbackColor))
            {
                return fallbackColor;
            }
        }

        return Color.Empty;
    }

    private Color GetAnsattColor(string? ansatt)
    {
        if (string.IsNullOrWhiteSpace(ansatt))
            return Color.Empty;

        var key = ansatt.Trim();

        if (_cfg.AnsattColorMap.TryGetValue(key, out var configured)
            && AppConfig.TryParseColor(configured, out var configuredColor))
        {
            return configuredColor;
        }

        if (_ansattAutoColorCache.TryGetValue(key, out var cached))
            return cached;

        var palette = new[]
        {
            Color.FromArgb(138, 43, 226),
            Color.FromArgb(233, 30, 99),
            Color.FromArgb(30, 136, 229),
            Color.FromArgb(46, 125, 50),
            Color.FromArgb(255, 143, 0),
            Color.FromArgb(0, 137, 123),
            Color.FromArgb(106, 27, 154),
            Color.FromArgb(198, 40, 40)
        };

        var idx = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(key)) % palette.Length;
        var color = palette[idx];
        _ansattAutoColorCache[key] = color;
        return color;
    }

    private static Color Blend(Color a, Color b, double amount)
    {
        amount = Math.Max(0d, Math.Min(1d, amount));
        var r = (int)Math.Round(a.R + ((b.R - a.R) * amount));
        var g = (int)Math.Round(a.G + ((b.G - a.G) * amount));
        var bb = (int)Math.Round(a.B + ((b.B - a.B) * amount));
        return Color.FromArgb(r, g, bb);
    }

    private static Color GetContrastColor(Color color)
    {
        var luminance = ((0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B)) / 255d;
        return luminance >= 0.62 ? Color.Black : Color.White;
    }

    private void ApplyTypeVaskLegendColors()
    {
        void Apply(RadioButton rb)
        {
            var color = GetTypeVaskColor(rb.Text);
            rb.ForeColor = color.IsEmpty ? SystemColors.ControlText : color;
        }

        Apply(_rbInOut);
        Apply(_rbInOutDiesel);
        Apply(_rbOut);
        Apply(_rbIn);
        Apply(_rbHeldag);
        Apply(_rbDiesel);
        Apply(_rbSjekket);
        Apply(_rbGjennom);
    }

    private void ApplyGridColorStyling()
    {
        if (_grid.Columns.Count == 0)
            return;

        DataGridViewColumn? typeCol = null;
        DataGridViewColumn? ansattCol = null;
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (string.Equals(col.DataPropertyName, "TypeVask", StringComparison.OrdinalIgnoreCase))
                typeCol = col;
            else if (string.Equals(col.DataPropertyName, "Ansatt", StringComparison.OrdinalIgnoreCase))
                ansattCol = col;
        }

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.DataBoundItem is not WorkerWashRow item)
                continue;

            if (typeCol is not null)
            {
                var cell = row.Cells[typeCol.Index];
                var color = GetTypeVaskColor(item.TypeVask);
                if (!color.IsEmpty)
                {
                    cell.Style.BackColor = color;
                    cell.Style.ForeColor = GetContrastColor(color);
                    cell.Style.SelectionBackColor = color;
                    cell.Style.SelectionForeColor = GetContrastColor(color);
                }
                else
                {
                    cell.Style.BackColor = Color.Empty;
                    cell.Style.ForeColor = Color.Empty;
                    cell.Style.SelectionBackColor = Color.Empty;
                    cell.Style.SelectionForeColor = Color.Empty;
                }
            }

            if (ansattCol is not null)
            {
                var cell = row.Cells[ansattCol.Index];
                var color = GetAnsattColor(item.Ansatt);
                if (!color.IsEmpty)
                {
                    var soft = Blend(color, Color.White, 0.88d);
                    cell.Style.BackColor = soft;
                    cell.Style.ForeColor = color;
                    cell.Style.SelectionBackColor = Blend(color, Color.White, 0.25d);
                    cell.Style.SelectionForeColor = GetContrastColor(Blend(color, Color.White, 0.25d));
                }
                else
                {
                    cell.Style.BackColor = Color.Empty;
                    cell.Style.ForeColor = Color.Empty;
                    cell.Style.SelectionBackColor = Color.Empty;
                    cell.Style.SelectionForeColor = Color.Empty;
                }
            }
        }

        try { _grid.Refresh(); } catch { }
    }

    private static Image LoadImageNoLock(string path)
    {
        // Clone the bitmap so the file isn't locked on disk.
        using var img = Image.FromFile(path);
        return new Bitmap(img);
    }

    private Image? TryLoadPlateImage()
    {
        try
        {
            // 1) External override: Documents\BilvaskRegistrering\assets\SkiltN.png
            var external = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "BilvaskRegistrering",
                "assets",
                "SkiltN.png");
            if (File.Exists(external))
                return LoadImageNoLock(external);

            // 2) Content file next to EXE: .\Assets\SkiltN.png (current project layout)
            var localAssets = Path.Combine(AppContext.BaseDirectory, "Assets", "SkiltN.png");
            if (File.Exists(localAssets))
                return LoadImageNoLock(localAssets);

            // 3) Backward compatible: .\Doc\SkiltN.png
            var localDoc = Path.Combine(AppContext.BaseDirectory, "Doc", "SkiltN.png");
            if (File.Exists(localDoc))
                return LoadImageNoLock(localDoc);

            // 4) Optional embedded resource fallback (if present)
            var asm = typeof(WorkerForm).Assembly;
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("Doc.SkiltN.png", StringComparison.OrdinalIgnoreCase)
                                     || n.EndsWith("SkiltN.png", StringComparison.OrdinalIgnoreCase));
            if (resName is not null)
            {
                using var s = asm.GetManifestResourceStream(resName);
                if (s is not null)
                {
                    using var tmp = Image.FromStream(s);
                    return new Bitmap(tmp);
                }
            }
        }
        catch
        {
            // Ignore and fall back to simplified plate.
        }

        return null;
    }

    private void LayoutPlateText()
    {
        if (_pbPlate is null) return;
        var pb = _pbPlate;
        if (pb.Width <= 0 || pb.Height <= 0) return;

        // Compute the "white area" inside the plate image.
        var insetLeft = (int)Math.Round(pb.Width * PlateLeftInsetRatio);
        var insetRight = (int)Math.Round(pb.Width * PlateRightInsetRatio);
        var insetTop = (int)Math.Round(pb.Height * 0.16);
        var insetBottom = (int)Math.Round(pb.Height * 0.16);

        var rect = new Rectangle(
            Math.Max(0, insetLeft),
            Math.Max(0, insetTop),
            Math.Max(1, pb.Width - insetLeft - insetRight),
            Math.Max(1, pb.Height - insetTop - insetBottom)
        );

        _lblPlate.Bounds = rect;

        var text = _lblPlate.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || text == "–" || text == "-")
            return;

        // Auto-fit font size to the available rectangle.
        const int maxSize = 66;
        const int minSize = 18;
        Font? best = null;
        for (var size = maxSize; size >= minSize; size--)
        {
            using var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point);
            var measured = TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding);
            if (measured.Width <= rect.Width - 8 && measured.Height <= rect.Height - 8)
            {
                best = new Font(f.FontFamily, f.Size, f.Style, GraphicsUnit.Point);
                break;
            }
        }

        if (best is not null)
        {
            var old = _plateFont;
            _plateFont = best;
            _lblPlate.Font = _plateFont;
            try { old?.Dispose(); } catch { }
        }
    }

    private void MarkUserEditedInputs()
    {
        _userEditedInputs = true;
    }

    public WorkerForm()
    {
        _cfg = AppConfig.Load();
        _audit = new WashAuditCsv(_cfg.DokumentFolder);

        InitializeComponent();

        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        ConfigureUiRuntime();

        Resize += (_, __) => ApplyResponsiveLayout();

        Shown += async (_, __) =>
        {
            AlignTopBars();
            ApplyResponsiveLayout();

            _chkOnlyUnconfirmed.Checked = _cfg.ShowOnlyUnconfirmed;

            InitDateFilterDefaults();

            StartDbStatusTimer();

            await ReloadAllAsync();
            StartAutoRefresh();
        };
        FormClosed += (_, __) =>
        {
            _isClosing = true;
            _timer.Stop();
            _dbTimer.Stop();
        };
    }


    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        try { _scanner?.Dispose(); } catch { }
        try { _flushTimer.Dispose(); } catch { }
        base.OnFormClosing(e);
    }

    private void ConfigureUiRuntime()
    {
        // Combo / filter defaults
        _cmbAnsatt.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbPeriode.DropDownStyle = ComboBoxStyle.DropDownList;

        // Manual Internnr pick: make it fast (type-ahead) and stable
        _cmbInternPick.DropDownStyle = ComboBoxStyle.DropDown;
        _cmbInternPick.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _cmbInternPick.AutoCompleteSource = AutoCompleteSource.ListItems;
        _cmbInternPick.IntegralHeight = false;
        _cmbInternPick.DropDownHeight = 420;
        if (_cmbPeriode.Items.Count == 0)
            _cmbPeriode.Items.AddRange(new object[] { "Skift", "Dag", "Uke", "Måned", "Periode" });

        _dtpFra.Format = DateTimePickerFormat.Custom;
        _dtpFra.CustomFormat = "yyyy-MM-dd";
        _dtpTil.Format = DateTimePickerFormat.Custom;
        _dtpTil.CustomFormat = "yyyy-MM-dd";

        // Events
        _btnSettings.Click += (_, __) => OpenSettings();
        _cmbPeriode.SelectedIndexChanged += async (_, __) => await OnDateFilterChangedAsync();
        _dtpFra.ValueChanged += async (_, __) => await OnDateFilterChangedAsync(fromFraChanged: true);
        _dtpTil.ValueChanged += async (_, __) => await OnDateFilterChangedAsync(fromFraChanged: false);

        _chkOnlyUnconfirmed.CheckedChanged += async (_, __) => await LoadWashesAsync();
        _btnRefresh.Click += async (_, __) => await ReloadAllAsync();
        _btnConfirm.Click += async (_, __) => await ConfirmSelectedAsync();
        _btnLagreSjekket.Click += async (_, __) => await SaveManualSjekketAsync();
        _cmbInternPick.SelectedIndexChanged += (_, __) => UpdateManualPickDisplay();

        _rbInOut.CheckedChanged += async (_, __) => { if (_rbInOut.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbInOutDiesel.CheckedChanged += async (_, __) => { if (_rbInOutDiesel.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbOut.CheckedChanged += async (_, __) => { if (_rbOut.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbIn.CheckedChanged += async (_, __) => { if (_rbIn.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbHeldag.CheckedChanged += async (_, __) => { if (_rbHeldag.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbDiesel.CheckedChanged += async (_, __) => { if (_rbDiesel.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbSjekket.CheckedChanged += async (_, __) => { if (_rbSjekket.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };
        _rbGjennom.CheckedChanged += async (_, __) => { if (_rbGjennom.Checked) { MarkUserEditedInputs(); await OnTypeVaskModeChangedAsync(); } };

        _txtKommentar.TextChanged += (_, __) =>
        {
            if (_txtKommentar.Focused) MarkUserEditedInputs();
        };

        _grid.SelectionChanged += (_, __) => UpdateDetailsFromSelection();

        SetupGridColumns();
        ApplyTypeVaskLegendColors();
        InitPlateVisual();
        SetCameraIndicator(false);
    }

    private void SetupGridColumns()
    {
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.AutoGenerateColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders;
        _grid.RowTemplate.Height = 46;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        _grid.ColumnHeadersHeight = 34;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.DefaultCellStyle.Padding = new Padding(2, 1, 2, 1);

        try
        {
            _grid.DefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        }
        catch { }

        _grid.Columns.Clear();

        DataGridViewTextBoxColumn Col(string prop, string header, float weight, int minWidth = 70)
            => new()
            {
                DataPropertyName = prop,
                HeaderText = header,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = weight,
                MinimumWidth = minWidth,
            };

        _grid.Columns.Add(Col("Dato", "Dato", 14, 150));
        _grid.Columns.Add(Col("Skift", "Skift", 10, 140));
        _grid.Columns.Add(Col("Internnr", "Internnr", 8, 80));
        _grid.Columns.Add(Col("RegNr", "Reg.nr", 10, 110));
        _grid.Columns.Add(Col("TypeKjoretoy", "Type kjøretøy", 12, 130));
        _grid.Columns.Add(Col("TypeVask", "Type vask", 14, 160));
        _grid.Columns.Add(Col("Sesong", "Sesong", 8, 80));
        _grid.Columns.Add(Col("Status", "Status", 8, 80));
        _grid.Columns.Add(Col("Kommentar", "Kommentar", 18, 160));
        _grid.Columns.Add(Col("Ansatt", "Ansatt", 12, 120));
        _grid.Columns.Add(Col("ConfirmedAt", "Bekreftet", 14, 140));

        _grid.CellFormatting += (_, e) =>
        {
            if (e.Value is string s
                && _grid.Columns[e.ColumnIndex].DataPropertyName == "RegNr")
            {
                e.Value = FormatPlateForDisplay(s);
                e.FormattingApplied = true;
            }
        };

        _grid.DataBindingComplete += (_, __) => ApplyGridColorStyling();
    }

    private void InitPlateVisual()
    {
        // The host panel is layouted in the designer; we build the inner visuals at runtime.
        if (_plateHost is null) return;

        try
        {
            _plateHost.Controls.Clear();
        }
        catch { /* ignore */ }

        _plateHost.BackColor = Color.White;
        _plateHost.BorderStyle = BorderStyle.None;
        _plateHost.Margin = new Padding(0);
        _plateHost.Padding = new Padding(0);

        var plateImg = TryLoadPlateImage();
        if (plateImg is not null)
        {
            _pbPlate = new PictureBox
            {
                Dock = DockStyle.Fill,
                Image = plateImg,
                SizeMode = PictureBoxSizeMode.StretchImage,
                Margin = new Padding(0),
                Padding = new Padding(0),
                BackColor = Color.White,
            };
            _plateHost.Controls.Add(_pbPlate);

            _lblPlate.Dock = DockStyle.None;
            _lblPlate.AutoSize = false;
            _lblPlate.TextAlign = ContentAlignment.MiddleCenter;
            _lblPlate.BackColor = Color.Transparent;

            // Important for transparency: parent the label to the PictureBox
            _pbPlate.Controls.Add(_lblPlate);
            _lblPlate.Parent = _pbPlate;

            _pbPlate.Resize += (_, __) => LayoutPlateText();
            _lblPlate.TextChanged += (_, __) => LayoutPlateText();
            LayoutPlateText();
        }
        else
        {
            // Fallback: just show the label on a bordered panel.
            _pbPlate = null;
            _plateHost.BorderStyle = BorderStyle.FixedSingle;
            _lblPlate.Dock = DockStyle.Fill;
            _lblPlate.AutoSize = false;
            _lblPlate.TextAlign = ContentAlignment.MiddleCenter;
            _lblPlate.BackColor = Color.White;
            _plateHost.Controls.Add(_lblPlate);
        }
    }

    private static void FixHeight(Control c, int height)
    {
        if (c is Button b) b.AutoSize = false;
        if (c.Height < height) c.Height = height;
        // Keep it flexible: only enforce a minimum height so the designer can still control layout.
        c.MinimumSize = new Size(c.MinimumSize.Width, height);
    }

    private void AlignTopBars()
    {
        try
        {
            var hDate = _dtpFra.PreferredSize.Height;
            FixHeight(_pnlCamStatus, hDate);
            FixHeight(_btnSettings, hDate);

            // make sure text fits
            _pnlCamStatus.MinimumSize = new Size(180, hDate);
            _btnSettings.MinimumSize = new Size(180, hDate);

            var hAns = _cmbAnsatt.PreferredSize.Height;
            FixHeight(_btnRefresh, hAns);
            FixHeight(_lblDbStatus, hAns);
            FixHeight(_btnConfirm, hAns);

            // widths so labels are not clipped
            _btnConfirm.MinimumSize = new Size(140, hAns);
            _btnRefresh.MinimumSize = new Size(140, hAns);
        }
        catch { /* ignore */ }
    }

    private void SetCameraIndicator(bool active)
    {
        if (active)
        {
            _pnlCamStatus.BackColor = Color.ForestGreen;
            _lblCamStatus.Text = "Kamera: Aktiv";
        }
        else
        {
            _pnlCamStatus.BackColor = Color.Firebrick;
            _lblCamStatus.Text = "Kamera: Ikke aktiv";
        }
    }


    private void UpdateDetailsFromSelection()
    {
        // In manual "Kun sjekket" mode, do not let grid selection overwrite the chosen mode.
        // The list is only for overview; the selected internnr controls the left pane.
        if (_manualSjekketMode)
        {
            UpdateManualPickDisplay();
            return;
        }

        if (_grid.CurrentRow?.DataBoundItem is not WorkerWashRow row) return;

        // Always update the big "selected" info
        _lblPlate.Text = FormatPlateForDisplay(row.RegNr);
        _lblInternnr.Text = "Internnr: " + (string.IsNullOrWhiteSpace(row.Internnr) ? "–" : row.Internnr);
        _lblDato.Text = "Dato: " + row.Dato.ToString("yyyy-MM-dd HH:mm");
        _lblTypeKjoretoy.Text = "Type kjøretøy: " + (row.TypeKjoretoy ?? "–");
        _lblSesong.Text = "Sesong: " + (row.Sesong ?? "–");
        _lblStatusValue.Text = "Status: " + (row.Status ?? "–");

        var isNewSelection = _selectedWashId != row.Id;
        if (isNewSelection)
        {
            _selectedWashId = row.Id;
            _userEditedInputs = false;
        }

        // IMPORTANT: auto-refresh updates the grid every few seconds.
        // Do NOT overwrite the user's current "Type vask" while they are about to confirm
        // or while the grid is refreshing in the background.
        if (!_userEditedInputs && !_suppressTypeVaskSyncFromGrid)
        {
            if (!string.IsNullOrWhiteSpace(row.TypeVask))
        {
            var tv = row.TypeVask.Trim();
            _rbInOut.Checked = tv.Equals(_rbInOut.Text, StringComparison.OrdinalIgnoreCase);
            _rbInOutDiesel.Checked = tv.Equals(_rbInOutDiesel.Text, StringComparison.OrdinalIgnoreCase);
            _rbOut.Checked = tv.Equals(_rbOut.Text, StringComparison.OrdinalIgnoreCase);
            _rbIn.Checked = tv.Equals(_rbIn.Text, StringComparison.OrdinalIgnoreCase);
            _rbHeldag.Checked = tv.Equals(_rbHeldag.Text, StringComparison.OrdinalIgnoreCase);
            _rbDiesel.Checked = tv.Equals(_rbDiesel.Text, StringComparison.OrdinalIgnoreCase);
            _rbSjekket.Checked = tv.Equals(_rbSjekket.Text, StringComparison.OrdinalIgnoreCase);
            _rbGjennom.Checked = tv.Equals(_rbGjennom.Text, StringComparison.OrdinalIgnoreCase);
            }
            else if (isNewSelection)
            {
                // Default for a new, unconfirmed row
                _rbInOut.Checked = true;
            }
        }

        _txtKommentar.Text = row.Kommentar ?? string.Empty;
    }

    private void StartDbStatusTimer()
    {
        if (string.IsNullOrWhiteSpace(_cfg.WorkerConnectionString))
        {
            _lblDbStatus.Text = "DB: Disabled";
            _lblDbStatus.ForeColor = System.Drawing.Color.Gray;
            return;
        }

        _dbTimer.Interval = 15000; // 15 sek
        _dbTimer.Tick += async (_, __) => await RefreshDbStatusAsync();
        _dbTimer.Start();

        _ = RefreshDbStatusAsync();
    }

    private async Task RefreshDbStatusAsync()
    {
        try
        {
            _dbTimer.Stop();

            if (string.IsNullOrWhiteSpace(_cfg.WorkerConnectionString))
            {
                _lblDbStatus.Text = "DB: Disabled";
                _lblDbStatus.ForeColor = System.Drawing.Color.Gray;
                return;
            }

            var (ok, error) = await WorkerDb.TryConnectAsync(_cfg.WorkerConnectionString);
            _lastDbError = error;

            if (ok)
            {
                _lblDbStatus.Text = "DB: Connected";
                _lblDbStatus.ForeColor = System.Drawing.Color.Green;
                _dbStatusToolTip.SetToolTip(_lblDbStatus, "Database connection OK");

                // One-time sync of vehicle lookup CSV into DB.
                // CSV usually stores plates with a space (e.g. "EC 50316") while ITS provides "EC50316".
                // Normalizing and syncing here makes worker_active_washes join correctly and enables vehicle info.
                if (!_vehicleLookupSynced)
                {
                    _vehicleLookupSynced = true;
                    try
                    {
                        var cnt = await WorkerDb.SyncEgenFlateFromCsvAsync(_cfg.WorkerConnectionString, _cfg.DokumentFolder);
                        if (cnt > 0)
                            SetStatus($"Synced {cnt} vehicles from CSV");
                    }
                    catch (Exception ex)
                    {
                        // Don't break the app if CSV sync fails.
                        SetStatus("CSV sync failed: " + ex.Message);
                    }
                }

                LoadVehiclePickFromCsv();
            }
            else
            {
                _lblDbStatus.Text = "DB: Offline";
                _lblDbStatus.ForeColor = System.Drawing.Color.Red;

                // Keep UI short; details are available on hover.
                var tip = string.IsNullOrWhiteSpace(_lastDbError)
                    ? "Database connection failed"
                    : "Database connection failed:\r\n" + _lastDbError;
                _dbStatusToolTip.SetToolTip(_lblDbStatus, tip);
            }
        }
        catch (Exception ex)
        {
            _lastDbError = ex.Message;
            _lblDbStatus.Text = "DB: Offline";
            _lblDbStatus.ForeColor = System.Drawing.Color.Red;
            _dbStatusToolTip.SetToolTip(_lblDbStatus, "Database connection failed:\r\n" + ex);
        }
        finally
        {
            if (!_isClosing && !string.IsNullOrWhiteSpace(_cfg.WorkerConnectionString))
                _dbTimer.Start();
        }
    }

    private void StartAutoRefresh()
    {

        _timer.Interval = Math.Max(2, _cfg.RefreshSeconds) * 1000;
        _timer.Tick += async (_, __) =>
        {
            // don't spam if user is interacting heavily
            if (!Visible) return;
            if (_manualSjekketMode) return;
            await LoadWashesAsync(silent: true);
        };

        // _cfg.OfflineQueuePath may point to a file; plate queue expects a folder.
        // Store offline queue alongside the CSV docs folder so the whole solution uses one place.
        var docFolder = string.IsNullOrWhiteSpace(_cfg.DokumentFolder)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : _cfg.DokumentFolder;
        _offline = new PlateOfflineQueue(docFolder);

        // Load lookup for manual Internnr pick (works even if DB is offline)
        LoadVehiclePickFromCsv();

        // Periodically flush offline events when DB becomes available.
        _flushTimer = new System.Threading.Timer(_ => _ = Task.Run(FlushOfflineAsync), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));

        SetCameraIndicator(false);

        if (_cfg.ItsListenerEnabled)
        {
            _scanner = new WorkerCameraScanner(_cfg.ItsListenerPrefix);
            _scanner.PlateRecognized += ScannerOnPlateRecognized;
            _scanner.Debug += (_, msg) => SafeSetTitleSuffix(msg);
            try
            {
                _scanner.Start();
                SetCameraIndicator(true);
            }
            catch (Exception ex)
            {
                SetCameraIndicator(false);
                SafeSetTitleSuffix("ITS start failed: " + ex.Message);
            }
        }
        else
        {
            SetCameraIndicator(false);
        }


        _timer.Start();
    }

    private async Task ReloadAllAsync()
    {
        await LoadAnsatterAsync();
        await LoadWashesAsync();
    }

    // Older call sites use ReloadAsync – keep a small wrapper for compatibility.
    private Task ReloadAsync() => ReloadAllAsync();

    private async Task LoadAnsatterAsync()
    {
        try
        {
            SetStatus("Laster ansatte…");
            var ans = await WorkerDb.GetAnsatterAsync(_cfg.WorkerConnectionString);

            var selectedId = (_cmbAnsatt.SelectedItem as Ansatt)?.Id;

            _cmbAnsatt.BeginUpdate();
            _cmbAnsatt.Items.Clear();
            foreach (var a in ans) _cmbAnsatt.Items.Add(a);
            _cmbAnsatt.EndUpdate();

            if (_cmbAnsatt.Items.Count > 0)
            {
                var toSelect = ans.FirstOrDefault(x => x.Id == selectedId) ?? ans.First();
                _cmbAnsatt.SelectedItem = toSelect;
            }

            SetStatus($"Ansatte: {ans.Count}");
        }
        catch (Exception ex)
        {
            SetStatus("Feil ved lasting av ansatte: " + ex.Message);
        }
    }

    private void LoadVehiclePickFromCsv()
    {
        try
        {
            _vehiclePick.Clear();
            _internToReg.Clear();
            _knownPlates.Clear();

            var docFolder = string.IsNullOrWhiteSpace(_cfg.DokumentFolder)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _cfg.DokumentFolder;

            var csvPath = Path.Combine(docFolder, "EgenFlate.csv");
            if (!File.Exists(csvPath))
            {
                // fallback: local copy (dev)
                var local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EgenFlate.csv");
                if (File.Exists(local)) csvPath = local;
                else return;
            }

            var lines = File.ReadAllLines(csvPath);
            if (lines.Length < 2) return;

            var delim = lines[0].Contains(';') ? ';' : ',';
            var headers = lines[0].Split(delim).Select(h => h.Trim()).ToArray();

            int idxIntern = Array.FindIndex(headers, h => h.Equals("Internnr", StringComparison.OrdinalIgnoreCase) || h.Equals("Intern nr", StringComparison.OrdinalIgnoreCase));
            int idxReg = Array.FindIndex(headers, h => h.Equals("Registreringsnummer", StringComparison.OrdinalIgnoreCase) || h.Equals("Regnr", StringComparison.OrdinalIgnoreCase) || h.Equals("Reg nr", StringComparison.OrdinalIgnoreCase));
            if (idxIntern < 0 || idxReg < 0) return;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(delim);
                if (parts.Length <= Math.Max(idxIntern, idxReg)) continue;

                var intern = parts[idxIntern].Trim();
                var reg = parts[idxReg].Trim();

                if (string.IsNullOrWhiteSpace(intern) || string.IsNullOrWhiteSpace(reg)) continue;

                var regNorm = NormalizePlate(reg);
                if (string.IsNullOrWhiteSpace(regNorm)) continue;

                if (!_internToReg.ContainsKey(intern))
                    _internToReg[intern] = regNorm;

                _knownPlates.Add(regNorm);

                _vehiclePick.Add(new VehiclePickItem { Internnr = intern, RegNr = regNorm });
            }

            // sort numeric where possible
            _vehiclePick.Sort((a, b) =>
            {
                if (int.TryParse(a.Internnr, out var ai) && int.TryParse(b.Internnr, out var bi))
                    return ai.CompareTo(bi);
                return string.Compare(a.Internnr, b.Internnr, StringComparison.OrdinalIgnoreCase);
            });

            PopulateManualInternPick();
        }
        catch
        {
            // ignore – CSV is optional
        }
    }

    private void PopulateManualInternPick()
    {
        try
        {
            if (_cmbInternPick is null) return;

            _cmbInternPick.BeginUpdate();
            _cmbInternPick.Items.Clear();
            foreach (var v in _vehiclePick) _cmbInternPick.Items.Add(v);
            _cmbInternPick.EndUpdate();

            if (_cmbInternPick.Items.Count > 0 && _cmbInternPick.SelectedIndex < 0)
                _cmbInternPick.SelectedIndex = 0;
        }
        catch { }
    }

    private void UpdateManualPickDisplay()
    {
        if (!_manualSjekketMode) return;
        if (_cmbInternPick.SelectedItem is not VehiclePickItem v) return;

        _lblPlate.Text = FormatPlateForDisplay(v.RegNr);
        _lblInternnr.Text = "Internnr: " + v.Internnr;

        // Clear fields that don't exist in CSV (keeps UI clean)
        _lblDato.Text = "Dato: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _lblTypeKjoretoy.Text = "Type kjøretøy: –";
        _lblSesong.Text = "Sesong: " + _cfg.DetermineSeason(DateTime.Now);
        _lblStatusValue.Text = "Status: –";
    }


    private void SetAutoRefreshSuspended(bool suspended)
    {
        try
        {
            if (suspended)
            {
                if (_autoRefreshSuspended) return;
                _autoRefreshSuspended = true;
                _timer.Stop();
            }
            else
            {
                if (!_autoRefreshSuspended) return;
                _autoRefreshSuspended = false;
                _timer.Start();
            }
        }
        catch { }
    }

    private void CaptureFiltersBeforeManual()
    {
        if (_savedFiltersCaptured) return;
        _savedFiltersCaptured = true;
        _savedFra = _dtpFra.Value;
        _savedTil = _dtpTil.Value;
        _savedPeriode = _cmbPeriode.SelectedItem;
        _savedOnlyUnconfirmed = _chkOnlyUnconfirmed.Checked;
    }

    private void RestoreFiltersAfterManual()
    {
        if (!_savedFiltersCaptured) return;

        try
        {
            _suppressDateEvents = true;
            _chkOnlyUnconfirmed.Checked = _savedOnlyUnconfirmed;
            if (_savedPeriode != null) _cmbPeriode.SelectedItem = _savedPeriode;
            _dtpFra.Value = _savedFra;
            _dtpTil.Value = _savedTil;
        }
        finally
        {
            _suppressDateEvents = false;
        }

        _savedFiltersCaptured = false;
    }


    private void SetManualSjekketUiState(bool enabled)
    {
        try
        {
            if (_lblInternPickLabel != null) _lblInternPickLabel.Enabled = enabled;
            if (_cmbInternPick != null) _cmbInternPick.Enabled = enabled;
            if (_btnLagreSjekket != null) _btnLagreSjekket.Enabled = enabled;

            // Disable unrelated controls while in manual "Kun sjekket" mode
            _btnConfirm.Enabled = !enabled;
            _btnRefresh.Enabled = !enabled;
            _btnSettings.Enabled = !enabled;
            _chkOnlyUnconfirmed.Enabled = !enabled;

            _cmbPeriode.Enabled = !enabled;
            _dtpFra.Enabled = !enabled;
            _dtpTil.Enabled = !enabled;

            _txtKommentar.Enabled = !enabled;

            if (enabled)
            {
                // Force today's view and ignore "Kun ikke bekreftet"
                _suppressDateEvents = true;
                _chkOnlyUnconfirmed.Checked = false;

                var today = DateTime.Today;
                _dtpFra.Value = today;
                _dtpTil.Value = today;
                _cmbPeriode.SelectedItem = "Dag";
                _suppressDateEvents = false;

                UpdateManualPickDisplay();
            }
        }
        catch { }
    }

    private async Task OnTypeVaskModeChangedAsync()
    {
        var want = _rbSjekket.Checked;
        if (_manualSjekketMode == want) return;

        if (want)
        {
            // Entering manual mode:
            // - freeze auto refresh (no 5-second pressure)
            // - remember previous filters so we can restore them when leaving
            CaptureFiltersBeforeManual();
            SetAutoRefreshSuspended(true);
        }

        _manualSjekketMode = want;
        SetManualSjekketUiState(want);

        if (!want)
        {
            // Leaving manual mode: restore filters. Auto refresh is re-enabled after the first reload.
            RestoreFiltersAfterManual();
        }

        await LoadWashesAsync();

        if (!want)
        {
            SetAutoRefreshSuspended(false);
        }
    }

    private async Task SaveManualSjekketAsync()
    {
        try
        {
            if (!_manualSjekketMode)
                return;

            if (_cmbAnsatt.SelectedItem is not Ansatt ansatt)
            {
                MessageBox.Show("Velg ansatt først.", "Mangler ansatt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_cmbInternPick.SelectedItem is not VehiclePickItem v)
            {
                MessageBox.Show("Velg internnr først.", "Mangler internnr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var plateNorm = NormalizePlate(v.RegNr);
            if (string.IsNullOrWhiteSpace(plateNorm))
            {
                MessageBox.Show("Ugyldig registreringsnummer for valgt internnr.", "Ugyldig data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Insert event (as if camera saw it), then immediately confirm as "Kun sjekket"
            SetStatus("Lagrer (Kun sjekket)…");

            // Ensure vehicle lookup is synced so DB trigger doesn't reject the insert
            try { await WorkerDb.SyncEgenFlateFromCsvAsync(_cfg.WorkerConnectionString, _cfg.DokumentFolder); } catch { }

            var occurredAtUtc = DateTime.UtcNow;
            var season = _cfg.DetermineSeason(DateTime.Now);

            var washId = await WorkerDb.InsertWashEventReturningIdAsync(
                _cfg.WorkerConnectionString,
                plateNorm,
                occurredAtUtc,
                sourceApp: "worker_sjekket",
                season: season,
                requireEgenFlate: true);

            if (washId <= 0)
            {
                MessageBox.Show("Kunne ikke lagre i databasen (DB offline?).", "DB-feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetStatus("DB-feil: kunne ikke lagre.");
                return;
            }

            var ok = await WorkerDb.ConfirmWashAsync(
                _cfg.WorkerConnectionString,
                washId,
                ansatt.Id,
                _rbSjekket.Text,
                kommentar: null);

            if (!ok)
            {
                // Should not happen because it's new, but keep user informed.
                MessageBox.Show("Denne vasken er allerede bekreftet.", "Allerede bekreftet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            await LoadWashesAsync();
            SetStatus("Lagret: Kun sjekket");
        }
        catch (Exception ex)
        {
            SetStatus("Feil ved lagring: " + ex.Message);
            try { MessageBox.Show(ex.ToString(), "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }
    }


    async Task LoadWashesAsync(bool silent = false)
    {
        try
        {
            if (!silent) SetStatus("Laster liste…");

            // If we're in shift view, keep the date range aligned with the current shift.
            // This prevents confusion when the day/shift changes (especially Skift 3 over midnight).
            var periode = (_cmbPeriode.SelectedItem as string) ?? "";
            if (!_manualSjekketMode && periode.Equals("Skift", StringComparison.OrdinalIgnoreCase))
            {
                var prev = _suppressDateEvents;
                _suppressDateEvents = true;
                try { ApplyPeriodeRules(fromFraChanged: true); }
                finally { _suppressDateEvents = prev; }
            }

            var (fromUtc, toUtc) = GetSelectedUtcRange();
            var rows = await WorkerDb.GetWashesAsync(
                _cfg.WorkerConnectionString,
                _chkOnlyUnconfirmed.Checked,
                _cfg.ShowOnlyUnntak,
                fromUtc,
                toUtc);


            // Manual "Kun sjekket" mode: show ONLY vehicles checked today
            if (_manualSjekketMode)
            {
                var today = DateTime.Today;
                rows = rows
                    .Where(r => r.Dato.Date == today &&
                                string.Equals(((r.TypeVask ?? "").Trim()), _rbSjekket.Text, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else if (periode.Equals("Skift", StringComparison.OrdinalIgnoreCase))
            {
                // Shift view: filter to the active shift window.
                var (_, startLocal, endLocal) = TurnusHelper.GetCurrentShiftWindow(DateTime.Now);
                rows = rows
                    .Where(r => r.Dato >= startLocal && r.Dato < endLocal)
                    .ToList();
            }

            // Sorting: group by shift first, then newest first.
            // (In Skift-view all rows will share one shift.)
            rows = rows
                .OrderBy(r => r.SkiftNr == 0 ? 99 : r.SkiftNr)
                .ThenByDescending(r => r.Dato)
                .ToList();

            var keepId = (_grid.CurrentRow?.DataBoundItem as WorkerWashRow)?.Id;
            var keepFirstDisplayed = _grid.Rows.Count > 0 ? _grid.FirstDisplayedScrollingRowIndex : -1;

            var prevSuppressTypeSync = _suppressTypeVaskSyncFromGrid;
            if (silent) _suppressTypeVaskSyncFromGrid = true;
            try
            {
                _grid.DataSource = rows;
                ApplyGridColorStyling();
                try { _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders); } catch { }

                if (rows.Count > 0)
            {
                // Re-select the same row after refresh (so user doesn't lose context)
                var selectedIndex = -1;
                if (keepId.HasValue)
                {
                    for (var i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].Id == keepId.Value)
                        {
                            selectedIndex = i;
                            break;
                        }
                    }
                }

                if (selectedIndex < 0) selectedIndex = 0;

                if (_grid.Rows.Count > selectedIndex)
                {
                    _grid.ClearSelection();
                    _grid.Rows[selectedIndex].Selected = true;
                    _grid.CurrentCell = _grid.Rows[selectedIndex].Cells[0];

                    // Keep scroll position when possible
                    if (keepFirstDisplayed >= 0 && keepFirstDisplayed < _grid.Rows.Count)
                        _grid.FirstDisplayedScrollingRowIndex = keepFirstDisplayed;
                }
                }

                UpdateDetailsFromSelection();
            }
            finally
            {
                _suppressTypeVaskSyncFromGrid = prevSuppressTypeSync;
            }

            if (!silent) SetStatus($"Viser: {rows.Count}");
        }
        catch (Exception ex)
        {
            // Show the full exception so it's immediately obvious if this is a permission / missing view issue.
            SetStatus("Feil ved lasting av liste: " + ex.Message);
            try
            {
                MessageBox.Show(ex.ToString(), "DB-feil (vaskeliste)", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // ignore (message loop may be tearing down)
            }
        }
    }

    private async Task ConfirmSelectedAsync()
    {
        try
        {
            if (_cmbAnsatt.SelectedItem is not Ansatt ansatt)
            {
                MessageBox.Show("Velg ansatt først.", "Mangler ansatt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_grid.CurrentRow?.DataBoundItem is not WorkerWashRow row)
            {
                MessageBox.Show("Velg en rad i listen.", "Mangler valg", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetStatus("Lagrer bekreftelse…");

            var typeVask = GetSelectedTypeVask();
            var kommentar = string.IsNullOrWhiteSpace(_txtKommentar.Text) ? null : _txtKommentar.Text.Trim();

            var ok = await WorkerDb.ConfirmWashAsync(_cfg.WorkerConnectionString, row.Id, ansatt.Id, typeVask, kommentar);

            if (!ok)
            {
                MessageBox.Show("Denne vasken er allerede bekreftet av noen.", "Allerede bekreftet",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            _userEditedInputs = false;
            await LoadWashesAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Feil ved bekreftelse: " + ex.Message);
        }
    }

    private string GetSelectedTypeVask()
    {
        if (_rbInOutDiesel.Checked) return _rbInOutDiesel.Text;
        if (_rbOut.Checked) return _rbOut.Text;
        if (_rbIn.Checked) return _rbIn.Text;
        if (_rbHeldag.Checked) return _rbHeldag.Text;
        if (_rbDiesel.Checked) return _rbDiesel.Text;
        if (_rbSjekket.Checked) return _rbSjekket.Text;
        if (_rbGjennom.Checked) return _rbGjennom.Text;
        return _rbInOut.Text; // default
    }

    // -------------------------
    // Date filtering (Periode / Fra / Til)
    // -------------------------
    private void InitDateFilterDefaults()
    {
        try
        {
            _suppressDateEvents = true;

            if (_cmbPeriode.Items.Count == 0)
                _cmbPeriode.Items.AddRange(new object[] { "Skift", "Dag", "Uke", "Måned", "Periode" });

            // Default to shift view to reduce confusion during busy periods.
            _cmbPeriode.SelectedItem = "Skift";
            // Dates will be set by ApplyPeriodeRules("Skift").
            ApplyPeriodeRules();
        }
        finally
        {
            _suppressDateEvents = false;
        }
    }

    private async Task OnDateFilterChangedAsync(bool fromFraChanged = true)
    {
        if (_suppressDateEvents)
            return;


        if (_manualSjekketMode)
            return;
        try
        {
            _suppressDateEvents = true;

            // For auto-periods we recalc end date. For manual period we only validate range.
            ApplyPeriodeRules(fromFraChanged);
        }
        finally
        {
            _suppressDateEvents = false;
        }

        // Avoid querying DB while the form is still initializing.
        if (!IsHandleCreated || !Visible)
            return;

        await LoadWashesAsync();
    }

    private void ApplyPeriodeRules(bool fromFraChanged = true)
    {
        var type = (_cmbPeriode.SelectedItem as string) ?? "Dag";
        var d = _dtpFra.Value.Date;

        if (type.Equals("Skift", StringComparison.OrdinalIgnoreCase))
        {
            // In shift mode, we keep the pickers as DATE only, but expand the range
            // across midnight for Skift 3 (22:00–06:00). Filtering by the exact
            // time window happens in LoadWashesAsync().
            _dtpTil.Enabled = false;
            var now = DateTime.Now;
            var (_, startLocal, endLocal) = TurnusHelper.GetCurrentShiftWindow(now);

            _dtpFra.Value = startLocal.Date;
            _dtpTil.Value = endLocal.Date;

            return;
        }

        if (type.Equals("Periode", StringComparison.OrdinalIgnoreCase))
        {
            _dtpTil.Enabled = true;
            if (_dtpTil.Value.Date < d)
                _dtpTil.Value = d;
            return;
        }

        _dtpTil.Enabled = false;

        if (!fromFraChanged)
            return; // ignore changes to "Til" when in auto-mode

        switch (type)
        {
            case "Dag":
                _dtpTil.Value = d;
                break;

            case "Uke":
                {
                    int delta = (int)d.DayOfWeek - (int)DayOfWeek.Monday;
                    if (delta < 0) delta += 7;
                    var monday = d.AddDays(-delta);
                    var sunday = monday.AddDays(6);
                    _dtpFra.Value = monday;
                    _dtpTil.Value = sunday;
                    break;
                }

            case "Måned":
                {
                    var first = new DateTime(d.Year, d.Month, 1);
                    var last = first.AddMonths(1).AddDays(-1);
                    _dtpFra.Value = first;
                    _dtpTil.Value = last;
                    break;
                }

            default:
                _dtpTil.Value = d;
                break;
        }
    }

    private (DateTime fromUtc, DateTime toUtc) GetSelectedUtcRange()
    {
        var (fromLocal, toLocal) = GetSelectedLocalRange();
        return (ConvertOsloToUtc(fromLocal), ConvertOsloToUtc(toLocal));
    }

    private (DateTime fromLocal, DateTime toLocal) GetSelectedLocalRange()
    {
        var type = (_cmbPeriode.SelectedItem as string) ?? "Dag";
        var fraDate = _dtpFra.Value.Date;

        if (type.Equals("Periode", StringComparison.OrdinalIgnoreCase))
        {
            var tilDate = _dtpTil.Value.Date;
            if (tilDate < fraDate) tilDate = fraDate;
            return (fraDate, tilDate.AddDays(1).AddTicks(-1));
        }

        // Auto modes already set the pickers in ApplyPeriodeRules.
        var til = _dtpTil.Value.Date;
        if (til < fraDate) til = fraDate;
        return (fraDate, til.AddDays(1).AddTicks(-1));
    }

    private static DateTime ConvertOsloToUtc(DateTime local)
    {
        // Use Europe/Oslo on Linux, W. Europe Standard Time on Windows.
        var tz = GetOsloTimeZone();
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
        }
        catch
        {
            // DST edge cases (invalid / ambiguous). Fall back to machine local conversion.
            return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        }
    }

    private static TimeZoneInfo GetOsloTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo"); } catch { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); } catch { }
        return TimeZoneInfo.Local;
    }

    private void OpenSettings()
    {
        try
        {
            if (!PasswordPrompt.Require(this, "Admin-passord", _cfg.UiAdminPassword,
                    "Skriv admin-passord for å åpne Innstillinger:"))
                return;

            using var dlg = new WorkerSettingsForm(_cfg);
            dlg.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SetStatus(string text)
    {
        try
        {
            if (_manualSjekketMode)
            {
                _lblStatus.Text = $"Kun sjekket | {text}";
                return;
            }

            var sh = TurnusHelper.GetShift(DateTime.Now);
            _lblStatus.Text = $"{sh.Display} | {text}";
        }
        catch
        {
            _lblStatus.Text = text;
        }
    }

    private void SafeSetTitleSuffix(string msg)
    {
        try
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                try { Text = "Bilvask Worker — " + msg; } catch { }
            }));
        }
        catch { }
    }

    private async void ScannerOnPlateRecognized(object? sender, string plate)
    {
        // Scanner can fire before StartAutoRefresh() finished; queue is optional.
        if (_offline is null) return;

        var plateNorm = CanonicalizePlateCandidate(plate);
        if (string.IsNullOrWhiteSpace(plateNorm) || !IsLikelyNorwegianPlate(plateNorm))
        {
            // Ignore logo reads / garbage
            var rawDisp = FormatPlateForDisplay(plate);
            SafeSetTitleSuffix($"IGNORED: {rawDisp}");
            return;
        }


        // Extra guard against SKYSS logo misreads that can sometimes look like a valid plate (e.g. SKxxxxx).
        // If we have a known fleet list from CSV, ignore SK* plates that are not in that list.
        if (_knownPlates.Count > 0 &&
            plateNorm.StartsWith("SK", StringComparison.OrdinalIgnoreCase) &&
            !_knownPlates.Contains(plateNorm))
        {
            SafeSetTitleSuffix($"IGNORED (SKYSS): {FormatPlateForDisplay(plateNorm)}");
            return;
        }

        var plateDisp = FormatPlateForDisplay(plateNorm);

        var occurredAtUtc = DateTime.UtcNow;

        // Suppress duplicates when a vehicle is standing in front of the camera.
        if (!_dedupe30min.ShouldProcess(plateNorm, occurredAtUtc))
        {
            SafeSetTitleSuffix($"SKIPPED (30m): {plateDisp}");
            return;
        }

        lock (_plateLock)
        {
            var now = DateTime.UtcNow;
            if (_lastPlate != null && string.Equals(_lastPlate, plateNorm, StringComparison.OrdinalIgnoreCase))
            {
                if ((now - _lastPlateAtUtc).TotalSeconds < _cfg.PlateDebounceSeconds)
                    return;
            }

            _lastPlate = plateNorm;
            _lastPlateAtUtc = now;
        }

        try
        {
            var season = _cfg.DetermineSeason(occurredAtUtc.ToLocalTime());
            var res = await TryStoreWashEventAsync(plateNorm, occurredAtUtc, sourceApp: "its", season: season);

            if (res == DbInsertOutcome.Inserted)
            {
                try { _audit.Append(occurredAtUtc, phase: "insert", source: "its", plate: plateNorm, dbOk: true, queued: false, error: null); } catch { }
                var target = RenderApiClient.IsConfigured() ? "Render/DB" : "DB";
                SafeSetTitleSuffix($"OK {target}: {plateDisp}");
                if (!_manualSjekketMode) await ReloadAsync();
            }
            else if (res == DbInsertOutcome.Deduped)
            {
                // Trigger dedupe (already registered) — keep CSV clean as well.
                SafeSetTitleSuffix($"SKIPPED: {plateDisp}");
            }
            else
            {
                // Failed
                try { _offline.Enqueue(plateNorm, occurredAtUtc); } catch { }
                try { _audit.Append(occurredAtUtc, phase: "queued", source: "its", plate: plateNorm, dbOk: false, queued: true, error: "db_or_render_fail"); } catch { }
                SafeSetTitleSuffix($"QUEUED: {plateDisp} (db/render fail)");
            }
        }
        catch (Exception ex)
        {
            try { _offline.Enqueue(plateNorm, occurredAtUtc); } catch { }
            try { _audit.Append(occurredAtUtc, phase: "queued", source: "its", plate: plateNorm, dbOk: false, queued: true, error: ex.Message); } catch { }
            SafeSetTitleSuffix($"QUEUED: {plateDisp} ({ex.Message})");
        }
    }

    private async Task<DbInsertOutcome> TryStoreWashEventAsync(string plate, DateTime occurredAtUtc, string sourceApp, string? season)
    {
        var anyInserted = false;
        var anyDeduped = false;
        var attempted = false;

        // 1) Cloud / Render API. This lets the local camera-agent send data to Render.
        if (RenderApiClient.IsConfigured())
        {
            attempted = true;
            var cloud = await RenderApiClient.TrySendWashEventAsync(plate, occurredAtUtc, season, sourceApp);
            if (cloud.Success)
            {
                if (cloud.Deduped) anyDeduped = true;
                else anyInserted = true;
            }
        }

        // 2) Existing local/direct PostgreSQL path. Kept so the old program can still work locally.
        if (!string.IsNullOrWhiteSpace(_cfg.WorkerConnectionString))
        {
            attempted = true;
            var local = await WorkerDb.TryInsertWashEventAsync(_cfg.WorkerConnectionString, plate, occurredAtUtc, sourceApp: sourceApp, season: season);
            if (local == DbInsertOutcome.Inserted) anyInserted = true;
            else if (local == DbInsertOutcome.Deduped) anyDeduped = true;
        }

        if (anyInserted) return DbInsertOutcome.Inserted;
        if (anyDeduped) return DbInsertOutcome.Deduped;
        return attempted ? DbInsertOutcome.Failed : DbInsertOutcome.Failed;
    }

    private async Task FlushOfflineAsync()
    {
        try
        {
            if (_offline is null)
                return;

            if (!RenderApiClient.IsConfigured() && !await WorkerDb.CanConnectAsync(_cfg.WorkerConnectionString))
                return;

            var items = _offline.DrainAll();
            if (items.Count == 0) return;

            foreach (var (occurredAtUtc, plate) in items)
            {
                try
                {
                    var season = _cfg.DetermineSeason(occurredAtUtc.ToLocalTime());
                    var res = await TryStoreWashEventAsync(plate, occurredAtUtc, sourceApp: "worker_offline", season: season);
                    if (res == DbInsertOutcome.Inserted)
                    {
                        try { _audit.Append(occurredAtUtc, phase: "flush_ok", source: "worker_offline", plate: plate, dbOk: true, queued: false, error: null); } catch { }
                    }
                    else if (res == DbInsertOutcome.Deduped)
                    {
                        // Already registered; do not re-enqueue and do not log duplicate.
                    }
                    else
                    {
                        throw new Exception("db_fail");
                    }
                }
                catch
                {
                    try { _offline.Enqueue(plate, occurredAtUtc); } catch { }
                    try { _audit.Append(occurredAtUtc, phase: "flush_fail", source: "worker_offline", plate: plate, dbOk: false, queued: true, error: null); } catch { }
                    break;
                }
            }

            if (!_manualSjekketMode) await ReloadAsync();
            SafeSetTitleSuffix("Offline queue flushed");
        }
        catch { }
    }



    private void ApplyResponsiveLayout()
    {
        try
        {
            // Adjust left column width dynamically (505px was too rigid on small/big screens)
            try
            {
                if (_root != null && _root.ColumnStyles != null && _root.ColumnStyles.Count >= 2)
                {
                    var w = ClientSize.Width;
                    if (w > 0)
                    {
                        float left = Math.Clamp(w * 0.28f, 420f, 620f);
                        _root.ColumnStyles[0].Width = left;
                    }
                }
            }
            catch { }

            // Scale headline fonts a bit so they don't overflow on small screens
            float baseW = 1920f;
            float baseH = 1080f;

            var fx = ClientSize.Width > 0 ? (ClientSize.Width / baseW) : 1f;
            var fy = ClientSize.Height > 0 ? (ClientSize.Height / baseH) : 1f;

            var s = Math.Clamp(Math.Min(fx, fy), 0.75f, 1.25f);

            if (Math.Abs(s - _uiScaleLast) < 0.03f)
                return;

            _uiScaleLast = s;

            try
            {
                _lblPlate.Font = new Font(_lblPlate.Font.FontFamily, 56f * s, FontStyle.Bold);
                _lblInternnr.Font = new Font(_lblInternnr.Font.FontFamily, 32f * s, FontStyle.Bold);
            }
            catch { }

            try
            {
                float gridFont = Math.Clamp(11f * s, 10f, 14f);
                _grid.DefaultCellStyle.Font = new Font("Segoe UI", gridFont, FontStyle.Regular, GraphicsUnit.Point);
                _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", Math.Clamp(gridFont + 0.5f, 10.5f, 15f), FontStyle.Bold, GraphicsUnit.Point);
                _grid.RowTemplate.Height = (int)Math.Clamp(46f * s, 36f, 68f);
                _grid.ColumnHeadersHeight = (int)Math.Clamp(34f * s, 30f, 46f);
                _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders);
                _grid.Invalidate();
            }
            catch { }
        }
        catch { }
    }


}

#pragma warning restore CS8602
