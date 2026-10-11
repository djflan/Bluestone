using System.Collections.Immutable;
using System.Text.Json;
using Bluestone.Domain.Midi;

namespace Bluestone.Profiles;

/// <summary>Protocol families a profile can claim compatibility with.</summary>
public enum ProtocolFamily
{
    Gm1,
    Gm2,
    Gs,
    Xg,
    Custom,
}

/// <summary>How a profile's claims have been checked.</summary>
public enum VerificationLevel
{
    /// <summary>Compiled from documentation; not checked against an instrument.</summary>
    Unverified,

    /// <summary>Checked against recorded traces or a compatible software instrument.</summary>
    Simulated,

    /// <summary>Checked against the physical instrument.</summary>
    HardwareVerified,
}

/// <summary>What a SysEx template does, which decides how carefully the UI must ask before sending it.</summary>
public enum SysExEffect
{
    /// <summary>Changes one parameter.</summary>
    Parameter,

    /// <summary>Resets some or all of the instrument's state.</summary>
    Reset,

    /// <summary>Transfers bulk data that can overwrite stored settings.</summary>
    Bulk,
}

public enum BankKind
{
    Melodic,
    Drums,
}

/// <summary>Where profile data came from and whether it may be redistributed.</summary>
public sealed record ProfileProvenance(
    ImmutableArray<string> Sources,
    ImmutableArray<string> Contributors,
    string License,
    bool RedistributionConfirmed,
    VerificationLevel Verification,
    string? Notes);

/// <summary>A rule matching a MIDI Identity Reply (universal non-real-time SysEx 06 02).</summary>
/// <param name="ManufacturerId">One or three bytes.</param>
/// <param name="Family">Optional two-byte family code (LSB first, as transmitted).</param>
/// <param name="Member">Optional two-byte member code (LSB first, as transmitted).</param>
public sealed record IdentityMatch(ImmutableArray<byte> ManufacturerId, ImmutableArray<byte>? Family, ImmutableArray<byte>? Member);

/// <summary>A program in a bank. <see cref="Program"/> holds the wire value; JSON uses one-based numbers.</summary>
public sealed record ProfileProgram(ProgramNumber Program, string Name, string? Category);

/// <summary>A bank addressed by optional bank MSB/LSB values.</summary>
public sealed record ProfileBank(string Id, string Name, BankKind Kind, SevenBitValue? Msb, SevenBitValue? Lsb, ImmutableArray<ProfileProgram> Programs)
{
    /// <summary>Selects <paramref name="program"/> in this bank. Encoding it for a protocol is the MIDI layer's job.</summary>
    public ProgramSelection Select(ProgramNumber program) => new(program, Msb, Lsb);
}

public sealed record DrumNote(NoteNumber Note, string Name);

/// <summary>A drum kit: its bank and program, and the instrument on each note.</summary>
public sealed record DrumKit(string Id, string Name, string? BankId, ProgramNumber Program, ImmutableArray<DrumNote> Notes);

public sealed record ProfileController(ControllerNumber Number, string Name, SevenBitValue? Default);

/// <summary>A registered parameter (RPN) the instrument responds to.</summary>
public sealed record ProfileParameter(SevenBitValue Msb, SevenBitValue Lsb, string Name, int Min, int Max, int? Default);

public sealed record TemplateParameter(string Name, int Min, int Max, int Default);

public sealed record InitializationStep(string TemplateId, TimeSpan DelayAfter);

/// <summary>An immutable, validated device profile: what an instrument understands, never where it is connected.</summary>
public sealed record DeviceProfile
{
    public const int CurrentSchemaVersion = 1;

    public required string Id { get; init; }

    public required string Version { get; init; }

    public required string Name { get; init; }

    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? Description { get; init; }

    /// <summary>Trademark and non-affiliation notices to show wherever the profile is described.</summary>
    public ImmutableArray<string> Notices { get; init; } = [];

    public required ProfileProvenance Provenance { get; init; }

    public ImmutableArray<ProtocolFamily> Protocols { get; init; } = [];

    public ImmutableArray<IdentityMatch> Identity { get; init; } = [];

    /// <summary>Channels that play drum kits by default.</summary>
    public ImmutableArray<MidiChannel> DrumChannels { get; init; } = [];

    public ImmutableArray<ProfileBank> Banks { get; init; } = [];

    public ImmutableArray<DrumKit> DrumKits { get; init; } = [];

    public ImmutableArray<ProfileController> Controllers { get; init; } = [];

    public ImmutableArray<ProfileParameter> Parameters { get; init; } = [];

    public ImmutableArray<SysExTemplate> Templates { get; init; } = [];

    /// <summary>Named parameters and the MIDI that sets each, for automating them (ADR 0031).</summary>
    public ImmutableArray<ParameterMapping> ParameterMap { get; init; } = [];

    /// <summary>Messages to send only when the user explicitly asks to initialize the instrument.</summary>
    public ImmutableArray<InitializationStep> Initialization { get; init; } = [];

    public ImmutableArray<string> Limitations { get; init; } = [];

    /// <summary>Namespaced extension data, preserved but not interpreted.</summary>
    public ImmutableDictionary<string, JsonElement> Extensions { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    public ProfileBank? FindBank(string id) => Banks.FirstOrDefault(b => b.Id == id);

    public SysExTemplate? FindTemplate(string id) => Templates.FirstOrDefault(t => t.Id == id);

    public ParameterMapping? FindMapping(string id) => ParameterMap.FirstOrDefault(m => m.Id == id);
}
