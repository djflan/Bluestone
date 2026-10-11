using System.Globalization;
using Bluestone.Application.Editing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bluestone.Presentation;

/// <summary>
/// The inspector's audio clip section: the gain and fades of the one audio clip selected in the arrangement. Fades are
/// edited in beats (quarter notes) and kept within the clip; a drag of a slider is one undo step.
/// </summary>
public sealed partial class AudioClipViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private TrackId _track;
    private ClipId _clip;
    private int _ppqn = 960;
    private bool _syncing;

    internal AudioClipViewModel(MainViewModel owner) => _owner = owner;

    public double MinimumGain { get; } = AudioClip.MinGainDecibels;

    public double MaximumGain { get; } = AudioClip.MaxGainDecibels;

    /// <summary>Exactly one audio clip is selected.</summary>
    [ObservableProperty]
    public partial bool HasClip { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    public partial double Gain { get; set; }

    /// <summary>The fade-in, in beats.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FadeInText))]
    public partial double FadeIn { get; set; }

    /// <summary>The fade-out, in beats.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FadeOutText))]
    public partial double FadeOut { get; set; }

    /// <summary>The clip's length in beats: the longest a fade can be.</summary>
    [ObservableProperty]
    public partial double MaximumFade { get; set; }

    /// <summary>Equal-power (quarter-sine) fades rather than linear ones.</summary>
    [ObservableProperty]
    public partial bool IsEqualPower { get; set; }

    public string GainText => MixerChannelRow.FormatGain(Gain);

    public string FadeInText => Beats(FadeIn);

    public string FadeOutText => Beats(FadeOut);

    internal void Sync(Sequence sequence, IReadOnlySet<ClipId> selected)
    {
        _syncing = true;
        try
        {
            var found = selected.Count == 1
                ? sequence.Tracks.Select(t => (Track: t, Clip: t.FindClip(selected.First()))).FirstOrDefault(x => x.Clip is AudioClip)
                : default;
            HasClip = found.Clip is AudioClip;
            if (found.Clip is not AudioClip clip)
            {
                return;
            }

            _track = found.Track.Id;
            _clip = clip.Id;
            _ppqn = sequence.Ppqn.TicksPerQuarterNote;
            Title = found.Track.ClipName(clip);
            Gain = clip.GainDecibels;
            MaximumFade = (double)clip.Length.Value / _ppqn;
            FadeIn = (double)clip.FadeIn.Value / _ppqn;
            FadeOut = (double)clip.FadeOut.Value / _ppqn;
            IsEqualPower = clip.FadeShape == FadeShape.EqualPower;
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnGainChanged(double value)
    {
        if (!_syncing && HasClip)
        {
            _owner.Execute(ClipCommands.SetAudioClipGain(_track, _clip, value));
        }
    }

    partial void OnFadeInChanged(double value) => SetFades();

    partial void OnFadeOutChanged(double value) => SetFades();

    partial void OnIsEqualPowerChanged(bool value) => SetFades();

    private void SetFades()
    {
        if (!_syncing && HasClip)
        {
            _owner.Execute(ClipCommands.SetAudioClipFades(_track, _clip, Ticks(FadeIn), Ticks(FadeOut), IsEqualPower ? FadeShape.EqualPower : FadeShape.Linear));
        }
    }

    private TickSpan Ticks(double beats) => new((long)Math.Round(Math.Max(0, beats) * _ppqn));

    private static string Beats(double beats) => string.Create(CultureInfo.InvariantCulture, $"{beats:0.##} beats");
}
