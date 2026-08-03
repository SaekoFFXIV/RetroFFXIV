using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace SnesEmulator.Rendering;

// Reconstructs the game's view-projection matrix from IGameGui.WorldToScreen
// projections via a direct-linear-transform least squares solve.  No game
// signatures or struct offsets — WorldToScreen is a stable Dalamud API, so
// this survives patches that would break a memory-read camera.
//
// 2D correspondences only observe rows 1, 2 and 4 (the depth row never
// affects x/y), so this returns the matrix with row 3 zeroed; the renderer
// calibrates row 3 separately against the live depth buffer.
public static class CameraEstimator
{
    public static string LastDiagnostic { get; private set; } = "";

    public static bool TryEstimate(IGameGui gameGui, Vector2 displaySize, Vector3 origin, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.Identity;
        if (displaySize.X < 64 || displaySize.Y < 64)
        {
            LastDiagnostic = "display too small";
            return false;
        }

        // Sample rings of world points around the origin; keep whatever
        // WorldToScreen accepts (points behind the camera are rejected).
        var pts = new List<(Vector3 P, double U, double V)>();
        float[] distances = { 1.5f, 3f, 6f, 12f };
        float[] heights = { -1f, 0f, 1f, 2f };

        for (var d = 0; d < distances.Length; d++)
        {
            for (var h = 0; h < heights.Length; h++)
            {
                for (var k = 0; k < 12; k++)
                {
                    var angle = k * MathF.PI / 6f + d * 0.35f;
                    var p = origin + new Vector3(
                        MathF.Sin(angle) * distances[d],
                        heights[h],
                        MathF.Cos(angle) * distances[d]);

                    if (!gameGui.WorldToScreen(p, out var sp))
                        continue;
                    if (sp.X < -50 || sp.X > displaySize.X + 50 || sp.Y < -50 || sp.Y > displaySize.Y + 50)
                        continue;

                    pts.Add((p, 2.0 * sp.X / displaySize.X - 1.0, 1.0 - 2.0 * sp.Y / displaySize.Y));
                    if (pts.Count >= 64)
                        goto done;
                }
            }
        }
done:
        if (pts.Count < 10)
        {
            LastDiagnostic = $"only {pts.Count} visible sample points";
            return false;
        }

        // 11 unknowns: row1 (0-3), row2 (4-7), row4 as M14(8), M24(9), M44(10).
        // Row4's z component (M43) is fixed to 1 as the scale anchor.
        var rows = pts.Count * 2;
        var a = new double[rows, 11];
        var b = new double[rows];

        for (var i = 0; i < pts.Count; i++)
        {
            var (p, u, v) = pts[i];
            var x = p.X; var y = p.Y; var z = p.Z;

            a[2 * i, 0] = x; a[2 * i, 1] = y; a[2 * i, 2] = z; a[2 * i, 3] = 1;
            a[2 * i, 8] = -u * x; a[2 * i, 9] = -u * y; a[2 * i, 10] = -u;
            b[2 * i] = u * z;

            a[2 * i + 1, 4] = x; a[2 * i + 1, 5] = y; a[2 * i + 1, 6] = z; a[2 * i + 1, 7] = 1;
            a[2 * i + 1, 8] = -v * x; a[2 * i + 1, 9] = -v * y; a[2 * i + 1, 10] = -v;
            b[2 * i + 1] = v * z;
        }

        if (!SolveLeastSquares(a, b, rows, 11, out var s))
        {
            LastDiagnostic = "least-squares solve singular";
            return false;
        }

        var m = new Matrix4x4(
            (float)s[0], (float)s[4], 0f, (float)s[8],
            (float)s[1], (float)s[5], 0f, (float)s[9],
            0f, 0f, 0f, 1f,
            (float)s[3], (float)s[7], 0f, (float)s[10]);

        // Self-validate: reproject the input points (x/y only) and require
        // small pixel residuals.
        var worst = 0.0;
        foreach (var (p, u, v) in pts)
        {
            var cx = s[0] * p.X + s[1] * p.Y + s[2] * p.Z + s[3];
            var cy = s[4] * p.X + s[5] * p.Y + s[6] * p.Z + s[7];
            var cw = s[8] * p.X + s[9] * p.Y + p.Z + s[10];
            if (Math.Abs(cw) < 1e-9)
            {
                LastDiagnostic = "degenerate w in validation";
                return false;
            }

            var pu = (cx / cw + 1) * 0.5 * displaySize.X;
            var pv = (1 - cy / cw) * 0.5 * displaySize.Y;
            var eu = (u + 1) * 0.5 * displaySize.X - pu;
            var ev = (1 - v) * 0.5 * displaySize.Y - pv;
            worst = Math.Max(worst, Math.Sqrt(eu * eu + ev * ev));
        }

        if (worst > 3.0)
        {
            LastDiagnostic = $"reprojection error {worst:F1}px";
            return false;
        }

        LastDiagnostic = $"ok ({pts.Count} pts, {worst:F2}px)";
        matrix = m;
        return true;
    }

    // Normal equations + Gaussian elimination with partial pivoting.
    private static bool SolveLeastSquares(double[,] a, double[] b, int rows, int n, out double[] x)
    {
        x = new double[n];
        var ata = new double[n, n];
        var atb = new double[n];

        for (var r = 0; r < rows; r++)
        {
            for (var i = 0; i < n; i++)
            {
                var ar = a[r, i];
                if (ar == 0) continue;
                atb[i] += ar * b[r];
                for (var j = i; j < n; j++)
                    ata[i, j] += ar * a[r, j];
            }
        }

        for (var i = 0; i < n; i++)
            for (var j = 0; j < i; j++)
                ata[i, j] = ata[j, i];

        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
                if (Math.Abs(ata[r, c]) > Math.Abs(ata[pivot, c]))
                    pivot = r;
            if (Math.Abs(ata[pivot, c]) < 1e-9)
                return false;

            if (pivot != c)
            {
                for (var j = 0; j < n; j++)
                    (ata[c, j], ata[pivot, j]) = (ata[pivot, j], ata[c, j]);
                (atb[c], atb[pivot]) = (atb[pivot], atb[c]);
            }

            for (var r = c + 1; r < n; r++)
            {
                var f = ata[r, c] / ata[c, c];
                for (var j = c; j < n; j++)
                    ata[r, j] -= f * ata[c, j];
                atb[r] -= f * atb[c];
            }
        }

        for (var r = n - 1; r >= 0; r--)
        {
            var sum = atb[r];
            for (var j = r + 1; j < n; j++)
                sum -= ata[r, j] * x[j];
            x[r] = sum / ata[r, r];
        }

        return true;
    }
}
