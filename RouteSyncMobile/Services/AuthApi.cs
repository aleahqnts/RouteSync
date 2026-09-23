using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FleetWiseMobile.Models;

namespace FleetWiseMobile.Services;

/// <summary>
/// Client for the sign-in, password change and password reset edge functions.
/// </summary>
/// <remarks>
/// These are the only paths a password travels, always over TLS, and it is verified and
/// hashed server-side.
///
/// Results are three-way rather than a boolean so a caller can tell an unreachable
/// function apart from a definitive rejection, and never treats the two the same way.
/// </remarks>
public class AuthApi
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>
    /// Ok, a definite refusal, or neither.
    /// </summary>
    /// <remarks>
    /// Refused and Unreachable are kept apart so a caller never treats a server that
    /// answered as one that could not be found. Failed splits off a server that answered
    /// with a fault of its own, which is neither the caller's doing nor the network's.
    /// </remarks>
    public enum Outcome { Ok, Denied, Unreachable, Failed }

    public record LoginResult(Outcome Outcome, string? Token, UserModel? User, string? Message);
    public record CallResult(Outcome Outcome, string? Message);
    public record ResetTokenResult(Outcome Outcome, string? Token, string? Message);

    public async Task<LoginResult> LoginAsync(string email, string password)
    {
        try
        {
            var res = await PostAsync("auth-login", new { email, password });
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var token = doc.RootElement.GetProperty("token").GetString();
                var user = MapUser(doc.RootElement.GetProperty("user"));
                if (token is null || user is null)
                    return new(Outcome.Unreachable, null, null, null);
                return new(Outcome.Ok, token, user, null);
            }

            // A 400, 401 or 429 is a decision by the server, not a transport failure.
            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                or HttpStatusCode.TooManyRequests)
                return new(Outcome.Denied, null, null, ErrorOf(body) ?? "Invalid email or password.");

            return new(Outcome.Unreachable, null, null, null); // 404/5xx: fn not there yet
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.Login] {ex.Message}");
            return new(Outcome.Unreachable, null, null, null);
        }
    }

    public async Task<CallResult> ChangePasswordAsync(string oldPassword, string newPassword)
    {
        if (SupabaseConfig.Jwt is null) return new(Outcome.Unreachable, null);
        try
        {
            var res = await PostAsync("change-password",
                new { old_password = oldPassword, new_password = newPassword },
                SupabaseConfig.Jwt);
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode) return new(Outcome.Ok, null);
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return new(Outcome.Denied, ErrorOf(body) ?? "Password change rejected.");
            return new(Outcome.Unreachable, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.ChangePwd] {ex.Message}");
            return new(Outcome.Unreachable, null);
        }
    }

    /// <summary>
    /// Asks for a reset code to be mailed to the address on the account.
    /// </summary>
    /// <remarks>
    /// A success here means the request was accepted, not that an account exists.
    /// The function deliberately answers the same way for an address it has never
    /// seen, so the app must not report anything more specific than "check your
    /// mail" either.
    /// </remarks>
    public async Task<CallResult> RequestPasswordResetAsync(string email)
    {
        try
        {
            var res = await PostAsync("password-reset-request", new { email });
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode) return new(Outcome.Ok, null);
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)
                return new(Outcome.Denied, ErrorOf(body) ?? "Reset request rejected.");
            return new(Outcome.Unreachable, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.ResetRequest] {ex.Message}");
            return new(Outcome.Unreachable, null);
        }
    }

    /// <summary>
    /// Exchanges a mailed code for a short-lived token that authorises one password change.
    /// </summary>
    public async Task<ResetTokenResult> VerifyResetCodeAsync(string email, string otp)
    {
        try
        {
            var res = await PostAsync("password-reset-verify", new { email, otp });
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var token = doc.RootElement.TryGetProperty("reset_token", out var t) ? t.GetString() : null;
                return token is null
                    ? new(Outcome.Unreachable, null, null)
                    : new(Outcome.Ok, token, null);
            }

            if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                or HttpStatusCode.TooManyRequests)
                return new(Outcome.Denied, null, ErrorOf(body) ?? "That code is invalid or has expired.");

            return new(Outcome.Unreachable, null, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.ResetVerify] {ex.Message}");
            return new(Outcome.Unreachable, null, null);
        }
    }

    /// <summary>
    /// Sets the new password using the token from <see cref="VerifyResetCodeAsync"/>.
    /// </summary>
    /// <remarks>
    /// No session comes back. The driver signs in with the new password, which keeps
    /// the reset path from being a second way to obtain a driver token.
    /// </remarks>
    public async Task<CallResult> CompletePasswordResetAsync(string resetToken, string newPassword)
    {
        try
        {
            var res = await PostAsync("password-reset-complete",
                new { reset_token = resetToken, new_password = newPassword });
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode) return new(Outcome.Ok, null);
            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return new(Outcome.Denied, ErrorOf(body) ?? "Password reset rejected.");
            return new(Outcome.Unreachable, null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.ResetComplete] {ex.Message}");
            return new(Outcome.Unreachable, null);
        }
    }

    public record InspectionResult(Outcome Outcome, string? Status, bool Blocked,
        List<string> Failed, List<string> Critical, string? Message);

    /// <summary>
    /// A photograph already in storage, named for the item it documents.
    /// </summary>
    /// <remarks>
    /// The object is uploaded when it is taken, so what travels with the inspection is its
    /// name rather than its bytes. The server confirms the object is there before
    /// recording it, since a name the phone believed in is not evidence that anything
    /// arrived.
    /// </remarks>
    /// <remarks>
    /// TakenAt is null for a photograph attached before the app was interrupted and
    /// resumed, since the draft holds the reference and not the moment. The server records
    /// arrival time in its place.
    /// </remarks>
    public record PhotoRef(int ItemId, string ObjectKey, DateTimeOffset? TakenAt);

    /// <summary>
    /// Sends a completed inspection for recording.
    /// </summary>
    /// <remarks>
    /// The server decides which faults ground the bus and grounds it, because the app
    /// holds no write on that gate and because a build that answered the question itself
    /// could drive away from a failed brake.
    /// </remarks>
    public async Task<InspectionResult> SubmitInspectionAsync(
        string tripId, Dictionary<string, string> results, string? notes,
        List<PhotoRef>? photos = null)
    {
        if (SupabaseConfig.Jwt is null)
            return new(Outcome.Unreachable, null, false, new(), new(), null);
        try
        {
            var res = await PostAsync("inspection-submit",
                new
                {
                    trip_id = tripId,
                    results,
                    notes,
                    photos = (photos ?? new()).Select(p => new
                    {
                        item_id = p.ItemId,
                        object_key = p.ObjectKey,
                        taken_at = p.TakenAt?.ToString("o"),
                    }),
                },
                SupabaseConfig.Jwt);
            var body = await res.Content.ReadAsStringAsync();

            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                return new(Outcome.Ok,
                    Str(root, "status"),
                    root.TryGetProperty("blocked", out var b) && b.GetBoolean(),
                    Strings(root, "failed"),
                    Strings(root, "critical"),
                    null);
            }

            if (res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                return new(Outcome.Denied, null, false, new(), new(),
                    ErrorOf(body) ?? "The inspection was not accepted.");

            // The server answered and something went wrong at its end, which is worth
            // saying plainly rather than sending a driver to check their signal.
            if ((int)res.StatusCode >= 500)
                return new(Outcome.Failed, null, false, new(), new(),
                    ErrorOf(body) ?? "The server could not record this inspection.");

            return new(Outcome.Unreachable, null, false, new(), new(), null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthApi.Inspection] {ex.Message}");
            return new(Outcome.Unreachable, null, false, new(), new(), null);
        }
    }

    private static List<string> Strings(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
           ? v.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
           : new();

    private static async Task<HttpResponseMessage> PostAsync(string fn, object body, string? jwt = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{SupabaseConfig.FunctionsUrl}/{fn}");
        req.Headers.TryAddWithoutValidation("apikey", SupabaseConfig.Key);
        if (jwt is not null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    // The function returns the users row in snake_case, without the password hash.
    private static UserModel? MapUser(JsonElement u)
    {
        try
        {
            return new UserModel
            {
                UserId = u.GetProperty("user_id").GetInt32(),
                FirstName = Str(u, "first_name"),
                MiddleName = Str(u, "middle_name"),
                LastName = Str(u, "last_name"),
                EmailAddress = Str(u, "email_address"),
                RoleId = u.TryGetProperty("role_id", out var r) ? r.GetInt32() : 0,
                AccountStatus = Str(u, "account_status"),
                ContactNumber = Str(u, "contact_number"),
                Address = Str(u, "address"),
                EmergencyContactName = Str(u, "emergency_contact_name"),
                EmergencyContactNumber = Str(u, "emergency_contact_number"),
                CreatedAt = Date(u, "created_at") ?? default,
                UpdatedAt = Date(u, "updated_at"),
                LastLogin = Date(u, "last_login"),
            };
        }
        catch { return null; }
    }

    private static string? Str(JsonElement u, string name)
        => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Date(JsonElement u, string name)
        => u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), out var d) ? d : null;
}
