using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// OpenAI 호환 로컬 llama-server (/v1/chat/completions) 클라이언트.
/// </summary>
public class LocalOpenAiCompatibleChatProvider
{
    private LocalLlmConfig config;
    private static UnityWebRequest activeLlmRequest;

    public LocalOpenAiCompatibleChatProvider(LocalLlmConfig cfg)
    {
        config = cfg ?? LocalLlmConfig.Load();
    }

    public bool IsEnabled => config != null && config.enabled;

    public LocalLlmConfig Config => config;

    public static void AbortActiveRequest()
    {
        if (activeLlmRequest == null)
        {
            return;
        }

        try
        {
            activeLlmRequest.Abort();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LocalLLM] abort: " + ex.Message);
        }

        activeLlmRequest = null;
    }

    /// <summary>Play 시작 시 llama-server health + 짧은 ping.</summary>
    public async Task<bool> WarmupAsync()
    {
        if (!IsEnabled)
        {
            return false;
        }

        int warmupTimeout = config.warmupTimeoutSeconds > 0
            ? config.warmupTimeoutSeconds
            : Mathf.Max(config.timeoutSeconds, 30);

        bool healthOk = await CheckHealthAsync(Mathf.Min(10, warmupTimeout));
        if (!healthOk)
        {
            Debug.LogWarning("[LocalLLM] health check failed: " + config.HealthUrl);
            return false;
        }

        LlmChatResult ping = await RequestChatAsync(
            "ping",
            new JArray { new JObject { { "role", "user" }, { "content", "ping" } } },
            32,
            config.temperatureUserTurn,
            warmupTimeout);

        bool ok = ping != null && !string.IsNullOrWhiteSpace(ping.text);
        Debug.Log(ok ? "[LocalLLM] warmup OK" : "[LocalLLM] warmup ping empty");
        return ok;
    }

    public async Task<bool> CheckHealthAsync(int timeoutSeconds)
    {
        if (!IsEnabled)
        {
            return false;
        }

        using (UnityWebRequest request = UnityWebRequest.Get(config.HealthUrl))
        {
            request.timeout = Mathf.Max(1, timeoutSeconds);
            await AwaitWebRequest(request.SendWebRequest());
            return request.result == UnityWebRequest.Result.Success;
        }
    }

    public async Task<LlmChatResult> RequestChatAsync(
        string systemPrompt,
        JArray openAiMessages,
        int maxTokens,
        float temperature,
        int? timeoutOverrideSeconds = null)
    {
        if (!IsEnabled)
        {
            Debug.LogError("[LocalLLM] disabled in config.");
            return null;
        }

        if (openAiMessages == null)
        {
            openAiMessages = new JArray();
        }

        JArray messages = new JArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new JObject
            {
                { "role", "system" },
                { "content", systemPrompt }
            });
        }

        foreach (JToken msg in openAiMessages)
        {
            messages.Add(msg);
        }

        JObject payload = new JObject
        {
            { "model", config.model },
            { "max_tokens", maxTokens },
            { "temperature", temperature },
            { "messages", messages }
        };

        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
        string endpoint = config.baseUrl.TrimEnd('/') + "/chat/completions";
        int timeoutSec = timeoutOverrideSeconds ?? config.timeoutSeconds;

        UnityWebRequest request = new UnityWebRequest(endpoint, "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = timeoutSec;
        activeLlmRequest = request;

        try
        {
            await AwaitWebRequest(request.SendWebRequest());

            if (request.result == UnityWebRequest.Result.ConnectionError
                && !string.IsNullOrEmpty(request.error)
                && request.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Debug.LogWarning("[LocalLLM] request timed out after " + timeoutSec + "s");
                return new LlmChatResult { timedOut = true };
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                if (request.result == UnityWebRequest.Result.ConnectionError
                    && !string.IsNullOrEmpty(request.error)
                    && request.error.IndexOf("abort", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return new LlmChatResult { aborted = true };
                }

                Debug.LogError("[LocalLLM] " + request.error);
                if (!string.IsNullOrEmpty(request.downloadHandler?.text))
                {
                    Debug.LogError("[LocalLLM] " + request.downloadHandler.text);
                }

                return null;
            }

            JObject json = JObject.Parse(request.downloadHandler.text);
            string text = json["choices"]?[0]?["message"]?["content"]?.ToString();
            string finishReason = json["choices"]?[0]?["finish_reason"]?.ToString();

            if (string.IsNullOrEmpty(text))
            {
                Debug.LogError("[LocalLLM] empty response content.");
                return null;
            }

            return new LlmChatResult
            {
                text = text,
                finishReason = finishReason
            };
        }
        finally
        {
            if (activeLlmRequest == request)
            {
                activeLlmRequest = null;
            }

            request.uploadHandler?.Dispose();
            request.downloadHandler?.Dispose();
            request.Dispose();
        }
    }

    /// <summary>Anthropic snapshot → OpenAI role/content 배열.</summary>
    public static JArray BuildOpenAiMessagesFromSnapshot(JArray snapshot)
    {
        JArray messages = new JArray();
        if (snapshot == null)
        {
            return messages;
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            JObject orig = snapshot[i] as JObject;
            if (orig == null)
            {
                continue;
            }

            messages.Add(new JObject
            {
                { "role", orig["role"]?.ToString() ?? "user" },
                { "content", orig["content"]?.ToString() ?? string.Empty }
            });
        }

        return messages;
    }

    private static Task AwaitWebRequest(UnityWebRequestAsyncOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }
}
