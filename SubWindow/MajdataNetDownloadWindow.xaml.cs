using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MajdataEdit.Ma2Export;
using MajdataEdit.MajdataNetDownload;

namespace MajdataEdit;

public partial class MajdataNetDownloadWindow : Window
{
    private static readonly string[] DownloadFileNames =
        { "maidata.txt", "track.mp3", "bg.jpg", "pv.mp4" };

    private CancellationTokenSource? _downloadCancellationTokenSource;
    private bool _isDownloading;

    public MajdataNetDownloadWindow(string? initialDirectory = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            SaveDirectoryTextBox.Text = initialDirectory;
        }

        UpdateSaveDirectoryState();
    }

    public string? DownloadedChartDirectory { get; private set; }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SongUrlTextBox.Focus();
    }

    private void SelectDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (UseTemporaryDirectoryCheckBox.IsChecked == true)
        {
            return;
        }

        var initialDirectory = Directory.Exists(SaveDirectoryTextBox.Text)
            ? SaveDirectoryTextBox.Text
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var selectedDirectory = FolderPicker.SelectFolder(
            this,
            MainWindow.GetLocalizedString("MajdataNetSelectDirectoryTitle"),
            initialDirectory);
        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            SaveDirectoryTextBox.Text = selectedDirectory;
        }
    }

    private void UseTemporaryDirectory_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSaveDirectoryState();
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            return;
        }

        var songUrl = SongUrlTextBox.Text.Trim();
        if (!MajdataNetChartDownloader.TryParseSongId(songUrl, out _))
        {
            ShowInputWarning("MajdataNetInvalidUrl", SongUrlTextBox);
            return;
        }

        var useTemporaryDirectory = UseTemporaryDirectoryCheckBox.IsChecked == true;
        string targetDirectory;
        string? generatedTemporaryDirectory = null;
        if (useTemporaryDirectory)
        {
            targetDirectory = Path.Combine(
                Path.GetTempPath(),
                "MajdataEdit-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(targetDirectory);
                generatedTemporaryDirectory = targetDirectory;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    MainWindow.GetLocalizedString("MajdataNetCreateTemporaryDirectoryFailed") + "\n" +
                    exception.Message,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            targetDirectory = SaveDirectoryTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetDirectory) || !Directory.Exists(targetDirectory))
            {
                ShowInputWarning("MajdataNetInvalidDirectory", SaveDirectoryTextBox);
                return;
            }
        }

        var existingFiles = DownloadFileNames
            .Select(fileName => Path.Combine(targetDirectory, fileName))
            .Where(File.Exists)
            .ToArray();
        if (existingFiles.Length > 0)
        {
            var overwriteMessage = MainWindow.GetLocalizedString("MajdataNetOverwritePrompt") +
                                   "\n\n" + string.Join("\n", existingFiles);
            if (MessageBox.Show(overwriteMessage, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) !=
                MessageBoxResult.Yes)
            {
                return;
            }
        }

        _downloadCancellationTokenSource = new CancellationTokenSource();
        SetDownloadingState(true);
        try
        {
            var progress = new Progress<string>(message => StatusTextBlock.Text = message);
            var result = await MajdataNetChartDownloader.DownloadAsync(
                songUrl,
                targetDirectory,
                progress,
                _downloadCancellationTokenSource.Token);
            DownloadedChartDirectory = result.ChartDirectory;
            SetDownloadingState(false);
            MessageBox.Show(
                string.Format(
                    MainWindow.GetLocalizedString("MajdataNetDownloadComplete"),
                    result.DownloadedFiles.Count,
                    result.ChartDirectory),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_downloadCancellationTokenSource?.IsCancellationRequested == true)
        {
            StatusTextBlock.Text = MainWindow.GetLocalizedString("MajdataNetDownloadCanceled");
        }
        catch (OperationCanceledException)
        {
            var message = MainWindow.GetLocalizedString("MajdataNetRequestTimedOut");
            MessageBox.Show(
                MainWindow.GetLocalizedString("MajdataNetDownloadFailed") + "\n" + message,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusTextBlock.Text = message;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                MainWindow.GetLocalizedString("MajdataNetDownloadFailed") + "\n" + exception.Message,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            _downloadCancellationTokenSource?.Dispose();
            _downloadCancellationTokenSource = null;
            if (_isDownloading)
            {
                SetDownloadingState(false);
            }

            if (!string.IsNullOrWhiteSpace(generatedTemporaryDirectory) &&
                !string.Equals(
                    DownloadedChartDirectory,
                    generatedTemporaryDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(generatedTemporaryDirectory);
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isDownloading)
        {
            DialogResult = false;
            return;
        }

        StatusTextBlock.Text = MainWindow.GetLocalizedString("MajdataNetCanceling");
        _downloadCancellationTokenSource?.Cancel();
    }

    private void SetDownloadingState(bool downloading)
    {
        _isDownloading = downloading;
        InputPanel.IsEnabled = !downloading;
        DownloadButton.IsEnabled = !downloading;
        DownloadProgressBar.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        Cursor = downloading ? Cursors.Wait : null;
        if (!downloading)
        {
            UpdateSaveDirectoryState();
        }
    }

    private void UpdateSaveDirectoryState()
    {
        if (SaveDirectoryTextBox == null || SelectDirectoryButton == null)
        {
            return;
        }

        var directorySelectionEnabled = UseTemporaryDirectoryCheckBox?.IsChecked != true;
        SaveDirectoryTextBox.IsEnabled = directorySelectionEnabled;
        SelectDirectoryButton.IsEnabled = directorySelectionEnabled;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // Temporary directories can be cleaned up later by the operating system.
        }
    }

    private void ShowInputWarning(string resourceKey, System.Windows.Controls.Control control)
    {
        MessageBox.Show(MainWindow.GetLocalizedString(resourceKey), Title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        control.Focus();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isDownloading)
        {
            return;
        }

        e.Cancel = true;
        StatusTextBlock.Text = MainWindow.GetLocalizedString("MajdataNetCanceling");
        _downloadCancellationTokenSource?.Cancel();
    }
}
