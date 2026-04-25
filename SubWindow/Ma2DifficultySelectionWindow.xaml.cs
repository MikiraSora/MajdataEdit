using System.Windows;
using MajdataEdit.Ma2Export;

namespace MajdataEdit;

public partial class Ma2DifficultySelectionWindow : Window
{
    public Ma2DifficultySelectionWindow(IReadOnlyList<Ma2DifficultyOption> options)
    {
        Options = options;
        InitializeComponent();
        DataContext = this;
        UpdateOkButtonState();
    }

    public IReadOnlyList<Ma2DifficultyOption> Options { get; }

    public IReadOnlyList<Ma2DifficultyOption> SelectedOptions =>
        Options.Where(x => x.IsSelected).ToArray();

    private void DifficultyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOkButtonState();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedOptions.Count == 0)
        {
            return;
        }

        DialogResult = true;
    }

    private void UpdateOkButtonState()
    {
        if (OkButton == null)
        {
            return;
        }

        OkButton.IsEnabled = Options.Any(x => x.IsSelected);
    }
}
