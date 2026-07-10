using DreamOfElectricStorage.Core;

namespace DreamOfElectricStorage.Core.Tests;

public class CirclePackerTests
{
    private static CirclePacker.Circle[] Circles(params double[] radii) =>
        radii.Select(r => new CirclePacker.Circle(r)).ToArray();

    private static void AssertNoOverlap(CirclePacker.Circle[] circles, double minGap = 0)
    {
        for (int i = 0; i < circles.Length; i++)
        {
            for (int j = i + 1; j < circles.Length; j++)
            {
                double dx = circles[i].X - circles[j].X;
                double dy = circles[i].Y - circles[j].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double required = circles[i].R + circles[j].R + minGap;
                Assert.True(dist >= required - 1e-4,
                    $"circles {i} and {j} overlap: dist {dist:F4} < {required:F4}");
            }
        }
    }

    private static void AssertContained(CirclePacker.Circle[] circles, double enclosing)
    {
        foreach (var c in circles)
        {
            double dist = Math.Sqrt(c.X * c.X + c.Y * c.Y) + c.R;
            Assert.True(dist <= enclosing + 1e-4,
                $"circle (r={c.R}) sticks out: {dist:F4} > {enclosing:F4}");
        }
    }

    [Fact]
    public void EmptyInputReturnsZero()
    {
        Assert.Equal(0, CirclePacker.Pack([]));
    }

    [Fact]
    public void SingleCircleIsCenteredWithOwnRadius()
    {
        var circles = Circles(10);
        double enclosing = CirclePacker.Pack(circles);

        Assert.Equal(10, enclosing, 3);
        Assert.Equal(0, circles[0].X, 3);
        Assert.Equal(0, circles[0].Y, 3);
    }

    [Fact]
    public void TwoCirclesTouchAndEnclosingSpansBoth()
    {
        var circles = Circles(10, 6);
        double enclosing = CirclePacker.Pack(circles);

        AssertNoOverlap(circles);
        AssertContained(circles, enclosing);
        Assert.Equal(16, enclosing, 3); // touching pair → MEC radius = r1 + r2

        double dx = circles[0].X - circles[1].X;
        double dy = circles[0].Y - circles[1].Y;
        Assert.Equal(16, Math.Sqrt(dx * dx + dy * dy), 3);
    }

    [Fact]
    public void ThreeEqualCirclesFormTriangle()
    {
        var circles = Circles(5, 5, 5);
        double enclosing = CirclePacker.Pack(circles);

        AssertNoOverlap(circles);
        AssertContained(circles, enclosing);
        // Known geometry: three unit circles pack into R = 1 + 2/√3 (scaled by 5).
        Assert.Equal(5 * (1 + 2 / Math.Sqrt(3)), enclosing, 2);
    }

    [Fact]
    public void ManyDescendingCirclesDoNotOverlap()
    {
        var circles = Circles(Enumerable.Range(1, 60).Select(i => (double)(61 - i)).ToArray());
        double enclosing = CirclePacker.Pack(circles);

        AssertNoOverlap(circles);
        AssertContained(circles, enclosing);
    }

    [Fact]
    public void MixedSizesFromSeededRandomDoNotOverlap()
    {
        var random = new Random(42);
        var circles = Circles(Enumerable.Range(0, 200).Select(_ => random.NextDouble() * 30 + 1).ToArray());
        double enclosing = CirclePacker.Pack(circles);

        AssertNoOverlap(circles);
        AssertContained(circles, enclosing);
    }

    [Fact]
    public void PaddingKeepsMinimumGap()
    {
        var circles = Circles(12, 9, 7, 5, 5, 4, 3, 2);
        double enclosing = CirclePacker.Pack(circles, padding: 6);

        AssertNoOverlap(circles, minGap: 6);
        AssertContained(circles, enclosing);
        Assert.Equal(12, circles[0].R, 6); // padding halo removed after packing
    }

    [Fact]
    public void PackingIsDeterministic()
    {
        var first = Circles(20, 15, 15, 10, 8, 8, 8, 5, 3, 2, 1);
        var second = Circles(20, 15, 15, 10, 8, 8, 8, 5, 3, 2, 1);

        double e1 = CirclePacker.Pack(first);
        double e2 = CirclePacker.Pack(second);

        Assert.Equal(e1, e2);
        for (int i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i].X, second[i].X);
            Assert.Equal(first[i].Y, second[i].Y);
        }
    }

    [Fact]
    public void PackIsRoughlyCenteredOnOrigin()
    {
        var circles = Circles(Enumerable.Range(1, 30).Select(i => (double)(31 - i)).ToArray());
        double enclosing = CirclePacker.Pack(circles);

        // Enclosing circle is recentered on the origin — the farthest node must not
        // exceed the enclosing radius, and the pack shouldn't be wildly lopsided.
        AssertContained(circles, enclosing);
        double maxReach = circles.Max(c => Math.Sqrt(c.X * c.X + c.Y * c.Y) + c.R);
        Assert.True(maxReach > enclosing - 1e-3, "enclosing circle should be tight");
    }
}
