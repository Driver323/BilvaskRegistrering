using System;
using System.Drawing;
using System.Windows.Forms;

namespace BilvaskRegistrering.Worker;

internal static class PasswordPrompt
{
    public static bool Require(IWin32Window owner, string title, string expectedPassword, string prompt)
    {
        if (string.IsNullOrEmpty(expectedPassword))
            return true;

        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(520, 180)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // label
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));  // input
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // buttons

        var lbl = new Label
        {
            Text = string.IsNullOrWhiteSpace(prompt) ? "Skriv passord:" : prompt,
            AutoSize = true,
            Dock = DockStyle.Top
        };

        // Centered input (fixed width)
        var inputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var txt = new TextBox
        {
            UseSystemPasswordChar = true,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 11F, FontStyle.Regular)
        };
        inputRow.Controls.Add(txt, 1, 0);

        // Centered buttons (same size)
        var btnRow = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            ColumnCount = 5,
            RowCount = 1,
            Height = 40,
            Margin = new Padding(0, 18, 0, 0)
        };
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        btnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 120, Height = 32, Anchor = AnchorStyles.None };
        var btnCancel = new Button { Text = "Avbryt", DialogResult = DialogResult.Cancel, Width = 120, Height = 32, Anchor = AnchorStyles.None };

        btnRow.Controls.Add(btnOk, 1, 0);
        btnRow.Controls.Add(btnCancel, 3, 0);

        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;

        root.Controls.Add(lbl, 0, 0);
        root.Controls.Add(inputRow, 0, 1);
        root.Controls.Add(btnRow, 0, 2);

        dlg.Controls.Add(root);

        txt.Focus();

        if (dlg.ShowDialog(owner) != DialogResult.OK)
            return false;

        var entered = txt.Text ?? string.Empty;
        var expected = expectedPassword ?? string.Empty;

        if (string.Equals(entered, expected, StringComparison.Ordinal))
            return true;

        MessageBox.Show(owner, "Feil passord.", "Tilgang nektet", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}
