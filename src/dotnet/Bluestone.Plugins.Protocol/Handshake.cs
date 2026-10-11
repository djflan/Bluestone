using System.Security.Cryptography;
using System.Text;

namespace Bluestone.Plugins.Protocol;

/// <summary>
/// The Hello handshake. The worker connects and sends <see cref="Hello"/> with the per-launch token the host gave it;
/// the host checks version, token, and mode and answers <see cref="HelloAck"/>. Any mismatch ends the connection.
/// </summary>
public static class Handshake
{
    /// <summary>A fresh random token for one worker launch.</summary>
    public static string CreateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static Hello CreateHello(string token, WorkerMode mode) =>
        new(ProtocolLimits.ProtocolVersion, token, Environment.ProcessId, mode);

    /// <summary>Host side: accepts the worker's Hello or throws <see cref="ProtocolException"/>.</summary>
    public static void ValidateHello(ProtocolMessage message, string expectedToken, WorkerMode expectedMode)
    {
        ArgumentNullException.ThrowIfNull(expectedToken);
        if (message is not Hello hello)
        {
            throw new ProtocolException($"Expected Hello but received {message.Type}.");
        }

        if (hello.ProtocolVersion != ProtocolLimits.ProtocolVersion)
        {
            throw new ProtocolException($"Protocol version mismatch: worker speaks {hello.ProtocolVersion}, host speaks {ProtocolLimits.ProtocolVersion}.");
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello.Token), Encoding.UTF8.GetBytes(expectedToken)))
        {
            throw new ProtocolException("The worker presented the wrong launch token.");
        }

        if (hello.Mode != expectedMode)
        {
            throw new ProtocolException($"The worker started in mode {hello.Mode}, expected {expectedMode}.");
        }
    }

    /// <summary>Worker side: accepts the host's reply or throws <see cref="ProtocolException"/>.</summary>
    public static void ValidateAck(ProtocolMessage message)
    {
        if (message is ErrorReply error)
        {
            throw new ProtocolException($"The host rejected the handshake: {error.Message}");
        }

        if (message is not HelloAck ack)
        {
            throw new ProtocolException($"Expected HelloAck but received {message.Type}.");
        }

        if (ack.ProtocolVersion != ProtocolLimits.ProtocolVersion)
        {
            throw new ProtocolException($"Protocol version mismatch: host speaks {ack.ProtocolVersion}, worker speaks {ProtocolLimits.ProtocolVersion}.");
        }
    }
}
