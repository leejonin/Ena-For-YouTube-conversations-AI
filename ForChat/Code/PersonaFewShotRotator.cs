using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// persona_variation_pool.json — 턴마다 few-shot 예시를 회전해 응답 다양성을 높인다.
/// </summary>
public class PersonaFewShotRotator
{
    private readonly Dictionary<string, List<string>> categories = new Dictionary<string, List<string>>();
    private readonly System.Random rng = new System.Random();
    private bool loaded;

    public void LoadFromDisk()
    {
        if (loaded)
        {
            return;
        }

        loaded = true;
        categories.Clear();

        string path = Path.Combine(Application.dataPath, "AI", "ForChat", "Resources", "persona_variation_pool.json");
        if (!File.Exists(path))
        {
            Debug.LogWarning("[PersonaRotator] pool not found: " + path);
            return;
        }

        try
        {
            JObject root = JObject.Parse(File.ReadAllText(path));
            JObject cats = root["categories"] as JObject;
            if (cats == null)
            {
                return;
            }

            foreach (JProperty prop in cats.Properties())
            {
                if (prop.Value is JArray arr && arr.Count > 0)
                {
                    var list = new List<string>();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        string s = arr[i]?.ToString()?.Trim();
                        if (!string.IsNullOrEmpty(s))
                        {
                            list.Add(s);
                        }
                    }

                    if (list.Count > 0)
                    {
                        categories[prop.Name] = list;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[PersonaRotator] load failed: " + ex.Message);
        }
    }

    public string BuildRotatedFewShot(
        bool isSelfTalk,
        bool isYoutube,
        bool isNewViewer,
        string excludeSimilarTo = null)
    {
        LoadFromDisk();
        if (categories.Count == 0)
        {
            return string.Empty;
        }

        var pickCategories = new List<string>();
        if (isNewViewer && categories.ContainsKey("new_viewer"))
        {
            pickCategories.Add("new_viewer");
        }

        if (isYoutube && categories.ContainsKey("youtube_chat"))
        {
            pickCategories.Add("youtube_chat");
        }

        if (isSelfTalk && categories.ContainsKey("self_talk"))
        {
            pickCategories.Add("self_talk");
        }

        string[] defaults = { "greeting", "reaction", "comfort", "tease" };
        for (int i = 0; i < defaults.Length && pickCategories.Count < 4; i++)
        {
            if (categories.ContainsKey(defaults[i]) && !pickCategories.Contains(defaults[i]))
            {
                pickCategories.Add(defaults[i]);
            }
        }

        while (pickCategories.Count < 3)
        {
            string key = PickRandomCategoryKey(pickCategories);
            if (string.IsNullOrEmpty(key))
            {
                break;
            }

            pickCategories.Add(key);
        }

        var sb = new StringBuilder(256);
        sb.Append(" [턴예시·회전] ");
        for (int c = 0; c < pickCategories.Count; c++)
        {
            string cat = pickCategories[c];
            if (!categories.TryGetValue(cat, out List<string> examples) || examples.Count == 0)
            {
                continue;
            }

            int exampleCount = rng.Next(1, 3);
            var usedIdx = new HashSet<int>();
            for (int e = 0; e < exampleCount && usedIdx.Count < examples.Count; e++)
            {
                int idx = rng.Next(0, examples.Count);
                if (!usedIdx.Add(idx))
                {
                    continue;
                }

                string example = examples[idx];
                if (ShouldExcludeExample(example, excludeSimilarTo))
                {
                    continue;
                }

                sb.Append(example).Append(' ');
            }
        }

        sb.Append("퓨샷 예시 문장 그대로 반복 금지 — 어조만 참고. ");
        return sb.ToString();
    }

    private static bool ShouldExcludeExample(string example, string excludeSimilarTo)
    {
        if (string.IsNullOrWhiteSpace(example) || string.IsNullOrWhiteSpace(excludeSimilarTo))
        {
            return false;
        }

        string flatExample = example.Replace(" ", string.Empty);
        string flatExclude = excludeSimilarTo.Replace(" ", string.Empty);
        if (flatExclude.IndexOf(flatExample, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        if (flatExample.Length >= 6
            && flatExclude.IndexOf(flatExample.Substring(0, 6), StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        return false;
    }

    private string PickRandomCategoryKey(List<string> exclude)
    {
        var keys = new List<string>();
        foreach (KeyValuePair<string, List<string>> kv in categories)
        {
            if (kv.Value == null || kv.Value.Count == 0)
            {
                continue;
            }

            if (exclude != null && exclude.Contains(kv.Key))
            {
                continue;
            }

            keys.Add(kv.Key);
        }

        if (keys.Count == 0)
        {
            return null;
        }

        return keys[rng.Next(0, keys.Count)];
    }
}
