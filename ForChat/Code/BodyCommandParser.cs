using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 사용자 채팅 문장에서 body_command_registry phrases를 부분 문자열 매칭한다.
/// </summary>
public static class BodyCommandParser
{
    private static readonly string[] ActionVerbHints =
    {
        "해봐", "해줘", "해봐요", "해주세요", "해", "하자", "보여", "보여줘", "해보", "들어", "올려"
    };

    public static bool TryParse(string userInput, out BodyCommandMatch match)
    {
        match = null;
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return false;
        }

        string normalized = userInput.Trim();
        bool hasActionVerb = ContainsAny(normalized, ActionVerbHints);
        BodyCommandRegistry.EnsureLoaded();
        List<BodyCommandEntry> commands = BodyCommandRegistry.GetSortedCommandsForMatching();
        if (commands.Count == 0)
        {
            Debug.LogError("[BodyCommand] registry has 0 commands — 1단계 SSOT 로드 실패.");
            return false;
        }

        BodyCommandMatch best = null;

        for (int i = 0; i < commands.Count; i++)
        {
            BodyCommandEntry entry = commands[i];
            if (entry.phrases == null)
            {
                continue;
            }

            for (int p = 0; p < entry.phrases.Length; p++)
            {
                string phrase = entry.phrases[p];
                if (string.IsNullOrWhiteSpace(phrase))
                {
                    continue;
                }

                if (normalized.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // 짧은 구문 오인식 완화: 2자 이하 구문은 동사 co-occur 필요
                if (phrase.Length <= 2 && !hasActionVerb)
                {
                    continue;
                }

                string poseName = ResolveFirstAvailablePose(entry.poseNames);
                if (string.IsNullOrEmpty(poseName))
                {
                    continue;
                }

                float confidence = Mathf.Clamp01(0.55f + phrase.Length * 0.02f + entry.priority * 0.001f);
                if (best == null || confidence > best.confidence)
                {
                    best = new BodyCommandMatch
                    {
                        commandId = entry.commandId,
                        poseName = poseName,
                        emotionHint = entry.emotionHint,
                        confidence = confidence
                    };
                }
            }
        }

        if (best == null)
        {
            return false;
        }

        match = best;
        return true;
    }

    private static string ResolveFirstAvailablePose(string[] poseNames)
    {
        if (poseNames == null || poseNames.Length == 0)
        {
            return string.Empty;
        }

        IdlePoseReferenceCache.EnsureLoaded(
            "Assets/AI/Model/Date/Idle/IdlePoseDataset.json",
            null,
            "NIN_Stand_At_Attention 1",
            true);

        for (int i = 0; i < poseNames.Length; i++)
        {
            string candidate = poseNames[i];
            if (IdlePoseReferenceCache.TryGetPoseByName(candidate, out _))
            {
                return candidate;
            }
        }

        Debug.LogWarning("[BodyCommand] no pose found in dataset for: " + string.Join(", ", poseNames));
        return string.Empty;
    }

    private static bool ContainsAny(string text, string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (text.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
