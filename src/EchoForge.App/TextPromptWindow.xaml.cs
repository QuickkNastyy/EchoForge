using System.Windows;

namespace EchoForge.App;

/// <summary>Small themed text prompt shared by recording and speaker rename actions.</summary>
public partial class TextPromptWindow : Window
{
    public TextPromptWindow() => InitializeComponent();

    public string Value => Input.Text.Trim();

    public static string? Ask(Window owner, string title, string prompt, string? current)
    {
        TextPromptWindow dialog = new()
        {
            Owner = owner,
            Title = title,
        };

        dialog.PromptText.Text = prompt;
        dialog.Input.Text = current ?? string.Empty;
        dialog.Loaded += (_, _) =>
        {
            dialog.Input.Focus();
            dialog.Input.SelectAll();
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
