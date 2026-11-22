using Majorsilence.Forms;

namespace P2PShare.Desktop.Dialogs;

/// <summary>A minimal modal text-entry dialog (classic WinForms has no built-in InputBox).</summary>
public sealed class PromptDialog : Form
{
    private readonly TextBox _input;

    public string Value => _input.Text;

    public PromptDialog(string title, string prompt, string initialValue = "")
    {
        Text = title;
        ClientSize = new System.Drawing.Size(460, 140);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var label = new Label { Left = 12, Top = 12, Width = 436, Text = prompt };
        _input = new TextBox { Left = 12, Top = 38, Width = 436, Text = initialValue };

        var ok = new Button { Text = "OK", Left = 272, Top = 74, Width = 85, Height = 28, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 363, Top = 74, Width = 85, Height = 28, DialogResult = DialogResult.Cancel };

        Controls.Add(label);
        Controls.Add(_input);
        Controls.Add(ok);
        Controls.Add(cancel);
    }
}
