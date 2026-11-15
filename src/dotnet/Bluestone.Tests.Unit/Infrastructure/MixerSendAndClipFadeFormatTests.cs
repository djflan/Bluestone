using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Devices;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Routing;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>Format 6 audio send routes and persisted audio clip gain and fades.</summary>
public sealed class MixerSendAndClipFadeFormatTests
{
    private static readonly ProjectSerializer Serializer = ProjectSerializer.Default;

    private static JsonObject ToJson(Project project) => JsonNode.Parse(Serializer.Serialize(new ProjectDocument(project)))!.AsObject();

    private static Project FromJson(JsonObject json) => Serializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Project;

    private static Project WithAudioClip(AudioClip clip) =>
        Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(Track.Create("Audio", TrackRole.Audio).WithClip(clip)) };

    private static AudioClip Clip() => new(ClipId.New(), Tick.Zero, new TickSpan(960), TickSpan.Zero, new AudioSource("a.wav", 48_000, 48_000, 1));

    [Fact]
    public void AudioSendRoutes_AreWrittenOnTheConnection_AndReadBack()
    {
        var reverb = Track.Create("Reverb", TrackRole.Effect);
        var vocal = Track.Create("Vocal", TrackRole.Audio);
        var send = SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Track(reverb.Id)) with
        {
            AudioPurpose = AudioRoutePurpose.Send,
            SendLevelDecibels = -9.5,
            SendPreFader = true,
        };
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(vocal).WithTrack(reverb),
            Connections = [send],
        };

        var json = ToJson(project);
        var restored = FromJson(json);

        var route = json["project"]!["connections"]![0]!.AsObject();
        Assert.Equal("track", (string)route["destination"]!["type"]!);
        Assert.Equal(reverb.Id.Value.ToString("D"), (string)route["destination"]!["id"]!);
        Assert.Equal("send", (string)route["purpose"]!);
        Assert.Equal(-9.5, (double)route["sendLevel"]!);
        Assert.True((bool)route["preFader"]!);
        Assert.Null(json["project"]!["mixer"]);
        Assert.Equal(send, Assert.Single(restored.Connections));
    }

    [Fact]
    public void AFormat6SendWithoutPreFader_IsReadAsPostFader()
    {
        var reverb = Track.Create("Reverb", TrackRole.Effect);
        var vocal = Track.Create("Vocal", TrackRole.Audio);
        var project = Project.CreateNew() with
        {
            Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(vocal).WithTrack(reverb),
            Connections =
            [
                SignalConnection.Create(SignalKind.Audio, SignalNode.Track(vocal.Id), SignalNode.Track(reverb.Id)) with
                {
                    AudioPurpose = AudioRoutePurpose.Send,
                },
            ],
        };
        var json = ToJson(project);
        json["project"]!["connections"]![0]!.AsObject().Remove("preFader");

        Assert.False(Assert.Single(FromJson(json).Connections).SendPreFader);
    }

    [Fact]
    public void AClipWithDefaultGainAndNoFades_WritesNoNewFields()
    {
        var clip = ToJson(WithAudioClip(Clip()))["project"]!["sequence"]!["tracks"]![0]!["clips"]![0]!.AsObject();

        Assert.False(clip.ContainsKey("gain"));
        Assert.False(clip.ContainsKey("fadeIn"));
        Assert.False(clip.ContainsKey("fadeOut"));
        Assert.False(clip.ContainsKey("fadeShape"));
    }

    [Fact]
    public void ClipGainAndFades_RoundTrip()
    {
        var clip = Clip() with { GainDecibels = -7.25, FadeIn = new TickSpan(10), FadeOut = new TickSpan(20), FadeShape = FadeShape.EqualPower };

        var json = ToJson(WithAudioClip(clip));
        var restored = Assert.IsType<AudioClip>(FromJson(json).Sequence.Tracks[0].Clips[0]);

        Assert.Equal("equalPower", (string)json["project"]!["sequence"]!["tracks"]![0]!["clips"]![0]!["fadeShape"]!);
        Assert.Equal((-7.25, new TickSpan(10), new TickSpan(20), FadeShape.EqualPower), (restored.GainDecibels, restored.FadeIn, restored.FadeOut, restored.FadeShape));
    }

    [Fact]
    public void AnUnknownFadeShape_IsAFormatErrorWithItsPath()
    {
        var json = ToJson(WithAudioClip(Clip()));
        json["project"]!["sequence"]!["tracks"]![0]!["clips"]![0]!.AsObject()["fadeShape"] = "s-curve";

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.Equal("$.project.sequence.tracks[0].clips[0].fadeShape", error.JsonPath);
    }
}
