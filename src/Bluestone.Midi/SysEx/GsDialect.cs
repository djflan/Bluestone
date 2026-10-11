using System.Collections.Immutable;
using Bluestone.Domain.Midi;
using System.Globalization;

namespace Bluestone.Midi.SysEx;

/// <summary>The parameter blocks of the GS address map that Bluestone distinguishes.</summary>
public enum GsArea
{
    /// <summary><c>00 00 xx</c>: system mode (Sound Canvas models with two part blocks).</summary>
    SystemMode,

    /// <summary><c>40 00 xx</c>: master tune, volume, key shift, pan, and GS Reset.</summary>
    System,

    /// <summary><c>40 01 30</c>–<c>37</c>.</summary>
    Reverb,

    /// <summary><c>40 01 38</c>–<c>3F</c>.</summary>
    Chorus,

    /// <summary>Other <c>40 01 xx</c> common parameters, such as the patch name and voice reserve.</summary>
    Common,

    /// <summary><c>40 1n xx</c> and <c>40 2n xx</c>: part parameters and part controller settings.</summary>
    Part,

    /// <summary><c>41 mp rr</c>: drum map <c>m</c>, parameter <c>p</c>, note <c>rr</c>.</summary>
    DrumSetup,

    /// <summary>Any other address, including model-specific extensions.</summary>
    Other,
}

/// <summary>A three-byte Roland GS address.</summary>
public readonly record struct GsAddress(byte High, byte Mid, byte Low)
{
    public GsArea Area => (High, Mid, Low) switch
    {
        (0x00, 0x00, _) => GsArea.SystemMode,
        (0x40, 0x00, _) => GsArea.System,
        (0x40, 0x01, >= 0x30 and <= 0x37) => GsArea.Reverb,
        (0x40, 0x01, >= 0x38 and <= 0x3F) => GsArea.Chorus,
        (0x40, 0x01, _) => GsArea.Common,
        (0x40, >= 0x10 and <= 0x2F, _) => GsArea.Part,
        (0x41, _, _) => GsArea.DrumSetup,
        _ => GsArea.Other,
    };

    /// <summary>
    /// For part parameters, the one-based part number. GS numbers its part blocks unusually: block 0
    /// is part 10, blocks 1–9 are parts 1–9, and blocks A–F are parts 11–16.
    /// </summary>
    public int? Part => Area == GsArea.Part ? PartOfBlock(Mid & 0x0F) : null;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{High:X2} {Mid:X2} {Low:X2}");

    private static int PartOfBlock(int block) => block == 0 ? 10 : block <= 9 ? block : block + 1;

    internal string Describe() => Area switch
    {
        GsArea.SystemMode or GsArea.System or GsArea.Reverb or GsArea.Chorus or GsArea.Common =>
            string.Create(CultureInfo.InvariantCulture, $"{(Area == GsArea.SystemMode ? "System Mode" : Area.ToString())} parameter {Low:X2}"),
        GsArea.Part => string.Create(CultureInfo.InvariantCulture, $"Part {Part} {(Mid >> 4 == 2 ? "controller " : string.Empty)}parameter {Low:X2}"),
        GsArea.DrumSetup => string.Create(CultureInfo.InvariantCulture, $"Drum Map {(Mid >> 4) + 1} note {Low} parameter {Mid & 0x0F:X2}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"parameter {this}"),
    };
}

/// <summary>A GS data set (DT1) message: <c>F0 41 dev 42 12 aa aa aa dd… cs F7</c>.</summary>
/// <param name="DeviceId">The device ID, normally <c>10</c>.</param>
/// <param name="Address">The address of the first byte written.</param>
/// <param name="Data">The bytes written, without address or checksum.</param>
/// <param name="ChecksumValid">False when the Roland checksum does not match; the message is still preserved and sent as is.</param>
public sealed record GsDataSet(byte DeviceId, GsAddress Address, ImmutableArray<byte> Data, bool ChecksumValid) : SysExInterpretation
{
    /// <summary>GS Reset: <c>40 00 7F 00</c>.</summary>
    public bool IsGsReset => Address is { High: 0x40, Mid: 0x00, Low: 0x7F };

    public override string Summary =>
        (IsGsReset ? "GS Reset" : $"GS {Address.Describe()}") + (ChecksumValid ? string.Empty : " · bad checksum");
}

/// <summary>
/// Roland GS (model <c>42</c>) data set messages. Other Roland models (for example the Sound Canvas
/// display, model <c>45</c>) and data requests are left uninterpreted.
/// </summary>
public static class GsDialect
{
    public const byte Roland = 0x41;
    public const byte Model = 0x42;
    public const byte DataSet = 0x12;

    /// <summary>The Roland checksum of an address and its data: the value that makes their sum 0 (mod 128).</summary>
    public static byte Checksum(ReadOnlySpan<byte> addressAndData) => SysExChecksum.Compute(addressAndData);

    /// <param name="body">The message without <c>F0</c> and <c>F7</c>, starting with <c>41</c>.</param>
    internal static GsDataSet? Interpret(ReadOnlySpan<byte> body)
    {
        // 41, device, model, command, address (3), data (at least 1), checksum.
        if (body.Length < 9 || body[2] != Model || body[3] != DataSet)
        {
            return null;
        }

        var payload = body[4..^1];
        return new GsDataSet(body[1], new GsAddress(body[4], body[5], body[6]), [.. payload[3..]], Checksum(payload) == body[^1]);
    }
}
