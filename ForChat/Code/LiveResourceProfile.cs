using System;
using System.IO;
using UnityEngine;

/// <summary>
/// live_resource_profile.json — OBS+이나 동시 가동 CPU/VRAM/TTS·barge-in 튜닝.
/// </summary>
[Serializable]
public class LiveResourceProfile
{
    public bool obsLiveMode = true;
    public int llamaThreads = 3;
    public int llamaGpuLayers = 12;
    public int llamaParallelSlots = 1;
    public int llamaContextSize = 4096;
    public int ttsOnnxThreads = 1;
    public int ttsTotalStepsObs = 7;
    public int bargeInMinIntervalMs = 2000;
    public int ttsPostInterruptDebounceMs = 400;
    public int unityTargetFrameRate = 45;

    private static LiveResourceProfile _cached;
    private static bool _loadAttempted;

    public static LiveResourceProfile Load()
    {
        if (_loadAttempted)
        {
            return _cached ?? CreateDefault();
        }

        _loadAttempted = true;
        string path = Path.Combine(Application.dataPath, "AI", "ForChat", "live_resource_profile.json");
        if (!File.Exists(path))
        {
            _cached = CreateDefault();
            return _cached;
        }

        try
        {
            string json = File.ReadAllText(path);
            _cached = JsonUtility.FromJson<LiveResourceProfile>(json);
            if (_cached == null)
            {
                _cached = CreateDefault();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LiveProfile] parse failed: " + ex.Message);
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

    private static LiveResourceProfile CreateDefault()
    {
        return new LiveResourceProfile();
    }

    /// <summary>OBS 라이브 중 TTS 합성 스텝 — 0이면 local_tts_config 기본값 사용.</summary>
    public int ResolveEffectiveTtsTotalSteps(int configDefaultSteps)
    {
        if (!obsLiveMode || ttsTotalStepsObs <= 0)
        {
            return configDefaultSteps;
        }

        return Mathf.Clamp(ttsTotalStepsObs, 5, 12);
    }
}
