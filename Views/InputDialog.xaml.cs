using System.Windows;

namespace Hanime1Downloader.CSharp.Views;

public partial class InputDialog : Window
{
    public string InputText => InputBox.Text;

    public InputDialog(string title, string prompt, string? defaultValue = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = defaultValue ?? string.Empty;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
