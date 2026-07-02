using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpaceMonger.Core.Diagnostics;

namespace SpaceMonger.Core.Services.Llm;

public class AnthropicClient : ILlmClient
{
    private const string ApiVersion = "2023-06-01";
    private const string Model = "claude-sonnet-4-20250514";
    private const string DeepSeekAnalysisModel = "deepseek-v4-pro";
    private const string DeepSeekChatModel = "deepseek-v4-flash";
    private const int AnalysisMaxTokens = 8192;
    private const int ChatMaxTokens = 4096;
    private const int ValidationMaxTokens = 10;
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromSeconds(300);
    public static string LastResponseStopReason { get; private set; } = string.Empty;
    public static string LastResponseEnvelopePath { get; private set; } = string.Empty;
    public static string LastResponseThinkingPath { get; private set; } = string.Empty;

    private readonly HttpClient _httpClient;

    public AnthropicClient(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _httpClient.BaseAddress ??= AnthropicOptions.GetBaseUri();
    }

    public async Task<string> SendAnalysisAsync(
        string systemPrompt,
        string fileMetadataJson,
        string apiKey,
        string? baseUrl,
        string? modelName,
        bool enableThinking,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(AnalysisTimeout);

        var requestBody = BuildRequestBody(
            systemPrompt,
            [new("user", fileMetadataJson)],
            AnalysisMaxTokens,
            GetModel(baseUrl, preferDeepSeekPro: true, modelName),
            IsDeepSeekAnthropicEndpoint(baseUrl));
        DebugBreakpoints.Hit("llm-analysis-request-built");

        return await SendRequestWithRetryAsync(requestBody, apiKey, baseUrl, timeoutCts.Token).ConfigureAwait(false);
    }

    public async Task<string> SendChatAsync(
        string systemPrompt,
        List<(string role, string content)> messages,
        string apiKey,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(
            systemPrompt,
            messages,
            ChatMaxTokens,
            GetModel(baseUrl, preferDeepSeekPro: false),
            IsDeepSeekAnthropicEndpoint(baseUrl));

        return await SendRequestWithRetryAsync(requestBody, apiKey, baseUrl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StreamChatAsync(
        string systemPrompt,
        List<(string role, string content)> messages,
        string apiKey,
        string? baseUrl,
        Action<string> onToken,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(
            systemPrompt,
            messages,
            ChatMaxTokens,
            GetModel(baseUrl, preferDeepSeekPro: false),
            IsDeepSeekAnthropicEndpoint(baseUrl));
        requestBody["stream"] = true;

        var jsonBody = requestBody.ToJsonString();
        using var request = CreateRequest(jsonBody, apiKey, baseUrl);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Anthropic API streaming request failed with status {(int)response.StatusCode}: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fullResponse = new StringBuilder();

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var eventData = line.Substring(6);
            if (eventData == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(eventData);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                continue;

            var eventType = typeElement.GetString();

            if (eventType == "content_block_delta"
                && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString();
                if (text is not null)
                {
                    fullResponse.Append(text);
                    onToken(text);
                }
            }
        }

        return fullResponse.ToString();
    }

    public async Task<ChatResponse> StreamChatWithThinkingAsync(
        string systemPrompt,
        List<(string role, string content)> messages,
        string apiKey,
        string? baseUrl,
        bool enableThinking,
        Action<string>? onThinkingToken,
        Action<string>? onTextToken,
        CancellationToken cancellationToken)
    {
        var requestBody = BuildRequestBody(
            systemPrompt,
            messages,
            ChatMaxTokens,
            GetModel(baseUrl, preferDeepSeekPro: false),
            IsDeepSeekAnthropicEndpoint(baseUrl) && !enableThinking);
        requestBody["stream"] = true;

        var jsonBody = requestBody.ToJsonString();
        using var request = CreateRequest(jsonBody, apiKey, baseUrl);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Anthropic API streaming request failed with status {(int)response.StatusCode}: {errorBody}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var fullText = new StringBuilder();
        var fullThinking = new StringBuilder();
        var currentBlockType = "text"; // Track current block type

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var eventData = line.Substring(6);
            if (eventData == "[DONE]")
                break;

            using var doc = JsonDocument.Parse(eventData);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                continue;

            var eventType = typeElement.GetString();

            // Handle content_block_start to detect thinking blocks
            if (eventType == "content_block_start"
                && root.TryGetProperty("content_block", out var contentBlock))
            {
                if (contentBlock.TryGetProperty("type", out var blockType))
                {
                    currentBlockType = blockType.GetString() ?? "text";
                }
            }

            if (eventType == "content_block_delta"
                && root.TryGetProperty("delta", out var delta))
            {
                // Handle thinking delta
                if (currentBlockType == "thinking"
                    && delta.TryGetProperty("thinking", out var thinkingElement))
                {
                    var thinking = thinkingElement.GetString();
                    if (thinking is not null)
                    {
                        fullThinking.Append(thinking);
                        onThinkingToken?.Invoke(thinking);
                    }
                }
                // Handle text delta
                else if (currentBlockType == "text"
                    && delta.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString();
                    if (text is not null)
                    {
                        fullText.Append(text);
                        onTextToken?.Invoke(text);
                    }
                }
            }
        }

        return new ChatResponse(fullText.ToString(), fullThinking.ToString());
    }

    public async Task<bool> ValidateApiKeyAsync(string apiKey, string? baseUrl)
    {
        var requestBody = new JsonObject
        {
            ["model"] = GetModel(baseUrl, preferDeepSeekPro: false),
            ["max_tokens"] = ValidationMaxTokens,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Hello"
                }
            }
        };

        using var request = CreateRequest(requestBody.ToJsonString(), apiKey, baseUrl);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return false;
        }

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var errorBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw new HttpRequestException(
            $"API key validation failed with status {(int)response.StatusCode}: {errorBody}");
    }

    private static JsonObject BuildRequestBody(
        string systemPrompt,
        List<(string role, string content)> messages,
        int maxTokens,
        string model,
        bool disableThinking)
    {
        var messagesArray = new JsonArray();
        foreach (var (role, content) in messages)
        {
            messagesArray.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = content
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = systemPrompt,
            ["messages"] = messagesArray
        };

        if (disableThinking)
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "disabled"
            };
        }

        return body;
    }

    private static string GetModel(string? baseUrl, bool preferDeepSeekPro, string? configuredModel = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            return configuredModel.Trim();
        }

        if (IsDeepSeekAnthropicEndpoint(baseUrl))
        {
            return preferDeepSeekPro ? DeepSeekAnalysisModel : DeepSeekChatModel;
        }

        return Model;
    }

    private static bool IsDeepSeekAnthropicEndpoint(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Contains("deepseek.com", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> SendRequestWithRetryAsync(
        JsonObject requestBody,
        string apiKey,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
        var jsonBody = requestBody.ToJsonString();

        using var request = CreateRequest(jsonBody, apiKey, baseUrl);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return await ParseResponseTextAsync(response, cancellationToken).ConfigureAwait(false);
        }

        // Handle rate limiting (429) and overloaded (529) with a single retry.
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (int)response.StatusCode == 529)
        {
            var retryAfter = GetRetryAfterDelay(response);
            await Task.Delay(retryAfter, cancellationToken).ConfigureAwait(false);

            using var retryRequest = CreateRequest(jsonBody, apiKey, baseUrl);

            using var retryResponse = await _httpClient.SendAsync(
                retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (retryResponse.IsSuccessStatusCode)
            {
                return await ParseResponseTextAsync(retryResponse, cancellationToken).ConfigureAwait(false);
            }

            var retryStatusCode = (int)retryResponse.StatusCode;
            var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new HttpRequestException(
                $"Anthropic API request failed after retry with status {retryStatusCode}: {retryBody}");
        }

        // Handle other error statuses.
        var statusCode = (int)response.StatusCode;
        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException(
                "Invalid API key. Please check your Anthropic API key and try again.");
        }

        if (statusCode >= 500)
        {
            throw new HttpRequestException(
                $"Anthropic API server error ({statusCode}). The service is temporarily unavailable. Please try again later.");
        }

        throw new HttpRequestException(
            $"Anthropic API request failed with status {statusCode}: {errorBody}");
    }

    private static HttpRequestMessage CreateRequest(string jsonBody, string apiKey, string? baseUrl)
    {
        var requestUri = AnthropicOptions.GetMessagesUri(baseUrl);
        var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return request;
    }

    private static async Task<string> ParseResponseTextAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        DebugBreakpoints.Hit("llm-response-body-read");
        LastResponseEnvelopePath = WriteResponseEnvelope(responseBody) ?? string.Empty;

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        LastResponseStopReason = ExtractStopReason(root) ?? string.Empty;
        LastResponseThinkingPath = WriteThinkingIfPresent(root) ?? string.Empty;

        var text = TryExtractAnthropicText(root)
            ?? TryExtractOpenAiCompatibleText(root);

        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        throw new InvalidOperationException(
            "Unexpected response format: could not extract response text. The endpoint returned a supported status code but not Anthropic content[0].text or OpenAI choices[0].message.content.");
    }

    private static string? TryExtractAnthropicText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var contentArray)
            || contentArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("text", out var textElement)
                && textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? TryExtractOpenAiCompatibleText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choices[0];

        if (firstChoice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var messageContent)
            && messageContent.ValueKind == JsonValueKind.String)
        {
            return messageContent.GetString();
        }

        if (firstChoice.TryGetProperty("text", out var textElement)
            && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString();
        }

        return null;
    }

    private static string? ExtractStopReason(JsonElement root)
    {
        if (root.TryGetProperty("stop_reason", out var stopReason)
            && stopReason.ValueKind == JsonValueKind.String)
        {
            return stopReason.GetString();
        }

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("finish_reason", out var finishReason)
            && finishReason.ValueKind == JsonValueKind.String)
        {
            return finishReason.GetString();
        }

        return null;
    }

    private static string? WriteThinkingIfPresent(JsonElement root)
    {
        var builder = new StringBuilder();

        if (root.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(thinking.GetString());
                }
                else if (block.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
                {
                    builder.AppendLine(reasoning.GetString());
                }
            }
        }

        if (root.TryGetProperty("reasoning_content", out var reasoningContent) && reasoningContent.ValueKind == JsonValueKind.String)
        {
            builder.AppendLine(reasoningContent.GetString());
        }

        if (builder.Length == 0)
            return null;

        return WriteDiagnosticFile("thinking", "thinking", builder.ToString());
    }

    private static string? WriteResponseEnvelope(string responseBody)
    {
        try
        {
            return WriteDiagnosticFile("api-envelopes", "api-envelope", responseBody, ".json");
        }
        catch
        {
            return null;
        }
    }

    private static string WriteDiagnosticFile(string directoryName, string filePrefix, string content, string extension = ".txt")
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceMonger Copilot",
            "logs",
            directoryName);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{filePrefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}");
        File.WriteAllText(path, content);
        return path;
    }

    private static TimeSpan GetRetryAfterDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
        }

        // Default retry delay if no retry-after header is provided.
        return TimeSpan.FromSeconds(5);
    }
}


