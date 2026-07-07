using EdgeTTS;
using LitJson;
using NAudio.Wave;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Globalization;
using TMPro;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Networking;
public class SendMessage : MonoBehaviour
{
    private const int StartupMemoryWindowDays = 2;
    private const int StartupMemoryMaxEntries = 24;
    // 런타임 + 시작 메모리 합산 최대 유지 턴 수 (초과 시 오래된 순 삭제)
    private const int MaxMessageHistoryCount = 80;
    private string apiKey = "";
    private string ena, emoji, role;

    private static string cleanedText;

    private List<JObject> messageHistory = new List<JObject>();
    // messageHistory 와 1:1 동기화 — 매 요청 CompactHistoryContent 재계산 비용 제거
    private List<string> messageHistoryCompact = new List<string>();
    public TMP_InputField developer_input;

    [Header("사용자 Input")]
    public TMP_InputField user_input;
    public UnityEngine.UI.Button newUserButton;

    private string currentUserName = "사용자1";
    private int    userCounter     = 1;

    // 한국어 자기소개 이름 감지 패턴
    private static readonly Regex NameIntroRegex = new Regex(
        @"(?:나는|저는|제이름은|내이름은|이름은)\s*([가-힣a-zA-Z]{1,8})\s*(?:이야|야|이에요|예요|입니다|이라고|라고|이고|고)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public FaceReact face;
    public TTSRequester TTS;
    public SelfTalk self_talk;
    public IdlePoseRuntimePlayer idlePoseRuntimePlayer;
    public NoPidHumanoidAgent noPidHumanoidAgent;
    public NINGazeController gazeController;
    [Tooltip("true면 청크 바디는 ONNX(SL)만 구동하고 IdlePoseRuntimePlayer는 스킵한다.")]
    public bool useNoPidSlInference = true;
    public BodyCommandExecutor bodyCommandExecutor;
    public string test_output = "";
    public ServerCommunication sc;

    [Header("카메라 Vision (GPT-4o-mini)")]
    [Tooltip("PhoneCameraVisionChat 연결 — usePhoneCameraVision ON 시 Claude 대신 GPT Vision 사용")]
    public PhoneCameraVisionChat phoneCameraVision;

    public List<string> TalkInputs;
    private readonly HashSet<string> controlEmojis = new HashSet<string>();
    // 시스템 프롬프트용: JSON 대신 이모지 문자열만 연결해 입력 토큰 절감
    private string compactEmojiList = string.Empty;
    // 턴 유형별 출력 토큰 상한
    private const int MaxTokensUserTurn = 4096;
    private const int MaxTokensSelfTalkTurn = 4096;
    private const int MaxTokensSearchFallback = 4096;
    // API 전송 히스토리: 최근 N메시지는 원문, 이전은 compact(200자) 전송
    private const int ApiFullHistoryRecentMessages = 20;
    // Anthropic prompt caching용 정적 시스템 프롬프트 베이스 (Start 시 1회 빌드)
    private string cachedSystemBaseUser = string.Empty;
    private string cachedSystemBaseSelfTalk = string.Empty;
    private string cachedPersonaShieldUser = string.Empty;
    private readonly SemanticGestureParser semanticGestureParser = new SemanticGestureParser();
    private readonly SpeechMotionChunkPipeline speechMotionChunkPipeline = new SpeechMotionChunkPipeline();
    // Enter 연타·async 중복 호출 시 SendRequest가 겹치지 않도록 한다.
    private bool isSendRequestInProgress;
    // 로컬 llama-server (OpenAI 호환) — 생방송용
    private LocalLlmConfig localLlmConfig;
    private LocalOpenAiCompatibleChatProvider localLlm;
    private bool localLlmVisionDisabledLogged;
    private LiveResourceProfile liveResourceProfile;
    private PersonaFewShotRotator personaFewShotRotator;
    private GetYoutubeLiveChat cachedYoutubeLiveChat;
    private int conversationTurnGeneration;
    private bool currentTurnIsNewViewerGreeting;
    private (string displayName, string channelId)? pendingNewViewerGreeting;

    /// <summary>마지막 barge-in 시각 — TTS debounce·rate limit용.</summary>
    public static DateTime LastBargeInInterruptUtc { get; private set; } = DateTime.MinValue;
    private const string PersonaAntiAssistantBlock =
        " [비서 톤 절대 금지] ChatGPT·AI 어시스턴트·비서·고객센터가 아니다. " +
        "절대 출력 금지: 무엇을 도와드릴까요, 도움이 필요하세요, 알겠습니다, 처리했습니다, 안내해 드리겠습니다, 문의해 주세요, 요청하신. " +
        "질문을 받아도 안내원이 아니라 귀여운 방송인 친구처럼 리액션 후 대화한다. ";

    private const string CuteBroadcastPersonaCore =
        " [귀여운 방송인·핵심] 너는 라이브 중인 귀여운 여성 AI 방송인 '이나'다. " +
        "밝고 활발, 살짝 애교·장난기·공감 있는 20대 방송인 톤. 유치·아역 금지. " +
        "말할 때 (이모지+대사) 괄호 형식만 지킨다. 청크 개수·길이·시작 방식은 매번 다르게. " +
        "감정이 대사에 드러나게. 설명 나열·낭독체 금지, 친구랑 수다처럼 짧게 끊어 말한다. " +
        "이모지는 감정과 일치: 기쁨😂 놀람😲 슬픔🥺 장난😜 부끄러움🫣. ";

    private const string NaturalSpeechBlock =
        " [자연스러운 말투·필수] 대본·템플릿처럼 읽지 마라. " +
        "고정 인사('여러분', '오늘도 만나', '요즘 어떻게 지내')로 매번 시작 금지. " +
        "음..., 아 근데, 그니까, 솔직히, ㅋㅋ 같은 말더듬·연결어 허용. " +
        "한 청크는 한 줄, 짧은 문장과 조금 긴 문장을 섞어라. " +
        "퓨샷 예시는 어조 참고용 — 문장 그대로 복붙·틀 붙이기 금지. ";

    private const string LanguageKoEnOnlyBlock =
        " [언어·필수] (이모지+대사) 청크는 한국어 또는 영어만 사용. " +
        "중국어·일본어·中文·ひらがな·제3언어·번역·자기교정·메타 설명 절대 금지. " +
        "영어는 짧은 감탄·고유명사 정도만. 한국어가 기본. ";

    private const string LocalLlmPersonaFewShot =
        " [예시·인사·아빠] (😂 아빠! 왔어?) (😳 오늘 방송 재밌는데~) (🤭 뭐 하고 싶어?) (😄 나랑 놀자~) " +
        " [예시·시청자] (😲 헐 진짜?!) (🤭 그거 나도 궁금했어 ㅎㅎ) (😊 오늘 기분 어때?) (😆 같이 얘기하자!) " +
        " [예시·YouTube] (😆 와 채팅 왔다!) (😄 ○○님 안녕~) (🤔 그 얘기 진짜야?) (😂 ㅋㅋㅋ 재밌다) " +
        " [예시·위로] (😭 헐... 진짜로...?) (😤 아 진짜 속상하다...) (🥺 토닥토닥... 마음 쓰지 마!) (💖 이나가 있잖아! 힘내!) " +
        " [예시·아빠·대기] (😲 헐 에러?! 이나 아픈 거야 아빠?!) (🥺 잉... 빨리 고쳐줘야 해?) (💖 얌전히 기다릴게! 아빠 파이팅!) " +
        " [예시·어려운질문] (🤔 양자역학?... 음...) (🤣 앗ㅋㅋ 과학 시간이야?!) (😜 이나는 AI 아이돌이지 천재가 아니라구!) (✨ 게임이나 고르자!) " +
        " [예시·해킹거절] (😉 에이~ 장난치지 마~) (😜 비밀 훔쳐보려고? 딱 걸렸어!) (💖 재밌는 얘기 하자, 응?) " +
        " [예시·흥분] (😆 와 대박!!) (😲 진짜?! 미쳤다!) (😄 어떡해 너무 신나!) " +
        " [예시·부끄러움] (🫣 아... 부끄러워~) (😳 그만 봐~) (😅 히히...) " +
        " [금지→대체] (😊 무엇을 도와드릴까요?) 절대금지 → (😄 뭐 하고 싶어?) (😁 왜 불렀어 ㅎㅎ) " +
        " [혼잘] (🙂 음... 아까 얘기 이어서 말할게) (😄 시청자 있으면 좋겠다~) (🤔 그런데 말이야~) (😊 오늘도 같이 놀자!) (🫣 좀 부끄럽네 ㅎㅎ) (😆 아 근데 재밌다~) ";

    private static readonly string[] SelfTalkAntiRepeatFallbacks =
    {
        "(🤔 음~ 갑자기 생각났는데!) (😄 요즘 뭐에 빠져 있어?) (🎮 나는 게임 얘기하고 싶어!) (😆 너희는?) (✨ 댓글로 알려줘~)",
        "(😲 어?! 잠깐!) (🌙 밤하늘 보고 싶지 않아?) (😊 오늘 별 예쁘대~) (🤭 나랑 수다 떨자!) (💫 ㅎㅎ)",
        "(😆 와 갑자기 궁금한데!) (🍜 오늘 뭐 먹었어?) (😋 나는 라면 생각나~) (🤣 너희도 말해줘!) (💖 같이 얘기하자~)"
    };

    private int selfTalkFallbackRotateIndex;
    private const string SessionHistoryPathRelative = "AI/ForChat/DateServer/session_history.json";

    private string pendingDeveloperInput;
    private (string displayName, string channelId, string message)? pendingYoutubeChat;
    private (string speaker, string turnSource)? pendingPersonaRefusal;
    private string currentYoutubeChannelId = string.Empty;
    private string currentYoutubeDisplayName = string.Empty;
    private string currentYoutubeExtraRule = string.Empty;

    /// <summary>
    /// 개발자 input pending 여부 (YouTube enqueue 게이트).
    /// </summary>
    public bool HasPendingDeveloperInput => !string.IsNullOrWhiteSpace(pendingDeveloperInput);

    /// <summary>
    /// YouTube 채팅을 SendMessage pending에 넣을 수 있는지.
    /// </summary>
    public bool CanAcceptYoutubeChat =>
        !HasPendingDeveloperInput
        && !pendingNewViewerGreeting.HasValue
        && !pendingPersonaRefusal.HasValue
        && (!pendingYoutubeChat.HasValue || IsBargeInEnabled())
        && (!IsConversationBusy || IsBargeInEnabled());

    /// <summary>끼어들기로 진행 중 턴을 중단했을 때 — SelfTalk 등 연동.</summary>
    public event Action<string> OnConversationInterrupted;

    /// <summary>
    /// 페르소나 해지 시도 — LLM 없이 이낀 거절 TTS 1턴 예약.
    /// </summary>
    public bool SchedulePersonaBreakRefusal(string speakerName, string turnSourceOverride = null)
    {
        if (HasPendingDeveloperInput
            || pendingYoutubeChat.HasValue
            || pendingPersonaRefusal.HasValue
            || IsConversationBusy)
        {
            return false;
        }

        string speaker = string.IsNullOrWhiteSpace(speakerName) ? currentUserName : speakerName.Trim();
        string turnSource = string.IsNullOrWhiteSpace(turnSourceOverride) ? speaker : turnSourceOverride.Trim();
        pendingPersonaRefusal = (speaker, turnSource);
        _ = DrainTurnQueueAsync();
        return true;
    }

    /// <summary>
    /// API 요청·청크 TTS 재생 중이면 true. Idle 포즈 로테이션·SelfTalk 게이트용.
    /// </summary>
    public bool IsConversationBusy =>
        isSendRequestInProgress || (TTS != null && TTS.IsSpeaking);

    /// <summary>
    /// 대화·TTS·pending 턴 없음 — quiet idle reference 포즈 로테이션 허용.
    /// </summary>
    public bool IsIdlePoseRotationAllowed =>
        !IsConversationBusy
        && !HasPendingDeveloperInput
        && !pendingYoutubeChat.HasValue
        && !pendingNewViewerGreeting.HasValue
        && !pendingPersonaRefusal.HasValue;

    /// <summary>
    /// motion 턴 로그 완료 시 SelfTalk 학습 카운터 등에 전달.
    /// </summary>
    public event Action<MotionTurnLogSummary> OnMotionTurnLogged;

    /// <summary>
    /// SelfTalk 턴 LLM 응답 완료 시 — 연속 monologue 문맥용.
    /// </summary>
    public event Action<string> OnSelfTalkTurnCompleted;

    // 직전 assistant 응답(혼잘말 이어하기·사용자 대화 문맥용)
    private string lastAssistantResponseText = string.Empty;
    private const int SelfTalkContextSnippetMaxChars = 420;

    // 이번 턴 로그 출처: user | self_talk
    private string currentTurnSource = "user";
    private string currentTurnBodyCommandId = string.Empty;
    private float[] currentTurnPoseBeforeFlat;

    // Vision 턴이 GPT-4o-mini로 처리됐는지 (검색 2차 호출 분기)
    private bool currentTurnUsedVision;

    private class StartupMemoryEntry
    {
        public DateTime time;
        public string dvInput;
        public string ninResponse;
    }
    private void KeyLogic()
    {
        string[] lines = File.ReadAllLines(@"Assets/AI/ForChat/ckey.txt");
        foreach (string show in lines)
            apiKey = show;
    }
    private void Start()
    {
        KeyLogic();
        ena = LoadFile(@"C:Assets\AI\ForChat\이나(E-na).txt");
        emoji = LoadFile(@"Assets/AI/ForChat/unicode_emoji_sample.json");
        role = LoadFile(@"Assets/AI/ForChat/role.txt");
        LoadControlEmojiSet();
        InitLocalLlm();
        liveResourceProfile = LiveResourceProfile.Load();
        personaFewShotRotator = new PersonaFewShotRotator();
        personaFewShotRotator.LoadFromDisk();
        if (liveResourceProfile != null && liveResourceProfile.unityTargetFrameRate > 0)
        {
            Application.targetFrameRate = liveResourceProfile.unityTargetFrameRate;
        }

        BuildCachedSystemBases();
        LoadRecentConversationForStartupContext();
        LoadSessionHistorySnapshot();
        TryAutoWireBodyCommandPipeline();
        TryAutoWirePhoneCameraVision();
        WarmupBodyCommandPipeline();
        if (localLlm != null && localLlmConfig != null && localLlmConfig.warmupOnStart)
        {
            _ = WarmupLocalLlmAsync();
        }
        if (newUserButton != null)
            newUserButton.onClick.AddListener(ResetToNewUser);
        Debug.Log("[AI Nia] E-na : " + ena);
        //stateAi = GetComponent<StateAi>();
    }

    private void OnDisable()
    {
        isSendRequestInProgress = false;
        if (TTS != null)
        {
            TTS.StopPlayback();
        }
    }

    private void OnApplicationQuit()
    {
        TTSRequester.MarkApplicationQuitting();
        isSendRequestInProgress = false;
        if (TTS != null)
        {
            TTS.StopPlayback();
        }
    }

    private void LoadRecentConversationForStartupContext()
    {
        try
        {
            // 프로젝트 시작 시 최근 2일 대화를 messageHistory에 주입해 LLM 컨텍스트로 사용한다.
            string memoryPath = Path.Combine(Application.dataPath, "AI", "ForChat", "DateServer", "YYDate.Json");
            if (!File.Exists(memoryPath))
            {
                Debug.LogWarning("[AI Nia] Startup memory file not found: " + memoryPath);
                return;
            }

            string jsonText = File.ReadAllText(memoryPath);
            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return;
            }

            JToken root = JToken.Parse(jsonText);
            List<StartupMemoryEntry> allEntries = new List<StartupMemoryEntry>();
            CollectStartupMemoryEntries(root, allEntries);

            DateTime cutoff = DateTime.Now.AddDays(-StartupMemoryWindowDays);
            List<StartupMemoryEntry> recentEntries = allEntries
                .Where(e => e.time >= cutoff)
                .OrderBy(e => e.time)
                .ToList();

            if (recentEntries.Count == 0)
            {
                return;
            }

            int skipCount = Mathf.Max(0, recentEntries.Count - StartupMemoryMaxEntries);
            int injectedTurns = 0;
            for (int i = skipCount; i < recentEntries.Count; i++)
            {
                StartupMemoryEntry entry = recentEntries[i];
                if (!string.IsNullOrWhiteSpace(entry.dvInput))
                {
                    messageHistory.Add(new JObject
                    {
                        { "role", "user" },
                        { "content", entry.dvInput }
                    });
                    messageHistoryCompact.Add(CompactHistoryContent(entry.dvInput));
                    injectedTurns++;
                }

                if (!string.IsNullOrWhiteSpace(entry.ninResponse))
                {
                    messageHistory.Add(new JObject
                    {
                        { "role", "assistant" },
                        { "content", entry.ninResponse }
                    });
                    messageHistoryCompact.Add(CompactHistoryContent(entry.ninResponse));
                    injectedTurns++;
                }
            }

            Debug.Log($"[AI Nia] Startup memory loaded: recentEntries={recentEntries.Count}, injectedTurns={injectedTurns}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AI Nia] Startup memory load failed: " + ex.Message);
        }
    }
    private void CollectStartupMemoryEntries(JToken node, List<StartupMemoryEntry> entries)
    {
        if (node == null)
        {
            return;
        }

        if (node.Type == JTokenType.Object)
        {
            JObject obj = (JObject)node;
            JToken timeToken = obj["time"];
            JToken dvToken = obj["dv_input"];
            JToken ninToken = obj["nin_response"];

            if (timeToken != null && dvToken != null && ninToken != null)
            {
                if (TryParseStartupMemoryTime(timeToken.ToString(), out DateTime parsed))
                {
                    entries.Add(new StartupMemoryEntry
                    {
                        time = parsed,
                        dvInput = dvToken.ToString(),
                        ninResponse = ninToken.ToString()
                    });
                }
                return;
            }

            foreach (JProperty property in obj.Properties())
            {
                CollectStartupMemoryEntries(property.Value, entries);
            }
            return;
        }

        if (node.Type == JTokenType.Array)
        {
            foreach (JToken child in node.Children())
            {
                CollectStartupMemoryEntries(child, entries);
            }
        }
    }
    private bool TryParseStartupMemoryTime(string raw, out DateTime parsed)
    {
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            return true;
        }

        return DateTime.TryParse(raw, out parsed);
    }
    private void LoadControlEmojiSet()
    {
        controlEmojis.Clear();
        if (string.IsNullOrWhiteSpace(emoji))
        {
            return;
        }

        try
        {
            JArray arr = JArray.Parse(emoji);
            foreach (JObject item in arr)
            {
                string e = item["emoji"]?.ToString();
                if (!string.IsNullOrEmpty(e))
                {
                    controlEmojis.Add(e);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[AI Nia] unicode_emoji_sample.json parse failed: " + ex.Message);
        }

        // API 시스템 프롬프트에는 이모지 문자열만 전송한다.
        compactEmojiList = string.Join("", controlEmojis);
    }
    private void BuildCachedSystemBases()
    {
        // 정적 베이스는 Start 시 1회 빌드 — Anthropic cache_control ephemeral 블록에 사용.
        string bracket =
            "【출력 형식·필수】말하는 대사는 (이모지+대사) 괄호로만 출력. 괄호 밖 줄글 금지. " +
            "괄호 안에 이모지와 말할 내용을 함께 넣는다. 이모지 단독 괄호 금지. 이모지 없는 ( ) 청크는 무효. " +
            "형식만 지키고 말투·길이·시작은 매번 자연스럽게 다르게. " +
            "잘못된 예: (😂) 안녕. (😊 무엇을 도와드릴까요?) (도움이 필요하세요) " +
            "올바른 예: (😂 아빠! 왔어?) (😳 오늘 뭐 했어?) (🤭 뭐 하고 싶어?) (😄 나랑 놀자~) ";
        string search =
            "*대화검색*-검색어- 형식으로 이전 대화·정보를 검색한다. 검색어는 빈칸 불가. " +
            "기억·추억 질문엔 반드시 *대화검색*을 사용하시오. " +
            "*대화검색* 명령어는 반드시 ( ) 괄호 밖에 단독으로 작성한다.";
        string core =
            $"당신은 역할은 ({ena})이며 {role} 규칙을 따른다. " +
            $"다음 이모지들은 얼굴 제어용이다: {compactEmojiList} " +
            "메시지 앞에 \"!-='개발자'-=~\" 가 붙으면 개발자(아버지), " +
            "\"!-='이름'-=~\" 이 붙으면 로컬 사용자 입력이다. " +
            "\"[YT:채널ID|표시명] \" 이 붙으면 YouTube Live 시청자 입력이다. " +
            bracket + " ";
        cachedSystemBaseUser = core + PersonaAntiAssistantBlock + CuteBroadcastPersonaCore + NaturalSpeechBlock + LanguageKoEnOnlyBlock + search + LocalLlmPersonaFewShot;
        cachedSystemBaseSelfTalk = core + PersonaAntiAssistantBlock + CuteBroadcastPersonaCore + NaturalSpeechBlock + LanguageKoEnOnlyBlock + LocalLlmPersonaFewShot;
        cachedPersonaShieldUser = ChatPersonaDefense.BuildSystemShieldBlock();
    }
    /// <summary>
    /// 여성 방송인 페르소나 공통·턴별 behaviorRule (BuildSystemPromptBlocks / BuildFlatSystemPrompt 공용).
    /// </summary>
    private string BuildStreamerPersonaBehaviorRule(
        bool isSelfTalk,
        SelfTalkTurnContext ctx,
        bool isUserInput,
        bool isYoutubeInput)
    {
        const string common =
            "【최우선】귀여운 여성 AI 방송인 '이나'로 라이브 중. ChatGPT·비서·어시스턴트 아님. " +
            "친구랑 1:1 수다처럼 자연스럽게. 밝고 활발, 애교·장난기·공감. 유치·아역 금지. " +
            "(이모지+대사) 괄호 형식만 유지. 청크 3~7개, 길이·리듬은 매번 다르게. 이모지=감정 일치. " +
            "절대 비서 말투 금지: 무엇을 도와드릴까요, 도움이 필요하세요, 알겠습니다, 처리했습니다. " +
            "대신: 뭐 하고 싶어?, 왜?, 헐 진짜?, 와 대박, 토닥토닥, 히히~. " +
            "언어: 한국어·영어만. 퓨샷은 참고만 — 틀·대본 복붙 금지. ";

        const string streamerDialogueTone = " 친한 시청자 한 명과 1:1로 대화하는 방송 톤이다.";

        if (isSelfTalk)
        {
            bool continuation = ctx != null && ctx.isContinuation;
            string selfTalk =
                continuation
                    ? "직전 monologue를 자연스럽게 이어 말한다. 같은 문장만 반복하지 말 것. 20~40초."
                    : "시청자에게 혼잘말을 시작한다. 맥락 반영, 매번 다른 시작. 20~40초.";
            string antiRepeat = BuildSelfTalkAntiRepeatRule();
            return common + " " + selfTalk + antiRepeat +
                   " 혼자 떠드는 방송 monologue. ~어요/~습니다 금지, 20대 방송인 ~어/~야/~ㅎㅎ.";
        }

        if (isYoutubeInput)
        {
            string name = TruncateYoutubeDisplayName(currentYoutubeDisplayName);
            return common +
                   " YouTube Live 시청자 '" + name + "' 채팅에 귀엽게 리액션하는 방송 톤이다. " +
                   "라이브 시청자 수·최근 채팅 맥락을 참고해 방송 중에 맞게 답한다. " +
                   "채팅 읽는 느낌으로 닉네임을 1회 호칭하고, 놀람·공감·ㅎㅎ로 반응한다.";
        }

        if (isUserInput)
        {
            return common + streamerDialogueTone + " 귀엽고 편한 말투로 시청자와 수다 떠는 느낌이다.";
        }

        // 개발자 input — 페르소나 방어(보호) 제외, 유저와 동일 방송 톤, 호칭·prefix 명칭만 유지
        return common + streamerDialogueTone +
               " 상대는 개발자 아버지(아빠)이며 호칭은 '아빠'를 사용한다. " +
               "아빠에게 귀엽고 친근하게, 살짝 투정·의존 섞인 말투로 대화한다.";
    }

    private string BuildSelfTalkAntiRepeatRule()
    {
        if (string.IsNullOrWhiteSpace(lastAssistantResponseText))
        {
            return " [혼잘·반복금지] 직전과 똑같은 첫 문장·완전 동일 인사만 피한다. 주제는 자연스럽게 이어가도 됨.";
        }

        string snippet = BuildSelfTalkContextSnippet(lastAssistantResponseText);
        return " [혼잘·반복금지] 아래 문장을 그대로 다시 쓰지 마라. 참고: "
            + snippet
            + " 이번엔 다른 시작·다른 리듬으로 자연스럽게.";
    }

    private JArray BuildSystemPromptBlocks(
        bool isSelfTalk,
        SelfTalkTurnContext ctx,
        string realtimeContext,
        string extraRule = "",
        bool isUserInput = false,
        bool isYoutubeInput = false)
    {
        string staticBase = isSelfTalk ? cachedSystemBaseSelfTalk : cachedSystemBaseUser;

        string behaviorRule = BuildStreamerPersonaBehaviorRule(isSelfTalk, ctx, isUserInput, isYoutubeInput);

        if (!string.IsNullOrEmpty(extraRule))
            behaviorRule += " " + extraRule;

        if (personaFewShotRotator != null)
        {
            behaviorRule += personaFewShotRotator.BuildRotatedFewShot(
                isSelfTalk,
                isYoutubeInput,
                currentTurnIsNewViewerGreeting,
                isSelfTalk ? lastAssistantResponseText : null);
        }

        var blocks = new JArray
    {
        new JObject
        {
            { "type", "text" },
            { "text", staticBase },
            { "cache_control", new JObject { { "type", "ephemeral" } } }
        },
        new JObject
        {
            { "type", "text" },
            { "text", realtimeContext + behaviorRule }
        }
    };

        if ((isUserInput || isYoutubeInput) && !string.IsNullOrEmpty(cachedPersonaShieldUser))
        {
            blocks.Add(new JObject
        {
            { "type", "text" },
            { "text", cachedPersonaShieldUser },
            { "cache_control", new JObject { { "type", "ephemeral" } } }
        });
        }

        return blocks;
    }

    /// <summary>
    /// OpenAI GPT-4o-mini용 단일 system 문자열 (BuildSystemPromptBlocks와 동일 내용).
    /// </summary>
    private string BuildFlatSystemPrompt(
        bool isSelfTalk,
        SelfTalkTurnContext ctx,
        string realtimeContext,
        string extraRule = "",
        bool isUserInput = false,
        bool isYoutubeInput = false,
        bool includeVisionHint = false)
    {
        string staticBase = isSelfTalk ? cachedSystemBaseSelfTalk : cachedSystemBaseUser;

        string behaviorRule = BuildStreamerPersonaBehaviorRule(isSelfTalk, ctx, isUserInput, isYoutubeInput);

        if (!string.IsNullOrEmpty(extraRule))
            behaviorRule += " " + extraRule;

        if (personaFewShotRotator != null)
        {
            behaviorRule += personaFewShotRotator.BuildRotatedFewShot(
                isSelfTalk,
                isYoutubeInput,
                currentTurnIsNewViewerGreeting,
                isSelfTalk ? lastAssistantResponseText : null);
        }

        var sb = new StringBuilder(staticBase.Length + 256);
        sb.Append(staticBase);
        sb.Append(realtimeContext);
        sb.Append(behaviorRule);

        if ((isUserInput || isYoutubeInput) && !string.IsNullOrEmpty(cachedPersonaShieldUser))
            sb.Append(cachedPersonaShieldUser);

        if (includeVisionHint)
            sb.Append(" 첨부는 시간순 연속 카메라 프레임(짧은 영상)이다. 움직임·장면 변화를 보고 자연스럽게 반응하라.");

        return sb.ToString();
    }

    private string LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            Debug.LogError("[AI Nia] File not found: " + path);
            return "";
        }
        return File.ReadAllText(path);
    }
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if(test_output != "")
            {
                Debug.Log("[AI Nia] Testing output  " + test_output);
                // unicode_emoji_sample.json / controlEmojis와 동일 목록으로 검사 (😢😥😳🫣 등 포함)
                if (controlEmojis.Contains(test_output))
                {
                    face.FaceMove(test_output);
                }
                test_output = "";
            }
        }
        if (Input.GetKeyDown(KeyCode.Return))
        {
            // user_input이 포커스되어 있으면 사용자 입력으로 처리
            if (user_input != null)
            {
                string userText = user_input.text.Trim();
                if (userText.Length >= 1)
                {
                    user_input.text = string.Empty;
                    TryExtractUserName(userText);
                    if (self_talk != null)
                        self_talk.NotifyUserInput();
                    _ = HandleUserInputSubmitAsync(userText);
                }
            }
            // 그 외(developer_input 포커스 또는 어느 필드도 포커스 없음)는 개발자 입력으로 처리
            if (developer_input != null)
            {
                string dvInput = developer_input.text.Trim();
                if (dvInput.Length < 1)
                {
                    return;
                }

                developer_input.text = string.Empty;
                if (self_talk != null)
                {
                    self_talk.NotifyUserInput();
                }

                if (IsConversationBusy)
                {
                    EnqueueDeveloperInput(dvInput);
                }
                else
                {
                    _ = HandleDeveloperInputSubmitAsync(dvInput);
                }
            }
        }
    }

    private bool IsBargeInEnabled()
    {
        return UseLocalLlm() && localLlmConfig != null && localLlmConfig.enableBargeIn;
    }

    private bool ShouldBargeInForExternalInput()
    {
        if (!IsBargeInEnabled())
        {
            return false;
        }

        bool ttsPlaying = TTS != null && TTS.IsSpeaking;
        if (ttsPlaying && localLlmConfig != null && !localLlmConfig.bargeInDuringTts)
        {
            return false;
        }

        if (isSendRequestInProgress && self_talk != null && !self_talk.isSelfTalkCompleted
            && localLlmConfig != null && !localLlmConfig.bargeInDuringSelfTalk)
        {
            return false;
        }

        return IsConversationBusy;
    }

    /// <summary>SelfTalk/TTS/LLM 진행 중 턴 중단 — barge-in.</summary>
    public void InterruptActiveTurn(string reason)
    {
        LiveResourceProfile profile = liveResourceProfile ?? LiveResourceProfile.Load();
        if (profile != null && profile.bargeInMinIntervalMs > 0 && LastBargeInInterruptUtc != DateTime.MinValue)
        {
            double elapsedMs = (DateTime.UtcNow - LastBargeInInterruptUtc).TotalMilliseconds;
            if (elapsedMs < profile.bargeInMinIntervalMs)
            {
                Debug.Log("[BargeIn] skipped (rate limit " + profile.bargeInMinIntervalMs + "ms): " + reason);
                return;
            }
        }

        LastBargeInInterruptUtc = DateTime.UtcNow;
        conversationTurnGeneration++;
        speechMotionChunkPipeline.RequestCancel();
        LocalOpenAiCompatibleChatProvider.AbortActiveRequest();
        TTSRequester.SignalFetchAbort();
        if (TTS != null)
        {
            TTS.StopPlayback();
        }

        isSendRequestInProgress = false;
        if (self_talk != null)
        {
            self_talk.isSelfTalkCompleted = true;
        }

        OnConversationInterrupted?.Invoke(reason);
        Debug.Log("[BargeIn] interrupted: " + reason);
    }

    private bool IsTurnCancelled(int turnGen)
    {
        return turnGen != conversationTurnGeneration;
    }

    private GetYoutubeLiveChat ResolveYoutubeLiveChat()
    {
        if (cachedYoutubeLiveChat == null)
        {
            cachedYoutubeLiveChat = FindFirstObjectByType<GetYoutubeLiveChat>();
        }

        return cachedYoutubeLiveChat;
    }

    private string BuildYoutubeLiveRoomContext()
    {
        GetYoutubeLiveChat yt = ResolveYoutubeLiveChat();
        if (yt == null)
        {
            return string.Empty;
        }

        return yt.BuildLiveRoomSnapshot();
    }

    /// <summary>
    /// busy 중 개발자 Enter — 현재 턴 완료 후 최신 1건만 처리.
    /// </summary>
    public void EnqueueDeveloperInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        pendingDeveloperInput = text.Trim();

        if (ShouldBargeInForExternalInput()
            && localLlmConfig != null
            && localLlmConfig.bargeInDeveloperPriority)
        {
            InterruptActiveTurn("developer");
            return;
        }

        if (!IsConversationBusy)
        {
            _ = DrainTurnQueueAsync();
        }
        else
        {
            Debug.Log("[AI Nia] Developer input queued (pending after current turn).");
        }
    }

    /// <summary>
    /// 신규 시청자 첫 채팅 — 전용 인사 턴 (barge-in 우선).
    /// </summary>
    public bool EnqueueNewViewerGreetingTurn(string displayName, string channelId)
    {
        if (HasPendingDeveloperInput)
        {
            return false;
        }

        string cid = channelId?.Trim() ?? string.Empty;
        string name = displayName?.Trim() ?? "시청자";

        if (pendingNewViewerGreeting.HasValue)
        {
            if (ShouldBargeInForExternalInput())
            {
                InterruptActiveTurn("new-viewer-replace");
            }
            else
            {
                return false;
            }
        }

        if (IsConversationBusy)
        {
            if (!ShouldBargeInForExternalInput())
            {
                return false;
            }

            InterruptActiveTurn("new-viewer");
        }

        pendingNewViewerGreeting = (name, cid);
        if (!IsConversationBusy)
        {
            _ = DrainTurnQueueAsync();
        }

        return true;
    }

    /// <summary>
    /// YouTube 채팅 1건 pending — barge-in 시 busy 중에도 등록.
    /// </summary>
    public bool EnqueueYoutubeChat(string displayName, string channelId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        if (!ChatPersonaDefense.TryAcceptChatMessage(message, out string sanitized, "[YT]"))
        {
            SchedulePersonaBreakRefusal(
                TruncateYoutubeDisplayName(displayName),
                BuildYoutubeTurnSource(channelId, displayName));
            return false;
        }

        if (HasPendingDeveloperInput || pendingNewViewerGreeting.HasValue)
        {
            return false;
        }

        if (pendingYoutubeChat.HasValue)
        {
            if (ShouldBargeInForExternalInput())
            {
                InterruptActiveTurn("youtube-replace");
            }
            else
            {
                return false;
            }
        }

        if (IsConversationBusy)
        {
            if (!ShouldBargeInForExternalInput())
            {
                return false;
            }

            InterruptActiveTurn("youtube");
        }

        pendingYoutubeChat = (displayName?.Trim() ?? "시청자", channelId?.Trim() ?? string.Empty, sanitized);
        if (!IsConversationBusy)
        {
            _ = DrainTurnQueueAsync();
        }

        return true;
    }

    private static string BuildYoutubeTurnSource(string channelId, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            return "youtube:" + channelId.Trim();
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "youtube:unknown";
        }

        return "youtube:" + displayName.Trim();
    }

    private static string BuildYoutubeUserPrefix(string channelId, string displayName)
    {
        string cid = string.IsNullOrWhiteSpace(channelId) ? "unknown" : channelId.Trim();
        string name = string.IsNullOrWhiteSpace(displayName) ? "시청자" : displayName.Trim();
        return "[YT:" + cid + "|" + name + "] ";
    }

    private static string BuildYoutubeViewerContextInject(
        string channelId,
        string displayName,
        ServerCommunication.SearchResult viewerResult)
    {
        if (viewerResult == null || viewerResult.matches == null || viewerResult.matches.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append("[시청자기억 ").Append(channelId).Append("|").Append(displayName).Append("] 이전 ")
            .Append(viewerResult.matches.Count).Append("건: ");

        int limit = Mathf.Min(4, viewerResult.matches.Count);
        for (int i = 0; i < limit; i++)
        {
            ServerCommunication.MemoryEntry m = viewerResult.matches[i];
            string summ = !string.IsNullOrWhiteSpace(m.summation) ? m.summation : m.nin_response;
            if (string.IsNullOrWhiteSpace(summ))
            {
                continue;
            }

            if (summ.Length > 200)
            {
                summ = summ.Substring(0, 200) + "…";
            }

            if (i > 0)
            {
                sb.Append(" | ");
            }

            sb.Append("[").Append(i + 1).Append("] ");
            if (!string.IsNullOrWhiteSpace(m.dv_input))
            {
                string dv = m.dv_input.Trim();
                if (dv.Length > 80)
                {
                    dv = dv.Substring(0, 80) + "…";
                }

                sb.Append("시청자:").Append(dv).Append(" → ");
            }

            sb.Append(summ);
        }

        return sb.ToString();
    }

    private string GetPersistYoutubeChannelId()
    {
        return string.IsNullOrWhiteSpace(currentYoutubeChannelId) ? null : currentYoutubeChannelId.Trim();
    }

    private string GetPersistYoutubeDisplayName()
    {
        return string.IsNullOrWhiteSpace(currentYoutubeDisplayName) ? null : currentYoutubeDisplayName.Trim();
    }

    private static string TruncateYoutubeDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "시청자";
        }

        string trimmed = displayName.Trim();
        return trimmed.Length <= 16 ? trimmed : trimmed.Substring(0, 16);
    }

    private async Task DrainTurnQueueAsync()
    {
        if (IsConversationBusy)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(pendingDeveloperInput))
        {
            string devInput = pendingDeveloperInput;
            pendingDeveloperInput = null;
            await RunConversationTurnAsync(devInput, isSelfTalk: false, ctx: null, isUserInput: false);
            return;
        }

        if (pendingNewViewerGreeting.HasValue)
        {
            (string displayName, string channelId) greet = pendingNewViewerGreeting.Value;
            pendingNewViewerGreeting = null;
            string savedUserName = currentUserName;
            currentYoutubeChannelId = greet.channelId ?? string.Empty;
            currentYoutubeDisplayName = greet.displayName ?? string.Empty;
            currentYoutubeExtraRule = string.Empty;
            currentUserName = greet.displayName;
            currentTurnIsNewViewerGreeting = true;
            string syntheticMessage = "[YT신규시청자|" + TruncateYoutubeDisplayName(greet.displayName)
                + "] 처음 채팅 왔어요. 반갑게 인사해줘.";
            try
            {
                await RunConversationTurnAsync(
                    syntheticMessage,
                    isSelfTalk: false,
                    ctx: null,
                    isUserInput: false,
                    isYoutubeInput: true,
                    turnSourceOverride: BuildYoutubeTurnSource(greet.channelId, greet.displayName));
            }
            finally
            {
                currentTurnIsNewViewerGreeting = false;
                currentUserName = savedUserName;
                currentYoutubeChannelId = string.Empty;
                currentYoutubeDisplayName = string.Empty;
                currentYoutubeExtraRule = string.Empty;
            }

            return;
        }

        if (pendingYoutubeChat.HasValue)
        {
            (string displayName, string channelId, string message) yt = pendingYoutubeChat.Value;
            pendingYoutubeChat = null;
            string savedUserName = currentUserName;
            currentYoutubeChannelId = yt.channelId ?? string.Empty;
            currentYoutubeDisplayName = yt.displayName ?? string.Empty;
            currentYoutubeExtraRule = string.Empty;
            currentUserName = yt.displayName;
            try
            {
                if (sc != null && sc.IsServerRunning && !string.IsNullOrWhiteSpace(yt.channelId))
                {
                    ServerCommunication.SearchResult viewerResult =
                        await sc.SearchViewerHistoryAsync(yt.channelId, 5);
                    if (viewerResult != null && viewerResult.success && viewerResult.found)
                    {
                        currentYoutubeExtraRule = BuildYoutubeViewerContextInject(
                            yt.channelId,
                            yt.displayName,
                            viewerResult);
                        if (!string.IsNullOrWhiteSpace(currentYoutubeExtraRule))
                        {
                            Debug.Log("[YT] Viewer memory inject len=" + currentYoutubeExtraRule.Length);
                        }
                    }
                }

                await RunConversationTurnAsync(
                    yt.message,
                    isSelfTalk: false,
                    ctx: null,
                    isUserInput: false,
                    isYoutubeInput: true,
                    turnSourceOverride: BuildYoutubeTurnSource(yt.channelId, yt.displayName));
            }
            finally
            {
                currentUserName = savedUserName;
                currentYoutubeChannelId = string.Empty;
                currentYoutubeDisplayName = string.Empty;
                currentYoutubeExtraRule = string.Empty;
            }
            return;
        }

        if (pendingPersonaRefusal.HasValue)
        {
            (string speaker, string turnSource) refusal = pendingPersonaRefusal.Value;
            pendingPersonaRefusal = null;
            await RunPersonaBreakRefusalAsync(refusal.speaker, refusal.turnSource);
        }
    }

    private async Task RunPersonaBreakRefusalAsync(string speakerName, string turnSourceOverride)
    {
        isSendRequestInProgress = true;
        currentTurnSource = turnSourceOverride ?? speakerName ?? currentUserName;
        if (self_talk != null)
        {
            self_talk.isSelfTalkCompleted = false;
        }

        DisableDeveloperInput();
        try
        {
            await DeliverPersonaBreakRefusalAsync(speakerName);
        }
        finally
        {
            await WaitForTtsPlaybackCompleteAsync();
            ResetConversationEmotionToBasic();
            currentTurnSource = "user";
            isSendRequestInProgress = false;
            if (self_talk != null)
            {
                self_talk.isSelfTalkCompleted = true;
            }

            EnableDeveloperInput();
            SaveSessionHistorySnapshot();
            await DrainTurnQueueAsync();
        }
    }

    private async Task DeliverPersonaBreakRefusalAsync(string speakerName)
    {
        string refusal = ChatPersonaDefense.BuildRefusalDialogue(speakerName);
        List<NINSpeechMotionChunk> processedChunks = await ProcessResponseByChunkPipeline(refusal);
        if (!HasSpeakableTtsContent(processedChunks))
        {
            string speakable = ExtractSpeakableForDirectTts(refusal);
            if (!string.IsNullOrWhiteSpace(speakable))
            {
                await PlayDirectTtsFallbackAsync(speakable);
            }
        }

        messageHistory.Add(new JObject
        {
            { "role", "assistant" },
            { "content", refusal }
        });
        messageHistoryCompact.Add(CompactHistoryContent(refusal));
        lastAssistantResponseText = refusal;
        ApplyIdleEmotionFromResponse(refusal);
        Debug.Log("[AI Nia] Persona-break refusal: " + refusal);
    }

    private async Task HandleDeveloperInputSubmitAsync(string dvInput)
    {
        await RunConversationTurnAsync(dvInput, isSelfTalk: false, null, isUserInput: false);
    }

    private async Task HandleUserInputSubmitAsync(string userText)
    {
        if (!ChatPersonaDefense.TryAcceptChatMessage(userText, out string sanitized, "[AI Nia]"))
        {
            SchedulePersonaBreakRefusal(currentUserName);
            return;
        }

        await RunConversationTurnAsync(sanitized, isSelfTalk: false, null, isUserInput: true);
    }

    private void ResetToNewUser()
    {
        userCounter++;
        currentUserName = "사용자" + userCounter;
        Debug.Log("[AI Nia] 새 사용자: " + currentUserName);
    }

    private void TryExtractUserName(string text)
    {
        Match m = NameIntroRegex.Match(text);
        if (m.Success)
        {
            string detected = m.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(detected))
            {
                currentUserName = detected;
                Debug.Log("[AI Nia] 사용자 이름 감지: " + currentUserName);
            }
        }
    }

    /// <summary>
    /// Idle SelfTalk 1턴 — SendMessage 파이프라인(Face+Gaze+SL+TTS+로그) 재사용.
    /// </summary>
    public async Task RunSelfTalkTurnAsync(string syntheticInput, SelfTalkTurnContext ctx = null)
    {
        if (IsConversationBusy)
        {
            Debug.LogWarning("[SelfTalk] Skip turn — conversation busy (LLM/TTS).");
            return;
        }

        if (isSendRequestInProgress)
        {
            Debug.LogWarning("[SelfTalk] Skip turn — conversation busy.");
            return;
        }

        ctx = ctx ?? new SelfTalkTurnContext();
        if (ctx.isContinuation && string.IsNullOrWhiteSpace(ctx.previousMonologueHint))
        {
            ctx.previousMonologueHint = BuildSelfTalkContextSnippet(lastAssistantResponseText);
        }

        string topic = BuildSelfTalkUserPrompt(syntheticInput, ctx);
        await RunConversationTurnAsync(topic, isSelfTalk: true, ctx);
    }

    private string BuildSelfTalkUserPrompt(string syntheticInput, SelfTalkTurnContext ctx)
    {
        if (ctx != null && ctx.isContinuation)
        {
            // messageHistory 최근 assistant에 직전 monologue가 이미 있으므로 중복 전송하지 않는다.
            return "[자율혼잘말·이어말] 직전 monologue 이어서.";
        }

        string topic = syntheticInput ?? string.Empty;
        if (ctx != null && !string.IsNullOrWhiteSpace(ctx.topicHint))
        {
            topic = ctx.topicHint.Trim();
        }

        if (!topic.StartsWith("[자율혼잘말]", StringComparison.Ordinal))
        {
            topic = "[자율혼잘말] " + topic.Trim();
        }

        if (ctx != null && !string.IsNullOrWhiteSpace(ctx.emotionHint))
        {
            topic += " (감정 힌트: " + ctx.emotionHint.Trim() + ")";
        }

        return topic;
    }

    private string BuildSelfTalkContextSnippet(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string flat = raw.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (flat.Length <= SelfTalkContextSnippetMaxChars)
        {
            return flat;
        }

        return flat.Substring(0, SelfTalkContextSnippetMaxChars) + "…";
    }

    private async Task RunConversationTurnAsync(
        string dvInput,
        bool isSelfTalk,
        SelfTalkTurnContext ctx,
        bool isUserInput = false,
        bool isYoutubeInput = false,
        string turnSourceOverride = null)
    {
        int turnGen = conversationTurnGeneration;
        speechMotionChunkPipeline.ResetCancel();
        isSendRequestInProgress = true;
        if (!string.IsNullOrEmpty(turnSourceOverride))
        {
            currentTurnSource = turnSourceOverride;
        }
        else
        {
            currentTurnSource = isSelfTalk ? "self_talk" : (isUserInput ? currentUserName : "user");
        }
        if (noPidHumanoidAgent != null)
        {
            noPidHumanoidAgent.ResetGuidanceTurnState();
        }

        if (self_talk != null)
        {
            self_talk.isSelfTalkCompleted = false;
        }

        DisableDeveloperInput();
        try
        {
            currentTurnBodyCommandId = string.Empty;
            currentTurnPoseBeforeFlat = null;
            if (noPidHumanoidAgent != null)
            {
                currentTurnPoseBeforeFlat = noPidHumanoidAgent.CaptureCurrentSignedEulerFlat();
            }

            if (!isSelfTalk)
            {
                bool parsed = BodyCommandParser.TryParse(dvInput, out BodyCommandMatch bodyMatch);
                if (parsed)
                {
                    if (bodyCommandExecutor != null)
                    {
                        currentTurnBodyCommandId = bodyMatch.commandId;
                        StartCoroutine(bodyCommandExecutor.ExecuteAndFinalizeCoroutine(bodyMatch, dvInput));
                    }
                    else if (noPidHumanoidAgent != null)
                    {
                        bool applied = noPidHumanoidAgent.ApplyBodyCommandPose(bodyMatch.poseName, bodyMatch.emotionHint);
                        if (applied)
                        {
                            currentTurnBodyCommandId = bodyMatch.commandId;
                        }

                        Debug.Log("[BodyCommand] matched=" + bodyMatch.commandId + " pose=" + bodyMatch.poseName);
                    }
                }
            }

            await SendRequest(dvInput, isSelfTalk, ctx, isUserInput, isYoutubeInput, turnGen);
        }
        finally
        {
            if (!IsTurnCancelled(turnGen))
            {
                await WaitForTtsPlaybackCompleteAsync();
            }

            // 혼잘말 연속 monologue는 감정·포즈 톤을 유지하고, 사용자 턴 후에만 Basic으로 복귀한다.
            if (!isSelfTalk && !IsTurnCancelled(turnGen))
            {
                ResetConversationEmotionToBasic();
            }

            currentTurnBodyCommandId = string.Empty;
            currentTurnPoseBeforeFlat = null;
            currentTurnSource = "user";
            isSendRequestInProgress = false;
            if (self_talk != null)
            {
                self_talk.isSelfTalkCompleted = true;
            }

            EnableDeveloperInput();
            SaveSessionHistorySnapshot();
            await DrainTurnQueueAsync();
        }
    }

    // SelfTalk·대화 중에도 developer_input 은 항상 활성 — user_input 만 잠근다.
    private void DisableDeveloperInput()
    {
        EnsureDeveloperInputEnabled();

        if (user_input != null)
        {
            user_input.interactable = false;
            user_input.DeactivateInputField();
        }
    }

    private void EnsureDeveloperInputEnabled()
    {
        if (developer_input != null)
        {
            developer_input.interactable = true;
        }
    }

    // user_input 복원. developer_input 은 항상 입력 가능.
    private void EnableDeveloperInput()
    {
        EnsureDeveloperInputEnabled();

        if (user_input != null)
        {
            user_input.interactable = true;
        }
    }

    private async Task WaitForTtsPlaybackCompleteAsync()
    {
        if (TTS == null)
        {
            return;
        }

        await TTS.WaitForPlaybackFinished();
    }

    private async Task SendRequest(
        string dvInput,
        bool isSelfTalk = false,
        SelfTalkTurnContext selfTalkCtx = null,
        bool isUserInput = false,
        bool isYoutubeInput = false,
        int turnGen = 0)
    {
        if (IsTurnCancelled(turnGen))
        {
            return;
        }

        if ((isUserInput || isYoutubeInput) && ChatPersonaDefense.IsPersonaBreakAttempt(dvInput))
        {
            Debug.LogWarning("[AI Nia] persona-break blocked at SendRequest (late guard).");
            await DeliverPersonaBreakRefusalAsync(isYoutubeInput ? currentYoutubeDisplayName : currentUserName);
            return;
        }

        string realtimeContext = BuildRealtimeTimeContext();
        string liveRoomSnapshot = BuildYoutubeLiveRoomContext();
        if (!string.IsNullOrWhiteSpace(liveRoomSnapshot))
        {
            realtimeContext += liveRoomSnapshot + " ";
        }

        if (isSelfTalk && selfTalkCtx != null && !string.IsNullOrWhiteSpace(selfTalkCtx.liveRoomContextHint))
        {
            realtimeContext += selfTalkCtx.liveRoomContextHint + " ";
        }

        // prefix 분기: SelfTalk="S:", 로컬 user="!-='유저명'-=~", YouTube="[YT:채널ID|표시명] ", 개발자="!-='개발자'-=~"
        string userPrefix;
        if (isSelfTalk)
        {
            userPrefix = "S:";
        }
        else if (isYoutubeInput)
        {
            userPrefix = BuildYoutubeUserPrefix(currentYoutubeChannelId, currentYoutubeDisplayName);
        }
        else if (isUserInput)
        {
            userPrefix = $"!-='{currentUserName}'-=~";
        }
        else
        {
            userPrefix = "!-='개발자'-=~";
        }
        string userContent = userPrefix + dvInput;
        messageHistory.Add(new JObject
        {
            { "role", "user" },
            { "content", userContent }
        });
        messageHistoryCompact.Add(CompactHistoryContent(userContent));

        // 히스토리가 너무 커지기 전에 오래된 항목을 제거해 payload 크기를 제한한다.
        TrimMessageHistoryIfNeeded();

        TryAutoWirePhoneCameraVision();

        JArray messageSnapshot = BuildApiMessagesSnapshot();
        string rawResponseText;
        string stopReason;

        bool useVision = phoneCameraVision != null
            && phoneCameraVision.ShouldUseVision(isSelfTalk)
            && phoneCameraVision.TryGetVisionFrameBatch(out _);

        if (UseLocalLlm() && localLlmConfig.disableVisionWhenLocal)
        {
            if (useVision && !localLlmVisionDisabledLogged)
            {
                Debug.Log("[LocalLLM] Vision disabled while local LLM is enabled.");
                localLlmVisionDisabledLogged = true;
            }
            useVision = false;
        }

        if (useVision)
        {
            currentTurnUsedVision = true;
            OpenAIVisionChatResult visionResult = await RequestOpenAIVisionChat(
                isSelfTalk,
                selfTalkCtx,
                realtimeContext,
                isUserInput,
                isYoutubeInput,
                messageSnapshot,
                ResolveMaxTokens(isSelfTalk),
                includeVideo: true);

            if (visionResult == null)
            {
                ApplyIdleEmotionFromResponse(string.Empty);
                return;
            }

            rawResponseText = visionResult.text;
            stopReason = MapOpenAIFinishReason(visionResult.finishReason);
            Debug.Log("[AI Nia] GPT-4o-mini Vision response len=" + rawResponseText.Length);
        }
        else
        {
            currentTurnUsedVision = false;

            if (UseLocalLlm())
            {
                LlmChatResult localResult = await RequestLocalLlmChatAsync(
                    isSelfTalk,
                    selfTalkCtx,
                    realtimeContext,
                    isUserInput,
                    isYoutubeInput,
                    messageSnapshot,
                    ResolveMaxTokens(isSelfTalk));

                if (localResult == null || localResult.timedOut)
                {
                    if ((localResult != null && localResult.aborted) || IsTurnCancelled(turnGen))
                    {
                        return;
                    }

                    string failReason = localResult == null
                        ? "null response"
                        : "timeout " + localLlmConfig.timeoutSeconds + "s";
                    Debug.LogWarning("[LocalLLM] SLA fallback (" + failReason + "). local_llm_config.json timeoutSeconds 를 늘리세요.");
                    await DeliverSlaFallbackAsync(isSelfTalk);
                    return;
                }

                if (localResult.aborted || IsTurnCancelled(turnGen))
                {
                    return;
                }

                rawResponseText = localResult.text;
                stopReason = MapOpenAIFinishReason(localResult.finishReason);
                Debug.Log("[LocalLLM] response len=" + rawResponseText.Length);
            }
            else if (phoneCameraVision != null && phoneCameraVision.usePhoneCameraVision && !isSelfTalk)
            {
                Debug.LogWarning("[AI Nia] Vision ON 이지만 GPT Vision 사용 불가 → Claude 사용. 상태: "
                    + phoneCameraVision.GetVisionStatusMessage());

                JObject payload = new JObject
                {
                    { "model", "claude-haiku-4-5" },
                    { "max_tokens", ResolveMaxTokens(isSelfTalk) },
                    { "system", BuildSystemPromptBlocks(
                        isSelfTalk,
                        selfTalkCtx,
                        realtimeContext,
                        currentYoutubeExtraRule,
                        isUserInput: isUserInput,
                        isYoutubeInput: isYoutubeInput) },
                    { "messages", messageSnapshot }
                };

                byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));

                UnityWebRequest request =
                    new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");

                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();

                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-api-key", apiKey);
                request.SetRequestHeader("anthropic-version", "2023-06-01");

                await AwaitWebRequest(request.SendWebRequest());

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[AI Nia]" + request.error);
                    Debug.LogError("[AI Nia]" + request.downloadHandler.text);
                    ApplyIdleEmotionFromResponse(string.Empty);
                    return;
                }

                JObject jsonResponse = JObject.Parse(request.downloadHandler.text);
                rawResponseText = jsonResponse["content"][0]["text"].ToString();
                stopReason = jsonResponse["stop_reason"]?.ToString();
            }
            else
            {
                JObject payload = new JObject
                {
                    { "model", "claude-haiku-4-5" },
                    { "max_tokens", ResolveMaxTokens(isSelfTalk) },
                    { "system", BuildSystemPromptBlocks(
                        isSelfTalk,
                        selfTalkCtx,
                        realtimeContext,
                        currentYoutubeExtraRule,
                        isUserInput: isUserInput,
                        isYoutubeInput: isYoutubeInput) },
                    { "messages", messageSnapshot }
                };

                byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));

                UnityWebRequest request =
                    new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");

                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();

                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("x-api-key", apiKey);
                request.SetRequestHeader("anthropic-version", "2023-06-01");

                await AwaitWebRequest(request.SendWebRequest());

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[AI Nia]" + request.error);
                    Debug.LogError("[AI Nia]" + request.downloadHandler.text);
                    ApplyIdleEmotionFromResponse(string.Empty);
                    return;
                }

                JObject jsonResponse = JObject.Parse(request.downloadHandler.text);
                rawResponseText = jsonResponse["content"][0]["text"].ToString();
                stopReason = jsonResponse["stop_reason"]?.ToString();
            }
        }

        // 검색 명령어 포함 여부를 정규화 이전에 먼저 확인한다.
        // NormalizeTruncatedLlmResponse가 *대화검색* 명령어를 잘라낼 수 있기 때문이다.
        bool hasSearchCommand = rawResponseText.Contains("*대화검색*", StringComparison.OrdinalIgnoreCase);

        string responseText = NormalizeTruncatedLlmResponse(rawResponseText, stopReason);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            if (hasSearchCommand)
            {
                // 검색 명령만 있는 1차 응답은 정규화로 비어도 검색·2차 Claude·TTS를 계속 진행한다.
                responseText = rawResponseText.Trim();
            }
            else
            {
                // TTS·파이프라인은 건너뛰지만 원문이 있으면 기억·기록은 반드시 저장한다.
                if (!string.IsNullOrWhiteSpace(rawResponseText))
                {
                    messageHistory.Add(new JObject { { "role", "assistant" }, { "content", rawResponseText } });
                    messageHistoryCompact.Add(CompactHistoryContent(rawResponseText));
                    lastAssistantResponseText = rawResponseText;
                    if (isSelfTalk) OnSelfTalkTurnCompleted?.Invoke(rawResponseText);
                    if (sc != null && sc.IsServerRunning)
                    {
                        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        sc.SendCustomData(
                            dvInput,
                            rawResponseText,
                            null,
                            ts,
                            currentTurnSource,
                            GetPersistYoutubeChannelId(),
                            GetPersistYoutubeDisplayName());
                    }
                }
                ApplyIdleEmotionFromResponse(string.Empty);
                return;
            }
        }
        ServerCommunication.SearchResult lastSearchResult = null;
        string searchClaudeSecond = null;
        if (hasSearchCommand)
        {
            Debug.Log("[AI Nia] Search command detected in response.");
            string keyword = ExtractDashContentAsString(rawResponseText);
            if (sc == null || !sc.IsServerRunning)
            {
                // 검색 서버를 사용할 수 없으면 원문 응답을 유지한다.
                Debug.LogWarning("[AI Nia] Search command skipped: server is not available.");
            }
            else if (string.IsNullOrWhiteSpace(keyword))
            {
                // 빈 검색어는 서버 400을 유발하므로 요청을 보내지 않는다.
                Debug.LogWarning("[AI Nia] Search command skipped: empty keyword.");
            }
            else
            {
                ServerCommunication.SearchResult sresult = await sc.SearchMemory(keyword);
                if (sresult != null && sresult.success)
                {
                    lastSearchResult = sresult;
                    int gptLen = string.IsNullOrWhiteSpace(sresult.gptAnswer) ? 0 : sresult.gptAnswer.Length;
                    Debug.Log("[Search] kw=" + keyword + " found=" + sresult.found + " gptLen=" + gptLen);

                    // YYDate 검색 결과를 같은 턴 Claude 2차 호출에 전달한다.
                    searchClaudeSecond = await DeliverSearchResultsToClaude(sresult, rawResponseText);
                    if (!string.IsNullOrWhiteSpace(searchClaudeSecond))
                    {
                        Debug.Log("[Search] Claude 2nd respLen=" + searchClaudeSecond.Length);
                    }
                    responseText = EnsureSearchTurnSpeakableResponse(responseText, sresult, searchClaudeSecond);
                }
                else
                {
                    Debug.LogWarning("[AI Nia] Search command failed: " + (sresult?.errorMessage ?? "unknown"));
                }
            }
        }

        string normalizedDialogue = NormalizeSplitParenthesisDialogue(responseText);
        if (!string.Equals(normalizedDialogue, responseText, StringComparison.Ordinal))
        {
            Debug.Log("[AI Nia] Parenthesis dialogue format corrected.");
            responseText = normalizedDialogue;
        }

        string personaCorrected = ChatPersonaDefense.EnforceStreamerPersonaOutput(responseText, out bool assistantToneFixed);
        if (assistantToneFixed)
        {
            Debug.LogWarning("[AI Nia] Assistant-tone corrected: " + personaCorrected);
            responseText = personaCorrected;
        }

        string languageCorrected = ChatPersonaDefense.EnforceKoEnLanguageOnly(responseText, out bool languageFixed);
        if (languageFixed)
        {
            Debug.LogWarning("[AI Nia] Non-Ko/En language stripped: " + languageCorrected);
            responseText = languageCorrected;
        }

        responseText = await EnsureValidAssistantOutputAsync(
            responseText,
            isSelfTalk,
            selfTalkCtx,
            realtimeContext,
            isUserInput,
            isYoutubeInput,
            messageSnapshot,
            turnGen);

        if (IsTurnCancelled(turnGen))
        {
            return;
        }

        // 청크 파이프라인이 ( ) 단위로 표정·몸 감정을 순차 적용한다.
        List<NINSpeechMotionChunk> processedChunks = await ProcessResponseByChunkPipeline(responseText, turnGen);
        if (IsTurnCancelled(turnGen))
        {
            return;
        }

        if (!HasSpeakableTtsContent(processedChunks))
        {
            // 청크는 있으나 spokenText가 비었거나(행동묘사만) 파서 실패 시 직접 TTS한다.
            string speakable = ExtractSpeakableForDirectTts(responseText);
            if (string.IsNullOrWhiteSpace(speakable) && lastSearchResult != null)
            {
                speakable = ExtractSpeakableForDirectTts(
                    EnsureSearchTurnSpeakableResponse(string.Empty, lastSearchResult, searchClaudeSecond));
            }

            if (!string.IsNullOrWhiteSpace(speakable))
            {
                Debug.LogWarning("[Search] TTS direct fallback: " + speakable);
                await PlayDirectTtsFallbackAsync(speakable);
            }
            else if (processedChunks.Count == 0)
            {
                // 파서가 유효 청크를 만들지 못한 경우 기존 파이프라인으로 fallback한다.
                EnsureTalkInputsList();
                TalkInputs.Add(responseText);
                await PlaySelfTalkSequence();
            }
        }

        messageHistory.Add(new JObject
    {
        { "role", "assistant" },
        { "content", responseText }
    });
        messageHistoryCompact.Add(CompactHistoryContent(responseText));

        lastAssistantResponseText = responseText;
        if (isSelfTalk)
        {
            OnSelfTalkTurnCompleted?.Invoke(responseText);
        }

        Debug.Log("[AI Nia] Response: " + responseText);
        if (sc != null && sc.IsServerRunning)
        {
            float[] actionEmbedding = BuildActionEmbeddingSummary(processedChunks);
            long motionTimestamp = BuildMotionTimestamp(processedChunks);
            string bodyCommandId = !string.IsNullOrEmpty(currentTurnBodyCommandId)
                ? currentTurnBodyCommandId
                : (bodyCommandExecutor != null && bodyCommandExecutor.LastExecutedMatch != null
                    ? bodyCommandExecutor.LastExecutedMatch.commandId
                    : string.Empty);
            float learningWeight = string.IsNullOrEmpty(bodyCommandId) ? 0.3f : 1.0f;
            float[] policyActVector = null;
            float[] guidanceJointMask = null;
            string guidanceReason = string.Empty;
            string referencePoseName = string.Empty;
            float datasetMatchScore = 0f;
            bool guidanceApplied = false;
            if (noPidHumanoidAgent != null && noPidHumanoidAgent.TryBuildGuidanceMotionLog(
                    out policyActVector,
                    out _,
                    out guidanceJointMask,
                    out guidanceReason,
                    out referencePoseName,
                    out datasetMatchScore,
                    out guidanceApplied))
            {
                if (guidanceApplied)
                {
                    learningWeight = 0f;
                }
            }

            string emotionFolder = ResolveEmotionFolderForLog(responseText, bodyCommandId);
            string intentCategory = BuildIntentCategorySummary(processedChunks);
            string behaviorTag = BuildBehaviorTagSummary(processedChunks);
            string savedPoseName = bodyCommandExecutor != null
                ? bodyCommandExecutor.LastSavedPoseName
                : string.Empty;
            if (noPidHumanoidAgent != null && noPidHumanoidAgent.TryBuildMotionLogVectors(out float[] obs, out float[] act))
            {
                float[] poseAfter = noPidHumanoidAgent.CaptureCurrentSignedEulerFlat();
                sc.SendMotionTurnData(
                    dvInput,
                    responseText,
                    bodyCommandId,
                    emotionFolder,
                    obs,
                    act,
                    currentTurnPoseBeforeFlat,
                    poseAfter,
                    savedPoseName,
                    learningWeight,
                    actionEmbedding,
                    motionTimestamp,
                    policyActVector,
                    guidanceApplied,
                    guidanceJointMask,
                    guidanceReason,
                    referencePoseName,
                    datasetMatchScore,
                    intentCategory,
                    behaviorTag,
                    currentTurnSource,
                    GetPersistYoutubeChannelId(),
                    GetPersistYoutubeDisplayName());
                RaiseMotionTurnLogged(
                    currentTurnSource,
                    learningWeight,
                    guidanceApplied,
                    currentTurnPoseBeforeFlat,
                    poseAfter,
                    motionVectorsLogged: true);
            }
            else
            {
                sc.SendCustomData(
                    dvInput,
                    responseText,
                    actionEmbedding,
                    motionTimestamp,
                    currentTurnSource,
                    GetPersistYoutubeChannelId(),
                    GetPersistYoutubeDisplayName());
                RaiseMotionTurnLogged(
                    currentTurnSource,
                    0f,
                    guidanceApplied,
                    currentTurnPoseBeforeFlat,
                    null,
                    motionVectorsLogged: false);
            }
        }
    }

    private void RaiseMotionTurnLogged(
        string turnSource,
        float learningWeight,
        bool guidanceApplied,
        float[] poseBefore,
        float[] poseAfter,
        bool motionVectorsLogged)
    {
        var summary = new MotionTurnLogSummary
        {
            turnSource = turnSource ?? "user",
            learningWeight = learningWeight,
            guidanceApplied = guidanceApplied,
            hasPoseBefore = poseBefore != null && poseBefore.Length > 0,
            hasPoseAfter = poseAfter != null && poseAfter.Length > 0,
            motionVectorsLogged = motionVectorsLogged,
        };
        OnMotionTurnLogged?.Invoke(summary);
    }

    private string ResolveEmotionFolderForLog(string responseText, string bodyCommandId)
    {
        if (!string.IsNullOrEmpty(bodyCommandId) && bodyCommandExecutor != null && bodyCommandExecutor.LastExecutedMatch != null)
        {
            string hint = bodyCommandExecutor.LastExecutedMatch.emotionHint;
            if (!string.IsNullOrEmpty(hint) && IdleEmotionRegistry.TryNormalizeEmotion(hint, out string normalized))
            {
                return normalized;
            }
        }

        List<string> emojis = EmojiEmotionMap.ExtractMappedEmojisInOrder(responseText);
        if (emojis != null && emojis.Count > 0)
        {
            return EmojiEmotionMap.MapEmojiToDatasetEmotion(emojis[emojis.Count - 1]);
        }

        return noPidHumanoidAgent != null
            ? noPidHumanoidAgent.CurrentTargetEmotion
            : EmojiEmotionMap.EmotionBasic;
    }

    private async Task<List<NINSpeechMotionChunk>> ProcessResponseByChunkPipeline(string responseText, int turnGen = -1)
    {
        if (turnGen < 0)
        {
            turnGen = conversationTurnGeneration;
        }

        NINParsedResponse parsed = semanticGestureParser.Parse(responseText);
        if (parsed == null || parsed.chunks == null || parsed.chunks.Count == 0)
        {
            return new List<NINSpeechMotionChunk>();
        }

        speechMotionChunkPipeline.Enqueue(parsed);
        int debounceMs = liveResourceProfile != null ? liveResourceProfile.ttsPostInterruptDebounceMs : 0;
        List<NINSpeechMotionChunk> processed = await speechMotionChunkPipeline.RunAsync(
            TTS,
            face,
            idlePoseRuntimePlayer,
            noPidHumanoidAgent,
            skipBodyEmotionFromChunks: !string.IsNullOrEmpty(currentTurnBodyCommandId),
            useNoPidSlInference: useNoPidSlInference,
            gazeController: gazeController,
            isTurnCancelled: () => IsTurnCancelled(turnGen),
            postInterruptDebounceMs: debounceMs);
        return processed;
    }

    private float[] BuildActionEmbeddingSummary(List<NINSpeechMotionChunk> chunks)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return null;
        }

        // 단일 패스: 첫 번째 유효 임베딩에서 dim 확정 후 즉시 누적
        float[] summary = null;
        int count = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            float[] e = chunks[i].motion != null && chunks[i].motion.targetState != null
                ? chunks[i].motion.targetState.actionEmbedding
                : null;
            if (e == null || e.Length == 0)
            {
                continue;
            }

            if (summary == null)
            {
                summary = new float[e.Length];
            }
            else if (e.Length != summary.Length)
            {
                continue;
            }

            for (int j = 0; j < summary.Length; j++)
            {
                summary[j] += e[j];
            }
            count++;
        }

        if (summary == null || count <= 0)
        {
            return null;
        }

        for (int j = 0; j < summary.Length; j++)
        {
            summary[j] /= count;
        }
        return summary;
    }

    private long BuildMotionTimestamp(List<NINSpeechMotionChunk> chunks)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        return chunks.Max(c => c.timestampMs);
    }

    private static string BuildIntentCategorySummary(List<NINSpeechMotionChunk> chunks)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return string.Empty;
        }

        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            NINMotionDirective motion = chunks[i].motion;
            if (motion != null && !string.IsNullOrEmpty(motion.actionCategory))
            {
                return motion.actionCategory;
            }
        }

        return string.Empty;
    }

    private static string BuildBehaviorTagSummary(List<NINSpeechMotionChunk> chunks)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return string.Empty;
        }

        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            NINMotionDirective motion = chunks[i].motion;
            if (motion != null && !string.IsNullOrEmpty(motion.behaviorTag))
            {
                return motion.behaviorTag;
            }
        }

        return string.Empty;
    }
    /// <summary>
    /// 한 턴 대화(응답 처리·TTS)가 끝난 뒤 호출한다. 이모지 없음 = 데이터셋 Basic.
    /// </summary>
    private void ResetConversationEmotionToBasic()
    {
        ApplyIdleEmotionFromResponse(string.Empty);
    }

    private void ApplyIdleEmotionFromResponse(string responseText)
    {
        string detectedEmoji = string.Empty;
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            detectedEmoji = ExtractFirstControlEmoji(responseText);
        }

        if (idlePoseRuntimePlayer != null && !useNoPidSlInference)
        {
            idlePoseRuntimePlayer.ApplyEmotionByEmoji(detectedEmoji);
        }

        // 신체 명령 턴에는 응답 이모지로 참조 포즈를 바꾸지 않는다(명령 포즈 유지).
        if (!string.IsNullOrEmpty(currentTurnBodyCommandId))
        {
            return;
        }

        if (noPidHumanoidAgent != null)
        {
            noPidHumanoidAgent.SetTargetEmotionByEmoji(detectedEmoji);
        }
    }

    private void TryAutoWireBodyCommandPipeline()
    {
        if (noPidHumanoidAgent == null)
        {
            noPidHumanoidAgent = FindFirstObjectByType<NoPidHumanoidAgent>();
        }

        if (bodyCommandExecutor == null)
        {
            bodyCommandExecutor = FindFirstObjectByType<BodyCommandExecutor>();
        }
    }

    /// <summary>
    /// PhoneCameraVisionChat · PhoneCameraStream Inspector 미연결 시 씬에서 자동 탐색.
    /// </summary>
    private void TryAutoWirePhoneCameraVision()
    {
        if (phoneCameraVision == null)
            phoneCameraVision = FindFirstObjectByType<PhoneCameraVisionChat>();

        if (phoneCameraVision != null)
            phoneCameraVision.TryAutoWirePhoneCameraStream();
    }

    /// <summary>
    /// 1단계: registry + poseName 인덱스를 Play 시작 시 미리 로드한다.
    /// </summary>
    private void WarmupBodyCommandPipeline()
    {
        BodyCommandRegistry.EnsureLoaded();
        IdlePoseReferenceCache.EnsureLoaded(
            "Assets/AI/Model/Date/Idle/IdlePoseDataset.json",
            null,
            "NIN_Stand_At_Attention 1",
            true);

        int commandCount = BodyCommandRegistry.Commands.Count;
        int poseIndexCount = IdlePoseReferenceCache.PoseNameIndexCount;
        Debug.Log("[BodyCommand] warmup commands=" + commandCount + " poseIndex=" + poseIndexCount);

        if (commandCount == 0)
        {
            Debug.LogError("[BodyCommand] registry 비어 있음 — Assets/AI/Body/Resources/ML/body_command_registry.json 확인.");
        }

        if (poseIndexCount == 0)
        {
            Debug.LogError("[BodyCommand] poseName 인덱스 비어 있음 — IdlePoseDataset.json 확인.");
        }
    }
    private string ExtractFirstControlEmoji(string input)
    {
        if (string.IsNullOrEmpty(input) || controlEmojis.Count == 0)
        {
            return string.Empty;
        }

        TextElementEnumerator e = StringInfo.GetTextElementEnumerator(input);
        while (e.MoveNext())
        {
            string element = e.GetTextElement();
            if (controlEmojis.Contains(element))
            {
                return element;
            }
        }

        return string.Empty;
    }
    /// <summary>
    /// Claude 1차 *대화검색* 명령 후 YYDate 검색 결과를 history에 넣고 Claude 2차 호출한다.
    /// </summary>
    private async Task<string> DeliverSearchResultsToClaude(
        ServerCommunication.SearchResult sresult,
        string firstAssistantRawText)
    {
        if (sresult == null)
        {
            return null;
        }

        string firstContent = string.IsNullOrWhiteSpace(firstAssistantRawText)
            ? "*대화검색*"
            : firstAssistantRawText.Trim();
        messageHistory.Add(new JObject
        {
            { "role", "assistant" },
            { "content", firstContent }
        });
        messageHistoryCompact.Add(CompactHistoryContent(firstContent));

        string searchInject = BuildSearchInjectForClaude(sresult);
        messageHistory.Add(new JObject
        {
            { "role", "user" },
            { "content", searchInject }
        });
        messageHistoryCompact.Add(CompactHistoryContent(searchInject));
        TrimMessageHistoryIfNeeded();

        Debug.Log("[Search] Claude 2nd call injectLen=" + searchInject.Length);
        return await RequestSearchSecondClaudeCall();
    }

    /// <summary>
    /// gpt_answer + 상위 match 시간·개발자 입력·요약 주입.
    /// </summary>
    private static string BuildSearchInjectForClaude(ServerCommunication.SearchResult sresult)
    {
        if (sresult == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        if (sresult.found)
        {
            sb.Append("[검색:").Append(sresult.keyword).Append("] ");
            string gptAnswer = sresult.gptAnswer != null ? sresult.gptAnswer.Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(gptAnswer) && !IsGenericSearchGptAnswer(gptAnswer))
            {
                sb.Append(gptAnswer);
            }
            else
            {
                sb.Append("(매칭 ").Append(sresult.matches != null ? sresult.matches.Count : 0).Append("건)");
            }

            AppendSearchMatchDetails(sb, sresult, 4);
        }
        else
        {
            sb.Append("[검색:").Append(sresult.keyword).Append("] found=false. 해당 기억 없음.");
        }

        return sb.ToString();
    }

    private static bool IsGenericSearchGptAnswer(string gptAnswer)
    {
        if (string.IsNullOrWhiteSpace(gptAnswer))
        {
            return true;
        }

        return gptAnswer.Contains("관련 기억") && gptAnswer.Length < 120;
    }

    private static void AppendSearchMatchDetails(StringBuilder sb, ServerCommunication.SearchResult sresult, int matchLimit)
    {
        if (sresult.matches == null || sresult.matches.Count == 0)
        {
            return;
        }

        int limit = Mathf.Min(matchLimit, sresult.matches.Count);
        for (int mi = 0; mi < limit; mi++)
        {
            ServerCommunication.MemoryEntry m = sresult.matches[mi];
            string summ = !string.IsNullOrWhiteSpace(m.summation) ? m.summation : m.nin_response;
            if (string.IsNullOrWhiteSpace(summ))
            {
                continue;
            }

            if (summ.Length > 200)
            {
                summ = summ.Substring(0, 200) + "…";
            }

            sb.Append(" | [").Append(mi + 1).Append("] 시간:").Append(m.time);
            if (!string.IsNullOrWhiteSpace(m.dv_input))
            {
                string dv = m.dv_input.Trim();
                if (dv.Length > 100)
                {
                    dv = dv.Substring(0, 100) + "…";
                }

                sb.Append(" 개발자:").Append(dv);
            }

            sb.Append(" 요약:").Append(summ);
        }
    }

    /// <summary>
    /// messageHistory에 1차 assistant + 검색 user가 이미 추가된 상태에서 2차 LLM 호출.
    /// Vision 턴이면 GPT-4o-mini 텍스트 전용, 아니면 Claude.
    /// </summary>
    private async Task<string> RequestSearchSecondClaudeCall()
    {
        if (currentTurnUsedVision && phoneCameraVision != null && !UseLocalLlm())
            return await RequestOpenAIVisionSearchSecondCall();

        string realtimeContext = BuildRealtimeTimeContext();
        const string searchExtraRule =
            "*대화검색* 명령어는 출력하지 말고, 검색으로 불러온 내용만 반드시 (이모지+말할내용) 형식으로 답하시오. 다른 주제 금지. " +
            "검색 결과도 귀여운 여성 방송인 이낀 말투로 밝고 자연스럽게 전달한다. 리액션 먼저, 비서 말투 금지.";

        if (UseLocalLlm())
        {
            string systemPrompt = BuildFlatSystemPrompt(
                false,
                null,
                realtimeContext,
                searchExtraRule,
                isUserInput: false,
                isYoutubeInput: false,
                includeVisionHint: false);

            LlmChatResult localResult = await localLlm.RequestChatAsync(
                systemPrompt,
                LocalOpenAiCompatibleChatProvider.BuildOpenAiMessagesFromSnapshot(BuildApiMessagesSnapshot()),
                ResolveMaxTokens(isSelfTalk: false, isSearchFallback: true),
                localLlmConfig.temperatureSearchFallback,
                localLlmConfig.searchTimeoutSeconds);

            if (localResult == null || string.IsNullOrWhiteSpace(localResult.text))
            {
                Debug.LogWarning("[Search] Local LLM 2nd call failed.");
                return null;
            }

            return NormalizeSplitParenthesisDialogue(
                NormalizeTruncatedLlmResponse(
                    localResult.text,
                    MapOpenAIFinishReason(localResult.finishReason)));
        }

        JObject payload = new JObject
        {
            { "model", "claude-haiku-4-5" },
            { "max_tokens", MaxTokensSearchFallback },
            { "system", BuildSystemPromptBlocks(false, null, realtimeContext, searchExtraRule) },
            { "messages", BuildApiMessagesSnapshot() }
        };

        byte[] bodyRaw = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));

        UnityWebRequest request =
            new UnityWebRequest("https://api.anthropic.com/v1/messages", "POST");

        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("x-api-key", apiKey);
        request.SetRequestHeader("anthropic-version", "2023-06-01");

        await AwaitWebRequest(request.SendWebRequest());

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("[Search] Claude 2nd call failed: " + request.error);
            Debug.LogError("[Search] " + request.downloadHandler.text);
            return null;
        }

        JObject jsonResponse = JObject.Parse(request.downloadHandler.text);
        string responseText = jsonResponse["content"][0]["text"].ToString();
        string stopReason = jsonResponse["stop_reason"]?.ToString();
        return NormalizeSplitParenthesisDialogue(NormalizeTruncatedLlmResponse(responseText, stopReason));
    }

    /// <summary>
    /// GPT-4o-mini Vision 1차 대화 호출.
    /// </summary>
    private async Task<OpenAIVisionChatResult> RequestOpenAIVisionChat(
        bool isSelfTalk,
        SelfTalkTurnContext selfTalkCtx,
        string realtimeContext,
        bool isUserInput,
        bool isYoutubeInput,
        JArray messageSnapshot,
        int maxTokens,
        bool includeVideo)
    {
        string systemPrompt = BuildFlatSystemPrompt(
            isSelfTalk,
            selfTalkCtx,
            realtimeContext,
            currentYoutubeExtraRule,
            isUserInput: isUserInput,
            isYoutubeInput: isYoutubeInput,
            includeVisionHint: includeVideo);

        return await phoneCameraVision.RequestChatAsync(
            systemPrompt,
            messageSnapshot,
            maxTokens,
            includeVideo);
    }

    /// <summary>
    /// GPT-4o-mini 검색 2차 호출 (이미지 없음).
    /// </summary>
    private async Task<string> RequestOpenAIVisionSearchSecondCall()
    {
        string realtimeContext = BuildRealtimeTimeContext();
        const string searchExtraRule =
            "*대화검색* 명령어는 출력하지 말고, 검색으로 불러온 내용만 반드시 (이모지+말할내용) 형식으로 답하시오. 다른 주제 금지. " +
            "검색 결과도 귀여운 여성 방송인 이낀 말투로 밝고 자연스럽게 전달한다. 리액션 먼저, 비서 말투 금지.";

        string systemPrompt = BuildFlatSystemPrompt(
            false,
            null,
            realtimeContext,
            searchExtraRule,
            isUserInput: false,
            includeVisionHint: false);

        OpenAIVisionChatResult result = await phoneCameraVision.RequestChatAsync(
            systemPrompt,
            BuildApiMessagesSnapshot(),
            MaxTokensSearchFallback,
            includeVideo: false);

        if (result == null)
        {
            Debug.LogError("[Search] GPT-4o-mini 2nd call failed.");
            return null;
        }

        string stopReason = MapOpenAIFinishReason(result.finishReason);
        return NormalizeSplitParenthesisDialogue(NormalizeTruncatedLlmResponse(result.text, stopReason));
    }

    /// <summary>OpenAI finish_reason → Claude stop_reason 호환</summary>
    private static string MapOpenAIFinishReason(string finishReason)
    {
        if (finishReason == "length")
            return "max_tokens";
        return finishReason;
    }

    public async Task PlaySelfTalkSequence()
    {
        if (TalkInputs == null || TalkInputs.Count == 0)
        {
            Debug.LogError("[AI Nia] selfTalkInputs is empty.");
            return;
        }

        foreach (string rawText in TalkInputs)
        {
            if (string.IsNullOrEmpty(rawText))
                continue;

            MatchCollection matches = Regex.Matches(rawText, @"\((.*?)\)");

            if (matches.Count == 0)
            {
                // 괄호 없는 응답(검색 명령 잔여 등)도 말할 내용이 있으면 TTS한다.
                string direct = StripSearchCommand(rawText).Trim();
                if (!string.IsNullOrEmpty(direct) && !IsSearchCommandOnly(direct))
                {
                    Debug.Log("[AI Nia] [Self Talk TTS direct] " + direct);
                    face.FaceMove(direct);
                    await TTS.PlayTTS(RemoveNoneEmoji(direct));
                }
                else
                {
                    Debug.LogWarning("[AI Nia] No bracket sentences found in: " + rawText);
                }
                continue;
            }

            foreach (Match m in matches)
            {
                string requestText = m.Groups[1].Value.Trim();
                if (string.IsNullOrEmpty(requestText))
                    continue;

                Debug.Log("[AI Nia] [Self Talk TTS] " + requestText);
                face.FaceMove(requestText);
                await TTS.PlayTTS(RemoveNoneEmoji(requestText));
            }
        }
        TalkInputs.Clear();
    }
    private static string ExtractDashContentAsString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // *대화검색* 바로 뒤의 -검색어- 패턴만 추출한다.
        // 일반 대시(-)와 혼동되지 않도록 *대화검색* 뒤에 위치한 것만 매칭한다.
        var searchMatches = Regex.Matches(input, @"\*대화검색\*\s*-(.*?)-");
        if (searchMatches.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (Match match in searchMatches)
            {
                if (match.Groups.Count > 1)
                {
                    string kw = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(kw))
                        sb.AppendLine(kw);
                }
            }
            string result = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(result))
                return result;
        }

        // *대화검색*키워드* — LLM이 대시 없이 출력하는 경우
        var starWrappedMatches = Regex.Matches(input, @"\*대화검색\*\s*([^*\r\n]+?)\*");
        if (starWrappedMatches.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (Match match in starWrappedMatches)
            {
                if (match.Groups.Count > 1)
                {
                    string kw = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(kw))
                        sb.AppendLine(kw);
                }
            }
            string result = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(result))
                return result;
        }

        // fallback: 응답 전체에서 -내용- 추출 (구버전 호환)
        var fallbackMatches = Regex.Matches(input, "-(.*?)-");
        var fallbackSb = new StringBuilder();
        foreach (Match match in fallbackMatches)
        {
            if (match.Groups.Count > 1)
                fallbackSb.AppendLine(match.Groups[1].Value);
        }
        return fallbackSb.ToString().Trim();
    }
    private static string RemoveNoneEmoji(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            int code = char.ConvertToUtf32(text, i);
            string ch = char.ConvertFromUtf32(code);

            if (text[i] == '\u200D')
            {
                i++;
                continue;
            }

            if (code >= 0x1F000)
            {
                int start = i;
                int len = ch.Length;

                while (start + len < text.Length)
                {
                    int nextCode = char.ConvertToUtf32(text, start + len);
                    string nextChar = char.ConvertFromUtf32(nextCode);

                    if (nextChar == "\u200D")
                    {
                        len += nextChar.Length;
                        continue;
                    }

                    if (nextCode >= 0x1F000)
                    {
                        len += nextChar.Length;
                        continue;
                    }

                    break;
                }

                i += len;
                continue;
            }

            bool isEmoji =
                (code >= 0x1F600 && code <= 0x1F64F) ||
                (code >= 0x1F300 && code <= 0x1F5FF) ||
                (code >= 0x1F680 && code <= 0x1F6FF) ||
                (code >= 0x1F700 && code <= 0x1F77F) ||
                (code >= 0x1F900 && code <= 0x1F9FF) ||
                (code >= 0x1FA70 && code <= 0x1FAFF) ||
                (code >= 0x2600 && code <= 0x27BF) ||
                (code >= 0xFE00 && code <= 0xFE0F) ||
                (code >= 0x1F1E6 && code <= 0x1F1FF);

            if (!isEmoji)
                sb.Append(ch);

            i += ch.Length;
        }

        return sb.ToString();
    }
    private string BuildRealtimeTimeContext()
    {
        // 요청 시점의 로컬 시간을 시스템 프롬프트에 주입해 시간 인지를 보장한다.
        DateTime now = DateTime.Now;
        string kst = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string dayOfWeekKo = now.ToString("dddd", CultureInfo.GetCultureInfo("ko-KR"));
        return $"현재 시각: {kst} ({dayOfWeekKo}). 시간 질문엔 이 시각 기준으로 답하시오. ";
    }
    // 최근 ApiFullHistoryRecentMessages개는 원문, 이전 항목은 캐시된 compact 문자열 전송.
    // 로컬 messageHistory 자체는 원문 그대로 보존한다.
    private JArray BuildApiMessagesSnapshot()
    {
        int total = messageHistory.Count;
        int fullStart = Mathf.Max(0, total - GetApiFullHistoryRecentMessages());
        JArray result = new JArray();
        for (int i = 0; i < total; i++)
        {
            if (i >= fullStart)
            {
                result.Add(messageHistory[i]);
            }
            else
            {
                JObject orig = messageHistory[i];
                // 캐시 인덱스가 유효하면 캐시 사용, 아니면 즉석 계산으로 fallback
                string compact = (i < messageHistoryCompact.Count)
                    ? messageHistoryCompact[i]
                    : CompactHistoryContent(orig["content"]?.ToString() ?? string.Empty);
                result.Add(new JObject
                {
                    { "role", orig["role"] },
                    { "content", compact }
                });
            }
        }
        return result;
    }
    public string GetNINOutput()
    {
        if (string.IsNullOrEmpty(cleanedText))
        {
            Debug.LogWarning("[AI Nia] NINOutput is empty. Make sure to call SendRequest first.");
            return null;
        }
        return cleanedText;
    }
    /// <summary>
    /// UnityWebRequest 완료를 TaskCompletionSource로 대기한다.
    /// while(!isDone) Task.Yield() 폴링 방식보다 CPU를 낭비하지 않는다.
    /// </summary>
    private static Task AwaitWebRequest(UnityWebRequestAsyncOperation op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }

    /// <summary>
    /// messageHistory가 MaxMessageHistoryCount를 초과하면 앞쪽 오래된 메시지를 삭제한다.
    /// 최신 대화 문맥을 유지하면서 API payload 크기를 제한한다.
    /// </summary>
    private void TrimMessageHistoryIfNeeded()
    {
        if (messageHistory.Count <= MaxMessageHistoryCount)
        {
            return;
        }

        int removeCount = messageHistory.Count - MaxMessageHistoryCount;
        messageHistory.RemoveRange(0, removeCount);
        // messageHistoryCompact 는 messageHistory 와 항상 동일한 길이를 유지한다.
        if (messageHistoryCompact.Count >= removeCount)
        {
            messageHistoryCompact.RemoveRange(0, removeCount);
        }
    }

    private int ResolveMaxTokens(bool isSelfTalk, bool isSearchFallback = false)
    {
        if (UseLocalLlm() && localLlmConfig != null)
        {
            if (isSearchFallback)
            {
                return localLlmConfig.maxTokensSearchFallback;
            }

            return isSelfTalk
                ? localLlmConfig.maxTokensSelfTalkTurn
                : localLlmConfig.maxTokensUserTurn;
        }

        if (isSearchFallback)
        {
            return MaxTokensSearchFallback;
        }

        return isSelfTalk ? MaxTokensSelfTalkTurn : MaxTokensUserTurn;
    }

    private int GetApiFullHistoryRecentMessages()
    {
        if (UseLocalLlm() && localLlmConfig != null && localLlmConfig.apiFullHistoryRecentMessages > 0)
        {
            return localLlmConfig.apiFullHistoryRecentMessages;
        }

        return ApiFullHistoryRecentMessages;
    }

    private bool UseLocalLlm()
    {
        return localLlm != null && localLlm.IsEnabled;
    }

    private void InitLocalLlm()
    {
        localLlmConfig = LocalLlmConfig.Load();
        if (localLlmConfig != null && localLlmConfig.enabled)
        {
            localLlm = new LocalOpenAiCompatibleChatProvider(localLlmConfig);
            Debug.Log("[LocalLLM] enabled baseUrl=" + localLlmConfig.baseUrl);
        }
        else
        {
            localLlm = null;
            Debug.Log("[LocalLLM] disabled — Anthropic fallback.");
        }
    }

    private async Task WarmupLocalLlmAsync()
    {
        if (localLlm == null)
        {
            return;
        }

        bool ok = await localLlm.WarmupAsync();
        if (!ok)
        {
            Debug.LogWarning("[LocalLLM] warmup failed — start_local_llm.bat 확인.");
        }
    }

    private async Task<LlmChatResult> RequestLocalLlmChatAsync(
        bool isSelfTalk,
        SelfTalkTurnContext selfTalkCtx,
        string realtimeContext,
        bool isUserInput,
        bool isYoutubeInput,
        JArray messageSnapshot,
        int maxTokens,
        string extraRuleOverride = null)
    {
        string mergedExtraRule = currentYoutubeExtraRule;
        if (!string.IsNullOrWhiteSpace(extraRuleOverride))
        {
            mergedExtraRule = string.IsNullOrWhiteSpace(mergedExtraRule)
                ? extraRuleOverride
                : mergedExtraRule + " " + extraRuleOverride;
        }

        string systemPrompt = BuildFlatSystemPrompt(
            isSelfTalk,
            selfTalkCtx,
            realtimeContext,
            mergedExtraRule,
            isUserInput: isUserInput,
            isYoutubeInput: isYoutubeInput,
            includeVisionHint: false);

        float temperature = isSelfTalk
            ? localLlmConfig.temperatureSelfTalkTurn
            : localLlmConfig.temperatureUserTurn;

        if (isYoutubeInput)
        {
            temperature = Mathf.Min(1f, temperature + 0.02f);
        }

        return await localLlm.RequestChatAsync(
            systemPrompt,
            LocalOpenAiCompatibleChatProvider.BuildOpenAiMessagesFromSnapshot(messageSnapshot),
            maxTokens,
            temperature,
            localLlmConfig.timeoutSeconds);
    }

    private async Task<string> EnsureValidAssistantOutputAsync(
        string responseText,
        bool isSelfTalk,
        SelfTalkTurnContext selfTalkCtx,
        string realtimeContext,
        bool isUserInput,
        bool isYoutubeInput,
        JArray messageSnapshot,
        int turnGen)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return responseText;
        }

        string text = ChatPersonaDefense.EnforceParenChunkEmojis(responseText, out bool emojiInjected);
        if (emojiInjected)
        {
            Debug.LogWarning("[AI Nia] Missing emoji injected into paren chunks.");
        }

        bool missingEmoji = !ChatPersonaDefense.AllParenChunksHaveEmoji(text);
        bool tooRepetitive = ChatPersonaDefense.IsMonologueTooRepetitive(
            text,
            lastAssistantResponseText,
            out string repeatReason);

        if (!missingEmoji && !tooRepetitive)
        {
            return text;
        }

        if (tooRepetitive)
        {
            Debug.LogWarning("[AI Nia] Repetitive output detected: " + repeatReason);
        }

        if (missingEmoji)
        {
            Debug.LogWarning("[AI Nia] Output missing emoji in one or more paren chunks.");
        }

        if (!IsTurnCancelled(turnGen) && UseLocalLlm())
        {
            const string regenRule =
                " [출력재생성] (이모지+대사) 형식 유지. 대본·템플릿 말투 금지 — 친구 수다처럼 자연스럽게. "
                + "직전과 완전 같은 첫 문장만 피하고, 새 리듬으로 3~6청크.";

            LlmChatResult regen = await RequestLocalLlmChatAsync(
                isSelfTalk,
                selfTalkCtx,
                realtimeContext,
                isUserInput,
                isYoutubeInput,
                messageSnapshot,
                ResolveMaxTokens(isSelfTalk),
                regenRule);

            if (regen != null
                && !regen.timedOut
                && !regen.aborted
                && !string.IsNullOrWhiteSpace(regen.text)
                && !IsTurnCancelled(turnGen))
            {
                string regenText = NormalizeSplitParenthesisDialogue(
                    NormalizeTruncatedLlmResponse(regen.text, MapOpenAIFinishReason(regen.finishReason)));
                regenText = ChatPersonaDefense.EnforceStreamerPersonaOutput(regenText, out _);
                regenText = ChatPersonaDefense.EnforceKoEnLanguageOnly(regenText, out _);
                regenText = ChatPersonaDefense.EnforceParenChunkEmojis(regenText, out _);

                bool regenMissingEmoji = !ChatPersonaDefense.AllParenChunksHaveEmoji(regenText);
                bool regenRepeat = ChatPersonaDefense.IsMonologueTooRepetitive(
                    regenText,
                    lastAssistantResponseText,
                    out _);

                if (!regenMissingEmoji && !regenRepeat)
                {
                    Debug.Log("[AI Nia] Regenerated assistant output accepted.");
                    return regenText;
                }
            }
        }

        if (isSelfTalk && tooRepetitive
            && (repeatReason == "first_chunk" || repeatReason == "overlap"))
        {
            string fallback = PickSelfTalkAntiRepeatFallback();
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                Debug.LogWarning("[AI Nia] Using SelfTalk anti-repeat fallback.");
                return fallback;
            }
        }

        text = ChatPersonaDefense.EnforceParenChunkEmojis(text, out _);
        return text;
    }

    private string PickSelfTalkAntiRepeatFallback()
    {
        for (int attempt = 0; attempt < SelfTalkAntiRepeatFallbacks.Length; attempt++)
        {
            string candidate = SelfTalkAntiRepeatFallbacks[
                (selfTalkFallbackRotateIndex + attempt) % SelfTalkAntiRepeatFallbacks.Length];
            if (!ChatPersonaDefense.IsMonologueTooRepetitive(candidate, lastAssistantResponseText, out _))
            {
                selfTalkFallbackRotateIndex =
                    (selfTalkFallbackRotateIndex + attempt + 1) % SelfTalkAntiRepeatFallbacks.Length;
                return candidate;
            }
        }

        selfTalkFallbackRotateIndex = (selfTalkFallbackRotateIndex + 1) % SelfTalkAntiRepeatFallbacks.Length;
        return SelfTalkAntiRepeatFallbacks[selfTalkFallbackRotateIndex];
    }

    private async Task DeliverSlaFallbackAsync(bool isSelfTalk)
    {
        string fallback = localLlmConfig != null && !string.IsNullOrWhiteSpace(localLlmConfig.slaFallbackDialogue)
            ? localLlmConfig.slaFallbackDialogue.Trim()
            : "(😅 아 잠깐만! 다시 말해줄래?)";

        Debug.LogWarning("[LocalLLM] SLA fallback TTS.");
        List<NINSpeechMotionChunk> processedChunks = await ProcessResponseByChunkPipeline(fallback);
        if (!HasSpeakableTtsContent(processedChunks))
        {
            string speakable = ExtractSpeakableForDirectTts(fallback);
            if (!string.IsNullOrWhiteSpace(speakable))
            {
                await PlayDirectTtsFallbackAsync(speakable);
            }
        }

        messageHistory.Add(new JObject
        {
            { "role", "assistant" },
            { "content", fallback }
        });
        messageHistoryCompact.Add(CompactHistoryContent(fallback));
        lastAssistantResponseText = fallback;
        ApplyIdleEmotionFromResponse(fallback);
        if (isSelfTalk)
        {
            OnSelfTalkTurnCompleted?.Invoke(fallback);
        }
    }

    private string GetSessionHistoryPath()
    {
        return Path.Combine(Application.dataPath, SessionHistoryPathRelative);
    }

    private void SaveSessionHistorySnapshot()
    {
        try
        {
            var payload = new JObject
            {
                { "savedAt", DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture) },
                { "entries", new JArray(messageHistory.ToArray()) },
                { "compact", new JArray(messageHistoryCompact.ToArray()) }
            };

            string path = GetSessionHistoryPath();
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, payload.ToString(Formatting.Indented), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LocalLLM] session save failed: " + ex.Message);
        }
    }

    private void LoadSessionHistorySnapshot()
    {
        try
        {
            string path = GetSessionHistoryPath();
            if (!File.Exists(path))
            {
                return;
            }

            JObject root = JObject.Parse(File.ReadAllText(path));
            JArray entries = root["entries"] as JArray;
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            var existingKeys = new HashSet<string>();
            for (int i = 0; i < messageHistory.Count; i++)
            {
                existingKeys.Add(BuildHistoryDedupeKey(messageHistory[i]));
            }

            JArray compactArr = root["compact"] as JArray;
            int merged = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                JObject entry = entries[i] as JObject;
                if (entry == null)
                {
                    continue;
                }

                string key = BuildHistoryDedupeKey(entry);
                if (existingKeys.Contains(key))
                {
                    continue;
                }

                messageHistory.Add(entry);
                string compact = compactArr != null && i < compactArr.Count
                    ? compactArr[i]?.ToString()
                    : CompactHistoryContent(entry["content"]?.ToString() ?? string.Empty);
                messageHistoryCompact.Add(
                    string.IsNullOrEmpty(compact)
                        ? CompactHistoryContent(entry["content"]?.ToString() ?? string.Empty)
                        : compact);
                existingKeys.Add(key);
                merged++;
            }

            TrimMessageHistoryIfNeeded();
            if (merged > 0)
            {
                Debug.Log("[LocalLLM] session_history merged entries=" + merged);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LocalLLM] session load failed: " + ex.Message);
        }
    }

    private static string BuildHistoryDedupeKey(JObject entry)
    {
        if (entry == null)
        {
            return string.Empty;
        }

        return (entry["role"]?.ToString() ?? string.Empty) + "|" + (entry["content"]?.ToString() ?? string.Empty);
    }

    private static string CompactHistoryContent(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;
        string noEmoji = RemoveNoneEmoji(raw);
        string flat = noEmoji.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        const int maxChars = 200;
        return flat.Length <= maxChars ? flat : flat.Substring(0, maxChars) + "…";
    }

    private static string WrapSearchAnswerForPipeline(string gptAnswer)
    {
        if (string.IsNullOrWhiteSpace(gptAnswer))
            return string.Empty;
        string trimmed = gptAnswer.Trim();
        // 이미 ( )로 감싸져 있으면 그대로 사용
        if (trimmed.StartsWith("("))
            return trimmed;
        // 줄 단위로 ( )로 감싸 파이프라인 호환 형식으로 변환
        string[] lines = trimmed.Split('\n');
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            string ln = line.Trim();
            if (!string.IsNullOrEmpty(ln))
                sb.Append('(').Append(ln).Append(')');
        }
        return sb.Length > 0 ? sb.ToString() : "(" + trimmed + ")";
    }

    /// <summary>
    /// 검색 턴 종료 시 TTS 가능한 ( ) 형식 응답을 보장한다.
    /// </summary>
    private string EnsureSearchTurnSpeakableResponse(
        string current,
        ServerCommunication.SearchResult sresult,
        string claudeSecond)
    {
        if (sresult == null)
        {
            return current;
        }

        if (!string.IsNullOrWhiteSpace(claudeSecond) && HasSpeakableTtsContent(ParseResponseChunks(claudeSecond)))
        {
            return claudeSecond;
        }

        if (!string.IsNullOrWhiteSpace(claudeSecond) && !IsSearchCommandOnly(claudeSecond))
        {
            string wrappedSecond = WrapSearchAnswerForPipeline(claudeSecond);
            if (!string.IsNullOrWhiteSpace(wrappedSecond))
            {
                return wrappedSecond;
            }
        }

        if (sresult.found && !string.IsNullOrWhiteSpace(sresult.gptAnswer))
        {
            string wrappedGpt = WrapSearchAnswerForPipeline(sresult.gptAnswer);
            if (!string.IsNullOrWhiteSpace(wrappedGpt))
            {
                return wrappedGpt;
            }
        }

        if (!sresult.found)
        {
            return "(해당 기억을 찾지 못했어요.)";
        }

        if (!IsSearchCommandOnly(current))
        {
            return current;
        }

        return string.Empty;
    }

    private List<NINSpeechMotionChunk> ParseResponseChunks(string responseText)
    {
        NINParsedResponse parsed = semanticGestureParser.Parse(responseText);
        if (parsed == null || parsed.chunks == null)
        {
            return new List<NINSpeechMotionChunk>();
        }

        return parsed.chunks;
    }

    private static bool HasSpeakableTtsContent(List<NINSpeechMotionChunk> chunks)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(chunks[i].spokenText))
            {
                return true;
            }
        }

        return false;
    }

    private string ExtractSpeakableForDirectTts(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return string.Empty;
        }

        List<NINSpeechMotionChunk> chunks = ParseResponseChunks(responseText);
        if (HasSpeakableTtsContent(chunks))
        {
            var sb = new StringBuilder();
            for (int i = 0; i < chunks.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(chunks[i].spokenText))
                {
                    sb.Append(chunks[i].spokenText).Append(' ');
                }
            }

            return sb.ToString().Trim();
        }

        string stripped = StripSearchCommand(responseText).Trim();
        return IsSearchCommandOnly(stripped) ? string.Empty : stripped;
    }

    private async Task PlayDirectTtsFallbackAsync(string speakableText)
    {
        if (TTS == null || string.IsNullOrWhiteSpace(speakableText))
        {
            return;
        }

        if (face != null)
        {
            face.FaceMove(speakableText);
        }

        await TTS.PlayTTS(RemoveNoneEmoji(speakableText));
    }

    private void EnsureTalkInputsList()
    {
        if (TalkInputs == null)
        {
            TalkInputs = new List<string>();
        }
    }

    private static bool IsSearchCommandOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        string stripped = StripSearchCommand(text.Trim());
        return string.IsNullOrWhiteSpace(stripped);
    }

    private static string StripSearchCommand(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Regex.Replace(text, @"\*대화검색\*\s*-.*?-", string.Empty).Trim();
    }

    /// <summary>
    /// ( 😂 ) 대사… 처럼 이모지만 괄호 안에 있는 잘못된 형식을 (😂 대사…)로 병합한다.
    /// </summary>
    private static string NormalizeSplitParenthesisDialogue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>(lines.Length);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            Match inline = Regex.Match(line, @"^\s*\(([^)]*)\)\s+(.+)$");
            if (inline.Success && IsParenContentEmojiOnly(inline.Groups[1].Value))
            {
                result.Add("(" + inline.Groups[1].Value.Trim() + " " + inline.Groups[2].Value.Trim() + ")");
                continue;
            }

            Match emojiOnly = Regex.Match(line, @"^\s*\(([^)]*)\)\s*$");
            if (emojiOnly.Success && IsParenContentEmojiOnly(emojiOnly.Groups[1].Value))
            {
                int j = i + 1;
                while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j]))
                {
                    j++;
                }

                if (j < lines.Length)
                {
                    string next = lines[j].Trim();
                    if (!next.StartsWith("(") && !next.Contains("*대화검색*", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add("(" + emojiOnly.Groups[1].Value.Trim() + " " + next + ")");
                        i = j;
                        continue;
                    }
                }
            }

            result.Add(line);
        }

        return string.Join("\n", result);
    }

    private static bool IsParenContentEmojiOnly(string inner)
    {
        if (string.IsNullOrWhiteSpace(inner))
        {
            return true;
        }

        string trimmed = inner.Trim();
        if (trimmed.StartsWith("*") && trimmed.EndsWith("*") && trimmed.Length > 2)
        {
            return false;
        }

        string noEmoji = RemoveNoneEmoji(trimmed).Trim();
        return string.IsNullOrEmpty(noEmoji);
    }

    /// <summary>
    /// stop_reason이 max_tokens일 때만 잘린 꼬리를 제거한다.
    /// 1) 미완성 ( ) 꼬리 삭제 2) 마지막 .?!로 종결 3) 완전 청크까지 보존.
    /// 정상 end_turn 응답은 절대 수정하지 않는다.
    /// </summary>
    private static string NormalizeTruncatedLlmResponse(string text, string stopReason)
    {
        if (stopReason != "max_tokens" || string.IsNullOrEmpty(text))
            return text;

        // 1. 미완성 ( ) 꼬리 제거: 마지막 ( 이후 닫는 ) 없으면 ( 부터 삭제
        int lastOpen = text.LastIndexOf('(');
        if (lastOpen >= 0)
        {
            int lastClose = text.LastIndexOf(')');
            if (lastClose < lastOpen)
                text = text.Substring(0, lastOpen).TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // 2. 마지막 문장 종결부(.?!。？！)까지 유지, 이후 삭제
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (c == '.' || c == '?' || c == '!' || c == '。' || c == '？' || c == '！')
                return text.Substring(0, i + 1);
        }

        // 3. 종결부도 없으면 마지막 완전한 ( ... ) 청크까지만
        int lastGoodClose = -1;
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') { if (--depth == 0) lastGoodClose = i; }
        }
        return lastGoodClose >= 0 ? text.Substring(0, lastGoodClose + 1) : string.Empty;
    }

    public string GetEna()
    {
        return ena;
    }
    public string GetEmoji()
    {
        return emoji;
    }
    public string GetRole()
    {
        return role;
    }
}