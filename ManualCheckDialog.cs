namespace VSCode_C_;

/// <summary>
/// Dialog for manual check confirmation — auto-sizes to fit all text.
/// </summary>
public sealed class ManualCheckDialog : Form
{
    public CheckStatus SelectedStatus { get; private set; } = CheckStatus.Skipped;

    public ManualCheckDialog(string title, string question, string? detail)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        Padding = new Padding(12);
        int maxLabelWidth = 540;

        // ── Question label (auto-size, word-wrap) ──
        var lblQ = new Label
        {
            Text = question,
            AutoSize = false,
            MaximumSize = new Size(maxLabelWidth, 0),
            Size = new Size(maxLabelWidth, 24),
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            TextAlign = ContentAlignment.TopLeft,
            Location = new Point(16, 16)
        };
        // Let the label measure its preferred height
        lblQ.Size = lblQ.GetPreferredSize(new Size(maxLabelWidth, 0));
        int y = lblQ.Bottom + 8;

        // ── Detail label (auto-size, word-wrap) ──
        Label? lblDetail = null;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lblDetail = new Label
            {
                Text = detail,
                AutoSize = false,
                MaximumSize = new Size(maxLabelWidth, 0),
                Size = new Size(maxLabelWidth, 20),
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8, FontStyle.Italic),
                TextAlign = ContentAlignment.TopLeft,
                Location = new Point(16, y)
            };
            lblDetail.Size = lblDetail.GetPreferredSize(new Size(maxLabelWidth, 0));
            y = lblDetail.Bottom + 16;
        }
        else
        {
            y += 8;
        }

        // ── Buttons ──
        int btnW = 120;
        int btnH = 42;
        int gap = 8;
        int totalBtnW = btnW * 4 + gap * 3;
        int btnStartX = (maxLabelWidth + 32 - totalBtnW) / 2;

        var btnPass = new Button { Text = "&Pass", Location = new Point(btnStartX, y), Size = new Size(btnW, btnH) };
        var btnFail = new Button { Text = "&Fail", Location = new Point(btnStartX + btnW + gap, y), Size = new Size(btnW, btnH) };
        var btnWarn = new Button { Text = "&Warn", Location = new Point(btnStartX + (btnW + gap) * 2, y), Size = new Size(btnW, btnH) };
        var btnSkip = new Button { Text = "&Skip", Location = new Point(btnStartX + (btnW + gap) * 3, y), Size = new Size(btnW, btnH) };

        btnPass.Click += (_, _) => { SelectedStatus = CheckStatus.Pass; DialogResult = DialogResult.OK; Close(); };
        btnFail.Click += (_, _) => { SelectedStatus = CheckStatus.Fail; DialogResult = DialogResult.OK; Close(); };
        btnWarn.Click += (_, _) => { SelectedStatus = CheckStatus.Warning; DialogResult = DialogResult.OK; Close(); };
        btnSkip.Click += (_, _) => { SelectedStatus = CheckStatus.Skipped; DialogResult = DialogResult.OK; Close(); };

        // ── Set form size to fit content ──
        int formW = maxLabelWidth + 32;
        int formH = y + btnH + 16;
        ClientSize = new Size(formW, formH);

        var controls = new List<Control> { lblQ, btnPass, btnFail, btnWarn, btnSkip };
        if (lblDetail is not null) controls.Insert(1, lblDetail);
        Controls.AddRange(controls.ToArray());
    }
}
