namespace Packet.Ax25.Session;

/// <summary>
/// The <c>packethacking/ax25spec</c> issue a session quirk is named after and works
/// around: the record of the deviation, in the code rather than only in the docs, so
/// the two cannot drift. Every <see cref="Ax25SessionQuirks"/> property whose name
/// starts with <c>Ax25Spec</c> carries one, and a test checks that the number in the
/// name and the number here agree (<c>Ax25SessionQuirksSpecIssueTests</c>).
/// </summary>
/// <remarks>
/// A quirk is deleted, and its attribute with it, when ax25sdl ships figures carrying
/// the issue's resolution; <see cref="RemovalTrackedIn"/> names the packet.net issue
/// for that.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class Ax25SpecIssueAttribute : Attribute
{
    /// <param name="issue">The <c>packethacking/ax25spec</c> issue number.</param>
    public Ax25SpecIssueAttribute(int issue)
    {
        Issue = issue;
    }

    /// <summary>The <c>packethacking/ax25spec</c> issue number.</summary>
    public int Issue { get; }

    /// <summary>The issue's URL.</summary>
    public string Url => $"https://github.com/packethacking/ax25spec/issues/{Issue}";

    /// <summary>The <c>packet-net/packet.net</c> issue that tracks removing the quirk once
    /// the spec resolves it, if one has been opened.</summary>
    public int RemovalTrackedIn { get; init; }
}
