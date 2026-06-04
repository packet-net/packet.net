using AwesomeAssertions;
using Packet.NetRom.Routing;
using Xunit;

namespace Packet.NetRom.Tests.Routing;

public class NetRomQualityTests
{
    [Theory]
    // (broadcastQuality, pathQuality) -> (a*b + 128) / 256, rounded
    [InlineData(255, 255, 254)]   // best × best
    [InlineData(0, 200, 0)]       // zero advertised → zero
    [InlineData(200, 0, 0)]       // zero path → zero
    [InlineData(192, 192, 144)]   // 36864 + 128 = 36992 / 256 = 144.5 → 144
    [InlineData(128, 128, 64)]    // 16384 + 128 = 16512 / 256 = 64.5 → 64
    public void Combine_matches_the_canonical_formula(byte bq, byte pq, int expected)
    {
        NetRomQuality.Combine(bq, pq).Should().Be((byte)expected);
    }

    [Fact]
    public void Worked_example_two_hops_of_a_200_quality_link_is_about_156()
    {
        // Research doc: a 200-quality direct link, two hops = 200×200/256 ≈ 156.
        // (200*200 + 128) / 256 = 40128 / 256 = 156.75 → 156.
        NetRomQuality.Combine(200, 200).Should().Be(156);
    }

    [Fact]
    public void Worked_example_three_hops_last_link_128_is_about_78()
    {
        // Research doc: three hops (last link 128) ≈ 78. The two-hop value (156)
        // combined with a 128 link: (156*128 + 128) / 256 = 19968 / 256 = 78.
        var twoHop = NetRomQuality.Combine(200, 200);   // 156
        NetRomQuality.Combine(twoHop, 128).Should().Be(78);
    }

    [Fact]
    public void Combine_is_monotonic_quality_never_increases_with_an_extra_hop()
    {
        // A hop can only attenuate: for any path quality < 255 (i.e. not a perfect
        // link), combining reduces the advertised quality. This is the loop-safety
        // invariant — quality decreases per hop.
        for (int bq = 1; bq <= 255; bq++)
        {
            for (int pq = 1; pq < 255; pq++)
            {
                NetRomQuality.Combine((byte)bq, (byte)pq)
                    .Should().BeLessThanOrEqualTo((byte)bq,
                        $"a hop at path quality {pq} must not raise advertised quality {bq}");
            }
        }
    }

    [Fact]
    public void Combine_result_is_always_a_valid_byte()
    {
        for (int bq = 0; bq <= 255; bq++)
        {
            for (int pq = 0; pq <= 255; pq++)
            {
                var q = NetRomQuality.Combine((byte)bq, (byte)pq);
                q.Should().BeInRange((byte)NetRomQuality.Min, (byte)NetRomQuality.Max);
            }
        }
    }
}
