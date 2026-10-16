using System.Net.Http.Headers;

namespace UIBlazor.Services;

/// <summary>
/// Shared auth-header logic for wire requests: Bearer for Authorization,
/// raw header value for custom key headers. Used by GetModelsAsync.
/// Completions requests apply the same rules through <see cref="ExtraHeadersPolicy"/>.
/// </summary>
internal static class LlmAuthHeader
{
    public static void Apply(HttpRequestMessage request, ConnectionProfile profile)
    {
        if (string.IsNullOrEmpty(profile.ApiKey))
            return;

        if (string.IsNullOrWhiteSpace(profile.ApiKeyHeader))
        {
            throw new InvalidOperationException("API key header must be specified when an API key is provided.");
        }

        if (string.Equals(profile.ApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
        }
        else
        {
            request.Headers.Add(profile.ApiKeyHeader, profile.ApiKey);
        }
    }
}
