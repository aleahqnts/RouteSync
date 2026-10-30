namespace FleetWiseMobile.Services;

// Takes a photograph with the device camera and returns it small enough to upload.
//
// The size is not a preference. The storage bucket refuses anything over half a megabyte,
// and a camera hands back several, so a photograph that has not been reduced is a
// photograph that cannot be sent.
public interface IPhotoCapture
{
    // Opens the camera and returns the photograph as JPEG bytes, reduced to
    // maxEdge on its longer side.
    // Returns null when the driver backed out, when the camera is unavailable, or when permission
    // was refused. None of those is an error: a photograph is optional, and an inspection
    // carries on without one.
    Task<byte[]?> CaptureAsync(int maxEdge = 1280, int quality = 75);
}

// Reduction through the cross-platform imaging stack, used by the desktop development
// build.
//
// Decodes the whole image before reducing it, which costs tens of megabytes for a modern
// camera. That is acceptable on a laptop and is the reason Android does not use this
// path: the phones this runs on are chosen for being cheap, and the allocation lands at
// the moment a driver is standing at a fault.
public class MauiPhotoCapture : IPhotoCapture
{
    public async Task<byte[]?> CaptureAsync(int maxEdge = 1280, int quality = 75)
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) return null;

            var shot = await MediaPicker.Default.CapturePhotoAsync();
            if (shot is null) return null;

            await using var source = await shot.OpenReadAsync();
            var image = Microsoft.Maui.Graphics.Platform.PlatformImage.FromStream(source);
            using var reduced = image.Downsize(maxEdge, true);

            using var buffer = new MemoryStream();
            await reduced.SaveAsync(buffer, Microsoft.Maui.Graphics.ImageFormat.Jpeg, quality / 100f);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotoCapture] {ex}");
            return null;
        }
    }
}
