using System.Text;
using Packet.Tait.Codeplug;

namespace Packet.Tait.Codeplug.Tests;

/// <summary>
/// A mock Tait radio over the <see cref="ISerialLine"/> seam: it recognises a written command
/// (bare <c>^</c>/<c>#</c>, or CR-terminated otherwise), looks up a scripted reply, and hands the
/// reply back on subsequent reads - exactly the lock-step the real radio drives. Every command it
/// sees is recorded so a test can assert the transmitted sequence.
/// </summary>
internal sealed class ScriptedRadio : ISerialLine
{
    private readonly IReadOnlyDictionary<string, string> _replies;
    private readonly Func<string, string?>? _fallback;
    private readonly Queue<byte> _rx = new();
    private readonly StringBuilder _pending = new();

    public ScriptedRadio(IReadOnlyDictionary<string, string> replies, Func<string, string?>? fallback = null)
    {
        _replies = replies;
        _fallback = fallback;
    }

    public List<string> CommandsSeen { get; } = new();

    public string PortName => "mock";

    public int Read(byte[] buffer, int offset, int count)
    {
        if (_rx.Count == 0)
        {
            throw new TimeoutException("scripted radio has nothing queued");
        }

        int n = 0;
        while (n < count && _rx.Count > 0)
        {
            buffer[offset + n++] = _rx.Dequeue();
        }

        return n;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            char c = (char)buffer[offset + i];
            if (c == '\r')
            {
                Complete(_pending.ToString());
                _pending.Clear();
            }
            else if (_pending.Length == 0 && (c == '^' || c == '#'))
            {
                Complete(c.ToString());
            }
            else
            {
                _pending.Append(c);
            }
        }
    }

    public void SetBaudRate(int baudRate)
    {
        // no-op for the mock
    }

    public void Dispose()
    {
        // nothing to release
    }

    private void Complete(string command)
    {
        CommandsSeen.Add(command);
        if (!_replies.TryGetValue(command, out string? reply))
        {
            reply = _fallback?.Invoke(command)
                ?? throw new InvalidOperationException($"scripted radio got an unexpected command: '{command}'");
        }

        foreach (byte b in Encoding.ASCII.GetBytes(reply))
        {
            _rx.Enqueue(b);
        }
    }
}
