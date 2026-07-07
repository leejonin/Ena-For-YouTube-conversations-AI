using NAudio.Wave;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Networking;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class TTSRequester : MonoBehaviour
{
    private LocalTtsConfig localTtsConfig;
    private LocalHttpTTSProvider localTtsProvider;
    private readonly OpenAITTSProvider openAiTtsProvider = new OpenAITTSProvider();
    private bool localTtsHealthy;

    private static bool s_applicationQuitting;

    /// <summary>끼어들기 시 진행 중 TTS HTTP 합성 세대 — 증가 시 조기 종료.</summary>
    public static int FetchGeneration { get; private set; }

    public static void SignalFetchAbort()
    {
        FetchGeneration++;
    }

    public static bool IsFetchCancelled(int capturedGeneration)
    {
        return capturedGeneration != FetchGeneration;
    }

    /// <summary>Play 중이며 앱/에디터 종료 직전이 아닐 때만 TTS 허용.</summary>
    public static bool IsTtsAllowed => !s_applicationQuitting && Application.isPlaying;

    public static void MarkApplicationQuitting()
    {
        s_applicationQuitting = true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetApplicationQuittingFlag()
    {
        s_applicationQuitting = false;
    }

#if UNITY_EDITOR
    [InitializeOnLoadMethod]
    private static void RegisterEditorLifecycleHooks()
    {
        EditorApplication.quitting += MarkApplicationQuitting;
        EditorApplication.playModeStateChanged += OnEditorPlayModeChanged;
    }

    private static void OnEditorPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            MarkApplicationQuitting();
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            s_applicationQuitting = false;
        }
    }
#endif

    private static readonly Regex LatinOrForeignScriptRegex = new Regex(
        @"[A-Za-z]+|[\u3040-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uF900-\uFAFF]+",
        RegexOptions.Compiled);

    // 한·영 TTS — 일본어·한자 등 제3언어만 제거 (영문 Latin은 유지)
    private static readonly Regex ForeignScriptRegex = new Regex(
        @"[\u3040-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\uF900-\uFAFF]+",
        RegexOptions.Compiled);

    private static readonly Regex LatinLetterRegex = new Regex(
        @"[A-Za-z]",
        RegexOptions.Compiled);

    private static readonly Regex HangulRegex = new Regex(
        @"[\uAC00-\uD7A3]",
        RegexOptions.Compiled);

    public class ReSetTTSStyle
    {
        // 귀여운 20대 여성 — F0 220~260Hz, 맑은 포먼트, 미세 비음
        public float pitchShiftBase = 1.11f;
        public float pitchVarianceDepth = 0.04f;
        public float formantShift = 1.08f;
        public float highBandDamping = 0.97f;
        public float nasalColorLevel = 0.13f;
        public float softCompress = 0.07f;
        public float ttsSpeed = 0.92f;

        /// <summary>
        /// Supertonic F2 원음 → 20대 귀여운 여성 톤 (경량 5단계).
        /// </summary>
        public float[] ApplyCuteYoungVoice(float[] samples, int sampleRate)
        {
            float[] p1 = PitchShiftWithVariance(samples, sampleRate, pitchShiftBase, pitchVarianceDepth);
            float[] p2 = FormantShift(p1, formantShift);
            float[] p3 = ApplyNasalColor(p2, nasalColorLevel);
            float[] p4 = ApplySpectralTilt(p3, highBandDamping);
            float[] p5 = SoftCompressor(p4, softCompress);
            return p5;
        }

        public float[] ApplyIdolVoice(float[] samples, int sampleRate)
        {
            return ApplyCuteYoungVoice(samples, sampleRate);
        }

        private float[] PitchShiftWithVariance(float[] samples, int sampleRate, float baseAmount, float varianceDepth)
        {
            float safeAmount = Mathf.Clamp(baseAmount, 0.92f, 1.18f);
            int newLength = Mathf.FloorToInt(samples.Length / safeAmount);
            if (newLength < 1)
            {
                return samples;
            }

            float[] output = new float[newLength];
            float sourcePos = 0f;
            float invRate = 1f / Mathf.Max(1, sampleRate);

            for (int i = 0; i < newLength; i++)
            {
                if (sourcePos >= samples.Length - 1)
                {
                    break;
                }

                float dynamicAmount = safeAmount;
                if (varianceDepth > 0.01f)
                {
                    float timeSec = sourcePos * invRate;
                    float lfoA = Mathf.Sin(2f * Mathf.PI * 2.0f * timeSec);
                    float lfoB = Mathf.Sin(2f * Mathf.PI * 3.3f * timeSec + 0.5f);
                    dynamicAmount = safeAmount * (1f + varianceDepth * (0.6f * lfoA + 0.4f * lfoB));
                    dynamicAmount = Mathf.Clamp(dynamicAmount, safeAmount * 0.96f, safeAmount * 1.06f);
                }

                int p = (int)sourcePos;
                float frac = sourcePos - p;

                if (p + 1 < samples.Length)
                    output[i] = Mathf.Lerp(samples[p], samples[p + 1], frac);
                else if (p < samples.Length)
                    output[i] = samples[p];
                else
                    output[i] = 0f;

                sourcePos += dynamicAmount;
            }

            return output;
        }

        /// <summary>
        /// 250~350Hz 대역을 살짝 강조해 한국어 ㄴ·ㅁ·ㅇ 계열 비음 뉘앙스.
        /// </summary>
        private float[] ApplyNasalColor(float[] samples, float amount)
        {
            if (samples == null || samples.Length < 4 || amount <= 0.0001f)
            {
                return samples;
            }

            float[] output = new float[samples.Length];
            float env = 0f;
            const float envAlpha = 0.025f;

            for (int i = 0; i < samples.Length; i++)
            {
                env += envAlpha * (Mathf.Abs(samples[i]) - env);
                float gate = Mathf.Clamp01(env * 3.5f);

                float mid = samples[i];
                if (i >= 2)
                {
                    mid = samples[i] - 0.5f * (samples[i - 1] + samples[i - 2]);
                }

                float v = samples[i] + mid * amount * gate * 0.4f;
                output[i] = Mathf.Clamp(v, -1f, 1f);
            }

            return output;
        }

        private float[] FormantShift(float[] samples, float amount)
        {
            float[] output = new float[samples.Length];

            output[0] = samples[0];
            if (samples.Length > 1)
                output[1] = samples[1];

            for (int i = 2; i < samples.Length; i++)
            {
                float s = samples[i] * amount - samples[i - 1] * (amount - 1f);

                if (float.IsNaN(s) || float.IsInfinity(s))
                    s = samples[i];

                output[i] = Mathf.Clamp(s, -1f, 1f);
            }

            return output;
        }

        private float[] AddAspiratedBreath(float[] samples, int sampleRate, float amount, float highPassHz)
        {
            if (amount <= 0.0001f)
            {
                return samples;
            }

            float[] output = new float[samples.Length];
            System.Random rand = new System.Random(1701);

            // ?????? 1?? ???????? ????? ???? ???? ?????? ?????.
            float dt = 1f / Mathf.Max(1f, sampleRate);
            float rc = 1f / (2f * Mathf.PI * Mathf.Max(50f, highPassHz));
            float alpha = rc / (rc + dt);
            float prevNoise = 0f;
            float prevHP = 0f;

            for (int i = 0; i < samples.Length; i++)
            {
                float rawNoise = (float)(rand.NextDouble() * 2.0 - 1.0);
                float hp = alpha * (prevHP + rawNoise - prevNoise);
                prevNoise = rawNoise;
                prevHP = hp;

                // ????? ??? ?????????? ??????? ????????? ??????? ??????? ???.
                float gate = Mathf.Clamp01(Mathf.Abs(samples[i]) * 2.2f);
                float v = samples[i] + hp * amount * gate;
                output[i] = Mathf.Clamp(v, -1f, 1f);
            }

            return output;
        }

        private float[] ApplySpectralTilt(float[] samples, float highBandKeep)
        {
            float[] output = new float[samples.Length];
            float low = 0f;
            float lowAlpha = 0.06f;
            float highKeep = Mathf.Clamp(highBandKeep, 0.5f, 0.95f);

            for (int i = 0; i < samples.Length; i++)
            {
                low += lowAlpha * (samples[i] - low);
                float high = samples[i] - low;
                // ?????? ??????? ?????? ?????? ??????? ???? pressed ?????? ??????.
                float v = low + high * highKeep;

                output[i] = Mathf.Clamp(v, -1f, 1f);
            }

            return output;
        }
        private float[] SoftCompressor(float[] samples, float amount)
        {
            float[] output = new float[samples.Length];
            float drive = Mathf.Lerp(1.8f, 4.5f, Mathf.Clamp01(amount));
            float norm = 1f - Mathf.Exp(-drive);

            for (int i = 0; i < samples.Length; i++)
            {
                float v = samples[i];
                // tan ??? ??? ??? exp ??? ????? ??(knee)?? ????? ????????? ?????? ?????.
                float s = Mathf.Sign(v);
                float a = Mathf.Abs(v);
                v = s * ((1f - Mathf.Exp(-a * drive)) / norm);
                output[i] = Mathf.Clamp(v, -1f, 1f);
            }

            return output;
        }

        private float[] CompressInterSententialPauses(float[] samples, int sampleRate, float compressRatio)
        {
            if (samples == null || samples.Length < sampleRate / 10)
            {
                return samples;
            }

            float ratio = Mathf.Clamp(compressRatio, 0.5f, 1f);
            if (ratio >= 0.999f)
            {
                return samples;
            }

            int window = Mathf.Max(32, sampleRate / 100);
            float threshold = 0.012f;
            var segments = new System.Collections.Generic.List<(int start, int end, bool silent)>();
            int segStart = 0;
            bool prevSilent = false;

            for (int i = 0; i < samples.Length; i += window)
            {
                int end = Mathf.Min(samples.Length, i + window);
                float sum = 0f;
                for (int j = i; j < end; j++)
                {
                    sum += samples[j] * samples[j];
                }

                float rms = Mathf.Sqrt(sum / Mathf.Max(1, end - i));
                bool silent = rms < threshold;
                if (i == 0)
                {
                    prevSilent = silent;
                    continue;
                }

                if (silent != prevSilent)
                {
                    segments.Add((segStart, i, prevSilent));
                    segStart = i;
                    prevSilent = silent;
                }
            }

            segments.Add((segStart, samples.Length, prevSilent));

            var output = new System.Collections.Generic.List<float>(samples.Length);
            for (int s = 0; s < segments.Count; s++)
            {
                int start = segments[s].start;
                int end = segments[s].end;
                int len = end - start;
                if (len <= 0)
                {
                    continue;
                }

                if (!segments[s].silent)
                {
                    for (int i = start; i < end; i++)
                    {
                        output.Add(samples[i]);
                    }

                    continue;
                }

                int compressedLen = Mathf.Max(1, Mathf.RoundToInt(len * ratio));
                for (int i = 0; i < compressedLen; i++)
                {
                    float src = start + (i / (float)compressedLen) * len;
                    int p = (int)src;
                    float frac = src - p;
                    float v = p + 1 < samples.Length
                        ? Mathf.Lerp(samples[p], samples[p + 1], frac)
                        : samples[Mathf.Min(p, samples.Length - 1)];
                    output.Add(v);
                }
            }

            return output.ToArray();
        }

        private float[] ProlongSentenceEndings(float[] samples, int sampleRate, int tailMs, float stretch)
        {
            if (samples == null || samples.Length == 0 || tailMs <= 0 || stretch <= 1.001f)
            {
                return samples;
            }

            int tailSamples = Mathf.Clamp(Mathf.RoundToInt(sampleRate * tailMs / 1000f), 1, samples.Length);
            int headLen = samples.Length - tailSamples;
            if (headLen <= 0)
            {
                return samples;
            }

            int newTailLen = Mathf.Max(tailSamples + 1, Mathf.RoundToInt(tailSamples * stretch));
            float[] output = new float[headLen + newTailLen];

            for (int i = 0; i < headLen; i++)
            {
                output[i] = samples[i];
            }

            for (int i = 0; i < newTailLen; i++)
            {
                float src = (i / (float)newTailLen) * tailSamples;
                int p = headLen + (int)src;
                float frac = src - (int)src;
                float baseVal = p + 1 < samples.Length
                    ? Mathf.Lerp(samples[p], samples[p + 1], frac)
                    : samples[Mathf.Min(p, samples.Length - 1)];
                float lift = 1f + 0.04f * (i / (float)newTailLen);
                output[headLen + i] = Mathf.Clamp(baseVal * lift, -1f, 1f);
            }

            return output;
        }
    }
    public AudioSource audioSource;
    public LipSync lipSync;

    private string apiKey = "";
    private int playbackSessionId;

    private void Start()
    {
        KeyLogic();
        InitLocalTts();
    }

    private void InitLocalTts()
    {
        localTtsConfig = LocalTtsConfig.Load();
        LiveResourceProfile profile = LiveResourceProfile.Load();
        if (profile != null && profile.unityTargetFrameRate > 0)
        {
            Application.targetFrameRate = profile.unityTargetFrameRate;
        }

        if (localTtsConfig == null || !localTtsConfig.enabled)
        {
            localTtsHealthy = false;
            Debug.Log("[LocalTTS] disabled — OpenAI TTS fallback.");
            return;
        }

        localTtsProvider = new LocalHttpTTSProvider(localTtsConfig);
        _ = WarmupLocalTtsAsync();
    }

    private bool UseLocalTts() =>
        localTtsConfig != null && localTtsConfig.enabled && localTtsHealthy && localTtsProvider != null;

    private async Task WarmupLocalTtsAsync()
    {
        if (localTtsConfig == null || !localTtsConfig.enabled)
        {
            return;
        }

        int timeout = localTtsConfig.warmupTimeoutSeconds > 0
            ? localTtsConfig.warmupTimeoutSeconds
            : 10;

        using (UnityWebRequest request = UnityWebRequest.Get(localTtsConfig.HealthUrl))
        {
            request.timeout = timeout;
            await AwaitWebRequest(request);
            localTtsHealthy = request.result == UnityWebRequest.Result.Success;
        }

        if (!localTtsHealthy)
        {
            Debug.LogWarning("[LocalTTS] health check failed: " + localTtsConfig.HealthUrl
                + " — start_local_tts.bat 확인.");
            return;
        }

        Debug.Log("[LocalTTS] health OK");

        if (!localTtsConfig.warmupOnStart)
        {
            return;
        }

        try
        {
            ReSetTTSStyle style = new ReSetTTSStyle();
            TTSSynthesisRequest req = new TTSSynthesisRequest
            {
                voice = localTtsConfig.voice,
                input = "워밍업",
                speed = ResolveSynthesisSpeed(style)
            };
            byte[] ping = await localTtsProvider.SynthesizeAsync(req, string.Empty);
            if (ping != null && ping.Length > 0)
            {
                Debug.Log("[LocalTTS] warmup synthesis OK");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LocalTTS] warmup synthesis failed: " + ex.Message);
        }
    }

    private static async Task AwaitWebRequest(UnityWebRequest request)
    {
        var op = request.SendWebRequest();
        while (!op.isDone)
        {
            await Task.Yield();
        }
    }

    private void OnDisable()
    {
        StopPlayback();
    }

    private void OnApplicationQuit()
    {
        MarkApplicationQuitting();
        StopPlayback();
    }

    /// <summary>
    /// Play 종료·비활성화 시 TTS HTTP 완료 후 재생되는 것을 막는다.
    /// </summary>
    public void StopPlayback()
    {
        playbackSessionId++;
        if (audioSource != null)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }

        if (lipSync != null)
        {
            lipSync.isSpeaking = false;
        }
    }

    private float ResolveSynthesisSpeed(ReSetTTSStyle style)
    {
        if (localTtsConfig != null && localTtsConfig.synthesisSpeed > 0.01f)
        {
            return Mathf.Clamp(localTtsConfig.synthesisSpeed, 0.7f, 2.0f);
        }

        return style.ttsSpeed;
    }

    private bool CanPlayTts()
    {
        return IsTtsAllowed;
    }
    private void KeyLogic()
    {
        string[] lines = File.ReadAllLines(@"Assets/AI/ForChat/key.txt");
        foreach (string show in lines)
            apiKey = show;
    }
    public async Task<byte[]> FetchTTSBytesAsync(string text)
    {
        return await FetchTTSBytesAsync(text, FetchGeneration);
    }

    public async Task<byte[]> FetchTTSBytesAsync(string text, int fetchGeneration)
    {
        if (!CanPlayTts())
        {
            return null;
        }

        if (IsFetchCancelled(fetchGeneration))
        {
            return null;
        }

        string styledInput = BuildIdolSpeechInput(text);
        if (string.IsNullOrWhiteSpace(styledInput))
        {
            return null;
        }

        ReSetTTSStyle style = new ReSetTTSStyle();
        bool koreanOnly = localTtsConfig != null && localTtsConfig.koreanOnly;
        if (!HasSpeakableTtsText(styledInput, koreanOnly))
        {
            Debug.Log("[TTS] 발화할 한·영 텍스트가 없어 건너뜁니다.");
            return null;
        }

        if (IsFetchCancelled(fetchGeneration))
        {
            return null;
        }

        bool allowOpenAi = !koreanOnly
            && (localTtsConfig == null
                || !localTtsConfig.enabled
                || localTtsConfig.fallbackToOpenAi);
        bool useOpenAiFirst = allowOpenAi && IsPrimarilyEnglish(styledInput);

        string voice = localTtsConfig != null ? localTtsConfig.voice : "F2";
        LiveResourceProfile profile = LiveResourceProfile.Load();
        int effectiveSteps = profile != null
            ? profile.ResolveEffectiveTtsTotalSteps(localTtsConfig != null ? localTtsConfig.totalSteps : 9)
            : (localTtsConfig != null ? localTtsConfig.totalSteps : 9);

        TTSSynthesisRequest req = new TTSSynthesisRequest
        {
            model = "gpt-4o-mini-tts",
            voice = voice,
            input = styledInput,
            speed = ResolveSynthesisSpeed(style),
            instructions = BuildTTSInstructions(),
            totalSteps = effectiveSteps
        };

        byte[] bytes = null;
        if (useOpenAiFirst)
        {
            try
            {
                bytes = await openAiTtsProvider.SynthesizeAsync(req, apiKey);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[OpenAI TTS] English synthesis failed: " + ex.Message);
            }
        }
        else if (UseLocalTts())
        {
            try
            {
                bytes = await localTtsProvider.SynthesizeAsync(req, apiKey, fetchGeneration);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[LocalTTS] synthesis failed: " + ex.Message);
            }
        }

        if (IsFetchCancelled(fetchGeneration))
        {
            return null;
        }

        if (bytes == null || bytes.Length == 0)
        {
            if (allowOpenAi && !useOpenAiFirst)
            {
                req.voice = "nova";
                bytes = await openAiTtsProvider.SynthesizeAsync(req, apiKey);
            }
        }

        if (!CanPlayTts() || IsFetchCancelled(fetchGeneration))
        {
            return null;
        }

        return bytes;
    }

    /// <summary>
    /// MP3 bytes 재생. HTTP 없음.
    /// </summary>
    public async Task PlayTTSFromBytes(byte[] audioData, string sourceText)
    {
        if (!CanPlayTts())
        {
            return;
        }

        if (audioSource == null)
        {
            Debug.LogError("[AI Nia] AudioSource is NULL in PlayTTSFromBytes!");
            return;
        }

        if (audioData == null || audioData.Length == 0)
        {
            return;
        }

        int sessionId = playbackSessionId;
        if (CanPlayTts())
        {
            SaveTtsOutput(audioData, sourceText);
        }

        float[] pcmSamples;
        int sampleRate;

        if (IsWavData(audioData))
        {
            pcmSamples = ConvertWavToFloatArray(audioData);
            sampleRate = ReadWavSampleRate(audioData);
        }
        else
        {
            using (var mp3 = new Mp3FileReader(new MemoryStream(audioData)))
            {
                sampleRate = mp3.WaveFormat.SampleRate;

                using (var pcm = WaveFormatConversionStream.CreatePcmStream(mp3))
                {
                    using (var mem = new MemoryStream())
                    {
                        WaveFileWriter.WriteWavFileToStream(mem, pcm);
                        byte[] wavBytes = mem.ToArray();
                        pcmSamples = ConvertWavToFloatArray(wavBytes);
                    }
                }
            }
        }

        ReSetTTSStyle style = new ReSetTTSStyle();
        float[] voiceSamples = style.ApplyCuteYoungVoice(pcmSamples, sampleRate);

        if (!CanPlayTts() || sessionId != playbackSessionId)
        {
            return;
        }

        AudioClip clip = AudioClip.Create("TTSVoice", voiceSamples.Length, 1, sampleRate, false);
        clip.SetData(voiceSamples, 0);

        audioSource.clip = clip;
        audioSource.Play();

        StartCoroutine(lipSync.InputTextForLipSync(sourceText, audioSource));

        await WaitForPlaybackFinished(sessionId);
    }

    public async Task PlayTTS(string text)
    {
        if (!CanPlayTts())
        {
            return;
        }

        if (audioSource == null)
        {
            Debug.LogError("[AI Nia] AudioSource is NULL in PlayTTS!");
            return;
        }

        int sessionId = playbackSessionId;
        byte[] mp3Data;
        try
        {
            mp3Data = await FetchTTSBytesAsync(text);
        }
        catch (Exception ex)
        {
            Debug.LogError(ex.Message);
            return;
        }

        if (!CanPlayTts() || sessionId != playbackSessionId || mp3Data == null)
        {
            return;
        }

        await PlayTTSFromBytes(mp3Data, text);
    }

    public bool IsSpeaking => CanPlayTts() && audioSource != null && audioSource.isPlaying;

    public async Task WaitForPlaybackFinished()
    {
        await WaitForPlaybackFinished(playbackSessionId);
    }

    private async Task WaitForPlaybackFinished(int sessionId)
    {
        if (audioSource == null)
        {
            return;
        }

        await WaitForAudioFinish(audioSource, sessionId);
    }

    private async Task WaitForAudioFinish(AudioSource audio, int sessionId)
    {
        while (CanPlayTts()
            && sessionId == playbackSessionId
            && audio != null
            && audio.isPlaying)
        {
            await Task.Yield();
        }
    }

    private float[] ConvertWavToFloatArray(byte[] wavBytes)
    {
        int pos = 44;
        int sampleCount = (wavBytes.Length - pos) / 2;
        float[] samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short value = BitConverter.ToInt16(wavBytes, pos);
            samples[i] = value / 32768f;
            pos += 2;
        }
        return samples;
    }

    private static bool IsWavData(byte[] data)
    {
        return data != null
            && data.Length > 12
            && data[0] == (byte)'R'
            && data[1] == (byte)'I'
            && data[2] == (byte)'F'
            && data[3] == (byte)'F';
    }

    private static int ReadWavSampleRate(byte[] wavBytes)
    {
        if (wavBytes == null || wavBytes.Length < 28)
        {
            return 22050;
        }

        return BitConverter.ToInt32(wavBytes, 24);
    }

    private string BuildIdolSpeechInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        normalized = Regex.Replace(normalized, @"([.!?])\1+", "$1");
        normalized = Regex.Replace(normalized, @"([.!?~])\s+", "$1");
        normalized = Regex.Replace(normalized, @"\s+([.!?~])", "$1");

        if (localTtsConfig != null && localTtsConfig.koreanOnly)
        {
            normalized = ExtractKoreanOnlySpeech(normalized);
        }
        else
        {
            normalized = ExtractBilingualSpeech(normalized);
        }

        return normalized;
    }

    /// <summary>
    /// TTS 재생 가능 여부 — koreanOnly면 한글 필수, 아니면 한글 또는 영문.
    /// </summary>
    private static bool HasSpeakableTtsText(string text, bool koreanOnly)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (koreanOnly)
        {
            return HangulRegex.IsMatch(text);
        }

        return HangulRegex.IsMatch(text) || LatinLetterRegex.IsMatch(text);
    }

    /// <summary>
    /// 영문 비중이 더 크면 OpenAI TTS 우선 사용.
    /// </summary>
    private static bool IsPrimarilyEnglish(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        int hangul = 0;
        int latin = 0;
        foreach (char c in text)
        {
            if (c >= '\uAC00' && c <= '\uD7A3')
            {
                hangul++;
            }
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                latin++;
            }
        }

        return latin > 0 && latin >= hangul;
    }

    private static readonly Regex DisallowedKoreanSpeechRegex = new Regex(
        @"[^\uAC00-\uD7A3\s0-9.,!?~]+",
        RegexOptions.Compiled);

    private static readonly Regex DisallowedBilingualSpeechRegex = new Regex(
        @"[^\uAC00-\uD7A3A-Za-z\s0-9.,!?~']+",
        RegexOptions.Compiled);

    /// <summary>
    /// TTS 입력 — 한글·영문·숫자·기본 구두점 허용, 일본어·한자 등 제3언어 제거.
    /// </summary>
    private static string ExtractBilingualSpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = ForeignScriptRegex.Replace(text, " ");
        cleaned = DisallowedBilingualSpeechRegex.Replace(cleaned, " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (!HasSpeakableTtsText(cleaned, koreanOnly: false))
        {
            return string.Empty;
        }

        return cleaned;
    }

    /// <summary>
    /// TTS 입력 — 한글·숫자·기본 구두점만 남기고 영/중/일·특수문자 제거.
    /// </summary>
    private static string ExtractKoreanOnlySpeech(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string cleaned = LatinOrForeignScriptRegex.Replace(text, " ");
        cleaned = DisallowedKoreanSpeechRegex.Replace(cleaned, " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (!HangulRegex.IsMatch(cleaned))
        {
            return string.Empty;
        }

        return cleaned;
    }

    // TTS ?? ?? ?? (Assets ?? ?? ??)
    private const string TtsOutputFolder = "Assets/AI/ForChat/TTS_Output";

    /// <summary>
    /// MP3 ???? ????? ????? ????.
    /// ???: TTS_yyyyMMdd_HHmmss_fff.mp4 ? ??? ???? ?? ? ? ?? ??.
    /// ??? ??? ?? ????.
    /// </summary>
    private void SaveTtsOutput(byte[] audioData, string sourceText)
    {
        if (audioData == null || audioData.Length == 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(TtsOutputFolder))
            {
                Directory.CreateDirectory(TtsOutputFolder);
            }

            string ext = IsWavData(audioData) ? ".wav" : ".mp3";
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string filePath = Path.Combine(TtsOutputFolder, "TTS_" + timestamp + ext);

            if (File.Exists(filePath))
            {
                filePath = Path.Combine(TtsOutputFolder, "TTS_" + timestamp + "_1" + ext);
            }

            File.WriteAllBytes(filePath, audioData);
            Debug.Log("[TTS] Saved: " + filePath + " (" + audioData.Length + " bytes)  src=\"" +
                      (sourceText.Length > 40 ? sourceText.Substring(0, 40) + "..." : sourceText) + "\"");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TTS] Save failed: " + ex.Message);
        }
    }

    private static readonly string CachedTtsInstructions =
        "Speak only in Korean. Cute bright female voice in her twenties, "
        + "F0 around 220-260Hz, clear tone with subtle nasal charm, lively but not childish.";

    private string BuildTTSInstructions() => CachedTtsInstructions;

}