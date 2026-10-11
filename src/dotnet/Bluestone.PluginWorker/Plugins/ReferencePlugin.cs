using System.Buffers.Binary;
using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;

namespace Bluestone.PluginWorker.Plugins;

/// <summary>
/// Shared behaviour of the reference plugins: parameter storage, versioned state, and sample-accurate processing.
/// A block is rendered in segments split at every parameter change and event, so a change at sample offset n
/// affects exactly the samples from n on. At the same offset, parameter changes apply before events.
/// </summary>
internal abstract class ReferencePlugin : IHostedPlugin
{
    public const string Format = "bluestone-reference";
    public const string ModuleId = "bluestone.reference";
    public const string StateFormat = "bluestone.reference-state";
    public const ushort StateVersion = 1;

    private static readonly byte[] StateMagic = "CRPS"u8.ToArray();

    private readonly double[] _values;

    protected ReferencePlugin(PluginIdentity identity, ImmutableArray<ParameterDescriptor> parameters)
    {
        Identity = identity;
        Parameters = parameters;
        _values = new double[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].Id != (uint)i)
            {
                throw new InvalidOperationException("Reference plugin parameter ids must be 0, 1, 2, ...");
            }
        }

        ResetToDefaults();
    }

    public PluginIdentity Identity { get; }

    public ImmutableArray<ParameterDescriptor> Parameters { get; }

    public virtual int LatencyFrames => 0;

    protected double SampleRate { get; private set; } = 48_000;

    protected int InputChannels { get; private set; }

    protected int OutputChannels { get; private set; }

    public static PluginIdentity CreateIdentity(string pluginId, string displayName, PluginKind kind) =>
        new(Format, ModuleId, pluginId, displayName, "Bluestone", kind, "1.0.0");

    public void Prepare(double sampleRate, int maxFrames, int inputChannels, int outputChannels)
    {
        ValidateChannels(inputChannels, outputChannels);
        SampleRate = sampleRate;
        InputChannels = inputChannels;
        OutputChannels = outputChannels;
        OnPrepared(maxFrames);
    }

    public double GetParameter(uint id) => id < (uint)_values.Length ? _values[id] : double.NaN;

    public bool TrySetParameter(uint id, double value)
    {
        if (id >= (uint)_values.Length || !NormalizedValue.IsValid(value))
        {
            return false;
        }

        _values[id] = value;
        return true;
    }

    public void Process(in ProcessArgs args, OutputEventList output)
    {
        var changes = args.ParameterChanges;
        var events = args.Events;
        var nextChange = 0;
        var nextEvent = 0;
        var position = 0;
        while (position < args.Frames)
        {
            while (nextChange < changes.Length && changes[nextChange].SampleOffset <= position)
            {
                TrySetParameter(changes[nextChange].ParameterId, changes[nextChange].Value);
                nextChange++;
            }

            while (nextEvent < events.Length && events[nextEvent].SampleOffset <= position)
            {
                HandleEvent(events[nextEvent], output);
                nextEvent++;
            }

            var end = args.Frames;
            if (nextChange < changes.Length)
            {
                end = Math.Min(end, changes[nextChange].SampleOffset);
            }

            if (nextEvent < events.Length)
            {
                end = Math.Min(end, events[nextEvent].SampleOffset);
            }

            Render(args, position, end);
            position = end;
        }
    }

    public PluginStateData SaveState()
    {
        var bytes = new byte[8 + (_values.Length * 12)];
        StateMagic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), StateVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)_values.Length);
        for (var i = 0; i < _values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (i * 12)), (uint)i);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(12 + (i * 12)), _values[i]);
        }

        return new PluginStateData(StateFormat, [.. bytes]);
    }

    public void LoadState(PluginStateData state)
    {
        ArgumentNullException.ThrowIfNull(state);
        double[] parsed;
        try
        {
            parsed = ParseState(state);
        }
        catch (InvalidDataException)
        {
            ResetToDefaults();
            throw;
        }

        for (var i = 0; i < parsed.Length; i++)
        {
            TrySetParameter((uint)i, parsed[i]);
        }
    }

    protected abstract void ValidateChannels(int inputChannels, int outputChannels);

    protected virtual void OnPrepared(int maxFrames)
    {
    }

    protected virtual void HandleEvent(in PluginEvent e, OutputEventList output)
    {
    }

    /// <summary>Renders frames [<paramref name="start"/>, <paramref name="end"/>) with the current parameter values.</summary>
    protected abstract void Render(in ProcessArgs args, int start, int end);

    protected double Value(uint id) => _values[id];

    private void ResetToDefaults()
    {
        for (var i = 0; i < _values.Length; i++)
        {
            _values[i] = Parameters[i].DefaultValue;
        }
    }

    private double[] ParseState(PluginStateData state)
    {
        if (state.Format != StateFormat)
        {
            throw new InvalidDataException($"Unknown state format '{state.Format}'.");
        }

        var data = state.Data.AsSpan();
        if (data.Length < 8 || !data[..4].SequenceEqual(StateMagic))
        {
            throw new InvalidDataException("The state is not reference-plugin state.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if (version != StateVersion)
        {
            throw new InvalidDataException($"Unknown state version {version}.");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        if (data.Length != 8 + (count * 12))
        {
            throw new InvalidDataException("The state has the wrong length.");
        }

        var values = new double[_values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Parameters[i].DefaultValue;
        }

        for (var i = 0; i < count; i++)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(data[(8 + (i * 12))..]);
            var value = BinaryPrimitives.ReadDoubleLittleEndian(data[(12 + (i * 12))..]);
            if (id >= (uint)values.Length || !NormalizedValue.IsValid(value))
            {
                throw new InvalidDataException($"The state holds an invalid parameter {id} = {value}.");
            }

            values[id] = value;
        }

        return values;
    }
}
