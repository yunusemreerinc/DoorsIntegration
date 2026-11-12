param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\src\DoorsIntegration.Wpf\Assets\doors-integration.png'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\DoorsIntegration.Wpf\Assets\doors-integration.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('DoorsIntegration.IconPackaging' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace DoorsIntegration
{
    public static class IconPackaging
    {
        public static void Convert(string sourcePath, string outputPath)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var frames = new List<byte[]>();
            using (var source = Image.FromFile(sourcePath))
            {
                if (source.Width != source.Height) throw new InvalidDataException("Icon source must be square.");
                foreach (int size in sizes)
                using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(bitmap))
                    using (var attributes = new ImageAttributes())
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0,
                            source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                    }
                    using (var memory = new MemoryStream())
                    {
                        using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                        {
                            int maskStride = ((size + 31) / 32) * 4;
                            writer.Write(40); // BITMAPINFOHEADER; ICO height includes the transparency mask.
                            writer.Write(size); writer.Write(size * 2);
                            writer.Write((ushort)1); writer.Write((ushort)32);
                            writer.Write(0); writer.Write(size * size * 4 + maskStride * size);
                            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                            for (int y = size - 1; y >= 0; y--)
                            for (int x = 0; x < size; x++)
                            {
                                var pixel = bitmap.GetPixel(x, y);
                                writer.Write(pixel.B); writer.Write(pixel.G); writer.Write(pixel.R); writer.Write(pixel.A);
                            }
                            for (int y = size - 1; y >= 0; y--)
                            {
                                var mask = new byte[maskStride];
                                for (int x = 0; x < size; x++)
                                    if (bitmap.GetPixel(x, y).A == 0) mask[x / 8] |= (byte)(0x80 >> (x % 8));
                                writer.Write(mask);
                            }
                        }
                        frames.Add(memory.ToArray());
                    }
                }
            }
            using (var stream = File.Create(outputPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                    writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(frames[i].Length); writer.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var frame in frames) writer.Write(frame);
            }
        }
    }
}
'@
}
[DoorsIntegration.IconPackaging]::Convert([System.IO.Path]::GetFullPath($SourcePath), [System.IO.Path]::GetFullPath($OutputPath))
Write-Output ([System.IO.Path]::GetFullPath($OutputPath))
