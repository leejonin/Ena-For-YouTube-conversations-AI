using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class SpeechMotionChunkPipeline
{
    private readonly Queue<NINSpeechMotionChunk> speechQueue = new Queue<NINSpeechMotionChunk>();
    private readonly Queue<NINSpeechMotionChunk> motionQueue = new Queue<NINSpeechMotionChunk>();
    private volatile bool cancelRequested;

    public void Enqueue(NINParsedResponse parsed)
    {
        if (parsed == null || parsed.chunks == null)
        {
            return;
        }

        for (int i = 0; i < parsed.chunks.Count; i++)
        {
            NINSpeechMotionChunk chunk = parsed.chunks[i];
            speechQueue.Enqueue(chunk);
            motionQueue.Enqueue(chunk);
        }
    }

    public void RequestCancel()
    {
        cancelRequested = true;
    }

    public void ResetCancel()
    {
        cancelRequested = false;
    }

    public async Task<List<NINSpeechMotionChunk>> RunAsync(
        TTSRequester tts,
        FaceReact face,
        IdlePoseRuntimePlayer idlePoseRuntimePlayer,
        NoPidHumanoidAgent noPidHumanoidAgent,
        bool skipBodyEmotionFromChunks = false,
        bool useNoPidSlInference = true,
        NINGazeController gazeController = null,
        Func<bool> isTurnCancelled = null,
        int postInterruptDebounceMs = 0)
    {
        if (postInterruptDebounceMs > 0 && SendMessage.LastBargeInInterruptUtc != DateTime.MinValue)
        {
            double elapsedMs = (DateTime.UtcNow - SendMessage.LastBargeInInterruptUtc).TotalMilliseconds;
            if (elapsedMs >= 0 && elapsedMs < postInterruptDebounceMs)
            {
                await Task.Delay(Mathf.Max(1, (int)(postInterruptDebounceMs - elapsedMs)));
            }
        }

        if (IsCancelled(isTurnCancelled))
        {
            return new List<NINSpeechMotionChunk>();
        }

        List<NINSpeechMotionChunk> chunks = new List<NINSpeechMotionChunk>();
        while (speechQueue.Count > 0 && motionQueue.Count > 0)
        {
            NINSpeechMotionChunk s = speechQueue.Dequeue();
            NINSpeechMotionChunk m = motionQueue.Dequeue();
            if (s.index == m.index)
            {
                chunks.Add(s);
            }
        }

        List<NINSpeechMotionChunk> processed = new List<NINSpeechMotionChunk>();
        int fetchGeneration = TTSRequester.FetchGeneration;

        Task<byte[]> currentBytesTask = null;
        if (chunks.Count > 0 && !string.IsNullOrWhiteSpace(chunks[0].spokenText))
        {
            currentBytesTask = tts.FetchTTSBytesAsync(chunks[0].spokenText, fetchGeneration);
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            if (IsCancelled(isTurnCancelled))
            {
                return processed;
            }

            NINSpeechMotionChunk chunk = chunks[i];

            ApplyMotion(
                chunk,
                face,
                idlePoseRuntimePlayer,
                noPidHumanoidAgent,
                skipBodyEmotionFromChunks,
                useNoPidSlInference,
                gazeController);

            if (!string.IsNullOrWhiteSpace(chunk.spokenText))
            {
                if (IsCancelled(isTurnCancelled))
                {
                    return processed;
                }

                Task<byte[]> nextBytesTask = null;
                if (i + 1 < chunks.Count && !string.IsNullOrWhiteSpace(chunks[i + 1].spokenText))
                {
                    nextBytesTask = tts.FetchTTSBytesAsync(chunks[i + 1].spokenText, fetchGeneration);
                }

                byte[] mp3 = currentBytesTask != null
                    ? await currentBytesTask
                    : await tts.FetchTTSBytesAsync(chunk.spokenText, fetchGeneration);

                if ((mp3 == null || mp3.Length == 0) && !IsCancelled(isTurnCancelled)
                    && !TTSRequester.IsFetchCancelled(fetchGeneration))
                {
                    mp3 = await tts.FetchTTSBytesAsync(chunk.spokenText, fetchGeneration);
                }

                if (IsCancelled(isTurnCancelled) || TTSRequester.IsFetchCancelled(fetchGeneration))
                {
                    return processed;
                }

                if (mp3 == null || mp3.Length == 0)
                {
                    Debug.LogWarning("[TTS] chunk synthesis empty, skipped: " + chunk.spokenText);
                    processed.Add(chunk);
                    currentBytesTask = nextBytesTask;
                    continue;
                }

                await tts.PlayTTSFromBytes(mp3, chunk.spokenText);
                currentBytesTask = nextBytesTask;
            }

            processed.Add(chunk);
        }

        return processed;
    }

    private bool IsCancelled(Func<bool> isTurnCancelled)
    {
        return cancelRequested || (isTurnCancelled != null && isTurnCancelled());
    }

    private void ApplyMotion(
        NINSpeechMotionChunk chunk,
        FaceReact face,
        IdlePoseRuntimePlayer idlePoseRuntimePlayer,
        NoPidHumanoidAgent noPidHumanoidAgent,
        bool skipBodyEmotionFromChunks,
        bool useNoPidSlInference,
        NINGazeController gazeController)
    {
        if (chunk == null || chunk.motion == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(chunk.motion.emoji))
        {
            if (face != null)
            {
                face.FaceMove(chunk.motion.emoji);
            }
        }

        if (gazeController != null && !string.IsNullOrEmpty(chunk.motion.gazeMode))
        {
            gazeController.ApplyGazeMode(chunk.motion.gazeMode);
        }

        if (!skipBodyEmotionFromChunks)
        {
            if (useNoPidSlInference && noPidHumanoidAgent != null)
            {
                noPidHumanoidAgent.SetTargetFromMotionDirective(chunk.motion);
            }
            else if (idlePoseRuntimePlayer != null && !string.IsNullOrWhiteSpace(chunk.motion.emoji))
            {
                idlePoseRuntimePlayer.ApplyEmotionByEmoji(chunk.motion.emoji);
            }
            else if (noPidHumanoidAgent != null && !string.IsNullOrWhiteSpace(chunk.motion.emoji))
            {
                noPidHumanoidAgent.SetTargetEmotionByEmoji(chunk.motion.emoji);
            }
        }
    }
}
