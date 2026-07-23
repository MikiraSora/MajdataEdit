using MajSimai;

namespace MajdataEdit;

internal static class ForceYellowSlideSegmentHelper
{
    public static bool[] ResolveFlags(SimaiNote note, int segmentCount)
    {
        if (segmentCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(segmentCount));
        }

        var result = new bool[segmentCount];
        var indices = note.ForceYellowSlideSegmentIndices ?? Array.Empty<int>();
        var previous = -1;
        foreach (var index in indices)
        {
            if (index <= previous || index < 0 || index >= segmentCount)
            {
                throw new InvalidOperationException("Force Yellow slide segment index is invalid");
            }

            result[index] = true;
            previous = index;
        }

        return result;
    }
}
