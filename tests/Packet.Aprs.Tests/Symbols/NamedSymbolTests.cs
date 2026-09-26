using System.Reflection;

namespace Packet.Aprs.Tests.Symbols;

/// <summary>
/// The named symbols (<c>AprsSymbol.Car</c> and so on): one for every symbol the APRS symbol tables
/// define, none for anything else, and <see cref="AprsSymbol.WithOverlay"/> for the alternate table.
/// </summary>
public class NamedSymbolTests
{
    private static readonly AprsSymbol[] Named = [.. typeof(AprsSymbol)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(p => p.PropertyType == typeof(AprsSymbol))
        .Select(p => (AprsSymbol)p.GetValue(null)!)];

    [Fact]
    public void Every_defined_symbol_has_exactly_one_name()
    {
        var defined = new List<AprsSymbol>();
        foreach (char table in "/\\")
        {
            for (char code = '!'; code <= '~'; code++)
            {
                var symbol = new AprsSymbol(table, code);
                if (symbol.Description is not null)
                {
                    defined.Add(symbol);
                }
            }
        }

        Named.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(defined);
    }

    [Fact]
    public void A_name_gives_the_table_and_code()
    {
        AprsSymbol.Car.Should().Be(new AprsSymbol('/', '>'));
        AprsSymbol.WeatherStation.Should().Be(new AprsSymbol('/', '_'));
        AprsSymbol.DirectionFinding.Should().Be(new AprsSymbol('/', '\\'));
        AprsSymbol.Emergency.Should().Be(new AprsSymbol('\\', '!'));
        AprsSymbol.Hospital.Description.Should().Be("Hospital");
    }

    [Fact]
    public void An_overlay_goes_on_an_alternate_table_symbol()
    {
        AprsSymbol.Gateway.WithOverlay('I').Should().Be(new AprsSymbol('I', '&'));
        AprsSymbol.OverlayDigipeater.WithOverlay('1').ToString().Should().Be("1#");
        AprsSymbol.Gateway.WithOverlay('I').WithOverlay('R').Overlay.Should().Be('R');
    }

    [Fact]
    public void An_overlay_is_refused_on_the_primary_table_or_when_not_0_9_or_A_Z()
    {
        Action primary = () => AprsSymbol.Car.WithOverlay('3');
        primary.Should().Throw<InvalidOperationException>();
        Action lowerCase = () => AprsSymbol.Gateway.WithOverlay('i');
        lowerCase.Should().Throw<ArgumentOutOfRangeException>();
    }
}
