# Reads a PNG and reports high-frequency energy over a rectangle, in luminance units (0-255).
#   chk2  mean |L(0,0) - L(1,0) - L(0,1) + L(1,1)| over disjoint 2x2 blocks. A buffer resolved at
#         half resolution and upsampled puts its noise exactly here, so this is the pattern measure.
#   lap   mean |4*L - (left+right+up+down)|, general high-frequency energy including real detail.
#   sd    luminance standard deviation over the rectangle, the contrast the two ride on.
# Rectangle is given as x,y,w,h in pixels, or as fractions with -Frac.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [Parameter(Mandatory = $true)][int[]]$Rect,
    [switch]$Frac,
    [string]$Label = ""
)

$ErrorActionPreference = "Stop"

if (-not ("CsvmHf" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CsvmHf
{
    public static double[] Measure(string path, double rx, double ry, double rw, double rh, bool frac)
    {
        using (Bitmap bmp = new Bitmap(path))
        {
            int W = bmp.Width, H = bmp.Height;
            int x0 = (int)(frac ? rx * W : rx);
            int y0 = (int)(frac ? ry * H : ry);
            int w  = (int)(frac ? rw * W : rw);
            int h  = (int)(frac ? rh * H : rh);
            if (x0 < 1) x0 = 1;
            if (y0 < 1) y0 = 1;
            if (x0 + w > W - 1) w = W - 1 - x0;
            if (y0 + h > H - 1) h = H - 1 - y0;

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = bd.Stride;
            byte[] buf = new byte[stride * H];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(bd);

            double[] lum = new double[W * H];
            for (int y = 0; y < H; y++)
            {
                int row = y * stride;
                for (int x = 0; x < W; x++)
                {
                    int i = row + x * 4;
                    lum[y * W + x] = 0.2126 * buf[i + 2] + 0.7152 * buf[i + 1] + 0.0722 * buf[i];
                }
            }

            double chk = 0.0; long chkN = 0;
            for (int y = y0; y + 1 < y0 + h; y += 2)
                for (int x = x0; x + 1 < x0 + w; x += 2)
                {
                    double a = lum[y * W + x], b = lum[y * W + x + 1];
                    double c = lum[(y + 1) * W + x], d = lum[(y + 1) * W + x + 1];
                    chk += Math.Abs(a - b - c + d); chkN++;
                }

            double lap = 0.0, sum = 0.0, sum2 = 0.0; long n = 0;
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                {
                    double p = lum[y * W + x];
                    lap += Math.Abs(4.0 * p - lum[y * W + x - 1] - lum[y * W + x + 1]
                                            - lum[(y - 1) * W + x] - lum[(y + 1) * W + x]);
                    sum += p; sum2 += p * p; n++;
                }

            double mean = sum / n;
            double var = (sum2 / n) - (mean * mean);
            if (var < 0) var = 0;
            return new double[] { chkN > 0 ? chk / chkN : 0.0, lap / n, Math.Sqrt(var), mean, n };
        }
    }
}
"@ -ReferencedAssemblies System.Drawing
}

foreach ($p in $Path) {
    $r = [CsvmHf]::Measure((Resolve-Path $p).Path, $Rect[0], $Rect[1], $Rect[2], $Rect[3], [bool]$Frac)
    "{0,-42} chk2={1,7:F3}  lap={2,7:F3}  sd={3,7:F3}  mean={4,7:F2}  px={5}" -f `
        (Split-Path $p -Leaf), $r[0], $r[1], $r[2], $r[3], [long]$r[4]
}
if ($Label) { "  ($Label)" }
