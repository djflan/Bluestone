using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>
/// <c>reference.sine</c>: a deterministic polyphonic sine instrument with <see cref="Polyphony"/> voices, a linear
/// <see cref="AttackFrames"/> attack and <see cref="ReleaseFrames"/> release. A voice's peak is
/// <c>Level * velocity / 127 * 0.5</c>. All outputs carry the same signal. When every voice is busy the oldest is stolen.
/// </summary>
internal sealed class SinePlugin : ReferencePlugin
{
    public const string PluginId = "reference.sine";
    public const uint LevelParameter = 0;
    public const int Polyphony = 8;
    public const int AttackFrames = 64;
    public const int ReleaseFrames = 256;

    private readonly Voice[] _voices = new Voice[Polyphony];
    private long _noteCounter;

    public SinePlugin()
        : base(CreateIdentity(PluginId, "Reference Sine", PluginKind.Instrument), [new ParameterDescriptor(LevelParameter, "Level", 0.5)])
    {
    }

    /// <summary>Voices currently sounding, including releasing ones.</summary>
    public int ActiveVoices => _voices.Count(v => v.Active);

    public static double Frequency(int note) => 440.0 * Math.Pow(2.0, (note - 69) / 12.0);

    protected override void ValidateChannels(int inputChannels, int outputChannels)
    {
        if (inputChannels != 0 || outputChannels < 1)
        {
            throw new NotSupportedException("Reference Sine has no audio inputs and needs at least one output.");
        }
    }

    protected override void HandleEvent(in PluginEvent e, OutputEventList output)
    {
        switch (e.Kind)
        {
            case PluginEventKind.NoteOn when e.Data2 > 0:
                StartVoice(e.Channel, e.Data1, e.Data2);
                break;
            case PluginEventKind.NoteOn:
            case PluginEventKind.NoteOff:
                for (var i = 0; i < _voices.Length; i++)
                {
                    ref var voice = ref _voices[i];
                    if (voice.Active && !voice.Releasing && voice.Channel == e.Channel && voice.Note == e.Data1)
                    {
                        voice.Releasing = true;
                        voice.ReleaseStartLevel = voice.Envelope;
                        voice.ReleaseAge = 0;
                    }
                }

                break;
            case PluginEventKind.ControlChange when e.Data1 is 120 or 123:
                // All Sound Off / All Notes Off.
                Array.Clear(_voices);
                break;
            default:
                break;
        }
    }

    protected override void Render(in ProcessArgs args, int start, int end)
    {
        var level = Value(LevelParameter);
        var first = args.Output.Slice(start, end - start);
        first.Clear();
        for (var v = 0; v < _voices.Length; v++)
        {
            ref var voice = ref _voices[v];
            if (!voice.Active)
            {
                continue;
            }

            var peak = level * voice.Velocity / 127.0 * 0.5;
            for (var i = 0; i < first.Length && voice.Active; i++)
            {
                first[i] += (float)(Math.Sin(voice.Phase) * voice.Envelope * peak);
                voice.Phase += voice.Increment;
                if (voice.Phase >= 2 * Math.PI)
                {
                    voice.Phase -= 2 * Math.PI;
                }

                Advance(ref voice);
            }
        }

        for (var c = 1; c < OutputChannels; c++)
        {
            first.CopyTo(args.Output.Slice((c * args.Frames) + start, end - start));
        }
    }

    private static void Advance(ref Voice voice)
    {
        if (voice.Releasing)
        {
            voice.ReleaseAge++;
            if (voice.ReleaseAge >= ReleaseFrames)
            {
                voice = default;
                return;
            }

            voice.Envelope = voice.ReleaseStartLevel * (1.0 - ((double)voice.ReleaseAge / ReleaseFrames));
        }
        else
        {
            voice.Age++;
            voice.Envelope = Math.Min(1.0, (double)voice.Age / AttackFrames);
        }
    }

    private void StartVoice(byte channel, byte note, byte velocity)
    {
        var index = -1;
        for (var i = 0; i < _voices.Length; i++)
        {
            if (!_voices[i].Active)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            index = 0;
            for (var i = 1; i < _voices.Length; i++)
            {
                if (_voices[i].Started < _voices[index].Started)
                {
                    index = i;
                }
            }
        }

        _voices[index] = new Voice
        {
            Active = true,
            Channel = channel,
            Note = note,
            Velocity = velocity,
            Increment = 2 * Math.PI * Frequency(note) / SampleRate,
            Started = ++_noteCounter,
        };
    }

    private struct Voice
    {
        public bool Active;
        public bool Releasing;
        public byte Channel;
        public byte Note;
        public byte Velocity;
        public double Phase;
        public double Increment;
        public double Envelope;
        public double ReleaseStartLevel;
        public int Age;
        public int ReleaseAge;
        public long Started;
    }
}
