using System.Drawing;
using System.Windows.Forms;

namespace WaveEdit.UI;

/// <summary>Minimal single-line text prompt (no WinForms VB dependency).</summary>
internal static class InputDialog
{
    public static string? Show(IWin32Window owner, string title, string prompt, string initial)
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(320, 120),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var lbl = new Label { Text = prompt, AutoSize = true, Location = new Point(12, 15) };
        var box = new TextBox { Text = initial, Location = new Point(12, 40), Width = 296 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(152, 78), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(233, 78), Width = 75 };
        f.Controls.AddRange(new Control[] { lbl, box, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        box.SelectAll();
        return f.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
