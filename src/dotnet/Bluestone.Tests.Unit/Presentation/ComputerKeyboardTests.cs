using Bluestone.Midi.Endpoints;
using Bluestone.Midi.Timing;
using Bluestone.Presentation;

namespace Bluestone.Tests.Unit.Presentation;

public sealed class ComputerKeyboardTests : IDisposable
{
    private readonly ComputerKeyboardProvider _provider = new(new VirtualClock());
    private readonly List<byte[]> _received = [];
    private readonly IMidiInput _input;
    private readonly ComputerKeyboardViewModel _keyboard;

    public ComputerKeyboardTests()
    {
        _input = _provider.OpenInputAsync(ComputerKeyboardProvider.InputId).AsTask().GetAwaiter().GetResult();
        _input.SetReceiver((message, _) => _received.Add(message.ToArray()));
        _keyboard = new ComputerKeyboardViewModel(_provider) { IsEnabled = true };
    }

    public void Dispose()
    {
        _input.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public void Provider_ExposesOneSoftwareInput()
    {
        var endpoint = Assert.Single(_provider.GetEndpoints());
        Assert.Equal(EndpointDirection.Input, endpoint.Direction);
        Assert.Equal(EndpointTransport.Software, endpoint.Transport);
    }

    [Fact]
    public void Press_SendsNoteOnFromMiddleC_AndReleaseSendsNoteOff()
    {
        _keyboard.Press(0);
        _keyboard.Release(0);

        Assert.Equal([[0x90, 60, 100], [0x80, 60, 0]], _received);
    }

    [Fact]
    public void Press_IgnoresAutoRepeat()
    {
        _keyboard.Press(4);
        _keyboard.Press(4);

        Assert.Single(_received);
        Assert.True(_keyboard.IsSounding);
    }

    [Fact]
    public void Release_AfterOctaveChange_StopsTheNoteThatStarted()
    {
        _keyboard.Press(0);
        _keyboard.OctaveUpCommand.Execute(null);
        _keyboard.Release(0);

        Assert.Equal([0x80, 60, 0], _received[^1]);
    }

    [Fact]
    public void Velocity_IsAdjustableAndClamped()
    {
        _keyboard.VelocityUpCommand.Execute(null);
        _keyboard.VelocityUpCommand.Execute(null);
        _keyboard.VelocityUpCommand.Execute(null);
        Assert.Equal(127, _keyboard.Velocity);

        _keyboard.Velocity = 0;
        Assert.Equal(1, _keyboard.Velocity);

        _keyboard.Velocity = 64;
        _keyboard.Press(0);
        Assert.Equal(64, _received[^1][2]);
    }

    [Fact]
    public void Octave_IsClamped()
    {
        for (var i = 0; i < 20; i++)
        {
            _keyboard.OctaveDownCommand.Execute(null);
        }

        Assert.Equal(ComputerKeyboardViewModel.MinOctave, _keyboard.Octave);
        Assert.Equal("C-2", _keyboard.OctaveText);
    }

    [Fact]
    public void Notes_UseTheSourceChannel_AndReleaseOnTheChannelTheyStarted()
    {
        var channel = 2;
        _keyboard.ChannelSource = () => channel;
        _keyboard.Press(0);
        channel = 5;
        _keyboard.Release(0);

        Assert.Equal([[0x92, 60, 100], [0x82, 60, 0]], _received);
        Assert.Equal("3", _keyboard.ChannelText);
    }

    [Fact]
    public void Disabling_ReleasesHeldNotes_AndIgnoresPresses()
    {
        _keyboard.Press(0);
        _keyboard.Press(7);
        _keyboard.IsEnabled = false;

        Assert.False(_keyboard.IsSounding);
        Assert.Equal(2, _received.Count(m => m[0] == 0x80));

        _keyboard.Press(0);
        Assert.Equal(4, _received.Count);
    }
}
