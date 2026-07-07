using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UniJSON;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UI;
using UnityEngine.UIElements;
using static UnityEngine.Rendering.DebugUI;

public class EmojiData
{
    public string emoji;
    public string name;
    public string unicode;
}
public class FaceReact : MonoBehaviour
{
    public SkinnedMeshRenderer face;
    public LipSync lip;
    public GameObject Ohg, heart, t_heart;

    private string feel;
    public static bool isSpeaking = false;

    private static List<EmojiData> emojis = new List<EmojiData>();
    private string jsonString;

    public GameObject teardrop;
    private List<Vector3> lspos = new List<Vector3> { new Vector3(0.0554f, 1.4832f, -0.0797f), new Vector3(0.0474f, 1.4799f, -0.0797f) };
    private List<Vector3> rspos = new List<Vector3> { new Vector3(-0.0554f, 1.4832f, -0.0797f), new Vector3(-0.0474f, 1.4799f, -0.0797f) };
    private List<GameObject> L_teardrop = new List<GameObject>(), R_teardrop = new List<GameObject>();
    private int teardropMax = 20;
    [SerializeField] private float teardropLifetimeSeconds = 6f;
    [SerializeField] private float teardropCleanupIntervalSeconds = 0.2f;
    private Coroutine teardropCleanupRoutine;

    [Header("전체 표정 (Indices)")]
    public int all_neutral = 0;
    public int all_angry = 1, all_fun = 2, all_joy = 3, all_sorrow = 4, all_surprised = 5;

    [Header("눈썹 (Indices)")]
    public int bwr_angry = 6;
    public int bwr_fun = 7, bwr_joy = 8, bwr_sorrow = 9, bwr_surprised = 10;

    [Header("눈 (Indices)")]
    public int eye_neutral = 11;
    public int eye_angry = 12, eye_close = 13, eye_close_R = 14, eye_close_L = 15, eye_fun = 16;
    public int eye_joy = 17, eye_joy_R = 18, eye_joy_L = 19, eye_sorrow = 20, eye_surprised = 21, eye_spread = 22, eye_lris_hide = 23, eye_highlight = 24;

    [Header("입 (Indices)")]
    public int mth_close = 25;
    public int mth_up = 26, mth_down = 27, mth_angry = 28, mth_smail = 29, mth_large = 30, mth_neutral = 31, mth_fun = 32, mth_joy = 33, mth_sorrow = 34, mth_surprised = 35;
    public int mth_skinfung = 36, mth_skinfung_R = 37, mth_skinfung_L = 38, mth_a = 39, mth_i = 40, mth_u = 41, mth_e = 42, mth_o = 43;

    private float Left_Teardrop_Obj_Spuwn_Randm()
    {
        return UnityEngine.Random.Range(0.0258f, 0.0442f);
    }
    private float Right_Teardrop_Obj_Spuwn_Randm()
    {
        return UnityEngine.Random.Range(-0.0258f, -0.0442f);
    }
    private void Start()
    {
        LoadEmojiData();
        Debug.Log($"[AI Nia] 이모지 데이터 {emojis.Count}개 로드 완료");
    }
    private void LoadEmojiData()
    {
        string filePath = "Assets/AI/ForChat/unicode_emoji_sample.json";
        jsonString = File.ReadAllText(filePath);
        var jsonArray = JArray.Parse(jsonString);
        emojis.Clear();
        foreach (var item in jsonArray)
        {
            emojis.Add(new EmojiData
            {
                emoji = (string)item["emoji"],
                name = (string)item["name"],
                unicode = (string)item["unicode"]
            });
        }
    }
    public void FaceMove(string input)
    {
        var charEnum = System.Globalization.StringInfo.GetTextElementEnumerator(input);

        while (charEnum.MoveNext())
        {
            string element = charEnum.GetTextElement();

            foreach (var e in emojis)
            {
                if (element == e.emoji)
                {
                    feel = e.emoji;
                    Debug.Log("[AI Nia]" + feel);
                    Debug.Log("[AI Nia] 입모양 동기화: " + lip.isSpeaking);
                    if (lip.isSpeaking == false)
                    {
                        Debug.Log("[AI Nia] 입모양 동기화");
                        switch (feel)
                        {
                            case "😀":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy, mth_joy }, new float[] { 100, 100 }, 1f));
                                break;

                            case "😁":
                                StartCoroutine(FaceUPGroup(new int[] { all_joy, bwr_fun, bwr_joy, mth_fun, mth_o }, new float[] { 14, 100, 100, 60, 10 }, 1f));
                                break;

                            case "😂":
                                StartCoroutine(FaceUPGroup(new int[] { all_joy }, new float[] { 100 }, 2f));
                                StartCoroutine(TeardropAlGo(2));
                                break;

                            case "🤣":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy, eye_joy, mth_joy }, new float[] { 150, 100, 150 }, 1f));
                                StartCoroutine(TeardropAlGo(20));
                                break;

                            case "😃":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy, mth_joy, mth_i }, new float[] { 60, 100, 100 }, 1f));
                                break;

                            case "😄":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy, eye_fun, mth_joy, mth_i }, new float[] { 60, 100, 100, 100 }, 1f));
                                break;

                            case "😅":
                                StartCoroutine(FaceUPGroup(new int[] { all_fun, bwr_sorrow, mth_large, mth_sorrow, mth_surprised, mth_i }, new float[] { 100, 50, 15, 62, 34, 100 }, 2f));
                                break;

                            case "😆":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy, eye_joy, mth_joy }, new float[] { 80, 100, 80 }, 1f));
                                break;

                            case "😉":
                                int ran = UnityEngine.Random.Range(0, 2);
                                if (ran == 0)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, mth_joy, eye_close_L }, new float[] { 100, 100, 100, 80 }, 1f));
                                else if (ran == 1)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, mth_joy, eye_close_R }, new float[] { 100, 100, 100, 80 }, 1f));
                                break;

                            case "😊":
                                Debug.Log(1);
                                StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_fun, mth_fun }, new float[] { 24, 100, 100 }, 1f, true));
                                break;

                            case "😤":
                                StartCoroutine(FaceUPGroup(new int[] { all_angry, mth_angry, mth_large, mth_sorrow }, new float[] { 70, 100, 50, 100 }, 1f));
                                break;

                            case "😡":
                                StartCoroutine(FaceUPGroup(new int[] { all_angry }, new float[] { 150 }, 1f));
                                break;

                            case "😠":
                                StartCoroutine(FaceUPGroup(new int[] { all_angry }, new float[] { 80 }, 1f));
                                break;

                            case "😲":
                                StartCoroutine(FaceUPGroup(new int[] { all_surprised }, new float[] { 100 }, 1f));
                                break;
                            case "😘":
                                int ran1 = UnityEngine.Random.Range(0, 2);
                                if (ran1 == 0)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, eye_close_L, mth_u }, new float[] { 100, 100, 80, 100 }, 1f, false, true));
                                else if (ran1 == 1)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, eye_close_R, mth_u }, new float[] { 100, 100, 80, 100 }, 1f, false, true));
                                break;
                            case "🤔":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_angry, bwr_sorrow, eye_close_L, eye_surprised, mth_close, mth_up, mth_angry }, new float[] { 37, 37, 30, 50, 100, 100, 100 }, 1f));
                                break;

                            // Sad
                            case "😢":
                                StartCoroutine(FaceUPGroup(new int[] { all_sorrow, eye_sorrow, mth_sorrow }, new float[] { 100, 100, 100 }, 1f));
                                StartCoroutine(TeardropAlGo(8));
                                break;

                            case "😥":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_sorrow, mth_sorrow, mth_large }, new float[] { 60, 80, 70, 25 }, 1f));
                                break;

                            // Embarrassed
                            case "😳":
                                StartCoroutine(FaceUPGroup(new int[] { all_fun, bwr_sorrow, eye_surprised, mth_o }, new float[] { 45, 55, 45, 15 }, 1f));
                                break;

                            // Shy
                            case "🫣":
                                int ranShy = UnityEngine.Random.Range(0, 2);
                                if (ranShy == 0)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_fun, eye_close_L, mth_o }, new float[] { 40, 55, 85, 12 }, 1f));
                                else
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_fun, eye_close_R, mth_o }, new float[] { 40, 55, 85, 12 }, 1f));
                                break;
                        }
                    }
                    if (lip.isSpeaking == true)
                    {
                        Debug.Log("[AI Nia] 입모양 동기화 중지");
                        switch (feel)
                        {
                            case "😀":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy }, new float[] { 100, 100 }, 1f));
                                break;

                            case "😁":
                                StartCoroutine(FaceUPGroup(new int[] { eye_joy, bwr_fun, bwr_joy }, new float[] { 14, 100, 100 }, 1f));
                                break;

                            case "😂":
                                StartCoroutine(FaceUPGroup(new int[] { eye_joy, bwr_joy }, new float[] { 100, 100 }, 1f));
                                StartCoroutine(TeardropAlGo(2));
                                break;

                            case "🤣":
                                StartCoroutine(FaceUPGroup(new int[] { eye_joy, bwr_joy }, new float[] { 150, 150 }, 1f));
                                StartCoroutine(TeardropAlGo(20));
                                break;

                            case "😃":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy }, new float[] { 60 }, 1f));
                                break;

                            case "😄":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_joy, eye_fun }, new float[] { 60, 100 }, 1f));
                                break;

                            case "😅":
                                StartCoroutine(FaceUPGroup(new int[] { all_fun, bwr_sorrow }, new float[] { 100, 50 }, 1f));
                                break;

                            case "😆":
                                StartCoroutine(FaceUPGroup(new int[] { eye_joy, bwr_joy }, new float[] { 100, 100 }, 1f));
                                break;

                            case "😉":
                                int ran = UnityEngine.Random.Range(0, 2);
                                if (ran == 0)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, eye_close_L }, new float[] { 100, 100, 80 }, 1f));
                                else if (ran == 1)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, eye_close_R }, new float[] { 100, 100, 80 }, 1f));
                                break;

                            case "😊":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_fun, mth_fun }, new float[] { 24, 100, 100 }, 1f, true));
                                break;

                            case "😤":
                                StartCoroutine(FaceUPGroup(new int[] { eye_angry, bwr_angry }, new float[] { 70, 70 }, 1f));
                                break;

                            case "😡":
                                StartCoroutine(FaceUPGroup(new int[] { eye_angry, bwr_angry }, new float[] { 80, 80 }, 1f));
                                break;

                            case "😠":
                                StartCoroutine(FaceUPGroup(new int[] { eye_angry, bwr_angry }, new float[] { 150, 150 }, 1f));
                                break;

                            case "😲":
                                StartCoroutine(FaceUPGroup(new int[] { eye_surprised, bwr_surprised }, new float[] { 100, 100 }, 1f));
                                break;
                            case "😘":
                                int ran1 = UnityEngine.Random.Range(0, 2);
                                if (ran1 == 0)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, eye_close_L }, new float[] { 100, 100, 80 }, 1f, false, true));
                                else if (ran1 == 1)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_fun, eye_fun, eye_close_R }, new float[] { 100, 100, 80 }, 1f, false, true));
                                break;
                            case "🤔":
                                StartCoroutine(FaceUPGroup(new int[] { bwr_angry, bwr_sorrow, eye_close_L, eye_surprised }, new float[] { 37, 37, 30, 50, }, 1f));
                                break;

                            case "😢":
                                StartCoroutine(FaceUPGroup(new int[] { eye_sorrow, bwr_sorrow }, new float[] { 100, 100 }, 1f));
                                StartCoroutine(TeardropAlGo(8));
                                break;

                            case "😥":
                                StartCoroutine(FaceUPGroup(new int[] { eye_sorrow, bwr_sorrow }, new float[] { 80, 60 }, 1f));
                                break;

                            case "😳":
                                StartCoroutine(FaceUPGroup(new int[] { eye_surprised, bwr_sorrow }, new float[] { 45, 50 }, 1f));
                                break;

                            case "🫣":
                                int ranShySpeak = UnityEngine.Random.Range(0, 2);
                                if (ranShySpeak == 0)
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_fun, eye_close_L }, new float[] { 40, 55, 85 }, 1f));
                                else
                                    StartCoroutine(FaceUPGroup(new int[] { bwr_sorrow, eye_fun, eye_close_R }, new float[] { 40, 55, 85 }, 1f));
                                break;
                        }
                    }
                }
            }
        }
    }
    public IEnumerator MothUPGroup(int[] indices, float[] targets)
    {
        List<Coroutine> ups = new List<Coroutine>();

        for (int i = 0; i < indices.Length; i++)
            ups.Add(StartCoroutine(MthUP(indices[i], targets[i])));

        yield return new WaitForSeconds(0.1f);

        for (int i = 0; i < indices.Length; i++)
            StartCoroutine(MthDown(indices[i], 0));
    }
    private IEnumerator FaceUPGroup(int[] indices, float[] targets, float f_speed, bool hg = false, bool ht = false, int f_si = 5, float face_wait = 4.5f)
    {
        Debug.Log("[AI Nia] 얼굴 동기화 시작");
        List<Coroutine> ups = new List<Coroutine>();

        if (hg) { Ohg.SetActive(hg); }
        if (ht)
        {
            StartCoroutine(MoveToPosition(new Vector3(0.0608f, 1.431f, -0.1668f)));
        }

        for (int i = 0; i < indices.Length; i+= f_si)
            ups.Add(StartCoroutine(FaceUP(indices[i], targets[i], f_speed)));

        foreach (var c in ups)
            yield return c;

        yield return new WaitForSeconds(face_wait);

        if (Ohg.activeSelf == true) { Ohg.SetActive(false); }

        for (int i = 0; i < indices.Length; i++)
            StartCoroutine(FaceDown(indices[i], 0));
    }
    private IEnumerator FaceUP(int index1, float targetValue, float f_speed)
    {
        float value = face.GetBlendShapeWeight(index1);
        while (value < targetValue)
        {
            value += f_speed;
            face.SetBlendShapeWeight(index1, value);
            yield return new WaitForSeconds(0.001f);
        }
        face.SetBlendShapeWeight(index1, targetValue);
    }
    private IEnumerator FaceDown(int index1, float targetValue)
    {
        float value = face.GetBlendShapeWeight(index1);
        while (value > targetValue)
        {
            value -= 1f;
            face.SetBlendShapeWeight(index1, value);
            yield return new WaitForSeconds(0.001f);
        }
        face.SetBlendShapeWeight(index1, targetValue);
    }
    private IEnumerator MthUP(int index, float targetValue)
    {
        float start = face.GetBlendShapeWeight(index);
        float duration = 0.02f;
        float time = 0f;

        while (time < duration)
        {
            time += Time.deltaTime;
            float t = time / duration;
            face.SetBlendShapeWeight(index, Mathf.Lerp(start, targetValue, t));
            yield return null;
        }

        face.SetBlendShapeWeight(index, targetValue + 10);
    }
    private IEnumerator MthDown(int index, float targetValue)
    {
        float start = face.GetBlendShapeWeight(index);
        float duration = 0.02f;
        float time = 0f;

        while (time < duration)
        {
            time += Time.deltaTime;
            float t = time / duration;
            face.SetBlendShapeWeight(index, Mathf.Lerp(start, targetValue, t));
            yield return null;
        }

        face.SetBlendShapeWeight(index, targetValue);
    }
    private IEnumerator TeardropAlGo(int tr_max)
    {
        if (tr_max != 0) { teardropMax = tr_max; }
        for (int tr = 0; tr < teardropMax; tr++)
        {

            if (L_teardrop.Count < teardropMax && R_teardrop.Count < teardropMax)
            {
                Vector3 spl = new Vector3(Left_Teardrop_Obj_Spuwn_Randm(), 1.4775f, -0.0797f);
                GameObject new_teardrop_L = Instantiate(teardrop, spl, Quaternion.identity);
                Vector3 spR = new Vector3(Right_Teardrop_Obj_Spuwn_Randm(), 1.4775f, -0.0797f);
                GameObject new_teardrop_R = Instantiate(teardrop, spR, Quaternion.identity);
                L_teardrop.Add(new_teardrop_L);
                R_teardrop.Add(new_teardrop_R);
                if (teardropLifetimeSeconds > 0f)
                {
                    Destroy(new_teardrop_L, teardropLifetimeSeconds);
                    Destroy(new_teardrop_R, teardropLifetimeSeconds);
                }
                yield return new WaitForSeconds(0.25f);
            }
            else { break; }
        }

        if (teardropCleanupRoutine != null)
        {
            StopCoroutine(teardropCleanupRoutine);
        }
        teardropCleanupRoutine = StartCoroutine(TeardropCleanupLoop());
    }
    private IEnumerator TeardropCleanupLoop()
    {
        while (true)
        {
            TeardropMM();

            if (L_teardrop.Count == 0 && R_teardrop.Count == 0)
            {
                teardropCleanupRoutine = null;
                yield break;
            }

            yield return new WaitForSeconds(teardropCleanupIntervalSeconds);
        }
    }
    private void TeardropMM()
    {

        for (int i = L_teardrop.Count - 1; i >= 0; i--)
        {
            if (L_teardrop[i] == null)
            {
                L_teardrop.RemoveAt(i);
                continue;
            }
            if (L_teardrop[i].transform.position.y < -1f)
            {
                Destroy(L_teardrop[i]);
                L_teardrop.RemoveAt(i);
            }
        }
        for (int i = R_teardrop.Count - 1; i >= 0; i--)
        {
            if (R_teardrop[i] == null)
            {
                R_teardrop.RemoveAt(i);
                continue;
            }
            if (R_teardrop[i].transform.position.y < -1f)
            {
                Destroy(R_teardrop[i]);
                R_teardrop.RemoveAt(i);
            }
        }
    }
    private IEnumerator MoveToPosition(Vector3 target, float speed = 0.05f)
    {
        Vector3 spl = new Vector3(0, 1.422f, -0.097f);
        Quaternion sr = Quaternion.Euler(-90f, 0, 0);
        GameObject new_heart = Instantiate(heart, spl, sr);
        while (Vector3.Distance(new_heart.transform.position, target) > 0.01f)
        {
            new_heart.transform.position = Vector3.MoveTowards(
                new_heart.transform.position,
                target,
                speed * Time.deltaTime
            );
            yield return null;
        }
        new_heart.transform.position = target; 
        Destroy(new_heart);
    }
}