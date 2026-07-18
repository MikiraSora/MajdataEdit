using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MajdataEdit.AcbAudioExport;
using MajdataEdit.Export;
using MajdataEdit.Ma2Export;

namespace MajdataEdit;

public partial class AcbAudioExportWindow : Window
{
    private readonly string _chartDirectory;
    private ExportSettings _settings = new();
    private bool _isExporting;

    public AcbAudioExportWindow(string chartDirectory)
    {
        _chartDirectory = chartDirectory;
        InitializeComponent();
        SourceAudioTextBox.Text = Path.Combine(chartDirectory, "track.mp3");
        DataObject.AddPastingHandler(BaseMusicIdTextBox, NumericTextBox_OnPaste);
        DataObject.AddPastingHandler(PreviewStartTextBox, NumericTextBox_OnPaste);
        DataObject.AddPastingHandler(PreviewEndTextBox, NumericTextBox_OnPaste);
        LoadExportSettings();
        UpdateFinalMusicIdPreview();
    }

    private void SelectOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(OutputDirectoryTextBox.Text)
            ? OutputDirectoryTextBox.Text
            : _chartDirectory;
        var selectedDirectory = FolderPicker.SelectFolder(this, "选择 ACB/AWB 导出目录", initialDirectory);
        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            OutputDirectoryTextBox.Text = selectedDirectory;
        }
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => character is < '0' or > '9');
    }

    private void NumericTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox textBox || !e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        var resultingLength = textBox.Text.Length - textBox.SelectionLength + text.Length;
        if (text.Any(character => character is < '0' or > '9') || resultingLength > textBox.MaxLength)
        {
            e.CancelCommand();
        }
    }

    private void MusicIdOption_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFinalMusicIdPreview();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isExporting)
        {
            return;
        }

        var baseMusicId = BaseMusicIdTextBox.Text.Trim();
        string finalMusicId;
        try
        {
            finalMusicId = ExportMusicId.BuildFinalMusicId(
                baseMusicId,
                IsUtageCheckBox.IsChecked == true,
                IsDxCheckBox.IsChecked == true);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            BaseMusicIdTextBox.Focus();
            BaseMusicIdTextBox.SelectAll();
            return;
        }

        if (!TryGetPreviewMilliseconds(out var previewStartMilliseconds, out var previewEndMilliseconds))
        {
            return;
        }

        try
        {
            _settings.BaseMusicId = baseMusicId;
            _settings.IsUtage = IsUtageCheckBox.IsChecked == true;
            _settings.IsDx = IsDxCheckBox.IsChecked == true;
            _settings.AudioPreviewStartMilliseconds = previewStartMilliseconds;
            _settings.AudioPreviewEndMilliseconds = previewEndMilliseconds;
            ExportSettingsStore.Save(_chartDirectory, _settings);
        }
        catch (Exception exception)
        {
            MessageBox.Show("无法更新 export.json：\n" + exception.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var outputDirectory = OutputDirectoryTextBox.Text;
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            MessageBox.Show("请先通过“导出目录”按钮选择有效的目录。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sourceAudioPath = SourceAudioTextBox.Text;
        if (!File.Exists(sourceAudioPath))
        {
            MessageBox.Show("当前谱面目录中没有 track.mp3：\n" + sourceAudioPath, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var outputPrefix = "music" + finalMusicId;
        var acbPath = Path.Combine(outputDirectory, outputPrefix + ".acb");
        var awbPath = Path.Combine(outputDirectory, outputPrefix + ".awb");
        if (File.Exists(acbPath) || File.Exists(awbPath))
        {
            var overwrite = MessageBox.Show(
                "目标 ACB/AWB 文件已存在，是否覆盖？\n" + acbPath + "\n" + awbPath,
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (overwrite != MessageBoxResult.Yes)
            {
                return;
            }
        }

        SetExportingState(true);
        try
        {
            var progress = new Progress<string>(message => StatusTextBlock.Text = message);
            await AcbAudioExporter.ExportAsync(
                sourceAudioPath,
                outputDirectory,
                finalMusicId,
                previewStartMilliseconds,
                previewEndMilliseconds,
                progress);
            MessageBox.Show("ACB/AWB 音频生成完成：\n" + acbPath + "\n" + awbPath, Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            SetExportingState(false);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show("ACB/AWB 音频生成失败：\n" + exception.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_isExporting)
            {
                SetExportingState(false);
            }
        }
    }

    private bool TryGetPreviewMilliseconds(out int previewStartMilliseconds, out int previewEndMilliseconds)
    {
        if (!int.TryParse(PreviewStartTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                out previewStartMilliseconds))
        {
            MessageBox.Show("音频预览起始位置必须是非负整数毫秒。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PreviewStartTextBox.Focus();
            PreviewStartTextBox.SelectAll();
            previewEndMilliseconds = 0;
            return false;
        }

        if (!int.TryParse(PreviewEndTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture,
                out previewEndMilliseconds) || previewEndMilliseconds <= previewStartMilliseconds)
        {
            MessageBox.Show("音频预览中止位置必须是整数毫秒，且必须大于起始位置。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PreviewEndTextBox.Focus();
            PreviewEndTextBox.SelectAll();
            return false;
        }

        return true;
    }

    private void LoadExportSettings()
    {
        try
        {
            _settings = ExportSettingsStore.Load(_chartDirectory);
            BaseMusicIdTextBox.Text = _settings.BaseMusicId;
            IsUtageCheckBox.IsChecked = _settings.IsUtage;
            IsDxCheckBox.IsChecked = _settings.IsDx;
            PreviewStartTextBox.Text = _settings.AudioPreviewStartMilliseconds.ToString(CultureInfo.InvariantCulture);
            PreviewEndTextBox.Text = _settings.AudioPreviewEndMilliseconds.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "读取 export.json 失败：" + exception.Message;
            PreviewStartTextBox.Text = _settings.AudioPreviewStartMilliseconds.ToString(CultureInfo.InvariantCulture);
            PreviewEndTextBox.Text = _settings.AudioPreviewEndMilliseconds.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void UpdateFinalMusicIdPreview()
    {
        if (FinalMusicIdTextBox == null || BaseMusicIdTextBox == null)
        {
            return;
        }

        var prefix = $"{(IsUtageCheckBox?.IsChecked == true ? '1' : '0')}" +
                     $"{(IsDxCheckBox?.IsChecked == true ? '1' : '0')}";
        FinalMusicIdTextBox.Text = prefix + BaseMusicIdTextBox.Text.PadRight(4, '_');
    }

    private void SetExportingState(bool exporting)
    {
        _isExporting = exporting;
        InputPanel.IsEnabled = !exporting;
        ConfirmButton.IsEnabled = !exporting;
        CancelButton.IsEnabled = !exporting;
        Cursor = exporting ? Cursors.Wait : null;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExporting)
        {
            e.Cancel = true;
        }
    }
}
