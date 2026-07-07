using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

#region Response Models

[System.Serializable]
public class SearchResponse
{
    public List<SearchItem> items;
}

[System.Serializable]
public class SearchItem
{
    public SearchId id;
}

[System.Serializable]
public class SearchId
{
    public string videoId;
}

[System.Serializable]
public class VideoResponse
{
    public List<VideoItem> items;
}

[System.Serializable]
public class VideoItem
{
    public LiveStreamingDetails liveStreamingDetails;
    public VideoSnippet snippet;
    public VideoStatistics statistics;
}

[System.Serializable]
public class VideoSnippet
{
    public string liveBroadcastContent;
}

[System.Serializable]
public class LiveStreamingDetails
{
    public string activeLiveChatId;
    public string concurrentViewers;
}

public class VideoStatistics
{
    public string viewCount;
}

public class YoutubeChat
{
    public string MessageId;
    public string Author;
    public string AuthorChannelId;
    public string Message;
}

public class LiveChatResponse
{
    public List<Item> items;
    public int pollingIntervalMillis;
    public string nextPageToken;
    public string offlineAt;

    public class Item
    {
        public string id;
        public Snippet snippet;
        public AuthorDetails authorDetails;
    }

    public class Snippet
    {
        public string displayMessage;
    }

    public class AuthorDetails
    {
        public string channelId;
        public string displayName;
    }
}

#endregion

/// <summary>
/// YouTube Live Chat 폴링 → SendMessage 턴 큐(쿨다운·최신 1건) 연동.
/// </summary>
public class GetYoutubeLiveChat : MonoBehaviour
{
    [Header("YouTube Channel / Video")]
    public string channelId = "";
    [Tooltip("지정 시 search API 생략 (quota 절약)")]
    public string videoId = "";

    [Header("Poll / Reply")]
    public bool chat_start = false;
    public bool autoStartOnPlay = true;
    [Tooltip("liveChat.messages.list 최소 간격(초). API pollingIntervalMillis 와 max. quota: 호출당 5")]
    public float minPollIntervalSeconds = 5f;
    [Tooltip("라이브 미감지 시 search/videos 재시도 간격(초). quota: search 100 + videos 1")]
    public float liveDiscoveryIntervalSeconds = 45f;
    [Tooltip("quota 403 시 초기 백오프(초), 성공 시 리셋")]
    public float quotaBackoffInitialSeconds = 120f;
    [Tooltip("SendMessage에 넘기는 LLM 턴 최소 간격(초)")]
    public float replyCooldownSeconds = 20f;
    public int minMessageLength = 2;
    public int maxQueueSize = 32;
    public bool requireMentionOrQuestion = false;

    [Tooltip("videos.list concurrentViewers 폴링 간격(초). quota 1/회")]
    public float viewerPollIntervalSeconds = 60f;

    [Header("References")]
    public SendMessage sendMessage;
    public SelfTalk selfTalk;

    private string apiKey;
    private string liveChatId;
    private string pageToken;
    private float pollIntervalSeconds = 5f;
    private float nextPollTime;
    private float nextLiveDiscoveryTime;
    private float nextReplyDispatchTime;
    private bool liveChatReady;
    private bool fetching;
    private int consecutiveFetchFailures;
    private string resolvedVideoId;
    private float quotaBackoffSeconds = 120f;
    private float lastQuotaWarningLogTime = -999f;

    private const int MaxFetchFailuresBeforeReset = 5;
    private const float MaxQuotaBackoffSeconds = 900f;
    private const float QuotaWarningLogCooldownSeconds = 60f;

    private readonly Queue<YoutubeChat> messageQueue = new Queue<YoutubeChat>();
    private readonly HashSet<string> seenMessageIds = new HashSet<string>();
    private readonly Queue<string> seenMessageIdOrder = new Queue<string>();
    private const int MaxSeenMessageIds = 512;
    private const int MaxRecentChatRing = 40;
    private const int MaxSnapshotChatLines = 12;
    private const int MaxSnapshotMessageChars = 40;
    private const int MaxGreetingsPerMinute = 3;
    private const string YouTubeApiRoot = "https://www.googleapis.com/youtube/v3/";
    private const string LiveChatMessagesPath = "liveChat/messages";

    public int CurrentConcurrentViewers { get; private set; }
    public DateTime LastViewerCountUtc { get; private set; } = DateTime.MinValue;

    private readonly Queue<YoutubeChat> recentChatRing = new Queue<YoutubeChat>();
    private readonly HashSet<string> knownViewerChannelIds = new HashSet<string>();
    private readonly HashSet<string> greetedViewerChannelIds = new HashSet<string>();
    private readonly Queue<DateTime> recentGreetingUtcTimes = new Queue<DateTime>();
    private float nextViewerPollTime;

    private void Start()
    {
        LoadApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            Debug.LogError("[YT] API 키가 설정되지 않았습니다.");
            return;
        }

        TryAutoWireReferences();

        if (autoStartOnPlay)
        {
            chat_start = true;
        }

        pollIntervalSeconds = Mathf.Max(1f, minPollIntervalSeconds);
        quotaBackoffSeconds = Mathf.Max(30f, quotaBackoffInitialSeconds);
    }

    private void OnDisable()
    {
        chat_start = false;
    }

    private void OnApplicationQuit()
    {
        chat_start = false;
        TTSRequester.MarkApplicationQuitting();
    }

    public void TryAutoWireReferences()
    {
        if (sendMessage == null)
        {
            sendMessage = FindFirstObjectByType<SendMessage>();
        }

        if (selfTalk == null)
        {
            selfTalk = FindFirstObjectByType<SelfTalk>();
        }
    }

    private void LoadApiKey()
    {
        string path = @"Assets/AI/ForChat/yt key.txt";
        if (!File.Exists(path))
        {
            Debug.LogError("[YT] API 키 파일이 존재하지 않습니다.");
            return;
        }

        apiKey = File.ReadAllText(path).Trim();
    }

    private async void Update()
    {
        if (!chat_start || string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        if (Time.time >= nextPollTime)
        {
            nextPollTime = Time.time + pollIntervalSeconds;
            await PollLiveChatStepAsync();
        }

        TryDispatchReplyToSendMessage();
    }

    private async Task PollLiveChatStepAsync()
    {
        if (fetching)
        {
            return;
        }

        fetching = true;
        try
        {
            await PollLiveChatStepCoreAsync();
        }
        catch (YouTubeHttpException ex)
        {
            HandleYouTubeHttpException(ex, "PollLiveChat");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[YT] PollLiveChat 예외: " + ex.Message);
        }
        finally
        {
            fetching = false;
        }
    }

    private async Task PollLiveChatStepCoreAsync()
    {
        if (!liveChatReady)
        {
            // 라이브 탐색은 짧은 간격으로 search(100 quota)를 반복하지 않는다.
            if (Time.time < nextLiveDiscoveryTime)
            {
                return;
            }

            nextLiveDiscoveryTime = Time.time + Mathf.Max(15f, liveDiscoveryIntervalSeconds);
            liveChatId = await ResolveLiveChatIdAsync();
            if (string.IsNullOrEmpty(liveChatId))
            {
                Debug.Log("[YT] 라이브 감지 중... (다음 탐색 " + liveDiscoveryIntervalSeconds + "초 후)");
                return;
            }

            liveChatReady = true;
            pageToken = null;
            consecutiveFetchFailures = 0;
            quotaBackoffSeconds = Mathf.Max(30f, quotaBackoffInitialSeconds);
            Debug.Log("[YT] LiveChatId 확보 videoId=" + resolvedVideoId + " chatId=" + liveChatId);
            pollIntervalSeconds = Mathf.Max(1f, minPollIntervalSeconds);
            nextPollTime = Time.time + pollIntervalSeconds;
            return;
        }

        LiveChatFetchResult result = await FetchChatPageAsync();
        if (result == null)
        {
            return;
        }

        consecutiveFetchFailures = 0;
        quotaBackoffSeconds = Mathf.Max(30f, quotaBackoffInitialSeconds);

        pollIntervalSeconds = Mathf.Max(minPollIntervalSeconds, result.PollingIntervalMillis / 1000f);
        nextPollTime = Time.time + pollIntervalSeconds;

        if (!string.IsNullOrEmpty(result.OfflineAt))
        {
            Debug.Log("[YT] 라이브 채팅 종료 — 재탐색");
            ResetLiveChatSession();
            return;
        }

        if (!string.IsNullOrEmpty(result.NextPageToken))
        {
            pageToken = result.NextPageToken;
        }

        if (result.NewMessages != null)
        {
            for (int i = 0; i < result.NewMessages.Count; i++)
            {
                EnqueueNewMessage(result.NewMessages[i]);
            }
        }

        if (liveChatReady && !string.IsNullOrEmpty(resolvedVideoId) && Time.time >= nextViewerPollTime)
        {
            nextViewerPollTime = Time.time + Mathf.Max(15f, viewerPollIntervalSeconds);
            await PollConcurrentViewersAsync();
        }
    }

    private void TryDispatchReplyToSendMessage()
    {
        if (messageQueue.Count == 0 || sendMessage == null)
        {
            return;
        }

        if (Time.time < nextReplyDispatchTime)
        {
            return;
        }

        if (!sendMessage.CanAcceptYoutubeChat)
        {
            return;
        }

        YoutubeChat latest = DequeueLatestMessage();
        if (latest == null)
        {
            return;
        }

        selfTalk?.NotifyUserInput();
        bool accepted = sendMessage.EnqueueYoutubeChat(latest.Author, latest.AuthorChannelId, latest.Message);
        if (accepted)
        {
            nextReplyDispatchTime = Time.time + replyCooldownSeconds;
            Debug.Log("[YT] → SendMessage: " + latest.Author + " (" + latest.AuthorChannelId + "): " + latest.Message);
        }
        else
        {
            messageQueue.Enqueue(latest);
        }
    }

    private YoutubeChat DequeueLatestMessage()
    {
        if (messageQueue.Count == 0)
        {
            return null;
        }

        YoutubeChat latest = null;
        while (messageQueue.Count > 0)
        {
            latest = messageQueue.Dequeue();
        }

        return latest;
    }

    private void EnqueueNewMessage(YoutubeChat chat)
    {
        if (chat == null || string.IsNullOrEmpty(chat.MessageId))
        {
            return;
        }

        if (seenMessageIds.Contains(chat.MessageId))
        {
            return;
        }

        RememberMessageId(chat.MessageId);

        if (!ChatPersonaDefense.TryAcceptChatMessage(chat.Message, out string sanitized, "[YT]"))
        {
            selfTalk?.NotifyUserInput();
            string turnSource = BuildYoutubeTurnSource(chat.AuthorChannelId, chat.Author);
            sendMessage?.SchedulePersonaBreakRefusal(chat.Author, turnSource);
            return;
        }

        if (sanitized.Length < minMessageLength)
        {
            return;
        }

        if (requireMentionOrQuestion && !PassesMentionOrQuestionFilter(sanitized))
        {
            return;
        }

        AppendRecentChatRing(chat.Author, chat.AuthorChannelId, sanitized);

        string cid = chat.AuthorChannelId?.Trim() ?? string.Empty;
        bool isFirstChat = !string.IsNullOrWhiteSpace(cid) && knownViewerChannelIds.Add(cid);
        if (isFirstChat && !greetedViewerChannelIds.Contains(cid))
        {
            ScheduleNewViewerGreeting(chat.Author, cid);
            Debug.Log("[YT] " + chat.Author + " (first chat → greeting): " + sanitized);
            return;
        }

        messageQueue.Enqueue(new YoutubeChat
        {
            MessageId = chat.MessageId,
            Author = chat.Author,
            AuthorChannelId = chat.AuthorChannelId,
            Message = sanitized
        });
        TrimMessageQueue();
        Debug.Log("[YT] " + chat.Author + ": " + sanitized);
    }

    private void AppendRecentChatRing(string author, string channelId, string message)
    {
        recentChatRing.Enqueue(new YoutubeChat
        {
            Author = author,
            AuthorChannelId = channelId,
            Message = message
        });

        while (recentChatRing.Count > MaxRecentChatRing)
        {
            recentChatRing.Dequeue();
        }
    }

    /// <summary>LLM 프롬프트용 라이브방송 스냅샷 — 시청자 수·최근 채팅.</summary>
    public string BuildLiveRoomSnapshot()
    {
        if (!liveChatReady && recentChatRing.Count == 0 && CurrentConcurrentViewers <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(512);
        sb.Append("[라이브방송] 시청자 ").Append(Mathf.Max(0, CurrentConcurrentViewers)).Append("명");
        if (recentChatRing.Count > 0)
        {
            sb.Append(" | 최근채팅: ");
            int lineCount = 0;
            foreach (YoutubeChat chat in recentChatRing)
            {
                if (lineCount >= MaxSnapshotChatLines)
                {
                    break;
                }

                if (lineCount > 0)
                {
                    sb.Append(" | ");
                }

                string nick = string.IsNullOrWhiteSpace(chat.Author) ? "시청자" : chat.Author.Trim();
                if (nick.Length > 12)
                {
                    nick = nick.Substring(0, 12);
                }

                string msg = chat.Message ?? string.Empty;
                if (msg.Length > MaxSnapshotMessageChars)
                {
                    msg = msg.Substring(0, MaxSnapshotMessageChars) + "…";
                }

                sb.Append(nick).Append(':').Append(msg);
                lineCount++;
            }
        }

        sb.Append(". ");
        return sb.ToString();
    }

    private void ScheduleNewViewerGreeting(string displayName, string channelId)
    {
        if (sendMessage == null || string.IsNullOrWhiteSpace(channelId))
        {
            return;
        }

        PruneRecentGreetingTimes();
        if (recentGreetingUtcTimes.Count >= MaxGreetingsPerMinute)
        {
            return;
        }

        bool accepted = sendMessage.EnqueueNewViewerGreetingTurn(displayName, channelId);
        if (accepted)
        {
            greetedViewerChannelIds.Add(channelId.Trim());
            recentGreetingUtcTimes.Enqueue(DateTime.UtcNow);
            Debug.Log("[YT] New viewer greeting scheduled: " + displayName + " (" + channelId + ")");
        }
    }

    private void PruneRecentGreetingTimes()
    {
        DateTime cutoff = DateTime.UtcNow.AddMinutes(-1);
        while (recentGreetingUtcTimes.Count > 0 && recentGreetingUtcTimes.Peek() < cutoff)
        {
            recentGreetingUtcTimes.Dequeue();
        }
    }

    private async Task PollConcurrentViewersAsync()
    {
        if (string.IsNullOrEmpty(resolvedVideoId))
        {
            return;
        }

        try
        {
            string videoUrl = BuildApiUrl("videos", new Dictionary<string, string>
            {
                { "part", "liveStreamingDetails,statistics" },
                { "id", resolvedVideoId },
                { "key", apiKey }
            });

            string videoJson = await GetYouTubeJsonAsync(videoUrl, "videos.list.viewers");
            VideoResponse videoObj = JsonConvert.DeserializeObject<VideoResponse>(videoJson);
            if (videoObj?.items == null || videoObj.items.Count == 0)
            {
                return;
            }

            string viewersRaw = videoObj.items[0].liveStreamingDetails?.concurrentViewers;
            if (!string.IsNullOrWhiteSpace(viewersRaw) && int.TryParse(viewersRaw, out int viewers))
            {
                CurrentConcurrentViewers = viewers;
                LastViewerCountUtc = DateTime.UtcNow;
            }
        }
        catch (YouTubeHttpException ex)
        {
            HandleYouTubeHttpException(ex, "PollViewers");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[YT] PollViewers 예외: " + ex.Message);
        }
    }

    private static bool PassesMentionOrQuestionFilter(string message)
    {
        if (message.IndexOf('?') >= 0 || message.IndexOf('？') >= 0)
        {
            return true;
        }

        return message.IndexOf("이나", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void RememberMessageId(string messageId)
    {
        seenMessageIds.Add(messageId);
        seenMessageIdOrder.Enqueue(messageId);
        while (seenMessageIdOrder.Count > MaxSeenMessageIds)
        {
            string old = seenMessageIdOrder.Dequeue();
            seenMessageIds.Remove(old);
        }
    }

    private void TrimMessageQueue()
    {
        while (messageQueue.Count > maxQueueSize)
        {
            messageQueue.Dequeue();
        }
    }

    private void ResetLiveChatSession()
    {
        liveChatReady = false;
        liveChatId = null;
        pageToken = null;
        resolvedVideoId = null;
        consecutiveFetchFailures = 0;
        nextLiveDiscoveryTime = Time.time + Mathf.Max(15f, liveDiscoveryIntervalSeconds);
        messageQueue.Clear();
        recentChatRing.Clear();
    }

    private static bool IsQuotaExceededError(string responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
        {
            return false;
        }

        return responseBody.IndexOf("exceeded your", StringComparison.OrdinalIgnoreCase) >= 0
            || responseBody.IndexOf("quotaExceeded", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void ApplyQuotaBackoff(string detail)
    {
        quotaBackoffSeconds = Mathf.Min(
            quotaBackoffSeconds * 2f,
            MaxQuotaBackoffSeconds);
        pollIntervalSeconds = quotaBackoffSeconds;
        nextPollTime = Time.time + pollIntervalSeconds;
        nextLiveDiscoveryTime = nextPollTime;

        if (Time.time - lastQuotaWarningLogTime >= QuotaWarningLogCooldownSeconds)
        {
            lastQuotaWarningLogTime = Time.time;
            Debug.LogWarning(
                "[YT] Quota exceeded — " + pollIntervalSeconds + "초 후 재시도. "
                + "Google Cloud Console → YouTube Data API v3 할당량 확인. "
                + "Inspector videoId 지정 시 search(100) 절약. "
                + detail);
        }
    }

    /// <summary>videos.list / search / PollLiveChat 등 공통 HTTP 오류 — 예외가 Update까지 전파되지 않게 처리.</summary>
    private void HandleYouTubeHttpException(YouTubeHttpException ex, string context)
    {
        if (ex.StatusCode == 403)
        {
            if (IsQuotaExceededError(ex.ResponseBody))
            {
                ApplyQuotaBackoff(context + ": " + TrimErrorBody(ex.ResponseBody));
            }
            else
            {
                Debug.LogWarning("[YT] " + context + " 403: " + TrimErrorBody(ex.ResponseBody));
                pollIntervalSeconds = Mathf.Max(minPollIntervalSeconds, 30f);
                nextPollTime = Time.time + pollIntervalSeconds;
                nextLiveDiscoveryTime = nextPollTime;
            }

            return;
        }

        Debug.LogWarning("[YT] " + context + " HTTP " + ex.StatusCode + ": " + TrimErrorBody(ex.ResponseBody));
        nextLiveDiscoveryTime = Time.time + Mathf.Max(liveDiscoveryIntervalSeconds, pollIntervalSeconds);
    }

    private static string BuildApiUrl(string path, Dictionary<string, string> query)
    {
        var sb = new StringBuilder(YouTubeApiRoot);
        sb.Append(path);
        sb.Append('?');
        bool first = true;
        foreach (KeyValuePair<string, string> kv in query)
        {
            if (string.IsNullOrEmpty(kv.Value))
            {
                continue;
            }

            if (!first)
            {
                sb.Append('&');
            }

            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key));
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(kv.Value));
        }

        return sb.ToString();
    }

    private static string MaskApiKeyInUrl(string url, string key)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(url))
        {
            return url;
        }

        return url.Replace(key, "***");
    }

    private static async Task AwaitWebRequest(UnityWebRequestAsyncOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.TrySetResult(true);
        await tcs.Task;
    }

    private async Task<string> GetYouTubeJsonAsync(string url, string logLabel)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            await AwaitWebRequest(request.SendWebRequest());

            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (string.IsNullOrEmpty(body) && request.downloadHandler?.data != null && request.downloadHandler.data.Length > 0)
            {
                body = Encoding.UTF8.GetString(request.downloadHandler.data);
            }
            long code = request.responseCode;

            if (request.result == UnityWebRequest.Result.Success)
            {
                return body;
            }

            string transportError = string.IsNullOrEmpty(request.error) ? "(no transport error)" : request.error;
            Debug.LogWarning("[YT] " + logLabel + " HTTP " + code + " transport=" + transportError
                + " url=" + MaskApiKeyInUrl(url, apiKey)
                + " body=" + TrimErrorBody(body));

            throw new YouTubeHttpException((int)code, body, transportError);
        }
    }

    private sealed class YouTubeHttpException : Exception
    {
        public int StatusCode { get; }
        public string ResponseBody { get; }
        public string TransportError { get; }

        public YouTubeHttpException(int statusCode, string responseBody, string transportError)
            : base("YouTube HTTP " + statusCode)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody ?? string.Empty;
            TransportError = transportError ?? string.Empty;
        }
    }

    private async Task<string> ResolveLiveChatIdAsync()
    {
        resolvedVideoId = string.IsNullOrWhiteSpace(videoId)
            ? await SearchLiveVideoIdAsync(channelId)
            : videoId.Trim();

        if (string.IsNullOrEmpty(resolvedVideoId))
        {
            return null;
        }

        string videoUrl = BuildApiUrl("videos", new Dictionary<string, string>
        {
            { "part", "liveStreamingDetails,snippet" },
            { "id", resolvedVideoId },
            { "key", apiKey }
        });

        string videoJson = await GetYouTubeJsonAsync(videoUrl, "videos.list");
        VideoResponse videoObj = JsonConvert.DeserializeObject<VideoResponse>(videoJson);
        if (videoObj?.items == null || videoObj.items.Count == 0)
        {
            Debug.LogWarning("[YT] videos.list 결과 없음 videoId=" + resolvedVideoId);
            return null;
        }

        VideoItem item = videoObj.items[0];
        string broadcastState = item.snippet?.liveBroadcastContent ?? "unknown";
        string chatId = item.liveStreamingDetails?.activeLiveChatId;

        if (broadcastState != "live")
        {
            Debug.Log("[YT] videoId=" + resolvedVideoId + " state=" + broadcastState
                + " — actual live 전까지 대기");
            return null;
        }

        if (string.IsNullOrEmpty(chatId))
        {
            Debug.LogWarning("[YT] live 중이지만 activeLiveChatId 없음 — YouTube Studio에서 「라이브 채팅」 활성화 확인. videoId="
                + resolvedVideoId);
            return null;
        }

        return chatId;
    }

    private async Task<string> SearchLiveVideoIdAsync(string targetChannelId)
    {
        string searchUrl = BuildApiUrl("search", new Dictionary<string, string>
        {
            { "part", "id" },
            { "channelId", targetChannelId },
            { "eventType", "live" },
            { "type", "video" },
            { "key", apiKey }
        });

        string searchJson = await GetYouTubeJsonAsync(searchUrl, "search.list");
        SearchResponse searchObj = JsonConvert.DeserializeObject<SearchResponse>(searchJson);
        if (searchObj?.items == null || searchObj.items.Count == 0)
        {
            return null;
        }

        return searchObj.items[0].id?.videoId;
    }

    private async Task<LiveChatFetchResult> FetchChatPageAsync()
    {
        if (string.IsNullOrEmpty(liveChatId))
        {
            return null;
        }

        try
        {
            var query = new Dictionary<string, string>
            {
                { "liveChatId", liveChatId },
                { "part", "id,snippet,authorDetails" },
                { "maxResults", "50" },
                { "key", apiKey }
            };

            if (!string.IsNullOrEmpty(pageToken))
            {
                query["pageToken"] = pageToken;
            }

            string url = BuildApiUrl(LiveChatMessagesPath, query);
            string json = await GetYouTubeJsonAsync(url, "liveChat.messages.list");
            LiveChatResponse res = JsonConvert.DeserializeObject<LiveChatResponse>(json);
            if (res == null)
            {
                return null;
            }

            List<YoutubeChat> newMessages = new List<YoutubeChat>();
            if (res.items != null)
            {
                for (int i = 0; i < res.items.Count; i++)
                {
                    LiveChatResponse.Item item = res.items[i];
                    if (item?.snippet == null || item.authorDetails == null)
                    {
                        continue;
                    }

                    newMessages.Add(new YoutubeChat
                    {
                        MessageId = item.id,
                        Author = item.authorDetails.displayName,
                        AuthorChannelId = item.authorDetails.channelId,
                        Message = item.snippet.displayMessage
                    });
                }
            }

            return new LiveChatFetchResult
            {
                NewMessages = newMessages,
                NextPageToken = res.nextPageToken,
                PollingIntervalMillis = res.pollingIntervalMillis > 0 ? res.pollingIntervalMillis : 5000,
                OfflineAt = res.offlineAt
            };
        }
        catch (YouTubeHttpException ex)
        {
            if (ex.StatusCode == 400 && ex.ResponseBody.IndexOf("pageTokenInvalid", StringComparison.Ordinal) >= 0)
            {
                Debug.LogWarning("[YT] pageToken 무효 — 토큰 초기화 후 재시도");
                pageToken = null;
                pollIntervalSeconds = Mathf.Max(1f, minPollIntervalSeconds);
                nextPollTime = Time.time + pollIntervalSeconds;
                return null;
            }

            consecutiveFetchFailures++;
            if (ex.StatusCode == 403)
            {
                if (IsQuotaExceededError(ex.ResponseBody))
                {
                    ApplyQuotaBackoff(TrimErrorBody(ex.ResponseBody));
                }
                else
                {
                    Debug.LogWarning("[YT] 403 — quota/권한: " + TrimErrorBody(ex.ResponseBody));
                    pollIntervalSeconds = Mathf.Max(minPollIntervalSeconds, 30f);
                    nextPollTime = Time.time + pollIntervalSeconds;
                }

                return null;
            }

            if (ex.StatusCode == 404)
            {
                string detail = TrimErrorBody(ex.ResponseBody);
                if (detail == "(empty)" && !string.IsNullOrEmpty(ex.TransportError))
                {
                    detail = ex.TransportError;
                }

                Debug.LogWarning("[YT] 404 liveChatMessages ("
                    + consecutiveFetchFailures + "/" + MaxFetchFailuresBeforeReset + "): " + detail);
                pollIntervalSeconds = 3f;
                nextPollTime = Time.time + pollIntervalSeconds;

                if (consecutiveFetchFailures >= MaxFetchFailuresBeforeReset)
                {
                    Debug.LogWarning("[YT] 채팅 404 반복 — 세션 재탐색. YouTube Studio에서 「라이브 채팅」 활성·실제 live 상태 확인.");
                    ResetLiveChatSession();
                }

                return null;
            }

            Debug.LogWarning("[YT] HTTP " + ex.StatusCode + ": " + TrimErrorBody(ex.ResponseBody));
            return null;
        }
    }

    private static string BuildYoutubeTurnSource(string authorChannelId, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(authorChannelId))
        {
            return "youtube:" + authorChannelId.Trim();
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "youtube:unknown";
        }

        return "youtube:" + displayName.Trim();
    }

    private static string TrimErrorBody(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "(empty)";
        }

        return body.Length <= 280 ? body : body.Substring(0, 280) + "...";
    }

    private class LiveChatFetchResult
    {
        public List<YoutubeChat> NewMessages;
        public string NextPageToken;
        public int PollingIntervalMillis;
        public string OfflineAt;
    }
}
