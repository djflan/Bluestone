using Bluestone.Application.Editing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Time;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// The arranger loop as the transport and ruler show it: whether it loops, its range (kept while it does not), exact
/// start and length, and the commands that change it. Every change is one undoable command.
/// </summary>
public sealed partial class ArrangerLoopViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    internal ArrangerLoopViewModel(MainViewModel owner) => _owner = owner;

    /// <summary>Whether playback loops over the range.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; private set; }

    /// <summary>Whether a range has been defined (it stays when looping is turned off).</summary>
    [ObservableProperty]
    public partial bool HasRange { get; private set; }

    /// <summary>The range's start as bar.beat.tick, or empty without a range.</summary>
    [ObservableProperty]
    public partial string StartText { get; private set; } = string.Empty;

    /// <summary>The range's length as bars.beats.ticks, or empty without a range.</summary>
    [ObservableProperty]
    public partial string LengthText { get; private set; } = string.Empty;

    /// <summary>What the transport's loop button says it will do.</summary>
    [ObservableProperty]
    public partial string ToggleTip { get; private set; } = string.Empty;

    private Project Project => _owner.Project;

    /// <summary>Turns looping on (creating four bars from the playhead's bar if there is no range yet) or off, keeping the range.</summary>
    [RelayCommand]
    private void Toggle() => _owner.Execute(LoopCommands.Toggle(Project, new Tick(_owner.PlayheadTick)));

    /// <summary>Loops the selected clips' span, or the editor's selected notes when the arrangement has no selection.</summary>
    [RelayCommand(CanExecute = nameof(CanLoopSelection))]
    private void LoopSelection()
    {
        if (SelectionRange() is { } range)
        {
            _owner.Execute(LoopCommands.LoopRegion(range));
        }
    }

    public bool CanLoopSelection() => SelectionRange() is not null;

    /// <summary>Sets the range (from a ruler drag), turning looping on when <paramref name="enable"/>.</summary>
    public void SetRange(TickRange range, bool enable, string label = "Set Loop") => _owner.Execute(LoopCommands.SetRange(range, enable, label));

    /// <summary>Moves the loop by <paramref name="ticks"/>, keeping its length.</summary>
    public void Nudge(long ticks)
    {
        if (ticks != 0)
        {
            _owner.Execute(LoopCommands.Nudge(ticks));
        }
    }

    /// <summary>Sets the start from bar.beat.tick text, keeping the length. Returns false (changing nothing) for text that is not a position.</summary>
    public bool CommitStart(string text)
    {
        var meter = Project.Sequence.MeterMap;
        if (!Formatting.TryParsePosition(text, meter, out var start))
        {
            Sync();
            return false;
        }

        var length = Project.Loop.Range?.Length ?? LoopCommands.DefaultRange(Project, start).Length;
        _owner.Execute(LoopCommands.SetStartAndLength(start, length));
        Sync();
        return true;
    }

    /// <summary>Sets the length from bars.beats.ticks text, keeping the start. Returns false for text that is not a positive length.</summary>
    public bool CommitLength(string text)
    {
        var meter = Project.Sequence.MeterMap;
        var start = Project.Loop.Range?.Start ?? LoopCommands.DefaultRange(Project, new Tick(_owner.PlayheadTick)).Start;
        if (!Formatting.TryParseLength(text, meter, start, out var length) || length.Value < 1)
        {
            Sync();
            return false;
        }

        _owner.Execute(LoopCommands.SetStartAndLength(start, length));
        Sync();
        return true;
    }

    internal void Sync()
    {
        var loop = Project.Loop;
        IsEnabled = loop.IsEnabled;
        HasRange = loop.Range is not null;
        var meter = Project.Sequence.MeterMap;
        if (loop.Range is { } range)
        {
            StartText = Formatting.Position(meter.ToBarBeatTick(range.Start));
            LengthText = Formatting.Length(range.Length, meter, range.Start);
        }
        else
        {
            StartText = string.Empty;
            LengthText = string.Empty;
        }

        ToggleTip = loop.IsEnabled
            ? $"Loop on: {StartText}, {LengthText} long. Click to turn off; the range stays (L)"
            : loop.Range is not null
                ? $"Loop off: {StartText}, {LengthText} long. Click to loop it (L)"
                : "Loop: four bars from the playhead's bar (L). Drag in the loop strip above the ruler to set a range";
        LoopSelectionCommand.NotifyCanExecuteChanged();
    }

    internal void SelectionChanged() => LoopSelectionCommand.NotifyCanExecuteChanged();

    // The selected clips' span; otherwise the editor's selected notes'.
    private TickRange? SelectionRange()
    {
        var clips = Project.Sequence.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => _owner.Arrangement.SelectedClips.Contains(c.Id))
            .ToList();
        if (clips.Count > 0)
        {
            return new TickRange(clips.Min(c => c.Start), clips.Max(c => c.End));
        }

        var notes = _owner.Editor.SelectedNotesSpan();
        return notes is { } span && span.End > span.Start ? new TickRange(span.Start, span.End) : null;
    }
}
