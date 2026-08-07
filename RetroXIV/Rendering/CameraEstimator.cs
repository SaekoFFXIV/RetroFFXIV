using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace RetroXIV.Rendering;

// Reconstructs the game's view-projection matrix from IGameGui.WorldToScreen
// projections via a direct-linear-transform least squares solve.  No game
// signatures or struct offsets — WorldToScreen is a stable Dalamud API, so
// this survives patches that would break a memory-read camera.
//
// 2D correspondences only observe clip X, Y and W (clip Z never affects the
// projected pixel), so this returns a System.Numerics row-vector matrix with
// its clip-Z output column zeroed. The renderer calibrates that column
// separately against the live depth buffer.
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

        // Solve the complete homogeneous 3x4 projection (clip X/Y/W).
        // The old fit fixed W's world-Z coefficient to one, which becomes
        // singular when the camera faces mostly along world X. Try each of
        // the 12 coefficients as the scale anchor and retain the fit with the
        // smallest reprojection residual.
        if (!TrySolveProjection(pts, displaySize, out var s, out var worst, out var anchor))
        {
            LastDiagnostic = "projective least-squares solve singular";
            return false;
        }

        // Vector4.Transform(v, m) uses a row vector, so output coordinates
        // occupy matrix columns. Preserve every coefficient solved above;
        // only the clip-Z output column is unknown/zero here.
        var m = new Matrix4x4(
            (float)s[0], (float)s[4], 0f, (float)s[8],
            (float)s[1], (float)s[5], 0f, (float)s[9],
            (float)s[2], (float)s[6], 0f, (float)s[10],
            (float)s[3], (float)s[7], 0f, (float)s[11]);

        if (worst > 3.0)
        {
            LastDiagnostic = $"reprojection error {worst:F1}px";
            return false;
        }

        LastDiagnostic = $"ok ({pts.Count} pts, {worst:F2}px, anchor={anchor})";
        matrix = m;
        return true;
    }

    private static bool TrySolveProjection(
        List<(Vector3 P, double U, double V)> pts,
        Vector2 displaySize,
        out double[] best,
        out double bestWorst,
        out int bestAnchor)
    {
        best = Array.Empty<double>();
        bestWorst = double.PositiveInfinity;
        bestAnchor = -1;
        var rows = pts.Count * 2;

        for (var anchor = 0; anchor < 12; anchor++)
        {
            var a = new double[rows, 11];
            var b = new double[rows];

            for (var i = 0; i < pts.Count; i++)
            {
                var (p, u, v) = pts[i];
                var h = new[] { (double)p.X, p.Y, p.Z, 1.0 };
                var rowX = new double[12];
                var rowY = new double[12];
                for (var j = 0; j < 4; j++)
                {
                    rowX[j] = h[j];
                    rowX[8 + j] = -u * h[j];
                    rowY[4 + j] = h[j];
                    rowY[8 + j] = -v * h[j];
                }

                FillAnchoredRow(rowX, anchor, a, b, 2 * i);
                FillAnchoredRow(rowY, anchor, a, b, 2 * i + 1);
            }

            if (!SolveLeastSquares(a, b, rows, 11, out var solved))
                continue;

            var coefficients = new double[12];
            coefficients[anchor] = 1.0;
            for (int source = 0, destination = 0; destination < coefficients.Length; destination++)
            {
                if (destination == anchor)
                    continue;
                coefficients[destination] = solved[source++];
            }

            // A perspective camera's W world-direction is the unit camera
            // forward vector. This removes DLT's global scale ambiguity and
            // lets the exact near-plane depth constant be applied afterward.
            var wDirectionLength = Math.Sqrt(
                coefficients[8] * coefficients[8]
                + coefficients[9] * coefficients[9]
                + coefficients[10] * coefficients[10]);
            if (wDirectionLength < 1e-8 || double.IsNaN(wDirectionLength))
                continue;
            for (var i = 0; i < coefficients.Length; i++)
                coefficients[i] /= wDirectionLength;

            var negativeW = 0;
            foreach (var (p, _, _) in pts)
            {
                var w = coefficients[8] * p.X + coefficients[9] * p.Y
                    + coefficients[10] * p.Z + coefficients[11];
                if (w < 0)
                    negativeW++;
            }
            if (negativeW > pts.Count / 2)
            {
                for (var i = 0; i < coefficients.Length; i++)
                    coefficients[i] = -coefficients[i];
            }

            var worst = GetWorstReprojectionError(pts, displaySize, coefficients);
            if (worst < bestWorst)
            {
                best = coefficients;
                bestWorst = worst;
                bestAnchor = anchor;
            }
        }

        return bestAnchor >= 0 && !double.IsNaN(bestWorst) && !double.IsInfinity(bestWorst);
    }

    private static void FillAnchoredRow(
        double[] fullRow, int anchor, double[,] a, double[] b, int row)
    {
        b[row] = -fullRow[anchor];
        for (int source = 0, destination = 0; source < fullRow.Length; source++)
        {
            if (source == anchor)
                continue;
            a[row, destination++] = fullRow[source];
        }
    }

    private static double GetWorstReprojectionError(
        List<(Vector3 P, double U, double V)> pts,
        Vector2 displaySize,
        double[] s)
    {
        var worst = 0.0;
        foreach (var (p, u, v) in pts)
        {
            var cx = s[0] * p.X + s[1] * p.Y + s[2] * p.Z + s[3];
            var cy = s[4] * p.X + s[5] * p.Y + s[6] * p.Z + s[7];
            var cw = s[8] * p.X + s[9] * p.Y + s[10] * p.Z + s[11];
            if (Math.Abs(cw) < 1e-9)
                return double.PositiveInfinity;

            var pu = (cx / cw + 1) * 0.5 * displaySize.X;
            var pv = (1 - cy / cw) * 0.5 * displaySize.Y;
            var eu = (u + 1) * 0.5 * displaySize.X - pu;
            var ev = (1 - v) * 0.5 * displaySize.Y - pv;
            worst = Math.Max(worst, Math.Sqrt(eu * eu + ev * ev));
        }

        return worst;
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
