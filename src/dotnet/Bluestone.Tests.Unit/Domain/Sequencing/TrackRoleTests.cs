using Bluestone.Domain.Devices;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using static Bluestone.Tests.Unit.Domain.Routing.RoutingFixture;

namespace Bluestone.Tests.Unit.Domain.Sequencing;

public sealed class TrackRoleTests
{
    private static AudioClip Audio(long start = 0, long length = 960) =>
        new(ClipId.New(), new Tick(start), new TickSpan(length), TickSpan.Zero, new AudioSource("audio/loop.wav", 48_000, 96_000, 2));

    [Fact]
    public void NewTracks_AreInstrumentTracksWithoutAnyRouting()
    {
        var track = Track.Create("Piano");

        Assert.Equal(TrackRole.Instrument, track.Role);
        Assert.Null(track.Group);
    }

    [Fact]
    public void ChangingTheRole_KeepsEverythingTheTrackHolds()
    {
        var track = NoteTrack("Piano").WithAutomation([AutomationLane.Create(AutomationTarget.ForPitchBend(One))]).WithMuted(true);

        var changed = track.WithRole(TrackRole.Hybrid);

        Assert.Equal(TrackRole.Hybrid, changed.Role);
        Assert.Equal(track.Clips, changed.Clips);
        Assert.Equal(track.Automation, changed.Automation);
        Assert.True(changed.IsMuted);
        Assert.Equal(track.Id, changed.Id);
    }

    [Fact]
    public void RoleChangeAndReconciliation_PreservePersistedAudioStripState()
    {
        var strip = new AudioStrip { GainDecibels = -9, Pan = -0.25, IsMuted = true, IsSoloed = true };
        var piano = Track.Create("Piano").WithAudioStrip(strip);
        var project = Empty().With(piano);

        var plan = TrackRoleConversion.ForRoleChange(project, piano.Id, TrackRole.Audio, TestDevices.Lookup);
        var changed = plan.Apply(project).Sequence.FindTrack(piano.Id)!;

        Assert.Equal(RoleChangeOutcome.Automatic, plan.Outcome);
        Assert.Equal(strip, changed.AudioStrip);

        var staleRole = piano.WithClip(Audio());
        var reconciled = TrackRoleConversion.Reconcile(Empty().With(staleRole)).Project.Sequence.FindTrack(staleRole.Id)!;

        Assert.Equal(TrackRole.Audio, reconciled.Role);
        Assert.Equal(strip, reconciled.AudioStrip);
    }

    [Fact]
    public void TheTrackModel_DoesNotRejectContentBecauseOfItsRole()
    {
        // Sequencing stays independent of workflow: conversion rules live in TrackRoleConversion.
        var track = Track.Create("x", TrackRole.Instrument).WithClip(Audio());

        Assert.IsType<AudioClip>(Assert.Single(track.Clips));
    }

    [Fact]
    public void ATrackCannotBeItsOwnGroup()
    {
        var track = Track.Create("g", TrackRole.Group);

        Assert.Throws<ArgumentException>(() => track.WithGroup(track.Id));
        Assert.Equal(track.Id, Track.Create("child").WithGroup(track.Id).Group);
    }

    [Fact]
    public void ArrangedEvents_IgnoreAudioClips_AndTheyCoexistWithNoteClips()
    {
        var track = NoteTrack("t", TrackRole.Hybrid).WithClip(Audio(5000));

        Assert.Equal(2, track.Clips.Length);
        Assert.Single(track.ArrangedEvents);
        Assert.Equal(TrackRole.Hybrid, TrackRoleConversion.Implied(track));
    }

    [Fact]
    public void Implied_FollowsTheClips()
    {
        Assert.Null(TrackRoleConversion.Implied(Track.Create("empty")));
        Assert.Equal(TrackRole.Instrument, TrackRoleConversion.Implied(NoteTrack("n")));
        Assert.Equal(TrackRole.Audio, TrackRoleConversion.Implied(Track.Create("a").WithClip(Audio())));
    }

    // Scenario 9
    [Fact]
    public void AudioOnAnInstrumentTrackWithAnAudioOutput_BecomesHybridAutomatically()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.Synth).WithMixerChannel(out var channel, "Piano out")
            .Connect(SignalKind.Audio, SignalNode.Track(piano.Id), SignalNode.Mixer(channel.Id));

        var plan = TrackRoleConversion.ForAddedClip(project, piano.Id, ClipContent.Audio, TestDevices.Lookup);
        var result = plan.Apply(project);

        Assert.Equal(RoleChangeOutcome.Automatic, plan.Outcome);
        Assert.Equal(TrackRole.Hybrid, result.Sequence.FindTrack(piano.Id)!.Role);
        // Nothing but the role changed.
        Assert.Equal(project.Connections, result.Connections);
        Assert.Equal(project.Chains, result.Chains);
        Assert.Equal(project.Mixer, result.Mixer);
        Assert.Equal(piano.Clips, result.Sequence.FindTrack(piano.Id)!.Clips);
    }

    [Fact]
    public void AudioOnAHardwareOnlyInstrumentTrack_AsksBeforeGivingItAnAudioPath()
    {
        var piano = NoteTrack("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.MidiFx).WithInstrument(out var mu)
            .Connect(SignalKind.Events, SignalNode.Track(piano.Id), SignalNode.ExternalPart(mu.Id));

        var plan = TrackRoleConversion.ForAddedClip(project, piano.Id, ClipContent.Audio, TestDevices.Lookup);

        Assert.Equal(RoleChangeOutcome.NeedsConfirmation, plan.Outcome);
        Assert.True(plan.AddsAudioPath);
        Assert.Contains("Master", plan.Message, StringComparison.Ordinal);

        var result = plan.Apply(project);

        var converted = result.Sequence.FindTrack(piano.Id)!;
        Assert.Equal(TrackRole.Hybrid, converted.Role);
        Assert.Equal(AudioStrip.Default, converted.AudioStrip);
        var output = Assert.Single(result.Connections, c => c.Kind == SignalKind.Audio);
        Assert.Equal(SignalNode.Track(piano.Id), output.Source);
        Assert.Equal(SignalNode.Master, output.Destination);
        Assert.Equal(AudioRoutePurpose.Output, output.AudioPurpose);
        Assert.Equal(project.Mixer, result.Mixer);
        // The hardware route, the effect, and the notes are untouched.
        Assert.Contains(project.Connections[0], result.Connections);
        Assert.Equal(project.ChainOf(piano.Id), result.ChainOf(piano.Id));
        Assert.Equal(piano.Clips, converted.Clips);
        Assert.Empty(SignalRoutingValidator.Errors(result.Validate()));
    }

    [Fact]
    public void NotesOnAnAudioTrack_MakeItHybrid_AndSayWhenNothingWillPlayThem()
    {
        var loops = Track.Create("Loops", TrackRole.Audio).WithClip(Audio());
        var project = Empty().WithTrackChain(loops, TestDevices.Filter);

        var plan = TrackRoleConversion.ForAddedClip(project, loops.Id, ClipContent.Notes, TestDevices.Lookup);

        Assert.Equal(RoleChangeOutcome.Automatic, plan.Outcome);
        Assert.Equal(TrackRole.Hybrid, plan.To);
        Assert.Contains("no instrument yet", plan.Message, StringComparison.Ordinal);
        Assert.Equal(TrackRole.Hybrid, plan.Apply(project).Sequence.FindTrack(loops.Id)!.Role);
        Assert.Equal(project.ChainOf(loops.Id), plan.Apply(project).ChainOf(loops.Id));
    }

    [Fact]
    public void NotesOnAnAudioTrackWithAnInstrument_NeedNoWarning()
    {
        var loops = Track.Create("Loops", TrackRole.Audio);
        var project = Empty().WithTrackChain(loops, TestDevices.Synth);

        var plan = TrackRoleConversion.ForAddedClip(project, loops.Id, ClipContent.Notes, TestDevices.Lookup);

        Assert.DoesNotContain("no instrument", plan.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TrackRole.Effect, ClipContent.Notes)]
    [InlineData(TrackRole.Effect, ClipContent.Audio)]
    [InlineData(TrackRole.Group, ClipContent.Notes)]
    [InlineData(TrackRole.Group, ClipContent.Audio)]
    public void EffectAndGroupTracks_RefuseClips_InsteadOfConvertingSilently(TrackRole role, ClipContent content)
    {
        var track = Track.Create("Bus", role);
        var project = Empty().With(track);

        var plan = TrackRoleConversion.ForAddedClip(project, track.Id, content, TestDevices.Lookup);

        Assert.Equal(RoleChangeOutcome.Refused, plan.Outcome);
        Assert.Contains("do not hold clips", plan.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => plan.Apply(project));
        Assert.Empty(project.Sequence.FindTrack(track.Id)!.Clips);
    }

    [Fact]
    public void ContentTheRoleAlreadyHolds_ChangesNothing()
    {
        var piano = NoteTrack("Piano");
        var hybrid = NoteTrack("H", TrackRole.Hybrid);
        var project = Empty().With(piano).With(hybrid);

        Assert.Equal(RoleChangeOutcome.NoChange, TrackRoleConversion.ForAddedClip(project, piano.Id, ClipContent.Notes, TestDevices.Lookup).Outcome);
        Assert.Equal(RoleChangeOutcome.NoChange, TrackRoleConversion.ForAddedClip(project, hybrid.Id, ClipContent.Audio, TestDevices.Lookup).Outcome);
        Assert.Same(project, TrackRoleConversion.ForAddedClip(project, piano.Id, ClipContent.Notes, TestDevices.Lookup).Apply(project));
    }

    [Fact]
    public void NarrowingARoleThatWouldHideContent_IsRefused()
    {
        var hybrid = NoteTrack("H", TrackRole.Hybrid).WithClip(Audio(5000));
        var project = Empty().With(hybrid);

        var toInstrument = TrackRoleConversion.ForRoleChange(project, hybrid.Id, TrackRole.Instrument, TestDevices.Lookup);
        var toAudio = TrackRoleConversion.ForRoleChange(project, hybrid.Id, TrackRole.Audio, TestDevices.Lookup);
        var toEffect = TrackRoleConversion.ForRoleChange(project, hybrid.Id, TrackRole.Effect, TestDevices.Lookup);

        Assert.All([toInstrument, toAudio, toEffect], p => Assert.Equal(RoleChangeOutcome.Refused, p.Outcome));
    }

    [Fact]
    public void AGroupWithMembers_CannotChangeRole_UntilTheyAreMoved()
    {
        var group = Track.Create("Drums", TrackRole.Group);
        var kick = NoteTrack("Kick").WithGroup(group.Id);
        var project = Empty().With(group).With(kick);

        Assert.Equal(RoleChangeOutcome.Refused, TrackRoleConversion.ForRoleChange(project, group.Id, TrackRole.Effect, TestDevices.Lookup).Outcome);
        var freed = project.With(kick.WithGroup(null));
        Assert.Equal(RoleChangeOutcome.Automatic, TrackRoleConversion.ForRoleChange(freed, group.Id, TrackRole.Effect, TestDevices.Lookup).Outcome);
    }

    [Fact]
    public void AnInstrumentTrackBecomingAnAudioTrack_AsksBeforeLeavingItsInstrumentDeaf()
    {
        var piano = Track.Create("Piano");
        var project = Empty().WithTrackChain(piano, TestDevices.Synth);

        var plan = TrackRoleConversion.ForRoleChange(project, piano.Id, TrackRole.Audio, TestDevices.Lookup);

        Assert.Equal(RoleChangeOutcome.NeedsConfirmation, plan.Outcome);
        Assert.True(plan.AddsAudioPath);
        var result = plan.Apply(project);
        var changed = result.Sequence.FindTrack(piano.Id)!;
        Assert.Equal(TrackRole.Audio, changed.Role);
        Assert.NotNull(changed.AudioStrip);
        Assert.Single(result.Connections, c =>
            c.Kind == SignalKind.Audio && c.AudioPurpose == AudioRoutePurpose.Output && c.Source == SignalNode.Track(piano.Id));
        Assert.Empty(SignalRoutingValidator.Errors(result.Validate()));
        Assert.Equal(project.ChainOf(piano.Id), result.ChainOf(piano.Id));
    }

    [Theory]
    [InlineData(TrackRole.Audio)]
    [InlineData(TrackRole.Effect)]
    [InlineData(TrackRole.Group)]
    public void ChangingAnUnroutedMidiOnlyTrackToAnAudioCapableRole_AddsAValidOutput(TrackRole role)
    {
        var midiOnly = Track.Create("MIDI");
        var project = Empty().With(midiOnly);

        var plan = TrackRoleConversion.ForRoleChange(project, midiOnly.Id, role, TestDevices.Lookup);
        var changed = plan.Apply(project);

        Assert.True(plan.AddsAudioPath);
        Assert.NotNull(changed.Sequence.FindTrack(midiOnly.Id)!.AudioStrip);
        var output = Assert.Single(changed.Connections.Where(c =>
            c.Kind == SignalKind.Audio && c.AudioPurpose == AudioRoutePurpose.Output && c.Source == SignalNode.Track(midiOnly.Id)));
        Assert.Equal(SignalNode.Master, output.Destination);
        Assert.Empty(SignalRoutingValidator.Errors(changed.Validate()));
    }

    [Fact]
    public void ChangingARoleWithAParkedOutput_PreservesItAndAddsAnActiveOutput()
    {
        var midiOnly = Track.Create("MIDI");
        var parkedOutput = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(midiOnly.Id), SignalNode.Master);
        var project = Empty().With(midiOnly) with { ParkedConnections = [parkedOutput] };

        var plan = TrackRoleConversion.ForRoleChange(project, midiOnly.Id, TrackRole.Effect, TestDevices.Lookup);
        var changed = plan.Apply(project);

        Assert.True(plan.AddsAudioPath);
        Assert.Equal([parkedOutput], changed.ParkedConnections);
        var output = Assert.Single(changed.Connections.Where(c =>
            c.Kind == SignalKind.Audio && c.AudioPurpose == AudioRoutePurpose.Output && c.Source == SignalNode.Track(midiOnly.Id)));
        Assert.Equal(SignalNode.Master, output.Destination);
    }

    [Fact]
    public void Reconcile_WidensTracksWhoseRoleCannotHoldTheirClips_AndNeverNarrows()
    {
        var wrong = NoteTrack("Wrong", TrackRole.Audio);
        var effect = NoteTrack("Fx", TrackRole.Effect);
        var wide = NoteTrack("Wide", TrackRole.Hybrid);
        var project = Empty().With(wrong).With(effect).With(wide);

        var (result, changes) = TrackRoleConversion.Reconcile(project);

        Assert.Equal(TrackRole.Instrument, result.Sequence.FindTrack(effect.Id)!.Role);
        Assert.Equal(TrackRole.Instrument, result.Sequence.FindTrack(wrong.Id)!.Role);
        Assert.Equal(TrackRole.Hybrid, result.Sequence.FindTrack(wide.Id)!.Role);
        Assert.Equal(2, changes.Length);

        // Reconciling again finds nothing more to do, and says so by returning the same project.
        var (again, none) = TrackRoleConversion.Reconcile(result);
        Assert.Same(result, again);
        Assert.Empty(none);
    }

    [Fact]
    public void AudioClips_TrimSplitAndCopyLikeAnyClip()
    {
        var clip = Audio(1000, 1000);

        var trimmed = clip.WithBounds(new Tick(1200), new Tick(1800));
        var (left, right) = clip.SplitAt(new Tick(1400), ClipId.New());
        var copy = clip.CopyTo(new Tick(5000));

        Assert.Equal((1200L, 600L, 200L), (trimmed.Start.Value, trimmed.Length.Value, trimmed.ContentOffset.Value));
        Assert.Equal(400, left.Length.Value);
        Assert.Equal((1400L, 600L, 400L), (right.Start.Value, right.Length.Value, ((AudioClip)right).ContentOffset.Value));
        Assert.Equal(((AudioClip)left).Source, ((AudioClip)right).Source);
        Assert.NotEqual(clip.Id, copy.Id);
        Assert.Equal(5000, copy.Start.Value);
    }

    [Fact]
    public void AnAudioClipCannotBeExtendedBeforeItsSourceStarts()
    {
        var clip = Audio(1000, 1000) with { ContentOffset = new TickSpan(100) };

        var extended = clip.WithBounds(new Tick(0), new Tick(2000));

        Assert.Equal(900, extended.Start.Value);
        Assert.Equal(0, extended.ContentOffset.Value);
    }

    [Fact]
    public void AudioSources_RejectNonsense()
    {
        Assert.Throws<ArgumentException>(() => new AudioSource(" ", 48_000, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSource("a.wav", 10, 0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSource("a.wav", 48_000, -1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioSource("a.wav", 48_000, 0, 0));
    }
}
