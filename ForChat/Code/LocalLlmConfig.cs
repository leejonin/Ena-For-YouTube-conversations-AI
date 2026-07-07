using System;
using System.IO;
using UnityEngine;

/// <summary>
/// local_llm_config.json — 생방송용 로컬 LLM SLA·토큰 설정.
/// </summary>
[Serializable]
public class LocalLlmConfig
{
    public bool enabled = true;
    public string baseUrl = "http://127.0.0.1:8080/v1";
    public string model = "qwen2.5-14b-instruct";
    public int timeoutSeconds = 60;
    public int warmupTimeoutSeconds = 45;
    public int searchTimeoutSeconds = 45;
    public int maxTokensUserTurn = 4096;
    public int maxTokensSelfTalkTurn = 4096;
    public int maxTokensSearchFallback = 4096;
    public int apiFullHistoryRecentMessages = 6;
    public float temperatureUserTurn = 0.82f;
    public float temperatureSelfTalkTurn = 0.88f;
    public float temperatureSearchFallback = 0.35f;
    public bool disableVisionWhenLocal = true;
    public bool warmupOnStart = true;
    public string slaFallbackDialogue = "(😅 아 잠깐만! 다시 말해줄래?)";
    public bool enableBargeIn = true;
    public bool bargeInDuringSelfTalk = true;
    public bool bargeInDuringTts = true;
    public bool bargeInDeveloperPriority = true;
    public int llamaParallelSlots = 2;

    private static LocalLlmConfig _cached;
    private static bool _loadAttempted;

    public static LocalLlmConfig Load()
    {
        if (_loadAttempted)
        {
            return _cached ?? CreateDefault();
        }

        _loadAttempted = true;
        string path = Path.Combine(Application.dataPath, "AI", "ForChat", "local_llm_config.json");
        if (!File.Exists(path))
        {
            Debug.LogWarning("[LocalLLM] config not found, defaults: " + path);
            _cached = CreateDefault();
            return _cached;
        }

        try
        {
            string json = File.ReadAllText(path);
            _cached = JsonUtility.FromJson<LocalLlmConfig>(json);
            if (_cached == null)
            {
                _cached = CreateDefault();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LocalLLM] config parse failed: " + ex.Message);
            _cached = CreateDefault();
        }

        return _cached;
    }

    public static void Reload()
    {
        _loadAttempted = false;
        _cached = null;
        Load();
    }

    private static LocalLlmConfig CreateDefault()
    {
        return new LocalLlmConfig();
    }

    public string HealthUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "http://127.0.0.1:8080/health";
            }

            string trimmed = baseUrl.TrimEnd('/');
            if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 3);
            }

            return trimmed + "/health";
        }
    }
}

public class LlmChatResult
{
    public string text;
    public string finishReason;
    public bool timedOut;
    public bool aborted;
}
