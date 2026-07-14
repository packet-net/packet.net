namespace Packet.Rig.Flrig.Tests;

public class FlrigMetersTests
{
    [Theory]
    [InlineData(0.0, 1.0)]     // anchors from hamlib flrig.c's swrtbl
    [InlineData(10.5, 1.5)]
    [InlineData(23.0, 2.0)]
    [InlineData(35.0, 2.5)]
    [InlineData(48.0, 3.0)]
    [InlineData(100.0, 10.0)]
    public void Interpolation_Hits_The_Hamlib_Anchor_Points(double meter, double swr)
        => FlrigMeters.InterpolateSwr(meter).Should().Be(swr);

    [Theory]
    [InlineData(5.25, 1.3)]   // midpoints, rounded to 0.1 exactly as hamlib rounds
    [InlineData(29.0, 2.3)]
    [InlineData(74.0, 6.5)]
    public void Interpolation_Is_Piecewise_Linear_Rounded_To_Tenths(double meter, double swr)
        => FlrigMeters.InterpolateSwr(meter).Should().BeApproximately(swr, 0.001);

    [Fact]
    public void Off_The_Table_Reads_As_Infinite()
        => FlrigMeters.InterpolateSwr(150).Should().Be(10.0);
}
