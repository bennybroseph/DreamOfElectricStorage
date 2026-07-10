using System;
using System.Collections.Generic;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// Deterministic sibling circle packing: front-chain placement (Wang et al. 2006, the
/// d3-hierarchy packSiblings algorithm) + smallest enclosing circle (Welzl, with a
/// seeded LCG shuffle so results are reproducible). Circles are packed tightly in
/// input order — sort biggest-first for the densest result. After packing, the
/// enclosing circle is centered at the origin.
/// </summary>
public static class CirclePacker
{
    public struct Circle
    {
        public double X, Y, R;
        public Circle(double r) { X = 0; Y = 0; R = r; }
        public Circle(double x, double y, double r) { X = x; Y = y; R = r; }
    }

    /// <summary>
    /// Packs <paramref name="circles"/> in place around the origin; returns the radius
    /// of the smallest enclosing circle. <paramref name="padding"/> guarantees at least
    /// that gap between any two circles (the returned radius includes the halo).
    /// </summary>
    public static double Pack(Circle[] circles, double padding = 0)
    {
        if (circles.Length == 0)
            return 0;

        double halo = padding / 2;
        if (halo > 0)
        {
            for (int i = 0; i < circles.Length; i++)
                circles[i].R += halo;
        }

        double enclosing = PackEnclose(circles);

        if (halo > 0)
        {
            for (int i = 0; i < circles.Length; i++)
                circles[i].R -= halo;
        }
        return enclosing;
    }

    private sealed class FrontNode(int index)
    {
        public readonly int I = index;
        public FrontNode Next = null!, Prev = null!;
    }

    private static double PackEnclose(Circle[] c)
    {
        int n = c.Length;

        c[0].X = 0; c[0].Y = 0;
        if (n == 1)
            return c[0].R;

        c[0].X = -c[1].R; c[1].X = c[0].R; c[1].Y = 0;
        if (n == 2)
            return c[0].R + c[1].R;

        Place(in c[1], in c[0], ref c[2]);

        var a = new FrontNode(0);
        var b = new FrontNode(1);
        var third = new FrontNode(2);
        a.Next = third.Prev = b;
        b.Next = a.Prev = third;
        third.Next = b.Prev = a;

        for (int i = 3; i < n; i++)
        {
        retry:
            Place(in c[a.I], in c[b.I], ref c[i]);

            // Walk the front outward from the a/b gap; a collision means the chain
            // section between the collider and the gap is now interior — cut it out
            // and retry this circle against the shortened front.
            FrontNode j = b.Next, k = a.Prev;
            double sj = c[b.I].R, sk = c[a.I].R;
            do
            {
                if (sj <= sk)
                {
                    if (Intersects(in c[j.I], in c[i]))
                    {
                        b = j;
                        a.Next = b; b.Prev = a;
                        goto retry;
                    }
                    sj += c[j.I].R;
                    j = j.Next;
                }
                else
                {
                    if (Intersects(in c[k.I], in c[i]))
                    {
                        a = k;
                        a.Next = b; b.Prev = a;
                        goto retry;
                    }
                    sk += c[k.I].R;
                    k = k.Prev;
                }
            } while (j != k.Next);

            var inserted = new FrontNode(i) { Prev = a, Next = b };
            a.Next = b.Prev = b = inserted;

            // Re-anchor the front at the pair whose weighted midpoint is nearest the
            // origin, so growth stays centered.
            double best = Score(a, c);
            FrontNode t = b;
            while ((t = t.Next) != b)
            {
                double score = Score(t, c);
                if (score < best)
                {
                    a = t;
                    best = score;
                }
            }
            b = a.Next;
        }

        // Only the front chain can touch the enclosing circle.
        var front = new List<Circle> { c[b.I] };
        FrontNode w = b;
        while ((w = w.Next) != b)
            front.Add(c[w.I]);
        Circle e = Enclose(front);

        for (int i = 0; i < n; i++)
        {
            c[i].X -= e.X;
            c[i].Y -= e.Y;
        }
        return e.R;
    }

    /// <summary>Places c tangent to both a and b, on the outside of the a→b front edge.</summary>
    private static void Place(in Circle b, in Circle a, ref Circle c)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double d2 = dx * dx + dy * dy;
        if (d2 > 0)
        {
            double a2 = a.R + c.R; a2 *= a2;
            double b2 = b.R + c.R; b2 *= b2;
            if (a2 > b2)
            {
                double x = (d2 + b2 - a2) / (2 * d2);
                double y = Math.Sqrt(Math.Max(0, b2 / d2 - x * x));
                c.X = b.X - x * dx - y * dy;
                c.Y = b.Y - x * dy + y * dx;
            }
            else
            {
                double x = (d2 + a2 - b2) / (2 * d2);
                double y = Math.Sqrt(Math.Max(0, a2 / d2 - x * x));
                c.X = a.X + x * dx - y * dy;
                c.Y = a.Y + x * dy + y * dx;
            }
        }
        else
        {
            c.X = a.X + c.R;
            c.Y = a.Y;
        }
    }

    private static bool Intersects(in Circle a, in Circle b)
    {
        double dr = a.R + b.R - 1e-6;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return dr > 0 && dr * dr > dx * dx + dy * dy;
    }

    private static double Score(FrontNode node, Circle[] c)
    {
        ref Circle a = ref c[node.I];
        ref Circle b = ref c[node.Next.I];
        double ab = a.R + b.R;
        double dx = (a.X * b.R + b.X * a.R) / ab;
        double dy = (a.Y * b.R + b.Y * a.R) / ab;
        return dx * dx + dy * dy;
    }

    // --- smallest enclosing circle of circles (Welzl / move-to-front, à la d3-enclose) ---

    private static Circle Enclose(List<Circle> circles)
    {
        var arr = circles.ToArray();

        // Deterministic LCG shuffle — expected O(n) and reproducible across runs.
        uint seed = 1;
        for (int m = arr.Length; m > 1; )
        {
            seed = seed * 1664525u + 1013904223u;
            int idx = (int)(seed / 4294967296.0 * m--);
            (arr[m], arr[idx]) = (arr[idx], arr[m]);
        }

        int i = 0;
        var basis = new List<Circle>();
        Circle? e = null;
        while (i < arr.Length)
        {
            Circle p = arr[i];
            if (e is { } current && EnclosesWeak(current, p))
            {
                i++;
            }
            else
            {
                basis = ExtendBasis(basis, p);
                e = EncloseBasis(basis);
                i = 0;
            }
        }
        return e!.Value;
    }

    private static List<Circle> ExtendBasis(List<Circle> basis, Circle p)
    {
        if (EnclosesWeakAll(p, basis))
            return [p];

        for (int i = 0; i < basis.Count; i++)
        {
            if (EnclosesNot(p, basis[i]) && EnclosesWeakAll(EncloseBasis2(basis[i], p), basis))
                return [basis[i], p];
        }

        for (int i = 0; i < basis.Count - 1; i++)
        {
            for (int j = i + 1; j < basis.Count; j++)
            {
                if (EnclosesNot(EncloseBasis2(basis[i], basis[j]), p)
                    && EnclosesNot(EncloseBasis2(basis[i], p), basis[j])
                    && EnclosesNot(EncloseBasis2(basis[j], p), basis[i])
                    && EnclosesWeakAll(EncloseBasis3(basis[i], basis[j], p), basis))
                {
                    return [basis[i], basis[j], p];
                }
            }
        }

        throw new InvalidOperationException("enclosing basis construction failed");
    }

    private static bool EnclosesNot(in Circle a, in Circle b)
    {
        double dr = a.R - b.R;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return dr < 0 || dr * dr < dx * dx + dy * dy;
    }

    private static bool EnclosesWeak(in Circle a, in Circle b)
    {
        double dr = a.R - b.R + Math.Max(a.R, Math.Max(b.R, 1)) * 1e-9;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        return dr > 0 && dr * dr > dx * dx + dy * dy;
    }

    private static bool EnclosesWeakAll(in Circle a, List<Circle> basis)
    {
        foreach (Circle b in basis)
        {
            if (!EnclosesWeak(a, b))
                return false;
        }
        return true;
    }

    private static Circle EncloseBasis(List<Circle> basis) => basis.Count switch
    {
        1 => basis[0],
        2 => EncloseBasis2(basis[0], basis[1]),
        _ => EncloseBasis3(basis[0], basis[1], basis[2]),
    };

    private static Circle EncloseBasis2(in Circle a, in Circle b)
    {
        double x21 = b.X - a.X, y21 = b.Y - a.Y, r21 = b.R - a.R;
        double l = Math.Sqrt(x21 * x21 + y21 * y21);
        return new Circle(
            (a.X + b.X + x21 / l * r21) / 2,
            (a.Y + b.Y + y21 / l * r21) / 2,
            (l + a.R + b.R) / 2);
    }

    private static Circle EncloseBasis3(in Circle a, in Circle b, in Circle c)
    {
        double a2 = a.X - b.X, a3 = a.X - c.X;
        double b2 = a.Y - b.Y, b3 = a.Y - c.Y;
        double c2 = b.R - a.R, c3 = c.R - a.R;
        double d1 = a.X * a.X + a.Y * a.Y - a.R * a.R;
        double d2 = d1 - b.X * b.X - b.Y * b.Y + b.R * b.R;
        double d3 = d1 - c.X * c.X - c.Y * c.Y + c.R * c.R;
        double ab = a3 * b2 - a2 * b3;
        double xa = (b2 * d3 - b3 * d2) / (ab * 2) - a.X;
        double xb = (b3 * c2 - b2 * c3) / ab;
        double ya = (a3 * d2 - a2 * d3) / (ab * 2) - a.Y;
        double yb = (a2 * c3 - a3 * c2) / ab;
        double qa = xb * xb + yb * yb - 1;
        double qb = 2 * (a.R + xa * xb + ya * yb);
        double qc = xa * xa + ya * ya - a.R * a.R;
        double r = -(Math.Abs(qa) > 1e-6 ? (qb + Math.Sqrt(qb * qb - 4 * qa * qc)) / (2 * qa) : qc / qb);
        return new Circle(a.X + xa + xb * r, a.Y + ya + yb * r, r);
    }
}
