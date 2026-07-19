using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MajdataEdit.CoverVideoExport;
using MajdataEdit.Export;
using MajdataEdit.Ma2Export;
using MajdataEdit.MusicXmlExport;
using Microsoft.Win32;

namespace MajdataEdit;

public partial class MusicXmlExportWindow : Window
{
    private static readonly IReadOnlyList<MusicXmlGenreOption> DefaultGenreOptions =
        new[]
        {
            new MusicXmlGenreOption(101, "POPSアニメ"),
            new MusicXmlGenreOption(102, "niconicoボーカロイド"),
            new MusicXmlGenreOption(103, "東方Project"),
            new MusicXmlGenreOption(104, "ゲームバラエティ"),
            new MusicXmlGenreOption(105, "maimai"),
            new MusicXmlGenreOption(106, "オンゲキCHUNITHM"),
            new MusicXmlGenreOption(107, "宴会場")
        };

    private static readonly string[] SlotNames =
        { "BAS_00", "ADV_01", "EXP_02", "MAS_03", "REMAS_04", "UTAGE_05" };

    private readonly string _chartDirectory;
    private readonly IReadOnlyList<MusicXmlInoteOption> _inoteOptions;
    private ExportSettings _settings = new();
    private bool _isExporting;

    public MusicXmlExportWindow(string chartDirectory)
    {
        _chartDirectory = chartDirectory;
        _inoteOptions = BuildInoteOptions();
        GenreOptions = new ObservableCollection<MusicXmlGenreOption>(DefaultGenreOptions);
        DifficultyRows = new ObservableCollection<MusicXmlDifficultyRow>();
        InitializeComponent();
        DataContext = this;
        DataObject.AddPastingHandler(BaseMusicIdTextBox, NumericTextBox_OnPaste);
        DataObject.AddPastingHandler(BpmTextBox, NumericTextBox_OnPaste);
        LoadExportSettings();
        UpdateFinalMusicIdPreview();
    }

    public ObservableCollection<MusicXmlGenreOption> GenreOptions { get; }

    public ObservableCollection<MusicXmlDifficultyRow> DifficultyRows { get; }

    private void SelectOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Directory.Exists(OutputDirectoryTextBox.Text)
            ? OutputDirectoryTextBox.Text
            : _chartDirectory;
        var selectedDirectory = FolderPicker.SelectFolder(this, "选择 Music.xml 导出目录", initialDirectory);
        if (!string.IsNullOrWhiteSpace(selectedDirectory))
        {
            OutputDirectoryTextBox.Text = selectedDirectory;
        }
    }

    private void ImportTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入基础 Music.xml 模板",
            Filter = "Music.xml|Music.xml|XML 文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
            FileName = "Music.xml",
            CheckFileExists = true,
            Multiselect = false
        };
        var currentTemplateDirectory = Path.GetDirectoryName(TemplatePathTextBox.Text);
        dialog.InitialDirectory = Directory.Exists(currentTemplateDirectory)
            ? currentTemplateDirectory
            : _chartDirectory;
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!string.Equals(Path.GetFileName(dialog.FileName), "Music.xml", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("请选择名为 Music.xml 的模板文件。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TemplatePathTextBox.Text = dialog.FileName;
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
        UpdateDifficultyRowVisibility();
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
            ShowInputWarning(exception.Message, BaseMusicIdTextBox);
            return;
        }

        if (!TryBuildMusicXmlSettings(out var musicXmlSettings, out var bpm, out var genre))
        {
            return;
        }

        try
        {
            _settings.BaseMusicId = baseMusicId;
            _settings.IsUtage = IsUtageCheckBox.IsChecked == true;
            _settings.IsDx = IsDxCheckBox.IsChecked == true;
            _settings.MusicXml = musicXmlSettings;
            ExportSettingsStore.Save(_chartDirectory, _settings);
        }
        catch (Exception exception)
        {
            MessageBox.Show("无法更新 export.json：\n" + exception.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var isUtage = IsUtageCheckBox.IsChecked == true;
        var difficultyRequests = GetActiveDifficultyRows().Select(row =>
        {
            var inote = _inoteOptions.First(option => option.Index == row.InoteIndex);
            return new MusicXmlDifficultyExport
            {
                SlotIndex = isUtage ? 0 : row.SlotIndex,
                IsEnabled = row.IsExportEnabled,
                Level = row.Level.Trim(),
                Designer = row.Designer,
                ChartContent = inote.ChartContent
            };
        }).ToArray();
        var request = new MusicXmlExportRequest
        {
            OutputDirectory = musicXmlSettings.OutputDirectory,
            TemplatePath = musicXmlSettings.TemplatePath,
            BaseMusicId = baseMusicId,
            FinalMusicId = finalMusicId,
            MusicName = musicXmlSettings.MusicName ?? string.Empty,
            ArtistName = musicXmlSettings.ArtistName ?? string.Empty,
            GenreId = genre.Id,
            GenreName = genre.Name,
            Bpm = bpm,
            LongMusic = musicXmlSettings.LongMusic,
            UtageKanjiName = musicXmlSettings.UtageKanjiName,
            Comment = musicXmlSettings.Comment,
            HSpeedInterpolationGrid = SimaiProcess.HSpeedInterpolationGrid,
            Difficulties = difficultyRequests
        };

        var targetDirectory = Path.Combine(
            request.OutputDirectory,
            "music",
            "music" + request.FinalMusicId);
        var targetPaths = new List<string> { Path.Combine(targetDirectory, "Music.xml") };
        targetPaths.AddRange(difficultyRequests
            .Where(difficulty => difficulty.IsEnabled)
            .Select(difficulty => Path.Combine(
                targetDirectory,
                $"{request.FinalMusicId}_{difficulty.SlotIndex:00}.ma2")));
        targetPaths.AddRange(MusicXmlResourceExporter.GetOutputPaths(
            request.OutputDirectory,
            baseMusicId,
            musicXmlSettings.GenerateCoverVideo,
            musicXmlSettings.GenerateAcbAwb,
            musicXmlSettings.GenerateJacketAb));
        var existingPaths = targetPaths.Where(File.Exists).ToArray();
        if (existingPaths.Length > 0)
        {
            var overwrite = MessageBox.Show(
                "以下目标文件已存在，是否覆盖？\n" + string.Join("\n", existingPaths),
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
            var result = await MusicXmlExporter.ExportAsync(request, progress);
            var resourcePaths = await MusicXmlResourceExporter.ExportAsync(
                _chartDirectory,
                request.OutputDirectory,
                baseMusicId,
                _settings.AudioPreviewStartMilliseconds,
                _settings.AudioPreviewEndMilliseconds,
                musicXmlSettings.GenerateCoverVideo,
                musicXmlSettings.GenerateAcbAwb,
                musicXmlSettings.GenerateJacketAb,
                progress);
            var resourceSummary = resourcePaths.Count == 0
                ? string.Empty
                : $"\n附加资源：{resourcePaths.Count} 个";
            MessageBox.Show(
                $"Music.xml 生成完成，共生成 {result.Ma2Paths.Count} 个 MA2：\n{result.TargetDirectory}{resourceSummary}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            SetExportingState(false);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show("Music.xml 或附加资源生成失败：\n" + exception.Message, Title,
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

    private bool TryBuildMusicXmlSettings(
        out MusicXmlExportSettings settings,
        out int bpm,
        out MusicXmlGenreOption genre)
    {
        settings = new MusicXmlExportSettings();
        bpm = 0;
        genre = GenreComboBox.SelectedItem as MusicXmlGenreOption ?? GenreOptions[0];

        if (string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text) ||
            !Directory.Exists(OutputDirectoryTextBox.Text))
        {
            ShowInputWarning("请先通过“导出目录”按钮选择有效的目录。", OutputDirectoryTextBox);
            return false;
        }

        if (string.IsNullOrWhiteSpace(TemplatePathTextBox.Text) || !File.Exists(TemplatePathTextBox.Text))
        {
            ShowInputWarning("请先导入一个存在的 Music.xml 模板。", TemplatePathTextBox);
            return false;
        }

        if (!string.Equals(Path.GetFileName(TemplatePathTextBox.Text), "Music.xml",
                StringComparison.OrdinalIgnoreCase))
        {
            ShowInputWarning("基础模板必须是名为 Music.xml 的文件。", TemplatePathTextBox);
            return false;
        }

        if (string.IsNullOrWhiteSpace(MusicNameTextBox.Text))
        {
            ShowInputWarning("歌曲名不能为空。", MusicNameTextBox);
            return false;
        }

        if (string.IsNullOrWhiteSpace(ArtistNameTextBox.Text))
        {
            ShowInputWarning("艺术家名不能为空。", ArtistNameTextBox);
            return false;
        }

        if (GenreComboBox.SelectedItem is not MusicXmlGenreOption selectedGenre)
        {
            ShowInputWarning("请选择歌曲分类。", GenreComboBox);
            return false;
        }

        genre = selectedGenre;
        if (!int.TryParse(BpmTextBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out bpm) || bpm <= 0)
        {
            ShowInputWarning("BPM 必须是正整数。", BpmTextBox);
            return false;
        }

        var generateCoverVideo = GenerateCoverVideoCheckBox.IsChecked == true;
        var generateAcbAwb = GenerateAcbAwbCheckBox.IsChecked == true;
        var generateJacketAb = GenerateJacketAbCheckBox.IsChecked == true;
        if (generateCoverVideo && CoverVideoExporter.FindSourcePath(_chartDirectory) == null)
        {
            ShowInputWarning("已勾选视频生成，但当前谱面目录中既没有 pv.mp4，也没有 bg.jpg。", OutputDirectoryTextBox);
            return false;
        }

        if (generateJacketAb && !File.Exists(Path.Combine(_chartDirectory, "bg.jpg")))
        {
            ShowInputWarning("已勾选封面 AB 生成，但当前谱面目录中没有 bg.jpg。", OutputDirectoryTextBox);
            return false;
        }

        if (generateAcbAwb && !File.Exists(Path.Combine(_chartDirectory, "track.mp3")))
        {
            ShowInputWarning("已勾选音频生成，但当前谱面目录中没有 track.mp3。", OutputDirectoryTextBox);
            return false;
        }

        foreach (var row in GetActiveDifficultyRows().Where(row => row.IsExportEnabled))
        {
            var inote = _inoteOptions.First(option => option.Index == row.InoteIndex);
            if (string.IsNullOrWhiteSpace(inote.ChartContent))
            {
                MessageBox.Show($"{row.SlotName} 选择的 {inote.DisplayName} 没有谱面内容。", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(row.Level))
            {
                MessageBox.Show($"请填写 {row.SlotName} 的难度定数。", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }


            try
            {
                MusicXmlExporter.ValidateLevelConstant(row.Level);
            }
            catch (ArgumentException exception)
            {
                MessageBox.Show($"{row.SlotName}：{exception.Message}", Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        settings = new MusicXmlExportSettings
        {
            OutputDirectory = OutputDirectoryTextBox.Text,
            TemplatePath = TemplatePathTextBox.Text,
            MusicName = MusicNameTextBox.Text,
            ArtistName = ArtistNameTextBox.Text,
            GenreId = genre.Id,
            GenreName = genre.Name,
            Bpm = BpmTextBox.Text.Trim(),
            LongMusic = LongMusicCheckBox.IsChecked == true,
            UtageKanjiName = UtageKanjiNameTextBox.Text,
            Comment = CommentTextBox.Text,
            GenerateCoverVideo = generateCoverVideo,
            GenerateAcbAwb = generateAcbAwb,
            GenerateJacketAb = generateJacketAb,
            Difficulties = DifficultyRows.Select(row => new MusicXmlDifficultySettings
            {
                SlotIndex = row.SlotIndex,
                IsEnabled = row.IsExportEnabled,
                InoteIndex = row.InoteIndex,
                Level = row.Level,
                Designer = row.Designer
            }).ToList()
        };
        return true;
    }

    private void LoadExportSettings()
    {
        try
        {
            _settings = ExportSettingsStore.Load(_chartDirectory);
        }
        catch (Exception exception)
        {
            _settings = new ExportSettings();
            StatusTextBlock.Text = "读取 export.json 失败：" + exception.Message;
        }

        BaseMusicIdTextBox.Text = _settings.BaseMusicId;
        IsUtageCheckBox.IsChecked = _settings.IsUtage;
        IsDxCheckBox.IsChecked = _settings.IsDx;

        var musicXml = _settings.MusicXml;
        OutputDirectoryTextBox.Text = musicXml?.OutputDirectory ?? string.Empty;
        TemplatePathTextBox.Text = musicXml?.TemplatePath ?? string.Empty;
        MusicNameTextBox.Text = musicXml?.MusicName ?? SimaiProcess.title ?? string.Empty;
        ArtistNameTextBox.Text = musicXml?.ArtistName ?? SimaiProcess.artist ?? string.Empty;
        BpmTextBox.Text = musicXml?.Bpm ?? GetDefaultBpm();
        LongMusicCheckBox.IsChecked = musicXml?.LongMusic ?? false;
        UtageKanjiNameTextBox.Text = musicXml?.UtageKanjiName ?? string.Empty;
        CommentTextBox.Text = musicXml?.Comment ?? string.Empty;
        GenerateCoverVideoCheckBox.IsChecked = musicXml?.GenerateCoverVideo ?? false;
        GenerateAcbAwbCheckBox.IsChecked = musicXml?.GenerateAcbAwb ?? false;
        GenerateJacketAbCheckBox.IsChecked = musicXml?.GenerateJacketAb ?? false;

        var genreId = musicXml?.GenreId ?? 105;
        var genreName = musicXml?.GenreName ?? "maimai";
        var genre = GenreOptions.FirstOrDefault(option => option.Id == genreId && option.Name == genreName);
        if (genre == null)
        {
            genre = new MusicXmlGenreOption(genreId, genreName);
            GenreOptions.Add(genre);
        }

        GenreComboBox.SelectedItem = genre;
        BuildDifficultyRows(musicXml);
        UpdateDifficultyRowVisibility();
    }

    private void BuildDifficultyRows(MusicXmlExportSettings? musicXml)
    {
        DifficultyRows.Clear();
        var savedRows = musicXml?.Difficulties ?? new List<MusicXmlDifficultySettings>();
        for (var slotIndex = 0; slotIndex < SlotNames.Length; slotIndex++)
        {
            var saved = savedRows.FirstOrDefault(row => row.SlotIndex == slotIndex);
            var defaultInoteIndex = slotIndex + 2;
            var inoteIndex = saved?.InoteIndex is >= 1 and <= 7
                ? saved.InoteIndex
                : defaultInoteIndex;
            var inote = _inoteOptions.First(option => option.Index == inoteIndex);
            DifficultyRows.Add(new MusicXmlDifficultyRow(
                slotIndex,
                SlotNames[slotIndex],
                _inoteOptions,
                saved?.IsEnabled ?? !string.IsNullOrWhiteSpace(inote.ChartContent),
                inoteIndex,
                saved?.Level ?? NormalizeMaidataLevel(inote.Level),
                saved?.Designer ?? SimaiProcess.designer ?? string.Empty));
        }
    }

    private static IReadOnlyList<MusicXmlInoteOption> BuildInoteOptions()
    {
        return Enumerable.Range(1, 7)
            .Select(index =>
            {
                var chart = SimaiProcess.fumens[index - 1] ?? string.Empty;
                var level = SimaiProcess.levels[index - 1] ?? string.Empty;
                var emptyMark = string.IsNullOrWhiteSpace(chart) ? "（空）" : string.Empty;
                var levelText = string.IsNullOrWhiteSpace(level) ? string.Empty : $" Lv.{level}";
                return new MusicXmlInoteOption(
                    index,
                    $"inote_{index} {SimaiProcess.GetDifficultyText(index - 1)}{levelText}{emptyMark}",
                    level,
                    chart);
            })
            .ToArray();
    }

    private static string GetDefaultBpm()
    {
        var wholeBpm = Ma2ExportMetadata.GetWholeBpm(SimaiProcess.other_commands);
        if (wholeBpm is > 0)
        {
            return Math.Round(wholeBpm.Value, MidpointRounding.AwayFromZero)
                .ToString(CultureInfo.InvariantCulture);
        }

        foreach (var chart in SimaiProcess.fumens.Where(chart => !string.IsNullOrWhiteSpace(chart)))
        {
            var match = Regex.Match(
                chart,
                @"^\s*(?:\|\|[^\r\n]*(?:\r?\n|$)\s*)*\(\s*(?<bpm>\d+(?:\.\d+)?)\s*\)",
                RegexOptions.CultureInvariant);
            if (match.Success &&
                decimal.TryParse(match.Groups["bpm"].Value, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var bpm) && bpm > 0)
            {
                return decimal.Round(bpm, 0, MidpointRounding.AwayFromZero)
                    .ToString(CultureInfo.InvariantCulture);
            }
        }

        return string.Empty;
    }

    private static string NormalizeMaidataLevel(string value)
    {
        var trimmed = value.Trim();
        var isPlus = trimmed.EndsWith('+');
        if (isPlus)
        {
            trimmed = trimmed[..^1];
        }

        if (!decimal.TryParse(trimmed, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var level))
        {
            return string.Empty;
        }

        if (isPlus && level == decimal.Truncate(level))
        {
            level += 0.7m;
        }

        return level.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private IEnumerable<MusicXmlDifficultyRow> GetActiveDifficultyRows()
    {
        var isUtage = IsUtageCheckBox.IsChecked == true;
        return DifficultyRows.Where(row => isUtage ? row.SlotIndex == 5 : row.SlotIndex != 5);
    }

    private void UpdateDifficultyRowVisibility()
    {
        if (DifficultyRows == null)
        {
            return;
        }

        var isUtage = IsUtageCheckBox?.IsChecked == true;
        var utageVisibility = isUtage ? Visibility.Visible : Visibility.Collapsed;
        if (UtageKanjiNameLabel != null)
        {
            UtageKanjiNameLabel.Visibility = utageVisibility;
            UtageKanjiNameTextBox.Visibility = utageVisibility;
            UtageCommentLabel.Visibility = utageVisibility;
            CommentTextBox.Visibility = utageVisibility;
        }

        foreach (var row in DifficultyRows)
        {
            row.RowVisibility = (isUtage ? row.SlotIndex == 5 : row.SlotIndex != 5)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (row.SlotIndex == 5)
            {
                row.SlotName = isUtage ? "UTAGE_00" : "UTAGE_05";
            }
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

    private void ShowInputWarning(string message, Control control)
    {
        MessageBox.Show(message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        control.Focus();
        if (control is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void SetExportingState(bool exporting)
    {
        _isExporting = exporting;
        InputPanel.IsEnabled = !exporting;
        ResourceOptionsPanel.IsEnabled = !exporting;
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

public sealed class MusicXmlGenreOption
{
    public MusicXmlGenreOption(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public int Id { get; }

    public string Name { get; }

    public string DisplayName => $"{Id} - {Name}";

    public override string ToString() => DisplayName;
}

public sealed class MusicXmlInoteOption
{
    public MusicXmlInoteOption(int index, string displayName, string level, string chartContent)
    {
        Index = index;
        DisplayName = displayName;
        Level = level;
        ChartContent = chartContent;
    }

    public int Index { get; }

    public string DisplayName { get; }

    public string Level { get; }

    public string ChartContent { get; }

    public override string ToString() => DisplayName;
}

public sealed class MusicXmlDifficultyRow : INotifyPropertyChanged
{
    private bool _isExportEnabled;
    private int _inoteIndex;
    private string _level;
    private string _designer;
    private string _slotName;
    private Visibility _rowVisibility = Visibility.Visible;

    public MusicXmlDifficultyRow(
        int slotIndex,
        string slotName,
        IReadOnlyList<MusicXmlInoteOption> inoteOptions,
        bool isExportEnabled,
        int inoteIndex,
        string level,
        string designer)
    {
        SlotIndex = slotIndex;
        _slotName = slotName;
        InoteOptions = inoteOptions;
        _isExportEnabled = isExportEnabled;
        _inoteIndex = inoteIndex;
        _level = level;
        _designer = designer;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int SlotIndex { get; }

    public string SlotName
    {
        get => _slotName;
        set => SetField(ref _slotName, value);
    }

    public Visibility RowVisibility
    {
        get => _rowVisibility;
        set => SetField(ref _rowVisibility, value);
    }

    public IReadOnlyList<MusicXmlInoteOption> InoteOptions { get; }

    public bool IsExportEnabled
    {
        get => _isExportEnabled;
        set => SetField(ref _isExportEnabled, value);
    }

    public int InoteIndex
    {
        get => _inoteIndex;
        set => SetField(ref _inoteIndex, value);
    }

    public string Level
    {
        get => _level;
        set => SetField(ref _level, value);
    }

    public string Designer
    {
        get => _designer;
        set => SetField(ref _designer, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
