using Bluestone.Domain.Midi;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Tests.Unit.Domain.Routing;
using static Bluestone.Tests.Unit.Domain.Routing.RoutingFixture;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

/// <summary>Automation of an external instrument's parameters (ADR 0031): a target naming the part and the profile's parameter key.</summary>
public sealed class InstrumentAutomationTests
{
    private static readonly ExternalInstrumentId Synth = ExternalInstrumentId.New();

    private static AutomationTarget Cutoff(string port = "A", int channel = 1, ExternalInstrumentId? instrument = null) =>
        AutomationTarget.ForInstrument(instrument ?? Synth, port, MidiChannel.FromNumber(channel), "filter-cutoff");

    private static AutomationLane Lane(AutomationTarget target) =>
        AutomationLane.Create(target).WithPoint(new AutomationPoint(Tick.Zero, ControlValue.Center)).WithPoint(new AutomationPoint(new Tick(960), ControlValue.Max));

    [Fact]
    public void AnInstrumentTarget_IsNeitherMidiNorADevice_AndCannotBecomeOneEvent()
    {
        var target = Cutoff(channel: 3);

        Assert.True(target.IsInstrument);
        Assert.False(target.IsMidi);
        Assert.False(target.IsDevice);
        Assert.True(target.IsValid);
        Assert.Equal((Synth, "A", 3, "filter-cutoff"), (target.Instrument, target.Port, target.Channel.Number, target.InstrumentParameter));
        Assert.Throws<InvalidOperationException>(() => target.CreateEvent(Tick.Zero, ControlValue.Max));
        Assert.Equal(target, target.WithChannel(MidiChannel.FromNumber(9)));
        Assert.Equal(new ControlValue(12345), target.AtMidi1Resolution(new ControlValue(12345)));
    }

    [Fact]
    public void ForInstrument_RefusesMissingOrOverlongParts()
    {
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForInstrument(default, "A", One, "x"));
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForInstrument(Synth, "", One, "x"));
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForInstrument(Synth, "A", One, " "));
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForInstrument(Synth, new string('p', 65), One, "x"));
        Assert.Throws<ArgumentException>(() => AutomationTarget.ForInstrument(Synth, "A", One, new string('k', 65)));
    }

    [Fact]
    public void Targets_AreEqualOnlyForTheSamePartAndParameter()
    {
        Assert.Equal(Cutoff(), Cutoff());
        Assert.NotEqual(Cutoff(), Cutoff(port: "B"));
        Assert.NotEqual(Cutoff(), Cutoff(channel: 2));
        Assert.NotEqual(Cutoff(), Cutoff(instrument: ExternalInstrumentId.New()));
        Assert.NotEqual(Cutoff(), AutomationTarget.ForInstrument(Synth, "A", One, "filter-resonance"));
    }

    [Fact]
    public void TwoLanesOnOneTrack_ForTheSamePartParameter_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => Track.Create("t").WithAutomation([Lane(Cutoff()), Lane(Cutoff())]));
        Assert.Equal(2, Track.Create("t").WithAutomation([Lane(Cutoff()), Lane(Cutoff(channel: 2))]).Automation.Length);
    }

    [Fact]
    public void InstrumentLanes_RenderNeitherEventsNorParameterChanges()
    {
        var track = Track.FromEvents(TrackId.New(), "t", [Note(0)]).WithAutomation([Lane(Cutoff())]);

        var rendered = TrackRendering.Render(track, new Ppqn(480));

        Assert.IsType<NoteEvent>(Assert.Single(rendered.Events));
        Assert.Empty(rendered.ParameterChanges);
        Assert.Equal(0, rendered.SuppressedEvents);
        Assert.Null(Track.Create("t").WithAutomation([Lane(Cutoff(channel: 5))]).FirstChannel);
    }

    [Fact]
    public void Sampling_KeepsFullResolution()
    {
        var samples = AutomationRenderer.Sample(Lane(Cutoff()), new TickSpan(480)).ToList();

        Assert.Equal([(Tick.Zero, ControlValue.Center), (new Tick(480), AutomationLane.Interpolate(new AutomationPoint(Tick.Zero, ControlValue.Center), new AutomationPoint(new Tick(960), ControlValue.Max), 480)), (new Tick(960), ControlValue.Max)], samples);
    }

    [Fact]
    public void Validation_WarnsAboutAMissingInstrumentOrPort_AndKeepsTheLane()
    {
        var project = Empty().WithInstrument(out var instrument);
        var track = Track.Create("t").WithAutomation([
            Lane(Cutoff(instrument: instrument.Id)),
            Lane(Cutoff(port: "Z", instrument: instrument.Id)),
            Lane(Cutoff(instrument: ExternalInstrumentId.New())),
        ]);

        var issues = project.With(track).Validate();

        Assert.Equal(2, issues.Count(i => i.Code == RoutingIssueCode.AutomationInstrumentMissing && i.Severity == RoutingSeverity.Warning));
        Assert.Contains(issues, i => i.Message.Contains("port Z of MU2000", StringComparison.Ordinal));
        Assert.Empty(project.With(track).Errors());
    }
}
