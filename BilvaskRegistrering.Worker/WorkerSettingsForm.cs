using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Npgsql;

namespace BilvaskRegistrering.Worker;

internal sealed class WorkerSettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly ToolTip _tip = new();

    private TextBox _txtDoc = null!;

    // Worker DB (editable)
    private TextBox _txtDbHost = null!;
    private NumericUpDown _numDbPort = null!;
    private TextBox _txtDbName = null!;
    private TextBox _txtDbUser = null!;
    private TextBox _txtDbPass = null!;
    private Label _lblConnPreview = null!;

    private NumericUpDown _numRefresh = null!;
    private CheckBox _chkOnlyUnconfirmed = null!;
    private CheckBox _chkOnlyUnntak = null!;

    private CheckBox _chkItsEnabled = null!;
    private NumericUpDown _numItsPort = null!;
    private TextBox _txtItsPath = null!;
    private Label _lblItsUrl = null!;
    private TextBox _txtTypeVaskColors = null!;
    private TextBox _txtAnsattColors = null!;

    public WorkerSettingsForm(AppConfig cfg)
    {
        _cfg = cfg;

        Text = "Innstillinger (Worker)";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        MinimizeBox = false;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(920, 540);
        ClientSize = new Size(1020, 600);

        BuildUi();
    }

    private void BuildUi()
    {
        // Parse current worker connection string to prefill fields.
        string host = "";
        int port = 5432;
        string db = "";
        string user = "";
        string pass = "";

        try
        {
            if (!string.IsNullOrWhiteSpace(_cfg.WorkerConnectionString))
            {
                var b = new NpgsqlConnectionStringBuilder(_cfg.WorkerConnectionString);
                host = b.Host ?? "";
                port = b.Port;
                db = b.Database ?? "";
                user = b.Username ?? "";
                pass = b.Password ?? "";
            }
        }
        catch { /* ignore */ }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 10,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));  // doc
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190)); // db group
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));  // refresh
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // only unconfirmed
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));  // only unntak
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));  // its
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 230)); // colors
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));  // spacer
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));  // buttons
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // note

        Label MakeLbl(string text) => new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            Margin = new Padding(0, 0, 8, 0)
        };

        // ========== Dokument-mappe ==========
        _txtDoc = new TextBox
        {
            Text = _cfg.DokumentFolder ?? "",
            Dock = DockStyle.Fill
        };
        _tip.SetToolTip(_txtDoc, _txtDoc.Text);
        _txtDoc.TextChanged += (_, __) => _tip.SetToolTip(_txtDoc, _txtDoc.Text);

        var btnBrowseDoc = new Button
        {
            Text = "Velg...",
            AutoSize = false,
            Width = 96,
            Height = 30,
            Anchor = AnchorStyles.Right
        };
        btnBrowseDoc.Click += (_, __) => BrowseDocFolder();

        var docRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        docRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        docRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        docRow.Controls.Add(_txtDoc, 0, 0);
        docRow.Controls.Add(btnBrowseDoc, 1, 0);

        root.Controls.Add(MakeLbl("Dokument-mappe"), 0, 0);
        root.Controls.Add(docRow, 1, 0);

        // ========== DB (worker) editable ==========
        var grpDb = new GroupBox { Text = "DB (worker)", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var dbGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6, Margin = new Padding(0) };
        dbGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        dbGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++)
            dbGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        dbGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _txtDbHost = new TextBox { Text = string.IsNullOrWhiteSpace(host) ? "10.44.1.158" : host, Dock = DockStyle.Fill };
        _numDbPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Math.Clamp(port, 1, 65535), Width = 120, Anchor = AnchorStyles.Left };
        _txtDbName = new TextBox { Text = db, Dock = DockStyle.Fill };
        _txtDbUser = new TextBox { Text = user, Dock = DockStyle.Fill };
        _txtDbPass = new TextBox { Text = pass, Dock = DockStyle.Fill, UseSystemPasswordChar = true };

        dbGrid.Controls.Add(new Label { Text = "Host:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        dbGrid.Controls.Add(_txtDbHost, 1, 0);

        dbGrid.Controls.Add(new Label { Text = "Port:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        dbGrid.Controls.Add(_numDbPort, 1, 1);

        dbGrid.Controls.Add(new Label { Text = "Database:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
        dbGrid.Controls.Add(_txtDbName, 1, 2);

        dbGrid.Controls.Add(new Label { Text = "Brukernavn:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 3);
        dbGrid.Controls.Add(_txtDbUser, 1, 3);

        dbGrid.Controls.Add(new Label { Text = "Passord:", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 4);
        dbGrid.Controls.Add(_txtDbPass, 1, 4);

        var dbActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true
        };

        var btnTestDb = new Button { Text = "Test tilkobling", Width = 110, Height = 30 };
        btnTestDb.Click += async (_, __) => await TestDbAsync();

        _lblConnPreview = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(10, 7, 0, 0)
        };

        void RefreshPreview()
        {
            var masked = MaskConnStr(BuildWorkerConnString());
            _lblConnPreview.Text = string.IsNullOrWhiteSpace(masked) ? "" : "Tilkobling: " + masked;
        }

        _txtDbHost.TextChanged += (_, __) => RefreshPreview();
        _numDbPort.ValueChanged += (_, __) => RefreshPreview();
        _txtDbName.TextChanged += (_, __) => RefreshPreview();
        _txtDbUser.TextChanged += (_, __) => RefreshPreview();
        _txtDbPass.TextChanged += (_, __) => RefreshPreview();

        dbActions.Controls.Add(btnTestDb);
        dbActions.Controls.Add(_lblConnPreview);
        dbGrid.Controls.Add(dbActions, 0, 5);
        dbGrid.SetColumnSpan(dbActions, 2);

        grpDb.Controls.Add(dbGrid);

        root.Controls.Add(MakeLbl("DB-tilkobling"), 0, 1);
        root.Controls.Add(grpDb, 1, 1);

        RefreshPreview();

        // ========== Auto-oppdater ==========
        _numRefresh = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 9999,
            Value = Math.Clamp(_cfg.RefreshSeconds, 1, 9999),
            DecimalPlaces = 0,
            Width = 90,
            Anchor = AnchorStyles.Left
        };

        root.Controls.Add(MakeLbl("Auto-oppdater (sek)"), 0, 2);
        root.Controls.Add(_numRefresh, 1, 2);

        // ========== Filters ==========
        _chkOnlyUnconfirmed = new CheckBox { Checked = _cfg.ShowOnlyUnconfirmed, AutoSize = true, Text = "Ja" };
        _chkOnlyUnconfirmed.CheckedChanged += (_, __) => _chkOnlyUnconfirmed.Text = _chkOnlyUnconfirmed.Checked ? "Ja" : "Nei";

        _chkOnlyUnntak = new CheckBox { Checked = _cfg.ShowOnlyUnntak, AutoSize = true, Text = "Ja" };
        _chkOnlyUnntak.CheckedChanged += (_, __) => _chkOnlyUnntak.Text = _chkOnlyUnntak.Checked ? "Ja" : "Nei";

        root.Controls.Add(MakeLbl("Kun ikke bekreftet"), 0, 3);
        root.Controls.Add(_chkOnlyUnconfirmed, 1, 3);

        root.Controls.Add(MakeLbl("Kun unntak"), 0, 4);
        root.Controls.Add(_chkOnlyUnntak, 1, 4);

        // ========== ITS ==========
        _chkItsEnabled = new CheckBox { Checked = _cfg.ItsListenerEnabled, AutoSize = true };
        _chkItsEnabled.Text = _chkItsEnabled.Checked ? "På" : "Av";
        _chkItsEnabled.CheckedChanged += (_, __) =>
        {
            _chkItsEnabled.Text = _chkItsEnabled.Checked ? "På" : "Av";
            UpdateItsUrl();
        };

        _numItsPort = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Value = Math.Clamp(_cfg.ItsListenerPort, 1, 65535),
            DecimalPlaces = 0,
            Width = 90,
            Anchor = AnchorStyles.Left
        };
        _numItsPort.ValueChanged += (_, __) => UpdateItsUrl();

        _txtItsPath = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(_cfg.ItsListenerPath) ? "/NotificationInfo/TollgateInfo" : _cfg.ItsListenerPath,
            Dock = DockStyle.Fill
        };
        _txtItsPath.TextChanged += (_, __) => UpdateItsUrl();

        _lblItsUrl = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 4, 0, 0)
        };

        var itsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = new Padding(0) };
        itsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        itsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        itsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // checkbox
        itsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));    // port
        itsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // path
        itsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));     // spacer

        _chkItsEnabled.Margin = new Padding(0, 4, 10, 0);
        _numItsPort.Margin = new Padding(0, 2, 10, 0);

        itsPanel.Controls.Add(_chkItsEnabled, 0, 0);
        itsPanel.Controls.Add(_numItsPort, 1, 0);
        itsPanel.Controls.Add(_txtItsPath, 2, 0);
        itsPanel.Controls.Add(_lblItsUrl, 0, 1);
        itsPanel.SetColumnSpan(_lblItsUrl, 3);

        UpdateItsUrl();

        root.Controls.Add(MakeLbl("ITS-lytter"), 0, 5);
        root.Controls.Add(itsPanel, 1, 5);

        // ========== Colors ==========
        var grpColors = new GroupBox { Text = "Farger i liste", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var colorsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
        colorsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        colorsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        colorsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        colorsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        colorsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        colorsGrid.Controls.Add(new Label
        {
            Text = "Type vask (format: Navn=#RRGGBB, én linje per valg)",
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 0);

        _txtTypeVaskColors = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
            Text = BuildTypeVaskColorText(_cfg.TypeVaskColorMap)
        };
        colorsGrid.Controls.Add(_txtTypeVaskColors, 0, 1);

        colorsGrid.Controls.Add(new Label
        {
            Text = "Ansatt (format: Navn=#RRGGBB, én linje per navn – tomt = automatisk fast farge)",
            AutoSize = true,
            Dock = DockStyle.Fill
        }, 0, 2);

        _txtAnsattColors = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10F),
            Text = BuildAnsattColorText(_cfg.AnsattColorMap)
        };
        colorsGrid.Controls.Add(_txtAnsattColors, 0, 3);

        colorsGrid.Controls.Add(new Label
        {
            Text = "Tips: bruk for eksempel #2DB34A, #FFE300, #1E9FE6. Tomme eller feil linjer blir ignorert.",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Dock = DockStyle.Fill
        }, 0, 4);

        grpColors.Controls.Add(colorsGrid);
        root.Controls.Add(MakeLbl("Farger"), 0, 6);
        root.Controls.Add(grpColors, 1, 6);

        // Spacer
        root.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 0, 7);
        root.SetColumnSpan(root.GetControlFromPosition(0, 7)!, 2);

        // ========== Buttons ==========
        var buttonsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) };
        buttonsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttonsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        buttonsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        buttonsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

        var btnOpen = new Button { Text = "Åpne mappe", AutoSize = false, Width = 130, Height = 34, Anchor = AnchorStyles.Right };
        var btnSave = new Button { Text = "Lagre", AutoSize = false, Width = 130, Height = 34, Anchor = AnchorStyles.Right };
        var btnClose = new Button { Text = "Lukk", AutoSize = false, Width = 130, Height = 34, Anchor = AnchorStyles.Right, DialogResult = DialogResult.Cancel };

        btnOpen.Click += (_, __) => OpenDocFolder();

        btnSave.Click += (_, __) =>
{
    try
    {
        var warn = SaveRuntimeSettings();

        var msg = "Innstillinger lagret.";

        if (!string.IsNullOrWhiteSpace(warn))
            msg += Environment.NewLine + Environment.NewLine + "Merk: " + warn;

        MessageBox.Show(this, msg, "OK", MessageBoxButtons.OK, MessageBoxIcon.Information);

        DialogResult = DialogResult.OK;
        Close();
    }
    catch (Exception ex)
    {
        MessageBox.Show(this, ex.Message, "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
};

        buttonsRow.Controls.Add(new Label(), 0, 0);
        buttonsRow.Controls.Add(btnOpen, 1, 0);
        buttonsRow.Controls.Add(btnSave, 2, 0);
        buttonsRow.Controls.Add(btnClose, 3, 0);

        root.Controls.Add(buttonsRow, 0, 8);
        root.SetColumnSpan(buttonsRow, 2);

        // ========== Note ==========
        var note = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.DimGray,
            Text = "Merk: UI-passord endres i Admin-appen (Innstillinger → Sikkerhet)."
        };
        root.Controls.Add(note, 0, 9);
        root.SetColumnSpan(note, 2);

        Controls.Add(root);

        AcceptButton = btnSave;
        CancelButton = btnClose;
    }

    private void BrowseDocFolder()
    {
        try
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Velg dokument-mappe for BilvaskRegistrering",
                UseDescriptionForTitle = true,
                SelectedPath = _txtDoc.Text
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
            {
                _txtDoc.Text = dlg.SelectedPath;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenDocFolder()
    {
        try
        {
            var folder = _txtDoc.Text.Trim();
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateItsUrl()
    {
        try
        {
            if (!_chkItsEnabled.Checked)
            {
                _lblItsUrl.Text = "(deaktivert)";
                return;
            }

            var port = (int)_numItsPort.Value;
            var path = (_txtItsPath.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(path))
                path = "/NotificationInfo/TollgateInfo";
            if (!path.StartsWith("/")) path = "/" + path;

            path = path.TrimEnd('/') + "/";

            _lblItsUrl.Text = $"URL: http://+:{port}{path}";
        }
        catch
        {
            _lblItsUrl.Text = "";
        }
    }

    private static bool IsPrivateOrLocalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.StartsWith("127.")) return true;
        if (host.StartsWith("10.")) return true;
        if (host.StartsWith("192.168.")) return true;
        if (host.StartsWith("172.")) return true;
        if (host.StartsWith("100.")) return true; // Tailscale
        return false;
    }

    private string BuildWorkerConnString()
    {
        var host = (_txtDbHost.Text ?? "").Trim();
        var port = (int)_numDbPort.Value;
        var db = (_txtDbName.Text ?? "").Trim();
        var user = (_txtDbUser.Text ?? "").Trim();
        var pass = _txtDbPass.Text ?? "";

        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(db) ||
            string.IsNullOrWhiteSpace(user))
            return string.Empty;

        var b = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = db,
            Username = user,
            Password = pass,
            SslMode = IsPrivateOrLocalHost(host) ? SslMode.Disable : SslMode.Prefer,
            TrustServerCertificate = true
        };
        return b.ConnectionString;
    }

    private async System.Threading.Tasks.Task TestDbAsync()
    {
        try
        {
            var connStr = BuildWorkerConnString();
            if (string.IsNullOrWhiteSpace(connStr))
            {
                MessageBox.Show(this, "Fyll inn Host/Port/Database/Username først.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Cursor = Cursors.WaitCursor;
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            var res = await cmd.ExecuteScalarAsync();

            MessageBox.Show(this, "OK: Tilkoblingen fungerer (SELECT 1 = " + (res?.ToString() ?? "null") + ").", "OK",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Feil: " + ex.Message, "Feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { Cursor = Cursors.Default; } catch { }
        }
    }

    private string? SaveRuntimeSettings()
    {
        var docFolder = _txtDoc.Text.Trim();
        if (string.IsNullOrWhiteSpace(docFolder))
            throw new InvalidOperationException("Dokument-mappe kan ikke være tom.");

        // Normalize ITS path
        var itsPath = (_txtItsPath.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(itsPath))
            itsPath = "/NotificationInfo/TollgateInfo";
        if (!itsPath.StartsWith("/")) itsPath = "/" + itsPath;

        var refresh = (int)_numRefresh.Value;
        var onlyUnconfirmed = _chkOnlyUnconfirmed.Checked;
        var onlyUnntak = _chkOnlyUnntak.Checked;

        var itsEnabled = _chkItsEnabled.Checked;
        var itsPort = (int)_numItsPort.Value;

        var connStr = BuildWorkerConnString();
        var typeVaskColors = ParseColorLines(_txtTypeVaskColors.Text);
        var ansattColors = ParseColorLines(_txtAnsattColors.Text);

        // Write to BOTH locations:
        var docsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BilvaskRegistrering", "settings.runtime.json");
        var pdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BilvaskRegistrering", "settings.runtime.json");

        bool docsOk = false, pdOk = false;
        Exception? docsEx = null;
        Exception? pdEx = null;

        try
        {
            WriteRuntimeJson(docsPath, docFolder, refresh, onlyUnconfirmed, onlyUnntak, itsEnabled, itsPort, itsPath, connStr, typeVaskColors, ansattColors);
            docsOk = true;
        }
        catch (Exception ex)
        {
            docsEx = ex;
        }

        try
        {
            WriteRuntimeJson(pdPath, docFolder, refresh, onlyUnconfirmed, onlyUnntak, itsEnabled, itsPort, itsPath, connStr, typeVaskColors, ansattColors);
            pdOk = true;
        }
        catch (Exception ex)
        {
            pdEx = ex;
        }

        if (!docsOk && !pdOk)
            throw new InvalidOperationException("Kunne ikke lagre innstillinger. " + (docsEx?.Message ?? pdEx?.Message ?? "Ukjent feil."));

        try { Directory.CreateDirectory(docFolder); } catch { /* ignore */ }

        if (docsOk && !pdOk)
            return $"Kunne ikke skrive til ProgramData ({Path.GetDirectoryName(pdPath) ?? pdPath}). Innstillingene er lagret i Dokumenter.";
        if (!docsOk && pdOk)
            return "Kunne ikke skrive til Dokumenter. Innstillingene er lagret i ProgramData.";

        return null;
    }

    private void WriteRuntimeJson(
        string path,
        string dokumentFolder,
        int refreshSeconds,
        bool showOnlyUnconfirmed,
        bool showOnlyUnntak,
        bool itsEnabled,
        int itsPort,
        string itsPath,
        string workerConnString,
        Dictionary<string, string> typeVaskColors,
        Dictionary<string, string> ansattColors)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                var node = JsonNode.Parse(text);
                root = node as JsonObject ?? new JsonObject();
            }
            catch
            {
                // Rename invalid file and start fresh
                try
                {
                    var dir = Path.GetDirectoryName(path)!;
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var backup = Path.Combine(dir, $"settings.runtime.json.invalid_{stamp}");
                    File.Move(path, backup, true);
                }
                catch { /* ignore */ }

                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        // Dokument.Folder
        var dokument = (root["Dokument"] as JsonObject) ?? new JsonObject();
        dokument["Folder"] = dokumentFolder;
        root["Dokument"] = dokument;

        // WorkerUi.*
        var workerUi = (root["WorkerUi"] as JsonObject) ?? new JsonObject();
        workerUi["RefreshSeconds"] = refreshSeconds;
        workerUi["ShowOnlyUnconfirmed"] = showOnlyUnconfirmed;
        workerUi["ShowOnlyUnntak"] = showOnlyUnntak;
        root["WorkerUi"] = workerUi;

        // Worker.* (compat)
        var worker = (root["Worker"] as JsonObject) ?? new JsonObject();
        worker["RefreshSeconds"] = refreshSeconds;
        worker["ShowOnlyUnconfirmed"] = showOnlyUnconfirmed;
        worker["ShowOnlyUnntak"] = showOnlyUnntak;
        root["Worker"] = worker;

        // ItsApi.*
        var its = (root["ItsApi"] as JsonObject) ?? new JsonObject();
        its["Enabled"] = itsEnabled;
        its["Port"] = itsPort;
        its["Path"] = itsPath;
        root["ItsApi"] = its;

        // WorkerColors.*
        var workerColors = (root["WorkerColors"] as JsonObject) ?? new JsonObject();
        var typeNode = new JsonObject();
        foreach (var kv in typeVaskColors.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            typeNode[kv.Key] = kv.Value;
        workerColors["TypeVask"] = typeNode;

        var ansattNode = new JsonObject();
        foreach (var kv in ansattColors.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            ansattNode[kv.Key] = kv.Value;
        workerColors["Ansatt"] = ansattNode;
        root["WorkerColors"] = workerColors;

        // Database + ConnectionStrings (worker)
        if (!string.IsNullOrWhiteSpace(workerConnString))
        {
            try
            {
                var b = new NpgsqlConnectionStringBuilder(workerConnString);

                var db = (root["Database"] as JsonObject) ?? new JsonObject();
                db["Enabled"] = true;
                db["Host"] = b.Host;
                db["WorkerHost"] = b.Host;
                db["Port"] = b.Port;
                db["Database"] = b.Database;
                db["Name"] = b.Database; // compat for older readers
                db["WorkerUser"] = b.Username;
                db["WorkerPassword"] = b.Password;
                db["SslMode"] = b.SslMode.ToString();
                db["TrustServerCertificate"] = b.TrustServerCertificate;
                root["Database"] = db;

                // Normalize ConnectionStrings to avoid case-insensitive duplicate keys (e.g., Worker + worker),
// which can crash the Admin app's ConfigurationBuilder JSON parser.
JsonObject cleanCs = new JsonObject();
if (root["ConnectionStrings"] is JsonObject oldCs)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in oldCs)
    {
        var name = kv.Key ?? "";
        if (string.IsNullOrWhiteSpace(name)) continue;
        if (name.Equals("worker", StringComparison.OrdinalIgnoreCase)) continue; // we will re-add canonical key
        if (!seen.Add(name)) continue;
        cleanCs[name] = kv.Value;
    }
}
cleanCs["Worker"] = workerConnString;
root["ConnectionStrings"] = cleanCs;
            }
            catch
            {
                // ignore DB settings write if parsing fails
            }
        }

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }


    private static Dictionary<string, string> ParseColorLines(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return map;

        var lines = text.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
        foreach (var raw in lines)
        {
            var line = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#")) continue;

            var idx = line.IndexOf('=');
            if (idx <= 0 || idx >= line.Length - 1) continue;

            var key = line.Substring(0, idx).Trim();
            var value = line.Substring(idx + 1).Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            if (!AppConfig.TryParseColor(value, out _)) continue;

            map[key] = value;
        }

        return map;
    }

    private static string BuildTypeVaskColorText(IReadOnlyDictionary<string, string> map)
    {
        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        foreach (var kv in map)
            defaults[kv.Key] = kv.Value;

        return string.Join(Environment.NewLine, defaults.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string BuildAnsattColorText(IReadOnlyDictionary<string, string> map)
    {
        if (map == null || map.Count == 0)
            return string.Empty;

        return string.Join(Environment.NewLine, map.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string MaskConnStr(string? connStr)
    {
        if (string.IsNullOrWhiteSpace(connStr))
            return string.Empty;

        try
        {
            var b = new NpgsqlConnectionStringBuilder(connStr);
            if (!string.IsNullOrWhiteSpace(b.Password))
                b.Password = "********";
            return b.ConnectionString;
        }
        catch
        {
            return connStr.Replace("Password=", "Password=********", StringComparison.OrdinalIgnoreCase);
        }
    }
}
