using System.Text;

namespace Bluestone.Midi.Files;

/// <summary>
/// Text in MIDI files has no declared encoding. Bluestone reads valid UTF-8 as UTF-8 (which covers
/// ASCII) and anything else as Latin-1, and always writes UTF-8.
/// </summary>
internal static class SmfText
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static byte[] Encode(string text) => Encoding.UTF8.GetBytes(text);
}
