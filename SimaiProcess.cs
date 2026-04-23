using MajSimai;
using System.IO;
using System.Text;
using System.Windows;

namespace MajdataEdit;

internal static class SimaiProcess
{
    public static string? title;
    public static string? artist;
    public static string? designer;
    public static string? other_commands;
    public static float first;
    public static string[] fumens = new string[7];
    public static string[] levels = new string[7];

    /// <summary>
    ///     the timing points that contains notedata
    /// </summary>
    public static List<SimaiTimingPoint> notelist = new();

    /// <summary>
    ///     the timing points made by "," in maidata
    /// </summary>
    public static List<SimaiTimingPoint> timinglist = new();

    /// <summary>
    ///     Reset all the data in the static class.
    /// </summary>
    public static void ClearData()
    {
        title = "";
        artist = "";
        designer = "";
        first = 0;
        fumens = new string[7];
        levels = new string[7];
        notelist = new List<SimaiTimingPoint>();
        timinglist = new List<SimaiTimingPoint>();
    }

    /// <summary>
    ///     Read the maidata.txt into the static class, including the variables. Show up a messageBox when enconter any
    ///     exception.
    /// </summary>
    /// <param name="filename">file path of maidata.txt</param>
    /// <returns>if the read process faced any error</returns>
    public static bool ReadData(string filename)
    {
        var i = 0;
        other_commands = "";
        try
        {
            var maidataTxt = File.ReadAllLines(filename, Encoding.UTF8);
            for (i = 0; i < maidataTxt.Length; i++)
                if (maidataTxt[i].StartsWith("&title="))
                    title = GetValue(maidataTxt[i]);
                else if (maidataTxt[i].StartsWith("&artist="))
                    artist = GetValue(maidataTxt[i]);
                else if (maidataTxt[i].StartsWith("&des="))
                    designer = GetValue(maidataTxt[i]);
                else if (maidataTxt[i].StartsWith("&first="))
                    first = float.Parse(GetValue(maidataTxt[i]));
                else if (maidataTxt[i].StartsWith("&lv_") || maidataTxt[i].StartsWith("&inote_"))
                    for (var j = 1; j < 8 && i < maidataTxt.Length; j++)
                    {
                        if (maidataTxt[i].StartsWith("&lv_" + j + "="))
                            levels[j - 1] = GetValue(maidataTxt[i]);
                        if (maidataTxt[i].StartsWith("&inote_" + j + "="))
                        {
                            var TheNote = "";
                            TheNote += GetValue(maidataTxt[i]) + "\n";
                            i++;
                            for (; i < maidataTxt.Length; i++)
                            {
                                if (i < maidataTxt.Length)
                                    if (maidataTxt[i].StartsWith("&"))
                                        break;
                                TheNote += maidataTxt[i] + "\n";
                            }

                            fumens[j - 1] = TheNote;
                        }
                    }
                else
                    other_commands += maidataTxt[i].Trim() + "\n";

            other_commands = other_commands.Trim();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show("在maidata.txt第" + (i + 1) + "行:\n" + e.Message, "读取谱面时出现错误");
            return false;
        }
    }

    /// <summary>
    ///     Save the static data to maidata.txt
    /// </summary>
    /// <param name="filename">file path of maidata.txt</param>
    public static void SaveData(string filename)
    {
        var maidata = new List<string>
        {
            "&title=" + title,
            "&artist=" + artist,
            "&first=" + first,
            "&des=" + designer,
            other_commands!
        };
        for (var i = 0; i < levels.Length; i++)
            if (levels[i] != null && levels[i] != "")
                maidata.Add("&lv_" + (i + 1) + "=" + levels[i].Trim());
        for (var i = 0; i < fumens.Length; i++)
            if (fumens[i] != null && fumens[i] != "")
                maidata.Add("&inote_" + (i + 1) + "=" + fumens[i].Trim());
        File.WriteAllLines(filename, maidata.ToArray());
    }

    private static string GetValue(string varline)
    {
        return varline.Substring(varline.IndexOf("=") + 1);
    }

    /// <summary>
    ///     This method serialize the fumen data and load it into the static class.
    /// </summary>
    /// <param name="text">fumen text</param>
    /// <param name="position">the position of the cusor, to get the return time</param>
    /// <returns>the song time at the position</returns>
    public static double Serialize(string text, long position = 0)
    {
        var _notelist = new List<SimaiTimingPoint>();
        var _timinglist = new List<SimaiTimingPoint>();
        try
        {
            var chart = SimaiParser.ParseChart(text, position, out var requestTime);

            notelist = chart.NoteTimings.ToArray().ToList();
            timinglist = chart.CommaTimings.ToArray().ToList();

            return requestTime;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            return 0;
        }
    }

    public static void ClearNoteListPlayedState()
    {
        notelist.Sort((x, y) => x.Timing.CompareTo(y.Timing));
    }

    private static bool isNote(char noteText)
    {
        var SlideMarks = "1234567890ABCDE"; ///ABCDE for touch
        foreach (var mark in SlideMarks)
            if (noteText == mark)
                return true;
        return false;
    }

    public static string GetDifficultyText(int index)
    {
        if (index == 0) return "EASY";
        if (index == 1) return "BASIC";
        if (index == 2) return "ADVANCED";
        if (index == 3) return "EXPERT";
        if (index == 4) return "MASTER";
        if (index == 5) return "Re:MASTER";
        if (index == 6) return "ORIGINAL";
        return "DEFAULT";
    }
}