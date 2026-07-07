using NAudio.CoreAudioApi;
using Newtonsoft.Json.Linq;
using NUnit;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UnityEngine;

public class LipSync : MonoBehaviour
{
    public FaceReact face;
    public  bool isSpeaking = false;

    List<char> a = new List<char>();
    List<char> e = new List<char>();
    List<char> o = new List<char>();
    List<char> u = new List<char>();
    List<char> i = new List<char>();

    void Awake()
    {
        for (int code = 0xAC00; code <= 0xD7A3; code++)
        {
            char ch = (char)code;
            int sIndex = code - 0xAC00;
            int vowel = (sIndex % (21 * 28)) / 28;

            if (vowel == 0 || vowel == 2) a.Add(ch);
            else if (vowel == 1 || vowel == 3 || vowel == 5 || vowel == 7 || vowel == 10 || vowel == 15) e.Add(ch);
            else if (vowel == 4 || vowel == 6 || vowel == 8 || vowel == 9 || vowel == 11 || vowel == 12) o.Add(ch);
            else if (vowel == 13 || vowel == 14 || vowel == 16 || vowel == 17 || vowel == 18) u.Add(ch);
            else if (vowel == 19 || vowel == 20) i.Add(ch);
        }
    }

    public IEnumerator InputTextForLipSync(string input, AudioSource audio)
    {
        if (string.IsNullOrEmpty(input) || audio.clip == null)
            yield break;

        input = input.Normalize(NormalizationForm.FormC);

        int charCount = input.Length;
        if (charCount == 0) yield break;

        float totalTime = audio.clip.length;
        float perCharTime = totalTime / charCount;

        isSpeaking = true;

        for (int idx = 0; idx < input.Length; idx++)
        {
            char ch = input[idx];

            PlayMouth(ch);

            float targetTime = perCharTime * (idx + 1);

            while (audio.isPlaying && audio.time < targetTime)
            {
                if (!Application.isPlaying || !TTSRequester.IsTtsAllowed)
                {
                    break;
                }

                yield return null;
            }
        }

        isSpeaking = false;
    }
    void PlayMouth(char ch)
    {
        if (a.Contains(ch))
            StartCoroutine(face.MothUPGroup(new[] { face.mth_a }, new[] { 50f }));
        else if (e.Contains(ch))
            StartCoroutine(face.MothUPGroup(new[] { face.mth_e }, new[] { 50f }));
        else if (o.Contains(ch))
            StartCoroutine(face.MothUPGroup(new[] { face.mth_o }, new[] { 50f }));
        else if (u.Contains(ch))
            StartCoroutine(face.MothUPGroup(new[] { face.mth_u }, new[] { 50f }));
        else if (i.Contains(ch))
            StartCoroutine(face.MothUPGroup(new[] { face.mth_i }, new[] { 50f }));
    }

}
