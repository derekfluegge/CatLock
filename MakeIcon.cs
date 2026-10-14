// Writes cat.ico (the cat face at several sizes) for CatLock.exe's file icon. Run by build.cmd.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var bmp = CatWalker.RenderFace(sizes[i], false))
                images[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);

        using (var w = new BinaryWriter(File.Create(args.Length > 0 ? args[0] : "cat.ico")))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);   // ICONDIR
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)                                 // ICONDIRENTRY
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    static byte[] Png(Bitmap bmp)
    {
        using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // 32-bit BGRA DIB with an (unused, all-zero) AND mask - the classic icon image format.
    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width, maskStride = ((s + 31) / 32) * 4;
        var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new byte[s * s * 4];
        for (int y = 0; y < s; y++)   // rows bottom-up
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (s - 1 - y) * data.Stride, pixels, y * s * 4, s * 4);
        bmp.UnlockBits(data);

        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2);            // BITMAPINFOHEADER (height covers XOR + AND)
            w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(pixels.Length + maskStride * s);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            w.Write(pixels);
            w.Write(new byte[maskStride * s]);
            return ms.ToArray();
        }
    }
}
