using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// 핸드폰 카메라 Vision ON/OFF 게이트 + GPT-4o-mini Vision API 연동.
/// 단일 이미지가 아닌 연속 프레임(영상 클립)을 AI에 전달한다.
/// </summary>
public class PhoneCameraVisionChat : MonoBehaviour
{
    [Header("Vision ON/OFF")]
    [Tooltip("true: 카메라 연속 프레임을 GPT-4o-mini Vision으로 대화에 사용 (Claude 대체)")]
    public bool usePhoneCameraVision = false;

    [Header("카메라 소스")]
    public PhoneCameraStream phoneCameraStream;

    [Header("Vision JPEG 설정")]
    [Tooltip("Vision API 전송 최대 변 길이 (px) — 토큰 절감")]
    public int visionMaxEdge = 512;

    [Range(1, 100)]
    [Tooltip("Vision JPEG 품질")]
    public int visionJpegQuality = 60;

    private readonly OpenAIVisionChatProvider provider = new OpenAIVisionChatProvider();

    private void Awake()
    {
        TryAutoWirePhoneCameraStream();
    }

    private void Start()
    {
        TryAutoWirePhoneCameraStream();
        provider.LoadApiKey();
    }

    /// <summary>
    /// Inspector 미연결 시 같은 오브젝트·씬에서 PhoneCameraStream 자동 탐색.
    /// </summary>
    public void TryAutoWirePhoneCameraStream()
    {
        if (phoneCameraStream != null)
            return;

        phoneCameraStream = GetComponent<PhoneCameraStream>();
        if (phoneCameraStream == null)
            phoneCameraStream = GetComponentInChildren<PhoneCameraStream>(true);
        if (phoneCameraStream == null)
            phoneCameraStream = GetComponentInParent<PhoneCameraStream>();
        if (phoneCameraStream == null)
            phoneCameraStream = FindFirstObjectByType<PhoneCameraStream>();

        if (phoneCameraStream != null)
            Debug.Log("[PhoneCameraVision] PhoneCameraStream 자동 연결: " + phoneCameraStream.gameObject.name);
    }

    /// <summary>Vision 실패 원인 디버그 문자열</summary>
    public string GetVisionStatusMessage()
    {
        if (!usePhoneCameraVision)
            return "Vision OFF";

        if (phoneCameraStream == null)
            return "PhoneCameraStream 미연결 — PhoneCameraVisionChat.phoneCameraStream 또는 씬에 PhoneCameraStream 추가";

        if (!provider.HasApiKey)
            return "OpenAI key.txt 없음";

        if (!phoneCameraStream.IsStreamConnected)
            return phoneCameraStream.StatusMessage;

        return phoneCameraStream.StatusMessage;
    }

    /// <summary>
    /// Vision 모드 사용 가능 여부 (SelfTalk 제외).
    /// </summary>
    public bool ShouldUseVision(bool isSelfTalk)
    {
        TryAutoWirePhoneCameraStream();

        if (!usePhoneCameraVision || isSelfTalk)
            return false;
        if (phoneCameraStream == null || !phoneCameraStream.IsStreamConnected)
            return false;
        if (!provider.HasApiKey)
            return false;
        return true;
    }

    /// <summary>
    /// Vision API용 연속 프레임(영상 클립).
    /// </summary>
    public bool TryGetVisionFrameBatch(out byte[][] frames)
    {
        frames = null;
        if (phoneCameraStream == null)
            return false;
        return phoneCameraStream.TryGetVisionFrameBatch(
            out frames, visionMaxEdge, visionJpegQuality);
    }

    /// <summary>
    /// GPT-4o-mini Vision/Chat 요청.
    /// </summary>
    public async Task<OpenAIVisionChatResult> RequestChatAsync(
        string systemPrompt,
        JArray messageSnapshot,
        int maxTokens,
        bool includeVideo)
    {
        byte[][] frames = null;
        if (includeVideo)
            TryGetVisionFrameBatch(out frames);

        JArray openAiMessages = OpenAIVisionChatProvider.BuildOpenAIMessagesFromSnapshot(
            messageSnapshot, frames, includeVideo && frames != null && frames.Length > 0);

        if (includeVideo && frames != null)
            Debug.Log("[PhoneCameraVision] GPT 전송 프레임 수: " + frames.Length);

        return await provider.RequestAsync(systemPrompt, openAiMessages, maxTokens);
    }
}
