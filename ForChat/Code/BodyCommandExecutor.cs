using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// BodyCommandMatch를 NoPidHumanoidAgent 참조 포즈 전환으로 실행하고 Idle 캡처·모션 로그를 연동한다.
/// </summary>
public class BodyCommandExecutor : MonoBehaviour
{
    [SerializeField] private NoPidHumanoidAgent noPidHumanoidAgent;
    [SerializeField] private ServerCommunication serverCommunication;
    [SerializeField] private IdlePoseCaptureRecorder poseCaptureRecorder;
    [SerializeField] private bool autoSaveCommandPoseToIdle = true;
    [SerializeField] private float transitionWaitTimeoutSeconds = 4.5f;

    private BodyCommandMatch lastExecutedMatch;
    private float[] lastPoseBeforeFlat;
    private float[] lastPoseAfterFlat;
    private string lastSavedPoseName = string.Empty;

    public BodyCommandMatch LastExecutedMatch => lastExecutedMatch;
    public string LastSavedPoseName => lastSavedPoseName;

    private void Awake()
    {
        if (noPidHumanoidAgent == null)
        {
            noPidHumanoidAgent = FindFirstObjectByType<NoPidHumanoidAgent>();
        }

        if (poseCaptureRecorder == null)
        {
            poseCaptureRecorder = FindFirstObjectByType<IdlePoseCaptureRecorder>();
        }

        if (serverCommunication == null)
        {
            serverCommunication = FindFirstObjectByType<ServerCommunication>();
        }
    }

    public bool TryExecute(BodyCommandMatch match, string sourceDvInput, out float[] poseBeforeFlat, out float[] poseAfterFlat)
    {
        poseBeforeFlat = null;
        poseAfterFlat = null;
        lastExecutedMatch = null;
        lastPoseBeforeFlat = null;
        lastPoseAfterFlat = null;

        if (match == null || noPidHumanoidAgent == null)
        {
            return false;
        }

        poseBeforeFlat = noPidHumanoidAgent.CaptureCurrentSignedEulerFlat();
        bool applied = noPidHumanoidAgent.ApplyBodyCommandPose(match.poseName, match.emotionHint);
        if (!applied)
        {
            Debug.LogWarning("[BodyCommand] pose apply failed id=" + match.commandId + " pose=" + match.poseName);
            return false;
        }

        Debug.Log("[BodyCommand] matched=" + match.commandId + " pose=" + match.poseName + " conf=" + match.confidence.ToString("F2"));
        lastExecutedMatch = match;
        lastPoseBeforeFlat = poseBeforeFlat;
        lastPoseAfterFlat = noPidHumanoidAgent.CaptureCurrentSignedEulerFlat();
        poseAfterFlat = lastPoseAfterFlat;
        return true;
    }

    public IEnumerator ExecuteAndFinalizeCoroutine(BodyCommandMatch match, string sourceDvInput)
    {
        if (!TryExecute(match, sourceDvInput, out float[] poseBefore, out _))
        {
            yield break;
        }

        float elapsed = 0f;
        while (elapsed < transitionWaitTimeoutSeconds && !noPidHumanoidAgent.IsEmotionTransitionComplete)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        lastPoseAfterFlat = noPidHumanoidAgent.CaptureCurrentSignedEulerFlat();
        lastSavedPoseName = string.Empty;
        if (autoSaveCommandPoseToIdle && poseCaptureRecorder != null)
        {
            string emotionFolder = ResolveEmotionFolder(match);
            lastSavedPoseName = poseCaptureRecorder.RecordCurrentPose(
                emotionFolder,
                "NIN_Cmd_" + match.commandId,
                IdlePoseCaptureRecorder.CaptureSource.BodyCommand,
                sourceDvInput,
                match.commandId);
        }

        Debug.Log("[BodyCommand] capture savedPose=" + lastSavedPoseName);
    }

    public void SendMotionLogForChatTurn(
        string dvInput,
        string ninResponse,
        string bodyCommandId,
        string emotionFolder,
        float learningWeight)
    {
        if (serverCommunication == null || !serverCommunication.IsServerRunning || noPidHumanoidAgent == null)
        {
            return;
        }

        noPidHumanoidAgent.TryBuildMotionLogVectors(out float[] obs, out float[] act);
        float[] poseAfter = noPidHumanoidAgent.CaptureCurrentSignedEulerFlat();
        serverCommunication.SendMotionTurnData(
            dvInput,
            ninResponse,
            bodyCommandId ?? string.Empty,
            emotionFolder ?? string.Empty,
            obs,
            act,
            lastPoseBeforeFlat,
            poseAfter,
            string.Empty,
            learningWeight);
    }

    private string ResolveEmotionFolder(BodyCommandMatch match)
    {
        if (match != null && !string.IsNullOrEmpty(match.emotionHint))
        {
            if (IdleEmotionRegistry.TryNormalizeEmotion(match.emotionHint, out string normalized))
            {
                return normalized;
            }

            return match.emotionHint;
        }

        return noPidHumanoidAgent != null ? noPidHumanoidAgent.CurrentTargetEmotion : EmojiEmotionMap.EmotionBasic;
    }
}
