using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>
/// <c>reference.transpose</c>: a MIDI effect. Semitones (id 0) maps [0, 1] to -24..+24 in 48 steps (default 0.5 is no
/// change). Note on, note off, and polyphonic pressure are transposed; a note-off follows the transposition its note-on
/// had, so a change in between leaves no hanging note. Notes pushed outside 0..127 are dropped. Every other event,
/// including controllers and system exclusive, passes through unchanged. Audio outputs are silent.
/// </summary>
internal sealed class TransposePlugin : ReferencePlugin
{
    public const string PluginId = "reference.transpose";
    public const uint SemitonesParameter = 0;

    private const int NotPlaying = -1;

    private readonly int[] _sounding = new int[16 * 128];

    public TransposePlugin()
        : base(CreateIdentity(PluginId, "Reference Transpose", PluginKind.MidiEffect), [new ParameterDescriptor(SemitonesParameter, "Semitones", 0.5, stepCount: 48)])
    {
        Array.Fill(_sounding, NotPlaying);
    }

    public static double NormalizedFor(int semitones) => (semitones + 24) / 48.0;

    protected override void ValidateChannels(int inputChannels, int outputChannels)
    {
    }

    protected override void HandleEvent(in PluginEvent e, OutputEventList output)
    {
        var shift = (int)Math.Round(Value(SemitonesParameter) * 48) - 24;
        switch (e.Kind)
        {
            case PluginEventKind.NoteOn when e.Data2 > 0:
                {
                    var note = e.Data1 + shift;
                    var key = (e.Channel * 128) + e.Data1;
                    if (note is >= 0 and <= 127)
                    {
                        _sounding[key] = note;
                        output.Add(e.WithData1(note));
                    }
                    else
                    {
                        _sounding[key] = NotPlaying;
                    }

                    break;
                }

            case PluginEventKind.NoteOn:
            case PluginEventKind.NoteOff:
                {
                    var key = (e.Channel * 128) + e.Data1;
                    var note = _sounding[key];
                    _sounding[key] = NotPlaying;
                    if (note != NotPlaying)
                    {
                        output.Add(e.WithData1(note));
                    }

                    break;
                }

            case PluginEventKind.PolyPressure:
                {
                    var note = e.Data1 + shift;
                    if (note is >= 0 and <= 127)
                    {
                        output.Add(e.WithData1(note));
                    }

                    break;
                }

            default:
                output.Add(e);
                break;
        }
    }

    protected override void Render(in ProcessArgs args, int start, int end)
    {
        for (var c = 0; c < OutputChannels; c++)
        {
            args.Output.Slice((c * args.Frames) + start, end - start).Clear();
        }
    }
}
