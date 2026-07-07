using Newtonsoft.Json;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

public class OpenAITTSProvider : ITTSProvider
{
    private const string Endpoint = "https://api.openai.com/v1/audio/speech";

    public async Task<byte[]> SynthesizeAsync(TTSSynthesisRequest request, string apiKey)
    {
        if (!TTSRequester.IsTtsAllowed)
        {
            return null;
        }

        var payload = new
        {
            model = request.model,
            voice = request.voice,
            input = request.input,
            speed = request.speed,
            instructions = request.instructions
        };

        byte[] jsonBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));

        using (UnityWebRequest www = new UnityWebRequest(Endpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(jsonBytes);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", "Bearer " + apiKey);

            var op = www.SendWebRequest();
            while (!op.isDone)
            {
                if (!TTSRequester.IsTtsAllowed)
                {
                    www.Abort();
                    return null;
                }

                await Task.Yield();
            }

            if (!TTSRequester.IsTtsAllowed)
            {
                return null;
            }

            if (www.result != UnityWebRequest.Result.Success)
            {
                throw new System.Exception("[AI Nia] TTS 요청 실패: " + www.error);
            }

            return www.downloadHandler.data;
        }
    }
}
