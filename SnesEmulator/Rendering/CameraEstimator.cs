using System;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace SnesEmulator.Rendering;

// Reconstructs the game's view-projection matrix from IGameGui.WorldToScreen
// projections via a direct-linear-transform least squares solve.  No game
// signatures or struct offsets — WorldToScreen is a stable Dalamud API, so
// this survives patches that would break a memory-read camera.
public static class CameraEstimator
{
    // 15 unknowns: all view-proj entries except M34, which is fixed to 1
    // (the perspective row maps z into w).
    public static bool TryEstimate(IGameGui gameGui, Vector2 displaySize, Vector3 origin, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (displaySize.X < 64 || displaySize.Y < 64)
            return false;

        // Candidate world points on rings around the origin, biased forward.
        Span<float> distances = stackalloc float[] { 2f, 4f, 8f, 16f };
        var rows = 0;
        var a = new double[64, 15]; // up to 32 points × 2 equations
        var b = new double[64];

        for (var d = 0; d < distances.Length; d++)
        {
            for (var k = 0; k < 8; k++)
            {
                var angle = k * MathF.PI / 4f + d * 0.3f;
                var p = origin + new Vector3(
                    MathF.Sin(angle) * distances[d],
                    (d - 1.5f) * 1.5f,
                    MathF.Cos(angle) * distances[d]);

                if (!gameGui.WorldToScreen(p, out var sp))
                    continue;
                if (sp.X < -100 || sp.X > displaySize.X + 100 || sp.Y < -100 || sp.Y > displaySize.Y + 100)
                    continue;

                var x = p.X; var y = p.Y; var z = p.Z;
                var u = 2.0 * sp.X / displaySize.X - 1.0;
                var v = 1.0 - 2.0 * sp.Y / displaySize.Y;

                // u equation: r1·p − u·r4·p = 0, with M34 = 1 moved to RHS.
                var r = rows;
                a[r, 0] = x; a[r, 1] = y; a[r, 2] = z; a[r, 3] = 1;
                a[r, 12] = -u * x; a[r, 13] = -u * y; a[r, 14] = -u;
                b[r] = u * z;
                rows++;

                r = rows;
                a[r, 4] = x; a[r, 5] = y; a[r, 6] = z; a[r, 7] = 1;
                a[r, 12] = -v * x; a[r, 13] = -v * y; a[r, 14] = -v;
                b[r] = v * z;
                rows++;

                if (rows >= 60)
                    goto done;
            }
        }
done:
        if (rows < 10)
            return false;

        if (!SolveLeastSquares(a, b, rows, out var s))
            return false;

        var m = new Matrix4x4(
            (float)s[0], (float)s[4], (float)s[8], (float)s[12],
            (float)s[1], (float)s[5], (float)s[9], (float)s[13],
            (float)s[2], (float)s[6], (float)s[10], 1f,
            (float)s[3], (float)s[7], (float)s[11], (float)s[14]);

        // Validate: reproject a held-out point and check pixel error.
        var test = origin + new Vector3(3f, 1f, 5f);
        if (!Project(m, test, displaySize, out var tp) || !gameGui.WorldToScreen(test, out var tpReal))
            return false;
        if (Vector2.Distance(tp, tpReal) > 4f)
            return false;

        matrix = m;
        return true;
    }

    private static bool Project(Matrix4x4 m, Vector3 p, Vector2 displaySize, out Vector2 screen)
    {
        // DLT solves column-vector convention (clip = m·p); Numerics Transform
        // is row-vector, so transpose.
        var clip = Vector4.Transform(new Vector4(p, 1f), Matrix4x4.Transpose(m));
        if (MathF.Abs(clip.W) < 1e-6f)
        {
            screen = Vector2.Zero;
            return false;
        }

        var u = clip.X / clip.W;
        var v = clip.Y / clip.W;
        screen = new Vector2((u + 1f) * 0.5f * displaySize.X, (1f - v) * 0.5f * displaySize.Y);
        return true;
    }

    // Normal equations + Gaussian elimination with partial pivoting.
    private static bool SolveLeastSquares(double[,] a, double[] b, int rows, out double[] x)
    {
        x = new double[15];
        var ata = new double[15, 15];
        var atb = new double[15];

        for (var r = 0; r < rows; r++)
        {
            for (var i = 0; i < 15; i++)
            {
                var ar = a[r, i];
                if (ar == 0) continue;
                atb[i] += ar * b[r];
                for (var j = i; j < 15; j++)
                    ata[i, j] += ar * a[r, j];
            }
        }

        for (var i = 0; i < 15; i++)
            for (var j = 0; j < i; j++)
                ata[i, j] = ata[j, i];

        // Elimination.
        for (var c = 0; c < 15; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < 15; r++)
                if (Math.Abs(ata[r, c]) > Math.Abs(ata[pivot, c]))
                    pivot = r;
            if (Math.Abs(ata[pivot, c]) < 1e-9)
                return false;

            if (pivot != c)
            {
                for (var j = 0; j < 15; j++)
                    (ata[c, j], ata[pivot, j]) = (ata[pivot, j], ata[c, j]);
                (atb[c], atb[pivot]) = (atb[pivot], atb[c]);
            }

            for (var r = c + 1; r < 15; r++)
            {
                var f = ata[r, c] / ata[c, c];
                for (var j = c; j < 15; j++)
                    ata[r, j] -= f * ata[c, j];
                atb[r] -= f * atb[c];
            }
        }

        for (var r = 14; r >= 0; r--)
        {
            var sum = atb[r];
            for (var j = r + 1; j < 15; j++)
                sum -= ata[r, j] * x[j];
            x[r] = sum / ata[r, r];
        }

        return true;
    }
}
