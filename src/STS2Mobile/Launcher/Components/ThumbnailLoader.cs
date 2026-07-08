using System;
using System.IO;
using Godot;

namespace STS2Mobile.Launcher.Components;

// Loads a cached image file into a Texture2D by sniffing its magic bytes rather
// than trusting the file extension. WorkshopThumbnailCache names files after the
// preview URL, and Steam CDN preview URLs usually carry no extension, so the
// cache falls back to ".img" — Godot's Image.Load() keys off the extension and
// fails ("unrecognized") on those. Detecting the real format from the header and
// using the matching LoadXxxFromBuffer makes loading extension-independent.
public static class ThumbnailLoader
{
    public static Texture2D LoadTexture(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12)
                return null;

            var image = new Image();
            Error err;

            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                err = image.LoadJpgFromBuffer(bytes);
            else if (
                bytes[0] == 0x89
                && bytes[1] == 0x50
                && bytes[2] == 0x4E
                && bytes[3] == 0x47
            )
                err = image.LoadPngFromBuffer(bytes);
            else if (
                bytes[0] == (byte)'R'
                && bytes[1] == (byte)'I'
                && bytes[2] == (byte)'F'
                && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W'
                && bytes[9] == (byte)'E'
                && bytes[10] == (byte)'B'
                && bytes[11] == (byte)'P'
            )
                err = image.LoadWebpFromBuffer(bytes);
            else
                err = image.Load(path); // last resort — lets a correct extension work

            if (err != Error.Ok)
                return null;
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex)
        {
            PatchHelper.Log($"[Workshop] Thumbnail decode failed for {path}: {ex.Message}");
            return null;
        }
    }
}
