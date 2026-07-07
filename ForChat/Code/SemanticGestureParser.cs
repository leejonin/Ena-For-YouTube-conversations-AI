using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

public class SemanticGestureParser
{
    private static readonly Regex BracketRegex = new Regex(@"\((.*?)\)", RegexOptions.Compiled | RegexOptions.Singleline);

    public NINParsedResponse Parse(string responseText)
    {
        NINParsedResponse parsed = new NINParsedResponse { rawResponse = responseText };
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return parsed;
        }

        MatchCollection matches = BracketRegex.Matches(responseText);
        if (matches.Count == 0)
        {
            AddChunk(parsed, responseText.Trim(), 0);
            return parsed;
        }

        int index = 0;
        foreach (Match m in matches)
        {
            string chunk = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(chunk))
            {
                continue;
            }

            AddChunk(parsed, chunk, index);
            index++;
        }

        return parsed;
    }

    private void AddChunk(NINParsedResponse parsed, string chunkText, int index)
    {
        string emoji = ExtractFirstEmoji(chunkText);
        string actionCategory = ResolveActionCategory(emoji, chunkText);
        IntentActionCategoryEntry registryEntry = null;
        IntentBehaviorRegistry.TryGetCategoryEntry(actionCategory, out registryEntry);

        NINTargetState targetState = BuildTargetState(actionCategory, registryEntry);
        string behaviorTag = registryEntry != null ? registryEntry.behaviorTag : "BasicIdle";
        string emotionFolder = IntentBehaviorRegistry.ResolveEmotionFolder(actionCategory, emoji);
        string gazeMode = registryEntry != null ? registryEntry.gazeMode : "IdleLookAround";

        // 행동 묘사(*...*) 청크는 TTS를 생략한다. 이모지는 표정·모션에 계속 반영된다.
        string spoken = IsActionDescription(chunkText)
            ? string.Empty
            : RemoveEmoji(chunkText);

        parsed.chunks.Add(new NINSpeechMotionChunk
        {
            index = index,
            rawText = chunkText,
            spokenText = spoken,
            timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            motion = new NINMotionDirective
            {
                actionCategory = actionCategory,
                behaviorTag = behaviorTag,
                emotionFolder = emotionFolder,
                gazeMode = gazeMode,
                emoji = emoji,
                targetState = targetState
            }
        });
    }

    // 이모지를 제거한 순수 텍스트가 *...* 행동 묘사인 경우 true 반환
    private bool IsActionDescription(string chunk)
    {
        string stripped = RemoveEmoji(chunk).Trim();
        return stripped.StartsWith("*") && stripped.EndsWith("*") && stripped.Length > 2;
    }

    private string ExtractFirstEmoji(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        List<string> mapped = EmojiEmotionMap.ExtractMappedEmojisInOrder(text);
        return mapped != null && mapped.Count > 0 ? mapped[0] : string.Empty;
    }

    private string RemoveEmoji(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        List<string> mapped = EmojiEmotionMap.ExtractMappedEmojisInOrder(text);
        if (mapped == null || mapped.Count == 0)
        {
            return text.Trim();
        }

        string result = text;
        for (int i = 0; i < mapped.Count; i++)
        {
            result = result.Replace(mapped[i], string.Empty);
        }

        return result.Trim();
    }

    private string ResolveActionCategory(string emoji, string text)
    {
        if (IntentBehaviorRegistry.TryResolveActionCategory(emoji, text, out IntentActionCategoryEntry entry)
            && entry != null)
        {
            return entry.actionCategory;
        }

        return "BasicIdle";
    }

    private NINTargetState BuildTargetState(string actionCategory, IntentActionCategoryEntry registryEntry)
    {
        NINTargetState state = new NINTargetState
        {
            leftHandLocal = new Vector3(-0.18f, 1.24f, 0.22f),
            rightHandLocal = new Vector3(0.18f, 1.24f, 0.22f),
            spineRotation = Quaternion.Euler(0f, 0f, 0f),
            neckRotation = Quaternion.Euler(0f, 0f, 0f),
            actionEmbedding = new float[16]
        };

        if (registryEntry != null)
        {
            IntentBehaviorRegistry.ApplyTargetStateFromCategory(
                new NINMotionDirective { actionCategory = actionCategory, targetState = state },
                registryEntry);
            return state;
        }

        state.actionEmbedding[0] = 1f;
        return state;
    }
}
