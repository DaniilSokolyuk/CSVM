# Writes an amplified absolute-difference image between two renders and reports what it holds.
# The difference isolates one pass's own contribution, so its checkerboard term says whether that
# pass resolves with a screen-space pattern rather than a smooth field.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$A,
    [Parameter(Mandatory = $true)][string]$B,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Gain = 8,
    [int[]]$Crop = @()
)

$ErrorActionPreference = "Stop"

if (-not ("CsvmDiff" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class CsvmDiff
{
    static byte[] Read(string path, out int w, out int h, out int stride)
    {
        using (Bitmap bmp = new Bitmap(path))
        {
            w = bmp.Width; h = bmp.Height;
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = bd.Stride;
            byte[] buf = new byte[stride * h];
            Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(bd);
            return buf;
        }
    }

    // Returns { changed pixel fraction, mean |dL| over changed pixels, max |dL|, chk2 of dL }.
    public static double[] Run(string a, string b, string outPath, int gain, int cx, int cy, int cw, int ch)
    {
        int w, h, s, w2, h2, s2;
        byte[] pa = Read(a, out w, out h, out s);
        byte[] pb = Read(b, out w2, out h2, out s2);
        if (w != w2 || h != h2) throw new Exception("size mismatch");
        if (cw <= 0) { cx = 0; cy = 0; cw = w; ch = h; }

        double[] dl = new double[cw * ch];
        long changed = 0; double sum = 0, max = 0;
        using (Bitmap outBmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
        {
            BitmapData od = outBmp.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            byte[] ob = new byte[od.Stride * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    int i = (y + cy) * s + (x + cx) * 4;
                    int dr = pa[i + 2] - pb[i + 2], dg = pa[i + 1] - pb[i + 1], db = pa[i] - pb[i];
                    double d = 0.2126 * dr + 0.7152 * dg + 0.0722 * db;
                    dl[y * cw + x] = d;
                    double ad = Math.Abs(d);
                    if (Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db) > 0) { changed++; sum += ad; if (ad > max) max = ad; }
                    int o = y * od.Stride + x * 4;
                    ob[o]     = (byte)Math.Min(255, Math.Abs(db) * gain);
                    ob[o + 1] = (byte)Math.Min(255, Math.Abs(dg) * gain);
                    ob[o + 2] = (byte)Math.Min(255, Math.Abs(dr) * gain);
                    ob[o + 3] = 255;
                }
            Marshal.Copy(ob, 0, od.Scan0, ob.Length);
            outBmp.UnlockBits(od);
            outBmp.Save(outPath, ImageFormat.Png);
        }

        double chk = 0; long n = 0;
        for (int y = 0; y + 1 < ch; y += 2)
            for (int x = 0; x + 1 < cw; x += 2)
            {
                chk += Math.Abs(dl[y * cw + x] - dl[y * cw + x + 1] - dl[(y + 1) * cw + x] + dl[(y + 1) * cw + x + 1]);
                n++;
            }

        return new double[] { (double)changed / (cw * (double)ch), changed > 0 ? sum / changed : 0, max, n > 0 ? chk / n : 0 };
    }
}
"@ -ReferencedAssemblies System.Drawing
}

$c = if ($Crop.Count -eq 4) { $Crop } else { @(0, 0, 0, 0) }
$r = [CsvmDiff]::Run((Resolve-Path $A).Path, (Resolve-Path $B).Path, $Out, $Gain, $c[0], $c[1], $c[2], $c[3])
"{0}  vs  {1}" -f (Split-Path $A -Leaf), (Split-Path $B -Leaf)
"   changed={0:P2}  mean|dL|={1:F3}  max|dL|={2:F0}  chk2(dL)={3:F3}  -> {4}" -f $r[0], $r[1], $r[2], $r[3], (Split-Path $Out -Leaf)
