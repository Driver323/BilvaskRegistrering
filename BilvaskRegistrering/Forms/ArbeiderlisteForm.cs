using System;
using System.Data;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Npgsql;

namespace BilvaskRegistrering.Forms;

public sealed class ArbeiderlisteForm : Form
{
    private readonly string _cs;
    private DataGridView _grid = null!;
    private CheckBox _chkKunIkkeBekreftet = null!;
    private Button _btnOppdater = null!;
    private Button _btnSlettBekreftelse = null!;
    private ComboBox _cmbPeriode = null!;
    private ComboBox _cmbSkift = null!;
    private DateTimePicker _dtFra = null!;
    private DateTimePicker _dtTil = null!;
    private bool _internalRangeUpdate;

    private System.Windows.Forms.Timer? _autoRefreshTimer;
    private bool _isLoading;
    private bool _autoRefreshBusy;
    private long _lastSigMaxId;
    private DateTime _lastSigMaxConfirmedUtc = DateTime.MinValue;

    public ArbeiderlisteForm(string adminConnectionString)
    {
        _cs = adminConnectionString;

        Text = "Bilvask – Arbeiderliste (kontroll)";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1400, 900);
        Font = new Font(FontFamily.GenericSansSerif, 11f);

        BuildUi();

        Shown += async (_, __) =>
        {
            // Ensure initial preset range applied (default Uke)
            ApplyPresetRange(_cmbPeriode.SelectedItem?.ToString());
            await LoadAsync();
            StartAutoRefresh();
        };

        FormClosed += (_, __) => StopAutoRefresh();
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 90 };

        _chkKunIkkeBekreftet = new CheckBox
        {
            Text = "Kun ikke bekreftet",
            AutoSize = true,
            Location = new Point(20, 15)
        };
        _chkKunIkkeBekreftet.CheckedChanged += async (_, __) => await LoadAsync();

        _btnOppdater = new Button
        {
            Text = "Oppdater",
            Width = 140,
            Height = 32,
            Location = new Point(220, 9)
        };
        _btnOppdater.Click += async (_, __) =>
        {
            // If a preset period is selected, refresh the window end (Til=now) so new rows are included.
            var preset = _cmbPeriode.SelectedItem?.ToString();
            if (!string.Equals(preset, "Egendefinert", StringComparison.OrdinalIgnoreCase))
                ApplyPresetRange(preset);
            await LoadAsync();
        };

        _btnSlettBekreftelse = new Button
        {
            Text = "Slett bekreftelse",
            Width = 170,
            Height = 32,
            Location = new Point(380, 9)
        };
        _btnSlettBekreftelse.Click += async (_, __) => await DeleteConfirmationAsync();


        var lblPeriode = new Label
        {
            Text = "Periode:",
            AutoSize = true,
            Location = new Point(20, 58)
        };

        _cmbPeriode = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150,
            Location = new Point(90, 54)
        };
        _cmbPeriode.Items.AddRange(new object[] { "Dag", "Uke", "Måned", "Kvartal", "Halvår", "År", "Egendefinert" });
        _cmbPeriode.SelectedIndex = 1; // Uke

        var lblFra = new Label
        {
            Text = "Fra:",
            AutoSize = true,
            Location = new Point(270, 58)
        };

        _dtFra = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd.MM.yyyy HH:mm",
            ShowUpDown = true,
            Width = 220,
            Location = new Point(310, 54),
            Value = DateTime.Now.AddDays(-7)
        };

        var lblTil = new Label
        {
            Text = "Til:",
            AutoSize = true,
            Location = new Point(560, 58)
        };

        _dtTil = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "dd.MM.yyyy HH:mm",
            ShowUpDown = true,
            Width = 220,
            Location = new Point(600, 54),
            Value = DateTime.Now
        };

        var lblSkift = new Label
        {
            Text = "Skift:",
            AutoSize = true,
            Location = new Point(850, 58)
        };

        _cmbSkift = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            Location = new Point(900, 54)
        };
        _cmbSkift.Items.AddRange(new object[] { "Aktuelt skift", "Skift 1", "Skift 2", "Skift 3", "Alle" });
        _cmbSkift.SelectedIndex = 0;
        _cmbSkift.SelectedIndexChanged += async (_, __) => await LoadAsync();

        _cmbPeriode.SelectedIndexChanged += (_, __) =>
        {
            if (_internalRangeUpdate) return;
            ApplyPresetRange(_cmbPeriode.SelectedItem?.ToString());
            _ = LoadAsync();
        };

        _dtFra.ValueChanged += async (_, __) =>
        {
            if (_internalRangeUpdate) return;
            EnsureRangeIsValid();
            SetPresetEgendefinert();
            await LoadAsync();
        };

        _dtTil.ValueChanged += async (_, __) =>
        {
            if (_internalRangeUpdate) return;
            EnsureRangeIsValid();
            SetPresetEgendefinert();
            await LoadAsync();
        };

        top.Controls.Add(_chkKunIkkeBekreftet);
        top.Controls.Add(_btnOppdater);
        top.Controls.Add(_btnSlettBekreftelse);
        top.Controls.Add(lblPeriode);
        top.Controls.Add(_cmbPeriode);
        top.Controls.Add(lblFra);
        top.Controls.Add(_dtFra);
        top.Controls.Add(lblTil);
        top.Controls.Add(_dtTil);
        top.Controls.Add(lblSkift);
        top.Controls.Add(_cmbSkift);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            RowHeadersVisible = false,
            AllowUserToResizeRows = false
        };
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.DataBindingComplete += (_, __) => ApplyGridLayout();

        Controls.Add(_grid);
        Controls.Add(top);
    }


    private void SetPresetEgendefinert()
    {
        if (_cmbPeriode == null) return;
        if (_cmbPeriode.SelectedItem?.ToString() == "Egendefinert") return;
        _internalRangeUpdate = true;
        _cmbPeriode.SelectedItem = "Egendefinert";
        _internalRangeUpdate = false;
    }

    private void EnsureRangeIsValid()
    {
        if (_dtFra == null || _dtTil == null) return;

        if (_dtFra.Value > _dtTil.Value)
        {
            _internalRangeUpdate = true;
            var tmp = _dtFra.Value;
            _dtFra.Value = _dtTil.Value;
            _dtTil.Value = tmp;
            _internalRangeUpdate = false;
        }
    }

    private void ApplyPresetRange(string? preset)
    {
        if (_dtFra == null || _dtTil == null) return;

        var now = DateTime.Now;
        var from = preset switch
        {
            "Dag" => now.AddDays(-1),
            "Uke" => now.AddDays(-7),
            "Måned" => now.AddMonths(-1),
            "Kvartal" => now.AddMonths(-3),
            "Halvår" => now.AddMonths(-6),
            "År" => now.AddYears(-1),
            _ => (DateTime?)null
        };

        if (from == null) return;

        _internalRangeUpdate = true;
        _dtTil.Value = now;
        _dtFra.Value = from.Value;
        EnsureRangeIsValid();
        _internalRangeUpdate = false;
    }


    private void StartAutoRefresh()
    {
        if (_autoRefreshTimer != null) return;

        _autoRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 900 // ~1s: feels immediate after scanning, without hammering DB
        };
        _autoRefreshTimer.Tick += (_, __) => _ = AutoRefreshTickAsync();
        _autoRefreshTimer.Start();
    }

    private void StopAutoRefresh()
    {
        try
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer?.Dispose();
        }
        catch { /* ignore */ }
        finally { _autoRefreshTimer = null; }
    }

    private async Task AutoRefreshTickAsync()
    {
        if (_autoRefreshBusy || _isLoading) return;
        _autoRefreshBusy = true;

        try
        {
            // Keep preset periods sliding (Til=now), so new scans fall inside the time window.
            var preset = _cmbPeriode.SelectedItem?.ToString();
            if (!string.Equals(preset, "Egendefinert", StringComparison.OrdinalIgnoreCase))
                ApplyPresetRange(preset);
            var (maxId, maxConfirmedUtc) = await QuerySignatureAsync();
            if (maxId != _lastSigMaxId || maxConfirmedUtc != _lastSigMaxConfirmedUtc)
            {
                await LoadAsync();
            }
        }
        catch
        {
            // ignore transient DB errors during polling
        }
        finally
        {
            _autoRefreshBusy = false;
        }
    }

    private async Task<(long maxId, DateTime maxConfirmedUtc)> QuerySignatureAsync()
    {
        EnsureRangeIsValid();
        var fromUtc = DateTime.SpecifyKind(_dtFra.Value, DateTimeKind.Local).ToUniversalTime();
        var toUtc = DateTime.SpecifyKind(_dtTil.Value, DateTimeKind.Local).ToUniversalTime();

        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync();

        string sql = @"
SELECT
  COALESCE(MAX(we.id), 0) AS max_id,
  COALESCE(MAX(wc.confirmed_at), TIMESTAMPTZ 'epoch') AS max_confirmed
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
WHERE
  -- show Unntak from ALL sources
  TRUE
  AND upper(regexp_replace(coalesce(we.plate,''), '[[:space:]-]', '', 'g')) ~ '^[A-Z]{2}[0-9]{5}$'
  AND LOWER(TRIM(COALESCE(we.status, ''))) = 'unntak'
  AND we.occurred_at >= @from
  AND we.occurred_at <= @to";

        if (_chkKunIkkeBekreftet.Checked)
            sql += " AND wc.confirmed_at IS NULL";

        sql += ";";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add("from", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = fromUtc;
        cmd.Parameters.Add("to", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = toUtc;

        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            long maxId = 0;
            try { maxId = r.IsDBNull(0) ? 0 : r.GetInt64(0); } catch { }

            DateTime maxConfirmed = DateTime.MinValue;
            try
            {
                if (!r.IsDBNull(1))
                {
                    maxConfirmed = r.GetDateTime(1);
                    if (maxConfirmed.Kind != DateTimeKind.Utc)
                        maxConfirmed = DateTime.SpecifyKind(maxConfirmed, DateTimeKind.Utc);
                }
            }
            catch { }

            return (maxId, maxConfirmed);
        }

        return (0, DateTime.MinValue);
    }

    private void UpdateSignatureFromDataTable(DataTable dt)
    {
        try
        {
            long maxId = 0;
            DateTime maxConfirmedLocal = DateTime.MinValue;

            foreach (DataRow row in dt.Rows)
            {
                if (row.Table.Columns.Contains("id") && row["id"] != DBNull.Value)
                {
                    if (long.TryParse(row["id"].ToString(), out var id) && id > maxId)
                        maxId = id;
                }

                if (row.Table.Columns.Contains("confirmed_at") && row["confirmed_at"] != DBNull.Value)
                {
                    if (row["confirmed_at"] is DateTime dtc2)
                    {
                        if (dtc2 > maxConfirmedLocal) maxConfirmedLocal = dtc2;
                    }
                    else if (DateTime.TryParse(row["confirmed_at"].ToString(), out var dtc) && dtc > maxConfirmedLocal)
                    {
                        maxConfirmedLocal = dtc;
                    }
                }
            }

            _lastSigMaxId = maxId;
            _lastSigMaxConfirmedUtc = maxConfirmedLocal == DateTime.MinValue
                ? DateTime.MinValue
                : DateTime.SpecifyKind(maxConfirmedLocal, DateTimeKind.Local).ToUniversalTime();
        }
        catch
        {
            // ignore
        }
    }

    private async Task LoadAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync();

            string sql = @"
SELECT
  we.id,
  we.occurred_at AT TIME ZONE 'Europe/Oslo' AS dato,
  we.internnr,
  upper(regexp_replace(coalesce(we.plate,''), '[[:space:]-]', '', 'g')) AS reg_nr,
  we.vehicle_type AS type_kjoretoy,
  COALESCE(wc.type_vask, 'Innvendig/uttvendig vask') AS type_vask,
  we.season AS sesong,
  we.status,
  wc.uregistrert_skade,
  a.navn AS ansatt,
  wc.confirmed_at AT TIME ZONE 'Europe/Oslo' AS confirmed_at
FROM public.wash_events we
LEFT JOIN public.wash_confirmations wc ON wc.wash_event_id = we.id
LEFT JOIN public.ansatter a ON a.id = wc.ansatt_id
WHERE
  -- show Unntak from ALL sources
  TRUE
  AND upper(regexp_replace(coalesce(we.plate,''), '[[:space:]-]', '', 'g')) ~ '^[A-Z]{2}[0-9]{5}$'
  AND LOWER(TRIM(COALESCE(we.status, ''))) = 'unntak'
  AND we.occurred_at >= @from
  AND we.occurred_at <= @to
";

                if (_chkKunIkkeBekreftet.Checked)
                    sql += " AND wc.confirmed_at IS NULL ";

            sql += " ORDER BY dato DESC LIMIT 2000;";

            EnsureRangeIsValid();
            await using var cmd = new NpgsqlCommand(sql, conn);
            // Npgsql requires UTC values when binding to 'timestamp with time zone' (timestamptz).
            // The UI pickers are local (Europe/Oslo), so convert to UTC for filtering.
            var fromUtc = DateTime.SpecifyKind(_dtFra.Value, DateTimeKind.Local).ToUniversalTime();
            var toUtc = DateTime.SpecifyKind(_dtTil.Value, DateTimeKind.Local).ToUniversalTime();
            cmd.Parameters.Add("from", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = fromUtc;
            cmd.Parameters.Add("to", NpgsqlTypes.NpgsqlDbType.TimestampTz).Value = toUtc;

            using var da = new NpgsqlDataAdapter(cmd);
            var dt = new DataTable();
            da.Fill(dt);

            // Add turnus/shift columns (computed client-side)
            if (!dt.Columns.Contains("skift")) dt.Columns.Add("skift", typeof(string));
            if (!dt.Columns.Contains("skift_nr")) dt.Columns.Add("skift_nr", typeof(int));

            foreach (DataRow r in dt.Rows)
            {
                // Prefer confirmed_at for shift grouping (who worked the shift), fall back to occurred_at
                DateTime tLocal;
                if (dt.Columns.Contains("confirmed_at") && r["confirmed_at"] != DBNull.Value)
                    tLocal = (DateTime)r["confirmed_at"];
                else
                    tLocal = (DateTime)r["dato"];

                var sh = BilvaskRegistrering.TurnusHelper.GetShift(tLocal);
                r["skift"] = sh.Display;
                r["skift_nr"] = sh.Nr;
            }

            // Apply shift filter
            var sel = _cmbSkift?.SelectedItem?.ToString() ?? "Alle";
            var view = dt.DefaultView;

            if (string.Equals(sel, "Aktuelt skift", StringComparison.OrdinalIgnoreCase))
            {
                var (_, startLocal, endLocal) = BilvaskRegistrering.TurnusHelper.GetCurrentShiftWindow(DateTime.Now);
                // Filter by time window on the chosen time column (confirmed_at when present)
                // DataView RowFilter can't easily do coalesce with DateTime, so filter by 'dato' range first and
                // then keep confirmed rows within range through a second pass.
                // We do a simple approach: keep rows where (confirmed_at if not null else dato) is in window.
                var keep = dt.Clone();
                foreach (DataRow r in dt.Rows)
                {
                    DateTime tLocal;
                    if (dt.Columns.Contains("confirmed_at") && r["confirmed_at"] != DBNull.Value)
                        tLocal = (DateTime)r["confirmed_at"];
                    else
                        tLocal = (DateTime)r["dato"];

                    if (tLocal >= startLocal && tLocal < endLocal)
                        keep.ImportRow(r);
                }
                view = keep.DefaultView;
            }
            else if (string.Equals(sel, "Skift 1", StringComparison.OrdinalIgnoreCase))
            {
                view.RowFilter = "skift_nr = 1";
            }
            else if (string.Equals(sel, "Skift 2", StringComparison.OrdinalIgnoreCase))
            {
                view.RowFilter = "skift_nr = 2";
            }
            else if (string.Equals(sel, "Skift 3", StringComparison.OrdinalIgnoreCase))
            {
                view.RowFilter = "skift_nr = 3";
            }
            else
            {
                view.RowFilter = string.Empty;
            }

            // Sort by shift, then newest first
            view.Sort = "skift_nr ASC, dato DESC";

            _grid.DataSource = view;
            ApplyGridLayout();

            UpdateSignatureFromDataTable(view.ToTable());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Feil ved lasting av liste:\n\n" + ex.GetBaseException().Message,
                "DB-feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ApplyGridLayout()
    {
        if (_grid == null) return;

        try
        {
            foreach (DataGridViewColumn col in _grid.Columns)
            {
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                col.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                col.MinimumWidth = 60;
                col.Visible = true;
            }

            SetHeaderAndWidth("id", "id", 50);
            SetHeaderAndWidth("dato", "dato", 130);
            SetHeaderAndWidth("internnr", "internnr", 75);
            SetHeaderAndWidth("reg_nr", "reg nr", 80);
            SetHeaderAndWidth("type_kjoretoy", "type kjøretøy", 125);
            SetHeaderAndWidth("type_vask", "type vask", 170);
            SetHeaderAndWidth("sesong", "sesong", 70);
            SetHeaderAndWidth("status", "status", 75);
            SetHeaderAndWidth("ansatt", "ansatt", 90);
            SetHeaderAndWidth("confirmed_at", "confirmed at", 110);
            SetHeaderAndWidth("skift_nr", "skift nr", 70);

            if (_grid.Columns.Contains("skift"))
                _grid.Columns["skift"].Visible = false;

            if (_grid.Columns.Contains("uregistrert_skade"))
            {
                var col = _grid.Columns["uregistrert_skade"];
                col.HeaderText = "Uregistrert skade";
                col.MinimumWidth = 280;
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                col.FillWeight = 1000;
                col.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            }

            _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            _grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.RowsDefaultCellStyle.Alignment = DataGridViewContentAlignment.TopLeft;
            _grid.RowTemplate.Height = 26;
        }
        catch
        {
            // keep grid usable even if some columns differ in older builds
        }
    }

    private void SetHeaderAndWidth(string columnName, string headerText, int width)
    {
        if (_grid == null || !_grid.Columns.Contains(columnName)) return;

        var col = _grid.Columns[columnName];
        col.HeaderText = headerText;
        col.Width = width;
        col.MinimumWidth = Math.Min(width, Math.Max(50, width));
    }

    private async Task DeleteConfirmationAsync()
    {
        if (_grid.CurrentRow == null) return;

        var idObj = _grid.CurrentRow.Cells["id"].Value;
        if (idObj == null) return;

        if (!long.TryParse(idObj.ToString(), out var washEventId)) return;

        var confirmObj = _grid.CurrentRow.Cells["confirmed_at"].Value;
        if (confirmObj == null || confirmObj == DBNull.Value)
        {
            MessageBox.Show(this, "Denne raden er ikke bekreftet enda.", "Info",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ok = MessageBox.Show(this,
            "Slette bekreftelsen for valgt vask?\n\nDette gjør at den blir 'ikke bekreftet' igjen i arbeider-appen.",
            "Bekreft", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (ok != DialogResult.Yes) return;
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync();

            const string sql = "DELETE FROM public.wash_confirmations WHERE wash_event_id = @id;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", washEventId);
            await cmd.ExecuteNonQueryAsync();

            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Feil ved sletting:\n\n" + ex.GetBaseException().Message,
                "DB-feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
