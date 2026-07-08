using System.Collections.Concurrent;
using System.Text;

namespace Packet.Radio.Tait.Tests;

/// <summary>Scripted <see cref="ISerialIo"/>: blocking reads against an in-memory queue,
/// with optional canned responses keyed on written command lines.</summary>
internal sealed class FakeSerialIo : ISerialIo
{
    private readonly BlockingCollection<byte[]> incoming = [];
    private readonly ConcurrentDictionary<string, (string Response, Func<bool>? OnlyWhen)> responses = new();
    private readonly StringBuilder written = new();
    private readonly List<byte> writtenBytes = [];
    private readonly List<int> baudRates = [];
    private readonly Lock gate = new();

    public string PortName => "fake";

    public string WrittenAscii
    {
        get
        {
            lock (gate)
            {
                return written.ToString();
            }
        }
    }

    /// <summary>Every byte written so far, in order (for inspecting binary SLIP framing).</summary>
    public byte[] WrittenBytes
    {
        get
        {
            lock (gate)
            {
                return [.. writtenBytes];
            }
        }
    }

    /// <summary>Every baud rate the driver re-clocked the port to, in order.</summary>
    public IReadOnlyList<int> BaudRates
    {
        get
        {
            lock (gate)
            {
                return [.. baudRates];
            }
        }
    }

    private volatile Exception? readFault;

    public void Enqueue(string ascii) => incoming.Add(Encoding.Latin1.GetBytes(ascii));

    public void Enqueue(byte[] bytes) => incoming.Add(bytes);

    /// <summary>Answer every write of <paramref name="commandWithoutCr"/> with
    /// <paramref name="responseAscii"/> — but only while <paramref name="onlyWhen"/> (evaluated at
    /// write time) returns true; while it returns false the command is silently swallowed. A null
    /// gate always answers. The gate models radio state a command can change, e.g. "a radio stuck
    /// in Transparent mode only answers CCDI once the <c>+++</c> escape has been seen".</summary>
    public void RespondTo(string commandWithoutCr, string responseAscii, Func<bool>? onlyWhen = null) =>
        responses[commandWithoutCr] = (responseAscii, onlyWhen);

    /// <summary>Make every subsequent <see cref="Read"/> throw <paramref name="fault"/> —
    /// models a hard IO failure (a dead head-end socket), which faults the driver's pump.</summary>
    public void FailReads(Exception fault) => readFault = fault;

    public int Read(byte[] buffer, int offset, int count)
    {
        if (readFault is { } fault)
        {
            throw fault;
        }
        if (!incoming.TryTake(out var chunk, TimeSpan.FromMilliseconds(25)))
        {
            throw new TimeoutException();
        }
        chunk.CopyTo(buffer.AsSpan(offset, count));
        return chunk.Length;
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        string ascii = Encoding.Latin1.GetString(buffer, offset, count);
        lock (gate)
        {
            written.Append(ascii);
            writtenBytes.AddRange(buffer.AsSpan(offset, count).ToArray());
        }
        if (responses.TryGetValue(ascii.TrimEnd('\r'), out var reply)
            && (reply.OnlyWhen is null || reply.OnlyWhen()))
        {
            Enqueue(reply.Response);
        }
    }

    public void SetBaudRate(int baudRate)
    {
        lock (gate)
        {
            baudRates.Add(baudRate);
        }
    }

    public void Dispose() => incoming.Dispose();
}
