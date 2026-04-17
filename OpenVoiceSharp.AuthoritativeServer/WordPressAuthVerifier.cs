using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenVoiceSharp.AuthoritativeServer;

internal sealed class WordPressAuthVerifier : IDisposable
{
    private readonly Uri VerifyUri;
    private readonly string? SharedSecret;
    private readonly HttpClient HttpClient;

    public WordPressAuthVerifier(string verifyUrl, string? sharedSecret, int timeoutSeconds)
    {
        VerifyUri = new Uri(verifyUrl, UriKind.Absolute);
        SharedSecret = string.IsNullOrWhiteSpace(sharedSecret) ? null : sharedSecret;
        HttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    public async Task<(bool isValid, string message)> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, VerifyUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (!string.IsNullOrWhiteSpace(SharedSecret))
            request.Headers.Add("X-OpenVoiceSharp-Secret", SharedSecret);

        HttpResponseMessage response;
        try
        {
            response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return (false, $"WordPress verify request failed: {exception.Message}");
        }

        string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (false, $"WordPress verify rejected token ({(int)response.StatusCode}).");

        if (TryParseValidFlag(responseText, out bool valid))
            return valid ? (true, "ok") : (false, "WordPress token invalid.");

        // If endpoint returns 200 with non-standard body, treat as failure for safety.
        return (false, "WordPress verify response did not contain a valid=true flag.");
    }

    private static bool TryParseValidFlag(string json, out bool valid)
    {
        valid = false;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (TryReadBoolean(root, "valid", out valid) ||
                TryReadBoolean(root, "success", out valid) ||
                TryReadBoolean(root, "authenticated", out valid))
                return true;

            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
            {
                if (TryReadBoolean(data, "valid", out valid) ||
                    TryReadBoolean(data, "success", out valid) ||
                    TryReadBoolean(data, "authenticated", out valid))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryReadBoolean(JsonElement element, string propertyName, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
            return false;

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        if (property.ValueKind == JsonValueKind.String)
        {
            string text = property.GetString() ?? string.Empty;
            if (bool.TryParse(text, out bool parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    public void Dispose() => HttpClient.Dispose();
}
