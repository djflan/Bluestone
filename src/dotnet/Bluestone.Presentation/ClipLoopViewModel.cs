using Bluestone.Application.Audio;
using Bluestone.Application.Editing;
using Bluestone.Audio.Files;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bluestone.Presentation;

/// <summary>
/// The loop of the selected clips, as the inspector shows it: on, off, or mixed across several clips, its start and
/// length (shown when they agree), and Consolidate. Also takes the arranger's loop-handle and repeat-marker drags.
/// Each change is one undoable command, however many clips it touches.
/// </summary>
public sealed partial class ClipLoopViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private List<(TrackId Track, Clip Clip)> _clips = [];
    private bool _syncing;

    internal ClipLoopViewModel(MainViewModel owner) => _owner = owner;

    [ObservableProperty]
    public partial bool HasClips { get; private set; }

    /// <summary>"Loop" for one clip, "Loop · 3 clips" for several.</summary>
    [ObservableProperty]
    public partial string Header { get; private set; } = "Loop";

    /// <summary>On, off, or null when the selected clips differ.</summary>
    [ObservableProperty]
    public partial bool? IsLooping { get; set; }

    /// <summary>The loop's start from the content's start, as bars.beats.ticks; empty when the clips differ ("Mixed" placeholder).</summary>
    [ObservableProperty]
    public partial string StartText { get; private set; } = string.Empty;

    /// <summary>The loop's length as bars.beats.ticks; empty when the clips differ.</summary>
    [ObservableProperty]
    public partial string LengthText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StartPlaceholder { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LengthPlaceholder { get; private set; } = string.Empty;

    /// <summary>What a looping selection plays, in words (passes, pickup), or why the fields are empty.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    private Sequence Sequence => _owner.Project.Sequence;

    private MeterMap Meter => Sequence.MeterMap;

    internal void Sync()
    {
        _syncing = true;
        try
        {
            var selected = _owner.Arrangement.SelectedClips;
            _clips = [.. Sequence.Tracks.SelectMany(t => t.Clips.Where(c => selected.Contains(c.Id)).Select(c => (t.Id, c)))];
            HasClips = _clips.Count > 0;
            Header = _clips.Count > 1 ? $"Loop · {_clips.Count} clips" : "Loop";
            var states = _clips.Select(c => c.Clip.IsLooping).Distinct().ToList();
            IsLooping = states.Count == 1 ? states[0] : null;
            var loops = _clips.Select(c => c.Clip.Loop).ToList();
            var starts = loops.Select(l => l?.Start).Distinct().ToList();
            var lengths = loops.Select(l => l?.Length).Distinct().ToList();
            StartText = starts is [{ } start] ? Formatting.Length(new TickSpan(start.Value), Meter, Tick.Zero) : string.Empty;
            LengthText = lengths is [{ } length] ? Formatting.Length(length, Meter, Tick.Zero) : string.Empty;
            StartPlaceholder = starts.Count > 1 ? "Mixed" : "Off";
            LengthPlaceholder = lengths.Count > 1 ? "Mixed" : "Off";
            Summary = _clips switch
            {
                [] => string.Empty,
                [var only] when only.Clip.IsLooping => Describe(only.Clip),
                [var only] when only.Clip.Loop is not null => "Loop off; its region is kept.",
                [_] => "Drag the clip's lower-right corner to repeat it.",
                _ when IsLooping is null => "Some of these clips loop.",
                _ => string.Empty,
            };
            ConsolidateCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsLoopingChanged(bool? value)
    {
        if (!_syncing && HasClips && value is { } on)
        {
            _owner.Execute(ClipLoopCommands.SetEnabled(Targets(), on));
        }
    }

    /// <summary>Sets every selected clip's loop start from bars.beats.ticks after the content's start. False (no change) for text that is not a length.</summary>
    public bool CommitStart(string text)
    {
        if (!HasClips || !Formatting.TryParseLength(text, Meter, Tick.Zero, out var start))
        {
            Sync();
            return false;
        }

        _owner.Execute(ClipLoopCommands.SetStart(Targets(), new Tick(start.Value)));
        Sync();
        return true;
    }

    /// <summary>Sets every selected clip's loop length. False (no change) for text that is not a positive length.</summary>
    public bool CommitLength(string text)
    {
        if (!HasClips || !Formatting.TryParseLength(text, Meter, Tick.Zero, out var length) || length.Value < 1)
        {
            Sync();
            return false;
        }

        _owner.Execute(ClipLoopCommands.SetLength(Targets(), length));
        Sync();
        return true;
    }

    /// <summary>The arranger's loop handle: the clip ends at <paramref name="endTick"/>, its loop repeating (and turned on).</summary>
    public void ExtendLooping(ClipId clip, long endTick)
    {
        if (TrackOf(clip) is { } track)
        {
            _owner.Execute(ClipLoopCommands.ExtendLooping(track, clip, new Tick(Math.Max(1, endTick))));
        }
    }

    /// <summary>The arranger's first repeat marker: the loop gets <paramref name="length"/> ticks.</summary>
    public void SetLoopLength(ClipId clip, long length)
    {
        if (TrackOf(clip) is { } track && length >= 1)
        {
            _owner.Execute(ClipLoopCommands.SetLength(track, clip, new TickSpan(length)));
        }
    }

    /// <summary>
    /// Writes out what the selected looping clips play: note clips as ordinary events, audio clips as a new file each.
    /// One undo step per audio clip (each waits for its file); note clips together in one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConsolidate))]
    private async Task ConsolidateAsync()
    {
        var looping = _clips.Where(c => c.Clip.IsLooping).ToList();
        foreach (var group in looping.Where(c => c.Clip is NoteClip).GroupBy(c => c.Track))
        {
            _owner.Execute(ClipLoopCommands.ConsolidateNotes(group.Key, [.. group.Select(c => c.Clip.Id)]));
        }

        foreach (var (track, clip) in looping.Where(c => c.Clip is AudioClip))
        {
            try
            {
                var result = await AudioConsolidation.ConsolidateAsync(_owner.Project, track, clip.Id, _owner.ProjectDirectory);
                if (!_owner.Execute(result.Command))
                {
                    // Refused (the clip changed meanwhile, with a message) or nothing to do: the file is not used.
                    File.Delete(result.Path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AudioFileException)
            {
                _owner.AddMessage(MessageSeverity.Warning, "Consolidate Clip", $"{Path.GetFileName((clip as AudioClip)?.Source.Location)} could not be consolidated: {ex.Message}");
            }
        }
    }

    private bool CanConsolidate() => _clips.Any(c => c.Clip.IsLooping);

    private List<(TrackId Track, ClipId Clip)> Targets() => [.. _clips.Select(c => (c.Track, c.Clip.Id))];

    private TrackId? TrackOf(ClipId clip) => Sequence.Tracks.FirstOrDefault(t => t.FindClip(clip) is not null)?.Id;

    private static string Describe(Clip clip)
    {
        var passes = clip.Passes.Count;
        var pickup = clip.Loop!.Start.Value > clip.ContentAt(0) ? "a pickup, then " : string.Empty;
        var repeats = passes <= 1 ? "no repeats yet (make the clip longer)" : $"{passes} passes";
        return $"Plays {pickup}{repeats}.";
    }
}
