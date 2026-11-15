// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Ultra.UI.Services;

/// <summary>
/// The icons of executables (the first icon resource of the file, as Windows Explorer shows them), cached per path.
/// Windows only: elsewhere, and for a file without an icon, there is none.
/// </summary>
internal static partial class ExecutableIcons
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the icon of an executable, extracted once per path.
    /// </summary>
    /// <param name="path">The path of the executable, or <c>null</c>.</param>
    /// <returns>The icon, or <c>null</c> when the file has none or cannot be read.</returns>
    public static Bitmap? GetIcon(string? path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        return Cache.GetOrAdd(path, static p =>
        {
            var pixels = TryGetPixels(p, out var width, out var height);
            return pixels is null ? null : CreateBitmap(pixels, width, height);
        });
    }

    /// <summary>
    /// Extracts the large icon of an executable as top-down BGRA pixels with straight (not premultiplied) alpha.
    /// </summary>
    /// <param name="path">The path of the executable.</param>
    /// <param name="width">The width of the icon, in pixels.</param>
    /// <param name="height">The height of the icon, in pixels.</param>
    /// <returns>The pixels, or <c>null</c> when the file has no (color) icon or cannot be read.</returns>
    internal static unsafe byte[]? TryGetPixels(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr icon;
        if (ExtractIconExW(path, 0, &icon, null, 1) == 0 || icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!GetIconInfo(icon, out var info))
            {
                return null;
            }

            try
            {
                // A monochrome icon has no color bitmap; such icons are not worth showing.
                if (info.hbmColor == IntPtr.Zero)
                {
                    return null;
                }

                BITMAP bitmap;
                if (GetObjectW(info.hbmColor, sizeof(BITMAP), &bitmap) == 0 || bitmap.bmWidth <= 0 || bitmap.bmHeight <= 0)
                {
                    return null;
                }

                var pixels = ReadPixels(info.hbmColor, bitmap.bmWidth, bitmap.bmHeight);
                if (pixels is null)
                {
                    return null;
                }

                // An icon without an alpha channel is transparent where its mask is white.
                var hasAlpha = false;
                for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
                {
                    hasAlpha = pixels[i] != 0;
                }

                if (!hasAlpha)
                {
                    var mask = info.hbmMask != IntPtr.Zero ? ReadPixels(info.hbmMask, bitmap.bmWidth, bitmap.bmHeight) : null;
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        var transparent = mask is not null && mask[i] != 0;
                        pixels[i + 3] = transparent ? (byte)0 : (byte)255;
                    }
                }

                width = bitmap.bmWidth;
                height = bitmap.bmHeight;
                return pixels;
            }
            finally
            {
                if (info.hbmColor != IntPtr.Zero)
                {
                    DeleteObject(info.hbmColor);
                }

                if (info.hbmMask != IntPtr.Zero)
                {
                    DeleteObject(info.hbmMask);
                }
            }
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    private static unsafe byte[]? ReadPixels(IntPtr bitmap, int width, int height)
    {
        // Room for the color table GetDIBits may write after the header, although a 32 bpp output has none.
        var info = stackalloc byte[sizeof(BITMAPINFOHEADER) + 256 * 4];
        var header = (BITMAPINFOHEADER*)info;
        *header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height, // Top-down.
            biPlanes = 1,
            biBitCount = 32,
        };

        var pixels = new byte[width * height * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* bits = pixels)
            {
                return GetDIBits(dc, bitmap, 0, (uint)height, bits, info, 0) == height ? pixels : null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private static unsafe Bitmap? CreateBitmap(byte[] pixels, int width, int height)
    {
        try
        {
            fixed (byte* data = pixels)
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, (IntPtr)data, new PixelSize(width, height), new Vector(96, 96), width * 4);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint ExtractIconExW(string file, int iconIndex, IntPtr* largeIcons, IntPtr* smallIcons, uint icons);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetObjectW(IntPtr gdiObject, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);
}
