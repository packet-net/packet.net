using System.Globalization;
using System.IO.Compression;

namespace Packet.Aprs.Corpus;

/// <summary>
/// Appends timestamped raw lines to one gzip file per UTC hour:
/// <c>{outDir}/{yyyy-MM}/aprsis-{yyyyMMdd-HH}.txt.gz</c>. Each record is
/// <c>{unix-ms}\t{raw line bytes}\n</c>; the raw bytes are written untouched (no decoding),
/// so a record can hold any byte except CR and LF. A file is named <c>.partial</c> while
/// open and renamed when its hour closes; a crash leaves a <c>.partial</c> whose content is
/// recoverable up to the last periodic flush.
/// </summary>
internal sealed class HourlyGzipWriter(string outDir) : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    private FileStream? file;
    private GZipStream? gzip;
    private string? partialPath;
    private DateTime currentHour;
    private DateTime lastFlush;

    public void Write(DateTime receivedUtc, ReadOnlySpan<byte> rawLine)
    {
        var hour = new DateTime(receivedUtc.Year, receivedUtc.Month, receivedUtc.Day, receivedUtc.Hour, 0, 0, DateTimeKind.Utc);
        if (gzip is null || hour != currentHour)
        {
            Close();
            Open(hour);
        }

        Span<byte> prefix = stackalloc byte[24];
        long ms = new DateTimeOffset(receivedUtc).ToUnixTimeMilliseconds();
        ms.TryFormat(prefix, out int n, provider: CultureInfo.InvariantCulture);
        prefix[n++] = (byte)'\t';
        gzip!.Write(prefix[..n]);
        gzip.Write(rawLine);
        gzip.WriteByte((byte)'\n');

        if (receivedUtc - lastFlush >= FlushInterval)
        {
            gzip.Flush();
            lastFlush = receivedUtc;
        }
    }

    public void Close()
    {
        if (gzip is null)
        {
            return;
        }

        gzip.Dispose();
        file!.Dispose();
        File.Move(partialPath!, partialPath![..^".partial".Length], overwrite: false);
        gzip = null;
        file = null;
        partialPath = null;
    }

    public void Dispose() => Close();

    private void Open(DateTime hour)
    {
        string dir = Path.Combine(outDir, hour.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        string finalPath = Path.Combine(dir, $"aprsis-{hour.ToString("yyyyMMdd-HH", CultureInfo.InvariantCulture)}.txt.gz");

        // A restart within the same hour must not clobber what that hour already captured.
        string stem = finalPath;
        for (int i = 1; File.Exists(stem) || File.Exists(stem + ".partial"); i++)
        {
            stem = finalPath.Replace(".txt.gz", $".{i}.txt.gz", StringComparison.Ordinal);
        }

        partialPath = stem + ".partial";
        file = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        currentHour = hour;
        lastFlush = DateTime.UtcNow;
    }
}
