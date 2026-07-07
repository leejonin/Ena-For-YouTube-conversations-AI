using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class NINTargetState
{
    // 양손 IK 목표 위치(로컬 기준) 6차원.
    public Vector3 leftHandLocal;
    public Vector3 rightHandLocal;

    // 척추/목 목표 회전(Quaternion) 8차원.
    public Quaternion spineRotation;
    public Quaternion neckRotation;

    // 의미 카테고리 임베딩(저차원).
    public float[] actionEmbedding;
}

[Serializable]
public class NINMotionDirective
{
    public string actionCategory;
    public string behaviorTag;
    public string emotionFolder;
    public string gazeMode;
    public string emoji;
    public NINTargetState targetState;
}

[Serializable]
public class NINSpeechMotionChunk
{
    public int index;
    public string spokenText;
    public string rawText;
    public long timestampMs;
    public NINMotionDirective motion;
}

[Serializable]
public class NINParsedResponse
{
    public string rawResponse;
    public List<NINSpeechMotionChunk> chunks = new List<NINSpeechMotionChunk>();
}

/// <summary>
/// SelfTalk idle 턴 1회 옵션.
/// </summary>
[Serializable]
public class SelfTalkTurnContext
{
    public string topicHint;
    public string emotionHint;
    /// <summary>true면 직전 assistant monologue를 이어서 말하도록 요청.</summary>
    public bool isContinuation;
    /// <summary>이어 말하기 참고용 직전 발화 요약(없으면 SendMessage 내부 캐시 사용).</summary>
    public string previousMonologueHint;
    /// <summary>YouTube 라이브 스냅샷 — 시청자 수·최근 채팅 요약.</summary>
    public string liveRoomContextHint;
}

/// <summary>
/// YYDate motion 턴 로그 요약 — SelfTalk 학습 카운터용.
/// </summary>
public class MotionTurnLogSummary
{
    public string turnSource;
    public float learningWeight;
    public bool guidanceApplied;
    public bool hasPoseBefore;
    public bool hasPoseAfter;
    public bool motionVectorsLogged;

    public bool IsValidTrainingTurn =>
        motionVectorsLogged
        && learningWeight > 0f
        && !guidanceApplied
        && hasPoseBefore
        && hasPoseAfter;
}
