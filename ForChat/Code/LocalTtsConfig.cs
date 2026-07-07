using System;
using System.IO;
using UnityEngine;

/// <summary>
/// local_tts_config.json — 생방송용 로컬 Supertonic 3 TTS 설정.
/// </summary>
[Serializable]
public class LocalTtsConfig
{
    public bool enabled = true;
    public string baseUrl = "http://127.0.0.1:8081";
    public string engine = "supertonic3";
    public string voice = "F2";
    public string lang = "ko";
    public int timeoutSeconds = 20;
    public int warmupTimeoutSeconds = 10;
    public bool warmupOnStart = true;
    public bool fallbackToOpenAi = false;
    public bool koreanOnly = false;
    public bool bilingualSegmentTts = true;
    public string outputFormat = "wav";
    public float synthesisSpeed = 0.92f;
    public int totalSteps = 9;
    public int onnxThreads = 2;
    public int maxConcurrentSynthesis = 1;
    public bool processPriorityBelowNormal = true;

    private static LocalTtsConfig _cached;
    private static bool _loadAttempted;

    public static LocalTtsConfig Load()
    {
        if (_loadAttempted)
        {
            return _cached ?? CreateDefault();
        }

        _loadAttempted = true;
        string path = Path.Combine(Application.dataPath, "AI", "ForChat", "local_tts_config.json");
        if (!File.Exists(path))
        {
            Debug.LogWarning("[LocalTTS] config not found, defaults: " + path);
            _cached = CreateDefault();
            return _cached;
        }

        try
        {
            string json = File.ReadAllText(path);
            _cached = JsonUtility.FromJson<LocalTtsConfig>(json);
            if (_cached == null)
            {
                _cached = CreateDefault();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LocalTTS] config parse failed: " + ex.Message);
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

    private static LocalTtsConfig CreateDefault()
    {
        return new LocalTtsConfig();
    }

    public string HealthUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "http://127.0.0.1:8081/health";
            }

            return baseUrl.TrimEnd('/') + "/health";
        }
    }

    public string SpeechEndpoint
    {
        get
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return "http://127.0.0.1:8081/v1/audio/speech";
            }

            return baseUrl.TrimEnd('/') + "/v1/audio/speech";
        }
    }
}
