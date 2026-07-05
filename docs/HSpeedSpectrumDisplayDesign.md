# HSpeed Spectrum Display Design

## Context

- Existing spectrum drawing is in `MainWindowCore.DrawWave()`.
- `editorSetting.DrawHSpeedChanges` controls whether HSpeed changes are drawn.
- `editorSetting.DrawEmptyHSpeedChanges` controls whether empty `RawContent` HSpeed interpolation points are included.
- Current HSpeed display draws each detected HSpeed change as an orange triangle near the bottom with a text label.
- Interpolated HSpeed syntax can generate many empty-content timing points in a short time span, so individual labels can overlap heavily.

## Goal

Improve HSpeed-related rendering in the spectrum view:

- Treat dense, short-time HSpeed changes in the same soflan group as one visual sequence.
- Draw grouped sequence points on a dashed line.
- Show the speed at the left and right ends of the line.
- If the sequence has an internal maximum or minimum speed, label those extrema.
- Use distinct vivid colors for different soflan groups.

## Confirmed Decisions

- Same-group HSpeed points are grouped by horizontal screen distance, not by absolute time.
- Default grouping threshold is `18px`.
- The grouping threshold must be user-configurable.
- Add an editor setting named `HSpeedDisplayGroupDistancePx`.
- Expose it near the existing HSpeed spectrum display settings.
- Default value is `18`; saved value is clamped to `1..200`.
- In grouped HSpeed lines, the highest speed point is placed at one third of the full spectrum height.
- In grouped HSpeed lines, the lowest speed point is placed at `height - 8`.
- If all speeds in a grouped line are equal, draw the line at `height * 2 / 3`.
- Isolated HSpeed changes use the same new visual language:
  - draw a short dashed horizontal segment extending `6px` left and right,
  - draw a solid point at the change,
  - draw the speed label beside the point,
  - use the soflan group's color instead of the old global orange triangle.
- Grouping is performed independently per soflan group.
- Interleaved events from other soflan groups do not break a group's sequence.
- Overlapping different-group lines use small vertical lane offsets by group order:
  `0, -5, +5, -10, +10, ...`.
- Offset Y values are clamped to `[height / 3, height - 8]`.
- Soflan group colors are mapped stably by group id, not by viewport appearance order.
- Initial vivid palette:
  `Orange`, `DeepSkyBlue`, `Lime`, `Magenta`, `Gold`, `Cyan`, `HotPink`, `SpringGreen`, `Tomato`, `MediumOrchid`.
- Palette overflow wraps by modulo.
- Label text keeps the existing group-id rule:
  - if only group `0` is present, show speed as `1.5x`;
  - if any non-zero group is present, show `[g]1.5x`.
- Endpoint labels are always drawn for each grouped sequence.
- Extra extrema labels only apply to internal points.
- If the sequence maximum or minimum is at an endpoint, do not draw a duplicate extrema label.
- Internal maximum/minimum labels are drawn only for strict extrema.
- Label placement and collision rules:
  - left endpoint label is placed to the left of the line and right-aligned;
  - right endpoint label is placed to the right of the line and left-aligned;
  - internal maximum label is placed above its point;
  - internal minimum label is placed below its point;
  - endpoint labels have priority over internal extrema labels;
  - hide an internal extrema label if it overlaps an endpoint label;
  - if internal maximum and minimum labels overlap, keep the one with larger speed delta; on tie, keep the maximum label.
- Draw every displayed HSpeed change as a solid point on grouped lines.
- Point radius is `2px`.
- Dashed lines connect the displayed points to show the sequence.
- If a grouped sequence crosses the visible viewport boundary, draw and label only visible points.
- Endpoint labels refer to the first and last visible points, not off-screen true sequence endpoints.
- Eligible display points keep the existing HSpeed-change detection semantics:
  - only timing points whose HSpeed differs from the previous value in the same soflan group are displayed;
  - `DrawHSpeedChanges = false` disables all HSpeed display;
  - `DrawEmptyHSpeedChanges = false` excludes empty-`RawContent` interpolation samples;
  - non-empty HSpeed change points remain eligible.
- HSpeed-to-Y normalization is calculated per grouped line, not across the whole viewport.
- Grouping uses adjacent-point chaining:
  - sort eligible points by time within each soflan group;
  - append a point to the current group when its X distance from the previous point is `<= HSpeedDisplayGroupDistancePx`;
  - otherwise start a new group.
- Draw a dashed grouped line when a group has at least `2` points.
- Use the isolated-point style only when a group has exactly `1` point.
- HSpeed line and point styling must be configurable, not hardcoded.
- Expose these configurable HSpeed display style values:
  - `HSpeedDisplayGroupDistancePx`, default `18`;
  - `HSpeedDisplayLineWidthPx`, default `2`;
  - `HSpeedDisplayPointRadiusPx`, default `2`;
  - `HSpeedDisplayLabelFontSize`, default `9`.
- Keep dash pattern, palette editing, point outline color, and label offsets as internal defaults for now.
- Clamp ranges:
  - `HSpeedDisplayGroupDistancePx`: `1..200`;
  - `HSpeedDisplayLineWidthPx`: `1..8`;
  - `HSpeedDisplayPointRadiusPx`: `1..8`;
  - `HSpeedDisplayLabelFontSize`: `6..18`.
- Editor settings UI places the 4 new display style inputs near existing HSpeed options, before `HSpeedInterpolationGrid`:
  1. `HSpeedDisplayGroupDistancePx`
  2. `HSpeedDisplayLineWidthPx`
  3. `HSpeedDisplayPointRadiusPx`
  4. `HSpeedDisplayLabelFontSize`
  5. `HSpeedInterpolationGrid`
- Drawing order:
  - HSpeed dashed lines, points, and labels are drawn after notes;
  - HSpeed display is drawn before play start and ghost cursor markers;
  - existing BPM label drawing order is not changed.
- HSpeed labels are clamped into the visible drawing area after their initial position is calculated.
- Highest and lowest HSpeed are determined by numeric value, not absolute magnitude.
- Negative HSpeed values do not receive special visual styling.
- Negative values are shown directly in labels, such as `-1x`; color continues to represent soflan group.
- Cross-group label collision handling:
  - keep a list of already drawn label rectangles;
  - if a new label overlaps an existing label, try shifting it one label height upward;
  - if it still overlaps, try shifting it one label height downward;
  - if it still overlaps, skip the new label;
  - label priority is endpoint labels, then internal extrema labels, then isolated-point labels.
- HSpeed labels must not draw background rectangles.
- Label readability relies on vivid group colors, placement, clamping, and collision avoidance.
- HSpeed labels may use a light text shadow, but no background block:
  draw semi-transparent black text at `(x + 1, y + 1)`, then draw the group-colored text at `(x, y)`.
- Opacity:
  - dashed lines use alpha `220`;
  - points use alpha `255`;
  - labels use alpha `255`;
  - text shadows use alpha `180`;
  - point outlines use alpha `180` black.
- Performance/resource handling:
  - create a small set of `Pen`, `Brush`, and `Font` objects per `DrawWave()` call and dispose them with `using`;
  - do not allocate a new `Font` per point or per label;
  - keep HSpeed event/group lists local, but avoid unnecessary LINQ in hot drawing paths.
- HSpeed label numeric format remains `0.###` with `InvariantCulture`, followed by `x`.
- The decision to include `[g]` in labels is based on visible HSpeed change events in the current viewport.
- If the current viewport has no non-zero soflan group HSpeed changes, group `0` labels omit `[0]`.
- Do not draw carried-in HSpeed state at the viewport boundary.
- Draw only real HSpeed change timing points that are visible in the current viewport.
- Implementation scope is limited to spectrum display and editor settings:
  - update HSpeed rendering in `MainWindowCore.DrawWave()` and helper methods if needed;
  - add 4 display configuration fields to `Majson`;
  - add 4 editor setting inputs to `EditorSettingPanel`;
  - keep parser, MA2 export, and SyntaxChecker unchanged.
- New editor setting labels:
  - `HSpeedDisplayGroupDistancePx`: `HSpeed display group distance` / `变速显示聚合距离`;
  - `HSpeedDisplayLineWidthPx`: `HSpeed line width` / `变速线宽`;
  - `HSpeedDisplayPointRadiusPx`: `HSpeed point radius` / `变速点半径`;
  - `HSpeedDisplayLabelFontSize`: `HSpeed label font size` / `变速标签字号`.
- `DrawEmptyHSpeedChanges` filtering is applied before grouping.
- After filtering, remaining eligible points still use the same grouping algorithm.
- Repeated internal extrema are labeled at most once per extrema type.
- If multiple internal points share the same maximum or minimum value, label the one closest to the line's midpoint.
- Two-point grouped lines draw only endpoint labels.
- Two-point grouped lines do not draw extra maximum/minimum labels.
- Isolated-point labels are placed to the right of the point by default and left-aligned.
- If clamping would make the isolated label overlap its point heavily, try placing it to the left.
- Isolated labels still follow global label collision handling.
- Remove the old orange triangle HSpeed marker entirely.
- All HSpeed display uses the new dashed-line, point, and label system.

## Open Questions

None.
