using System.Collections.Immutable;
using Bluestone.Plugins.Protocol;
using Bluestone.Plugins.Protocol.Exchange;
using Bluestone.PluginWorker.Plugins;

namespace Bluestone.PluginWorker;

/// <summary>
/// Serves <see cref="RenderEvents"/>: a fresh copy of the plugin, given the request's state and parameters, runs over
/// the timeline in blocks. Live instances are not touched, so rendering never disturbs what they hold (notes, state).
/// </summary>
internal static class OfflineRender
{
    private const int OutputCapacityPerBlock = 4096;

    public static ProtocolMessage Run(RenderEvents request)
    {
        if (request.Plugin.Kind != PluginKind.MidiEffect)
        {
            return new ErrorReply(ErrorCode.NotSupported, $"{request.Plugin.DisplayName} is not a MIDI effect; only MIDI effects are rendered offline.");
        }

        var plugin = HostedPlugins.Create(request.Plugin, request.ModulePath, out var createError);
        if (plugin is null)
        {
            return new ErrorReply(ErrorCode.PluginNotFound, createError ?? $"Plugin {request.Plugin} is not available in this worker.");
        }

        try
        {
            return Render(plugin, request);
        }
        finally
        {
            (plugin as IDisposable)?.Dispose();
        }
    }

    private static ProtocolMessage Render(IHostedPlugin plugin, RenderEvents request)
    {
        try
        {
            plugin.Prepare(request.SampleRate, request.BlockFrames, 0, 0);
        }
        catch (NotSupportedException ex)
        {
            return new ErrorReply(ErrorCode.NotSupported, ex.Message);
        }

        string? stateError = null;
        if (request.State is { } state)
        {
            try
            {
                plugin.LoadState(state);
            }
            catch (InvalidDataException ex)
            {
                stateError = ex.Message;
            }
        }

        foreach (var parameter in request.Parameters)
        {
            plugin.TrySetParameter(parameter.Id, parameter.Value);
        }

        try
        {
            var (events, dropped) = Process(plugin, request);
            return new RenderedEvents(events, dropped, stateError);
        }
#pragma warning disable CA1031 // A plugin failure must become an error reply, whatever it is.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new ErrorReply(ErrorCode.PluginError, $"{request.Plugin.DisplayName} failed while rendering: {ex.Message}");
        }
    }

    private static (ImmutableArray<TimelineEvent> Events, int Dropped) Process(IHostedPlugin plugin, RenderEvents request)
    {
        // Stable sorts: events at one frame keep the order the host gave them.
        var input = request.Events.OrderBy(e => e.Frame).ToArray();
        var changes = request.Changes.OrderBy(c => c.Frame).ToArray();
        var blockEvents = new PluginEvent[Math.Max(1, input.Length)];
        var blockChanges = new ParameterChange[Math.Max(1, changes.Length)];
        var output = new OutputEventList(OutputCapacityPerBlock);
        var result = ImmutableArray.CreateBuilder<TimelineEvent>();
        var dropped = 0;
        int nextEvent = 0, nextChange = 0, nextTempo = 0;
        var tempo = 120.0;
        for (var start = 0L; start < request.EndFrame; start += request.BlockFrames)
        {
            var frames = (int)Math.Min(request.BlockFrames, request.EndFrame - start);
            var end = start + frames;
            while (nextTempo < request.Tempo.Length && request.Tempo[nextTempo].Frame <= start)
            {
                tempo = request.Tempo[nextTempo++].BeatsPerMinute;
            }

            var eventCount = 0;
            while (nextEvent < input.Length && input[nextEvent].Frame < end)
            {
                blockEvents[eventCount++] = input[nextEvent].Event.WithSampleOffset((int)(input[nextEvent].Frame - start));
                nextEvent++;
            }

            var changeCount = 0;
            while (nextChange < changes.Length && changes[nextChange].Frame < end)
            {
                var change = changes[nextChange++];
                blockChanges[changeCount++] = new ParameterChange(change.ParameterId, (int)(Math.Max(change.Frame, start) - start), change.Value);
            }

            output.Clear();
            plugin.Process(new ProcessArgs(frames, [], [], blockEvents.AsSpan(0, eventCount), blockChanges.AsSpan(0, changeCount), new TransportState(start, tempo, true)), output);
            dropped += output.Dropped;
            foreach (ref readonly var e in output.Events)
            {
                if (result.Count == ProtocolLimits.MaxRenderEvents)
                {
                    dropped++;
                    continue;
                }

                result.Add(new TimelineEvent(start + Math.Clamp(e.SampleOffset, 0, frames - 1), e.WithSampleOffset(0)));
            }
        }

        return (result.ToImmutable(), dropped);
    }
}
