using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using MajdataEdit.Export;
using MajdataEdit.JacketAbExport;
using MajdataEdit.Ma2Export;

namespace MajdataEdit;

public partial class JacketAbExportWindow : Window
{
    private readonly string _chartDirectory;
    private ExportSettings _settings = new();
    private bool _isExporting;

    public JacketAbExportWindow(string chartDirectory)
    {
        _chartDirectory = chartDirectory;
        InitializeComponent();
        SourceImageTextBox.Text = Path.Combine(chartDirectory, "bg.jpg");
        DataObject.AddPastingHandler(BaseMusicIdTextBox, BaseMusicIdTextBox_OnPaste);
        LoadExportSettings();
        UpdateFinalMusicIdPreview();
    }

    private void SelectOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(OutputDirectoryTextBox.Text)
            ? OutputDirectoryTextBox.Text
            : _chartDirectory;
        var selectedDirectory = FolderPicker.SelectFolder(this, "选择封面 AB 导出目录", initialDirectory);
        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            OutputDirectoryTextBox.Text = selectedDirectory;
        }
    }

    private void BaseMusicIdTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => character is < '0' or > '9');
    }

    private void BaseMusicIdTextBox_OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        var resultingLength = BaseMusicIdTextBox.Text.Length - BaseMusicIdTextBox.SelectionLength + text.Length;
        if (text.Any(character => character is < '0' or > '9') || resultingLength > 4)
        {
            e.CancelCommand();
        }
    }

    private void BaseMusicIdTextBox_TextChanged(object sender, RoutedEventArgs e)
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
            finalMusicId = ExportMusicId.BuildFinalMusicId(baseMusicId, isUtage: false, isDx: false);
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            BaseMusicIdTextBox.Focus();
            BaseMusicIdTextBox.SelectAll();
            return;
        }

        try
        {
            _settings.BaseMusicId = baseMusicId;
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

        var sourceImagePath = SourceImageTextBox.Text;
        if (!File.Exists(sourceImagePath))
        {
            MessageBox.Show("当前谱面目录中没有 bg.jpg：\n" + sourceImagePath, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var normalPath = Path.Combine(outputDirectory, "jacket", $"ui_jacket_{finalMusicId}.ab");
        var smallPath = Path.Combine(outputDirectory, "jacket_s", $"ui_jacket_{finalMusicId}_s.ab");
        if (File.Exists(normalPath) || File.Exists(smallPath))
        {
            var overwrite = MessageBox.Show(
                "目标封面 AB 文件已存在，是否覆盖？\n" + normalPath + "\n" + smallPath,
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
            await JacketAbExporter.ExportAsync(
                sourceImagePath,
                outputDirectory,
                finalMusicId,
                progress);
            MessageBox.Show("封面 AB 文件生成完成：\n" + normalPath + "\n" + smallPath, Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            SetExportingState(false);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show("封面 AB 文件生成失败：\n" + exception.Message, Title,
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

    private void LoadExportSettings()
    {
        try
        {
            _settings = ExportSettingsStore.Load(_chartDirectory);
            BaseMusicIdTextBox.Text = _settings.BaseMusicId;
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = "读取 export.json 失败：" + exception.Message;
        }
    }

    private void UpdateFinalMusicIdPreview()
    {
        if (FinalMusicIdTextBox == null || BaseMusicIdTextBox == null)
        {
            return;
        }

        FinalMusicIdTextBox.Text = "00" + BaseMusicIdTextBox.Text.PadRight(4, '_');
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
