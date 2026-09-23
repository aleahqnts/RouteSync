using Android.Graphics;
using Android.Media;
using FleetWiseMobile.Services;

namespace FleetWiseMobile.Platforms.Android;

/// <summary>
/// Takes a photograph and reduces it without ever holding the full image in memory.
/// </summary>
/// <remarks>
/// A phone camera produces eight to twelve megapixels, which is several megabytes encoded
/// and around forty-eight megabytes decoded. Decoding that and then shrinking it is the
/// obvious approach and the wrong one here: the allocation peaks on a low-end handset at
/// the moment the driver is standing at a fault, and a failure there loses the photograph
/// and possibly the walk-around behind it.
///
/// Instead the bounds are read without decoding, a power of two sampling factor is chosen
/// from them, and the image is decoded already reduced. Peak memory is a few megabytes
/// whatever the camera produces.
/// </remarks>
public class AndroidPhotoCapture : IPhotoCapture
{
    public async Task<byte[]?> CaptureAsync(int maxEdge = 1280, int quality = 75)
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) return null;

            var shot = await MediaPicker.Default.CapturePhotoAsync();
            if (shot is null) return null;

            // The stream is read twice, for the bounds and then for the pixels, and a
            // camera stream is not seekable. Copying once is cheaper than decoding twice.
            byte[] original;
            await using (var source = await shot.OpenReadAsync())
            await using (var copy = new MemoryStream())
            {
                await source.CopyToAsync(copy);
                original = copy.ToArray();
            }

            return Reduce(original, maxEdge, quality);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotoCapture] {ex}");
            return null;
        }
    }

    private static byte[]? Reduce(byte[] original, int maxEdge, int quality)
    {
        // Bounds only. No pixels are allocated, and the returned bitmap is null by design.
        var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(original, 0, original.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0) return null;

        var options = new BitmapFactory.Options
        {
            InSampleSize = SampleSizeFor(bounds.OutWidth, bounds.OutHeight, maxEdge),
        };

        using var decoded = BitmapFactory.DecodeByteArray(original, 0, original.Length, options);
        if (decoded is null) return null;

        // Sampling only halves, so the result is at most twice the target and is brought
        // the rest of the way by an ordinary scale. Both bitmaps exist briefly here, which
        // is affordable because the first one is already small.
        using var sized = ScaleToEdge(decoded, maxEdge);

        // A phone held upright usually records the rotation in the file rather than in the
        // pixels. Re-encoding drops that record, so a photograph that looked upright to the
        // driver arrives on its side unless the rotation is applied to the pixels first.
        using var upright = ApplyOrientation(sized, original);

        using var buffer = new MemoryStream();
        upright.Compress(Bitmap.CompressFormat.Jpeg!, quality, buffer);
        return buffer.ToArray();
    }

    /// <summary>Largest power of two that leaves both sides at or above the target.</summary>
    private static int SampleSizeFor(int width, int height, int maxEdge)
    {
        var sample = 1;
        while (width / (sample * 2) >= maxEdge || height / (sample * 2) >= maxEdge)
            sample *= 2;
        return sample;
    }

    private static Bitmap ScaleToEdge(Bitmap source, int maxEdge)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxEdge) return Bitmap.CreateBitmap(source);

        var ratio = (double)maxEdge / longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        return Bitmap.CreateScaledBitmap(source, width, height, true)!;
    }

    private static Bitmap ApplyOrientation(Bitmap source, byte[] original)
    {
        int orientation;
        try
        {
            using var stream = new MemoryStream(original);
            using var exif = new ExifInterface(stream);
            orientation = exif.GetAttributeInt(
                ExifInterface.TagOrientation, (int)Orientation.Normal);
        }
        catch
        {
            // A file without readable metadata is upright as far as anything here can tell.
            return Bitmap.CreateBitmap(source);
        }

        var degrees = orientation switch
        {
            (int)Orientation.Rotate90 => 90f,
            (int)Orientation.Rotate180 => 180f,
            (int)Orientation.Rotate270 => 270f,
            _ => 0f,
        };
        if (degrees == 0f) return Bitmap.CreateBitmap(source);

        using var matrix = new Matrix();
        matrix.PostRotate(degrees);
        return Bitmap.CreateBitmap(source, 0, 0, source.Width, source.Height, matrix, true)!;
    }
}
