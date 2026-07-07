using Newtonsoft.Json;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

/// <summary>
/// 로컬 Supertonic 3 TTS — OpenAI 호환 /v1/audio/speech (WAV).
/// </summary>
public class LocalHttpTTSProvider : ITTSProvider
{
    private readonly LocalTtsConfig config;

    public LocalHttpTTSProvider(LocalTtsConfig cfg)
    {
        config = cfg ?? LocalTtsConfig.Load();
    }

    public Task<byte[]> SynthesizeAsync(TTSSynthesisRequest request, string apiKey)
    {
        return SynthesizeAsync(request, apiKey, TTSRequester.FetchGeneration);
    }

    public async Task<byte[]> SynthesizeAsync(TTSSynthesisRequest request, string apiKey, int fetchGeneration)
    {
        if (!TTSRequester.IsTtsAllowed)
        {
            return null;
        }

        if (TTSRequester.IsFetchCancelled(fetchGeneration))
        {
            return null;
        }

        var payloadObj = new
        {
            model = request.model,
            voice = string.IsNullOrWhiteSpace(request.voice) ? config.voice : request.voice,
            input = request.input,
            speed = request.speed > 0 ? request.speed : config.synthesisSpeed,
            instructions = request.instructions,
            total_steps = request.totalSteps > 0 ? (int?)request.totalSteps : null
        };

        byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payloadObj));
        int timeout = config.timeoutSeconds > 0 ? config.timeoutSeconds : 20;

        using (UnityWebRequest www = new UnityWebRequest(config.SpeechEndpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(jsonBytes);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = timeout;

            var op = www.SendWebRequest();
            while (!op.isDone)
            {
                if (!TTSRequester.IsTtsAllowed || TTSRequester.IsFetchCancelled(fetchGeneration))
                {
                    www.Abort();
                    return null;
                }

                await Task.Yield();
            }

            if (!TTSRequester.IsTtsAllowed || TTSRequester.IsFetchCancelled(fetchGeneration))
            {
                return null;
            }

            if (www.result != UnityWebRequest.Result.Success)
            {
                throw new System.Exception("[LocalTTS] 요청 실패: " + www.error + " " + www.downloadHandler.text);
            }

            return www.downloadHandler.data;
        }
    }
}
