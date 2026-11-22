using System.Globalization;
using Bluestone.Presentation;

namespace Bluestone.Desktop.Controls;

/// <summary>What a point in a chain carries now, in words, for tooltips and screen readers.</summary>
internal static class SignalDescription
{
    /// <summary>Its label (when it has one), the notes, the level, and where it is also sent, joined with " · ".</summary>
    public static string Describe(string? label, bool carriesNotes, bool notesHeld, bool carriesAudio, MeterLevels? levels, string? routes)
    {
        var parts = new List<string>();
        if (label is not null)
        {
            parts.Add(label);
        }

        if (carriesNotes)
        {
            parts.Add(notesHeld ? "notes held" : "no notes");
        }

        if (carriesAudio)
        {
            var peak = levels is null ? MeterLevels.FloorDecibels : Math.Max(levels.PeakLeft, levels.PeakRight);
            parts.Add(levels is not { IsActive: true } || peak <= MeterLevels.FloorDecibels ? "silent"
                : string.Create(CultureInfo.InvariantCulture, $"peak {peak:0.0} dB{(peak > 0 ? ", clipping" : string.Empty)}"));
        }

        if (!string.IsNullOrEmpty(routes))
        {
            parts.Add($"also sent to {routes}");
        }

        return string.Join(" · ", parts);
    }
}
