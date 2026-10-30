using System.Net;
using System.Text;
using System.Text.Json;

namespace FleetWise.Services
{
    // Client for the password reset edge functions, shared with the driver app.
    //
    // The reset runs on the edge rather than here so that both surfaces use one
    // implementation, one rate limiter and one audit shape. Codes, hashing and the
    // account lookup never happen in this process.
    //
    // The functions are deployed without gateway JWT verification, since the caller
    // is locked out by definition, so no key is sent with these calls.
    //
    // Results are three-way rather than a boolean so a caller can tell an unreachable
    // function apart from a definitive rejection, and never treats the two the same way.
    public class PasswordResetApi
    {
        private readonly HttpClient _http;
        private readonly string _functionsUrl;

        public PasswordResetApi(HttpClient http, IConfiguration config)
        {
            _http = http;
            _functionsUrl = $"{config["Supabase:Url"]!.TrimEnd('/')}/functions/v1";
        }

        public enum Outcome { Ok, Denied, Unreachable }

        public record CallResult(Outcome Outcome, string? Message);
        public record TokenResult(Outcome Outcome, string? Token, string? Message);

        // Asks for a code to be mailed to the address on the account.
        //
        // Success means the request was accepted, not that an account exists. The
        // function answers the same way for an address it has never seen, so the page
        // must not say anything more specific than "check your mail" either.
        public async Task<CallResult> RequestAsync(string email)
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
            catch
            {
                return new(Outcome.Unreachable, null);
            }
        }

        // Exchanges a mailed code for a short-lived token authorising one password change.
        public async Task<TokenResult> VerifyAsync(string email, string otp)
        {
            try
            {
                var res = await PostAsync("password-reset-verify", new { email, otp });
                var body = await res.Content.ReadAsStringAsync();

                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    var token = doc.RootElement.TryGetProperty("reset_token", out var t)
                        ? t.GetString() : null;
                    return token is null
                        ? new(Outcome.Unreachable, null, null)
                        : new(Outcome.Ok, token, null);
                }

                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest
                    or HttpStatusCode.TooManyRequests)
                    return new(Outcome.Denied, null, ErrorOf(body) ?? "That code is invalid or has expired.");

                return new(Outcome.Unreachable, null, null);
            }
            catch
            {
                return new(Outcome.Unreachable, null, null);
            }
        }

        // Sets the new password using the token from VerifyAsync.
        //
        // No session comes back. The user signs in with the new password, which keeps
        // the reset path from becoming a second way to obtain a dashboard cookie.
        public async Task<CallResult> CompleteAsync(string resetToken, string newPassword)
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
            catch
            {
                return new(Outcome.Unreachable, null);
            }
        }

        private async Task<HttpResponseMessage> PostAsync(string fn, object body)
        {
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            return await _http.PostAsync($"{_functionsUrl}/{fn}", content);
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
    }
}
