using System.Text;
using System.Text.Json.Nodes;
using Bluestone.Domain.Mixing;
using Bluestone.Domain.Projects;
using Bluestone.Domain.Sequencing;
using Bluestone.Domain.Time;
using Bluestone.Infrastructure.Projects;

namespace Bluestone.Tests.Unit.Infrastructure;

/// <summary>Sends and audio clip gain and fades: optional fields added to format 4 without a version change.</summary>
public sealed class MixerSendAndClipFadeFormatTests
{
    private static readonly ProjectSerializer Serializer = ProjectSerializer.Default;

    private static JsonObject ToJson(Project project) => JsonNode.Parse(Serializer.Serialize(new ProjectDocument(project)))!.AsObject();

    private static Project FromJson(JsonObject json) => Serializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString())).Project;

    private static Project WithAudioClip(AudioClip clip) =>
        Project.CreateNew() with { Sequence = Sequence.CreateEmpty(Ppqn.Default).WithTrack(Track.Create("Audio", TrackRole.Audio).WithClip(clip)) };

    private static AudioClip Clip() => new(ClipId.New(), Tick.Zero, new TickSpan(960), TickSpan.Zero, new AudioSource("a.wav", 48_000, 48_000, 1));

    [Fact]
    public void Sends_AreWrittenOnTheirChannel_AndReadBack()
    {
        var reverb = MixerChannel.Create("Reverb");
        var vocal = MixerChannel.Create("Vocal").WithSend(new MixerSend(reverb.Id, -9.5, preFader: true));
        var project = Project.CreateNew() with { Mixer = new Mixer { Channels = [vocal, reverb] } };

        var json = ToJson(project);
        var restored = FromJson(json);

        var send = json["project"]!["mixer"]!["channels"]![0]!["sends"]![0]!.AsObject();
        Assert.Equal(reverb.Id.Value.ToString("D"), (string)send["target"]!);
        Assert.Equal(-9.5, (double)send["level"]!);
        Assert.True((bool)send["preFader"]!);
        Assert.Null(json["project"]!["mixer"]!["channels"]![1]!["sends"]);
        Assert.Equal(project.Mixer.Channels, restored.Mixer.Channels);
    }

    [Fact]
    public void ASendToItself_IsAFormatErrorAtTheMixer()
    {
        var vocal = MixerChannel.Create("Vocal");
        var json = ToJson(Project.CreateNew() with { Mixer = new Mixer { Channels = [vocal] } });
        json["project"]!["mixer"]!["channels"]![0]!.AsObject()["sends"] = JsonNode.Parse($$"""[ { "target": "{{vocal.Id}}", "level": 0 } ]""");

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.Equal("$.project.mixer", error.JsonPath);
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
