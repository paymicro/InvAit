using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UIBlazor.Models;

namespace UIBlazor.Services;

public class ChatService(IServiceProvider serviceProvider, AiSettingsProvider aiSettingsProvider)
{
    public async Task<AiModelList> GetModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{aiSettingsProvider.Current.Endpoint}/v1/models");
        
        if (!string.IsNullOrEmpty(aiSettingsProvider.Current.ApiKey))
        {
            if (string.IsNullOrWhiteSpace(aiSettingsProvider.Current.ApiKeyHeader))
            {
                throw new InvalidOperationException("API key header must be specified when an API key is provided.");
            }

            if (string.Equals(aiSettingsProvider.Current.ApiKeyHeader, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", aiSettingsProvider.Current.ApiKey);
            }
            else
            {
                request.Headers.Add(aiSettingsProvider.Current.ApiKeyHeader, aiSettingsProvider.Current.ApiKey);
            }
        }
        
        var httpClient = serviceProvider.GetRequiredService<HttpClient>();
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Getting models failed: {await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
        }
        
        return await response.Content.ReadFromJsonAsync<AiModelList>(cancellationToken)
               ?? throw new JsonException("Models deserialization exception");
    }
}