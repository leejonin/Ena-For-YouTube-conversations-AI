using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// OpenAI GPT-4o-mini Chat Completions (Vision) API 호출.
/// key.txt(OpenAI) 사용 — TTS와 동일.
/// </summary>
public class OpenAIVisionChatProvider
{
    private const string Endpoint = "https://api.openai.com/v1/chat/completions";
    private const string Model = "gpt-4o-mini";

    private string apiKey = string.Empty;

    public void LoadApiKey()
    {
        const string path = @"Assets/AI/ForChat/key.txt";
        if (!File.Exists(path))
        {
            Debug.LogError("[OpenAIVision] key.txt not found: " + path);
            return;
        }

        foreach (string line in File.ReadAllLines(path))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                apiKey = line.Trim();
                break;
            }
        }
    }

    public bool HasApiKey => !string.IsNullOrEmpty(apiKey);

    /// <summary>
    /// GPT-4o-mini 대화 요청. includeVideo=true 시 마지막 user에 연속 프레임 JPEG 첨부(detail:low).
    /// </summary>
    public async Task<OpenAIVisionChatResult> RequestAsync(
        string systemPrompt,
        JArray openAiMessages,
        int maxTokens)
    {
        if (!HasApiKey)
        {
            Debug.LogError("[OpenAIVision] OpenAI API key is empty.");
            return null;
        }

        JArray messages = new JArray
        {
            new JObject { { "role", "system" }, { "content", systemPrompt } }
        };
        foreach (JToken msg in openAiMessages)
        {
            messages.Add(msg);
        }

        JObject payload = new JObject
        {
            { "model", Model },
            { "max_tokens", maxTokens },
            { "messages", messages }
        };

        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));

        using (UnityWebRequest request = new UnityWebRequest(Endpoint, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);

            var op = request.SendWebRequest();
            var tcs = new TaskCompletionSource<bool>();
            op.completed += _ => tcs.TrySetResult(true);
            await tcs.Task;

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[OpenAIVision] Request failed: " + request.error);
                Debug.LogError("[OpenAIVision] " + request.downloadHandler.text);
                return null;
            }

            JObject json = JObject.Parse(request.downloadHandler.text);
            string text = json["choices"]?[0]?["message"]?["content"]?.ToString();
            string finishReason = json["choices"]?[0]?["finish_reason"]?.ToString();

            if (string.IsNullOrEmpty(text))
            {
                Debug.LogError("[OpenAIVision] Empty response content.");
                return null;
            }

            return new OpenAIVisionChatResult
            {
                text = text,
                finishReason = finishReason
            };
        }
    }

    /// <summary>
    /// Anthropic snapshot → OpenAI messages. 마지막 user에 연속 프레임(영상) multimodal 첨부.
    /// </summary>
    public static JArray BuildOpenAIMessagesFromSnapshot(JArray snapshot, byte[][] jpegFrames, bool includeVideo)
    {
        JArray messages = new JArray();
        if (snapshot == null || snapshot.Count == 0)
            return messages;

        for (int i = 0; i < snapshot.Count; i++)
        {
            JObject orig = snapshot[i] as JObject;
            if (orig == null)
                continue;

            string role = orig["role"]?.ToString() ?? "user";
            string content = orig["content"]?.ToString() ?? string.Empty;
            bool isLast = i == snapshot.Count - 1;

            if (isLast && role == "user" && includeVideo && jpegFrames != null && jpegFrames.Length > 0)
            {
                JArray parts = new JArray
                {
                    new JObject
                    {
                        { "type", "text" },
                        { "text", content + " [첨부: 시간순 연속 카메라 프레임 " + jpegFrames.Length + "장]" }
                    }
                };

                for (int f = 0; f < jpegFrames.Length; f++)
                {
                    byte[] frame = jpegFrames[f];
                    if (frame == null || frame.Length == 0)
                        continue;

                    string b64 = Convert.ToBase64String(frame);
                    parts.Add(new JObject
                    {
                        { "type", "image_url" },
                        {
                            "image_url", new JObject
                            {
                                { "url", "data:image/jpeg;base64," + b64 },
                                { "detail", "low" }
                            }
                        }
                    });
                }

                messages.Add(new JObject
                {
                    { "role", "user" },
                    { "content", parts }
                });
            }
            else
            {
                messages.Add(new JObject
                {
                    { "role", role },
                    { "content", content }
                });
            }
        }

        return messages;
    }
}

public class OpenAIVisionChatResult
{
    public string text;
    public string finishReason;
}
