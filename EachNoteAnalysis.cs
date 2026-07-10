using MajSimai;

namespace MajdataEdit;

internal sealed class EachNoteAnalysis
{
    private const double TimeTolerance = 1e-9;

    private readonly HashSet<SimaiNote> eachNotes;

    private EachNoteAnalysis(HashSet<SimaiNote> eachNotes, int groupCount)
    {
        this.eachNotes = eachNotes;
        GroupCount = groupCount;
    }

    public static EachNoteAnalysis Empty { get; } = new(new HashSet<SimaiNote>(), 0);

    public int GroupCount { get; }

    public bool Contains(SimaiNote note)
    {
        return eachNotes.Contains(note);
    }

    public static EachNoteAnalysis Analyze(IEnumerable<SimaiTimingPoint> timingPoints)
    {
        var eachNotes = new HashSet<SimaiNote>();
        var currentGroupNotes = new List<SimaiNote>();
        var hasCurrentGroup = false;
        var currentGroupTime = 0d;
        var groupCount = 0;

        foreach (var timingPoint in timingPoints
                     .Where(timingPoint => timingPoint.Notes.Length != 0)
                     .OrderBy(timingPoint => timingPoint.Timing))
        {
            if (!hasCurrentGroup || Math.Abs(timingPoint.Timing - currentGroupTime) > TimeTolerance)
            {
                CompleteCurrentGroup();
                currentGroupNotes.Clear();
                currentGroupTime = timingPoint.Timing;
                hasCurrentGroup = true;
            }

            foreach (var note in timingPoint.Notes)
            {
                if (IsCandidate(note))
                {
                    currentGroupNotes.Add(note);
                }
            }
        }

        CompleteCurrentGroup();
        return new EachNoteAnalysis(eachNotes, groupCount);

        void CompleteCurrentGroup()
        {
            if (currentGroupNotes.Count <= 1)
            {
                return;
            }

            eachNotes.UnionWith(currentGroupNotes);
            groupCount++;
        }
    }

    private static bool IsCandidate(SimaiNote note)
    {
        if (note.IsMine || note.IsMineSlide || note.IsSlideNoHead)
        {
            return false;
        }

        return note.Type is SimaiNoteType.Tap
            or SimaiNoteType.Hold
            or SimaiNoteType.Touch
            or SimaiNoteType.TouchHold
            or SimaiNoteType.Slide;
    }
}
