using System.Security.Cryptography;

namespace FleetWise.Services
{
    /// <summary>The script nonce for one request.</summary>
    /// <remarks>
    /// The content security policy runs only the inline scripts that carry this value, so
    /// a script written into a page by anyone else is refused: they cannot know it. The
    /// middleware in Program.cs puts it in the header and every view reads it from here,
    /// both from the one instance the request holds, so the two always agree.
    /// </remarks>
    public sealed class CspNonce
    {
        public string Value { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    }
}
