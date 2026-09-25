using System.IO.Compression;

namespace Packet.Aprs.Corpus;

/// <summary>Reads the collector's hourly gzip files: records of <c>{unix-ms}\t{raw line}\n</c>.</summary>
internal static class CorpusReader
{
    public readonly record struct Record(long ReceivedUnixMs, byte[] Line);

    /// <summary>Every packet line (server comments starting <c>#</c> skipped), oldest file first.
    /// A <c>.partial</c> file still being written is read up to its last flush.</summary>
    public static IEnumerable<Record> Read(string corpusDir)
    {
        IEnumerable<string> files = Directory.EnumerateFiles(corpusDir, "aprsis-*.txt.gz*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        foreach (string file in files)
        {
            foreach (Record r in ReadFile(file))
            {
                yield return r;
            }
        }
    }

    public static IEnumerable<Record> ReadFile(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        var line = new List<byte>(512);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int n;
            try
            {
                n = gz.Read(buffer, 0, buffer.Length);
            }
            catch (InvalidDataException)
            {
                yield break; // truncated .partial file: stop at what was flushed
            }
            catch (EndOfStreamException)
            {
                yield break;
            }

            if (n == 0)
            {
                yield break;
            }

            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    line.Add(buffer[i]);
                    continue;
                }

                int tab = line.IndexOf((byte)'\t');
                if (tab > 0 && tab + 1 < line.Count && line[tab + 1] != (byte)'#'
                    && long.TryParse(System.Text.Encoding.ASCII.GetString([.. line.GetRange(0, tab)]), out long ms))
                {
                    yield return new Record(ms, [.. line.GetRange(tab + 1, line.Count - tab - 1)]);
                }

                line.Clear();
            }
        }
    }
}
