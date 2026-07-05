using System;
using System.Data;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Npgsql;

namespace BilvaskRegistrering.Forms;

public sealed class AnsatteForm : Form
{
    private readonly string _cs;
    private DataGridView _grid = null!;
    private Button _btnRefresh = null!;
    private Button _btnAdd = null!;
    private Button _btnDeactivate = null!;
    private Button _btnActivate = null!;

    public AnsatteForm(string adminConnectionString)
    {
        _cs = adminConnectionString;
        Text = "Ansatte";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 600);
        Font = new Font(FontFamily.GenericSansSerif, 11f);

        BuildUi();
        Shown += async (_, __) => await RefreshAsync();
    }

    private void BuildUi()
    {
        _btnRefresh = new Button { Text = "Oppdater", Width = 120, Height = 36, Margin = new Padding(5) };
        _btnRefresh.Click += async (_, __) => await RefreshAsync();

        _btnAdd = new Button { Text = "Legg til", Width = 120, Height = 36, Margin = new Padding(5) };
        _btnAdd.Click += async (_, __) => await AddAsync();

        _btnDeactivate = new Button { Text = "Deaktiver", Width = 120, Height = 36, Margin = new Padding(5) };
        _btnDeactivate.Click += async (_, __) => await SetActiveAsync(false);

        _btnActivate = new Button { Text = "Aktiver", Width = 120, Height = 36, Margin = new Padding(5) };
        _btnActivate.Click += async (_, __) => await SetActiveAsync(true);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        top.Controls.AddRange(new Control[] { _btnRefresh, _btnAdd, _btnActivate, _btnDeactivate });

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };

        Controls.Add(_grid);
        Controls.Add(top);
    }

    private async Task RefreshAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync();

            const string sql = @"
SELECT id, ansattnummer, navn, aktiv, created_at
FROM public.ansatter
ORDER BY aktiv DESC, navn ASC;";

            using var da = new NpgsqlDataAdapter(sql, conn);
            var dt = new DataTable();
            da.Fill(dt);

            _grid.DataSource = dt;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "DB-feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task AddAsync()
    {
        using var dlg = new AddAnsattDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync();

            const string sql = @"INSERT INTO public.ansatter (ansattnummer, navn, pin, aktiv) VALUES (@ansattnummer, @navn, @pin, TRUE);";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("ansattnummer", dlg.AnsattNummer.Trim());
            cmd.Parameters.AddWithValue("navn", dlg.AnsattNavn.Trim());
            cmd.Parameters.AddWithValue("pin", (object?)dlg.PinOrNull ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "DB-feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private long? SelectedId()
    {
        if (_grid.CurrentRow?.DataBoundItem is DataRowView drv)
        {
            if (long.TryParse(drv.Row["id"].ToString(), out var id)) return id;
        }
        return null;
    }

    private async Task SetActiveAsync(bool active)
    {
        var id = SelectedId();
        if (id == null) return;

        try
        {
            await using var conn = new NpgsqlConnection(_cs);
            await conn.OpenAsync();

            const string sql = @"UPDATE public.ansatter SET aktiv = @a WHERE id = @id;";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("a", active);
            cmd.Parameters.AddWithValue("id", id.Value);
            await cmd.ExecuteNonQueryAsync();

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.GetBaseException().Message, "DB-feil", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class AddAnsattDialog : Form
{
    private TextBox _txtNumber = null!;
    private TextBox _txtName = null!;
    private TextBox _txtPin = null!;

    public string AnsattNummer => _txtNumber.Text;
    public string AnsattNavn => _txtName.Text;
    public string? PinOrNull => string.IsNullOrWhiteSpace(_txtPin.Text) ? null : _txtPin.Text.Trim();

    public AddAnsattDialog()
    {
        Text = "Legg til ansatt";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(420, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lbl0 = new Label { Text = "Ansattnummer:", AutoSize = true, Location = new Point(20, 25) };
        _txtNumber = new TextBox { Location = new Point(120, 20), Width = 260 };

        var lbl1 = new Label { Text = "Navn:", AutoSize = true, Location = new Point(20, 65) };
        _txtName = new TextBox { Location = new Point(120, 60), Width = 260 };

        var lbl2 = new Label { Text = "PIN (valgfri):", AutoSize = true, Location = new Point(20, 105) };
        _txtPin = new TextBox { Location = new Point(120, 100), Width = 260 };

        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 120, Height = 32, Location = new Point(120, 155) };
        var btnCancel = new Button { Text = "Avbryt", DialogResult = DialogResult.Cancel, Width = 120, Height = 32, Location = new Point(260, 155) };

        btnOk.Click += (_, __) =>
        {
            if (string.IsNullOrWhiteSpace(_txtNumber.Text) || string.IsNullOrWhiteSpace(_txtName.Text))
            {
                MessageBox.Show(this, "Skriv inn ansattnummer og navn.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None;
            }
        };

        Controls.AddRange(new Control[] { lbl0, _txtNumber, lbl1, _txtName, lbl2, _txtPin, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
