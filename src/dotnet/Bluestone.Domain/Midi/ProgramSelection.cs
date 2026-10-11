namespace Bluestone.Domain.Midi;

/// <summary>
/// Selects a program, optionally within a bank addressed by bank MSB and LSB. This is the musical
/// intent; how it travels (bank select controllers then a program change in MIDI 1.0, or a single
/// program change carrying the bank in MIDI 2.0) is decided by the protocol encoder.
/// </summary>
/// <param name="Program">The program within the bank.</param>
/// <param name="BankMsb">The bank's most significant part, or <see langword="null"/> to leave it unchanged.</param>
/// <param name="BankLsb">The bank's least significant part, or <see langword="null"/> to leave it unchanged.</param>
public readonly record struct ProgramSelection(ProgramNumber Program, SevenBitValue? BankMsb = null, SevenBitValue? BankLsb = null);
