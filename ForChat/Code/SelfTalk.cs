using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Idle 기반 능동 혼잘말 + Editor Play Body ML 학습 턴 카운터·자동 재학습.
/// LLM/청크/SL/로그는 SendMessage 파이프라인을 재사용한다.
/// </summary>
public class SelfTalk : MonoBehaviour
{
    [Header("References")]
    public SendMessage sendMessage;
    public GetYoutubeLiveChat youtubeLiveChat;

    [Header("Idle Proactive Self-Talk")]
    [Tooltip("사용자 입력 없을 때 혼잘말 시작까지 대기(초)")]
    public float idleThresholdSeconds = 180f;
    [Tooltip("혼잘말 턴 종료 후 다음 턴까지 최소 대기(초)")]
    public float cooldownAfterTurnSeconds = 45f;
    [Tooltip("직전 혼잘말 후 이 시간(초) 안이면 연속 monologue 후보")]
    public float continuationWindowSeconds = 30f;
    [Tooltip("연속 monologue 턴 사이 최소 대기(초) — idleThreshold보다 짧게")]
    public float continuationCooldownSeconds = 8f;
    [Tooltip("한 번의 idle 구간에서 연속 혼잘말 최대 턴 수")]
    public int maxConsecutiveSelfTalkTurns = 2;
    [Tooltip("barge-in 직후 proactive SelfTalk 재시작까지 대기(초)")]
    public float interruptSelfTalkCooldownSeconds = 3f;
    public bool enableProactiveSelfTalk = true;

    [Header("Optional Topic / Emotion")]
    public List<string> topicPool = new List<string>();
    public List<string> emotionHints = new List<string>();

    [Header("Body ML Training Collection (Editor Play)")]
    public bool enableTrainingCollection = false;
    public int retrainTurnThresholdMin = 50;
    public int retrainTurnThresholdMax = 100;
    [Tooltip("0이면 min~max 사이에서 Start 시 1회 결정")]
    public int retrainAtTurnCount = 0;

    [Header("Runtime Debug (Read Only)")]
    public bool isSelfTalkCompleted = true;
    public int validTrainingTurnCount;
    public int retrainTargetTurnCount;
    public bool isRetrainInProgress;
    public int consecutiveSelfTalkCount;

    private float lastUserInputTime;
    private float lastSelfTalkEndTime;
    private float lastInterruptTime = -999f;
    private bool selfTalkTurnInProgress;
    private string lastSelfTalkResponseSnippet = string.Empty;
    private int lastTopicPoolIndex = -1;
    private readonly System.Random topicRandom = new System.Random();

    /// <summary>연속 monologue 구간 — developer_input 잠금용.</summary>
    public bool IsActiveSelfTalkSession()
    {
        if (selfTalkTurnInProgress)
        {
            return true;
        }

        if (sendMessage != null && sendMessage.IsConversationBusy)
        {
            return true;
        }

        if (consecutiveSelfTalkCount > 0
            && Time.time - lastSelfTalkEndTime < continuationWindowSeconds)
        {
            return true;
        }

        return false;
    }

    private void Start()
    {
        lastUserInputTime = Time.time;
        lastSelfTalkEndTime = -cooldownAfterTurnSeconds;
        ResolveRetrainTargetTurnCount();

        if (sendMessage == null)
        {
            sendMessage = FindFirstObjectByType<SendMessage>();
        }

        if (youtubeLiveChat == null)
        {
            youtubeLiveChat = FindFirstObjectByType<GetYoutubeLiveChat>();
        }

        if (sendMessage != null)
        {
            sendMessage.OnMotionTurnLogged += HandleMotionTurnLogged;
            sendMessage.OnSelfTalkTurnCompleted += HandleSelfTalkTurnCompleted;
            sendMessage.OnConversationInterrupted += HandleConversationInterrupted;
        }
    }

    private void OnDestroy()
    {
        if (sendMessage != null)
        {
            sendMessage.OnMotionTurnLogged -= HandleMotionTurnLogged;
            sendMessage.OnSelfTalkTurnCompleted -= HandleSelfTalkTurnCompleted;
            sendMessage.OnConversationInterrupted -= HandleConversationInterrupted;
        }
    }

    private void HandleConversationInterrupted(string reason)
    {
        selfTalkTurnInProgress = false;
        consecutiveSelfTalkCount = 0;
        isSelfTalkCompleted = true;
        lastInterruptTime = Time.time;
    }

    private void Update()
    {
        if (!enableProactiveSelfTalk || sendMessage == null)
        {
            return;
        }

        if (isRetrainInProgress || selfTalkTurnInProgress)
        {
            return;
        }

        if (sendMessage.IsConversationBusy || !isSelfTalkCompleted)
        {
            return;
        }

        if (Time.time - lastInterruptTime < interruptSelfTalkCooldownSeconds)
        {
            return;
        }

        float now = Time.time;
        float sinceLastSelfTalk = now - lastSelfTalkEndTime;
        bool userSpokeAfterLastSelfTalk = lastUserInputTime >= lastSelfTalkEndTime;

        // 연속 monologue: 직전 혼잘말 직후·사용자 미개입·짧은 cooldown 경과
        if (consecutiveSelfTalkCount > 0
            && consecutiveSelfTalkCount < maxConsecutiveSelfTalkTurns
            && !userSpokeAfterLastSelfTalk
            && sinceLastSelfTalk >= continuationCooldownSeconds
            && sinceLastSelfTalk < continuationWindowSeconds)
        {
            _ = TriggerSelfTalkAsync(isContinuation: true);
            return;
        }

        if (sinceLastSelfTalk < cooldownAfterTurnSeconds)
        {
            return;
        }

        if (now - lastUserInputTime < idleThresholdSeconds)
        {
            return;
        }

        _ = TriggerSelfTalkAsync(isContinuation: false);
    }

    /// <summary>
    /// SendMessage에서 사용자 Enter 입력 시 호출 — idle·연속 monologue 타이머 리셋.
    /// </summary>
    public void NotifyUserInput()
    {
        lastUserInputTime = Time.time;
        consecutiveSelfTalkCount = 0;
        lastSelfTalkResponseSnippet = string.Empty;
    }

    private void HandleSelfTalkTurnCompleted(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return;
        }

        string flat = responseText.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        const int maxChars = 280;
        lastSelfTalkResponseSnippet = flat.Length <= maxChars
            ? flat
            : flat.Substring(0, maxChars) + "…";
    }

    private void HandleMotionTurnLogged(MotionTurnLogSummary summary)
    {
        if (!enableTrainingCollection || summary == null)
        {
            return;
        }

        if (!summary.IsValidTrainingTurn)
        {
            return;
        }

        validTrainingTurnCount++;
        Debug.Log("[SelfTalk] valid training turn " + validTrainingTurnCount + "/" + retrainTargetTurnCount);

        if (validTrainingTurnCount >= retrainTargetTurnCount && !isRetrainInProgress)
        {
            StartCoroutine(RunRetrainWhenThresholdReached());
        }
    }

    private async Task TriggerSelfTalkAsync(bool isContinuation)
    {
        if (selfTalkTurnInProgress || sendMessage == null)
        {
            return;
        }

        while (sendMessage.IsConversationBusy)
        {
            await Task.Delay(100);
        }

        selfTalkTurnInProgress = true;
        try
        {
            SelfTalkTurnContext ctx = BuildTurnContext(isContinuation);
            string prompt;
            if (isContinuation)
            {
                prompt = "직전 monologue를 같은 흐름으로 이어서 말해줘.";
            }
            else
            {
                prompt = string.IsNullOrWhiteSpace(ctx.topicHint)
                    ? "시청자에게 자연스럽게 혼잘말을 시작해줘."
                    : ctx.topicHint;
            }

            await sendMessage.RunSelfTalkTurnAsync(prompt, ctx);
            lastSelfTalkEndTime = Time.time;

            if (isContinuation)
            {
                consecutiveSelfTalkCount++;
            }
            else
            {
                consecutiveSelfTalkCount = 1;
            }
        }
        finally
        {
            selfTalkTurnInProgress = false;
        }
    }

    private SelfTalkTurnContext BuildTurnContext(bool isContinuation)
    {
        var ctx = new SelfTalkTurnContext
        {
            isContinuation = isContinuation
        };

        if (isContinuation)
        {
            ctx.previousMonologueHint = lastSelfTalkResponseSnippet;
        }
        else
        {
        if (topicPool != null && topicPool.Count > 0)
        {
            int idx = topicRandom.Next(0, topicPool.Count);
            if (topicPool.Count > 1 && idx == lastTopicPoolIndex)
            {
                idx = (idx + 1) % topicPool.Count;
            }

            lastTopicPoolIndex = idx;
            ctx.topicHint = topicPool[idx];
        }

            if (emotionHints != null && emotionHints.Count > 0)
            {
                int idx = topicRandom.Next(0, emotionHints.Count);
                ctx.emotionHint = emotionHints[idx];
            }
        }

        if (youtubeLiveChat == null)
        {
            youtubeLiveChat = FindFirstObjectByType<GetYoutubeLiveChat>();
        }

        if (youtubeLiveChat != null)
        {
            ctx.liveRoomContextHint = youtubeLiveChat.BuildLiveRoomSnapshot();
        }

        return ctx;
    }

    private void ResolveRetrainTargetTurnCount()
    {
        if (retrainAtTurnCount > 0)
        {
            retrainTargetTurnCount = retrainAtTurnCount;
            return;
        }

        int min = Mathf.Max(1, retrainTurnThresholdMin);
        int max = Mathf.Max(min, retrainTurnThresholdMax);
        retrainTargetTurnCount = topicRandom.Next(min, max + 1);
    }

    private IEnumerator RunRetrainWhenThresholdReached()
    {
#if UNITY_EDITOR
        isRetrainInProgress = true;
        bool previousProactive = enableProactiveSelfTalk;
        enableProactiveSelfTalk = false;
        Debug.Log("[SelfTalk] Retrain threshold reached (" + validTrainingTurnCount + "). Starting pipeline...");

        yield return null;

        bool ok = NINConversationRetrainRunner.RunPipeline(Debug.Log);
        if (ok)
        {
            validTrainingTurnCount = 0;
            ResolveRetrainTargetTurnCount();
            Debug.Log("[SelfTalk] Retrain OK. Next target=" + retrainTargetTurnCount + " turns. SL ONNX hot-reload applied.");
        }
        else
        {
            Debug.LogWarning("[SelfTalk] Retrain failed or blocked. Counter kept at " + validTrainingTurnCount);
        }

        enableProactiveSelfTalk = previousProactive;
        isRetrainInProgress = false;
#else
        Debug.LogWarning("[SelfTalk] Auto retrain is Editor-only.");
        yield break;
#endif
    }
}
