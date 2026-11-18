using FleetWise.Services;
using Xunit;

namespace RouteSyncWeb.Tests;

public class ProfilePhotoTests
{
    [Fact]
    public void Url_carries_the_object_name_as_its_version()
    {
        Assert.Equal("/Profile/Photo/7?v=0123456789abcdef0123456789abcdef",
            ProfilePhotoStore.Url(7, "7/0123456789abcdef0123456789abcdef.jpg"));
    }

    [Fact]
    public void No_photo_has_no_url()
    {
        Assert.Null(ProfilePhotoStore.Url(7, null));
        Assert.Null(ProfilePhotoStore.Url(7, ""));
    }

    [Fact]
    public void Only_jpeg_bytes_are_accepted()
    {
        Assert.True(ProfilePhotoStore.IsJpeg(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.False(ProfilePhotoStore.IsJpeg(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.False(ProfilePhotoStore.IsJpeg(new byte[] { 0xFF, 0xD8 }));
    }
}
