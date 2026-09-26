using System.Reflection;

namespace Packet.Aprs.Tests;

/// <summary>
/// The two presets. What each flag tolerates, and that turning it off alone rejects the defect, is
/// checked case by case in the language-neutral vectors (<c>spec/aprs/cases</c>, run by
/// <c>Vectors/AprsVectorTests</c>).
/// </summary>
public class ParseOptionsTests
{
    [Fact]
    public void Strict_turns_every_flag_off_and_lenient_turns_every_flag_on()
    {
        foreach (PropertyInfo p in typeof(AprsParseOptions).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            ((bool)p.GetValue(AprsParseOptions.Strict)!).Should().BeFalse(p.Name);
            ((bool)p.GetValue(AprsParseOptions.Lenient)!).Should().BeTrue(p.Name);
        }
    }
}
