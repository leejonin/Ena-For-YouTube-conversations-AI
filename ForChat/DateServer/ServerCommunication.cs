using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class ServerCommunication : MonoBehaviour
{
    // ──────────────────────────────────────────────
    //  직렬화 모델
    // ──────────────────────────────────────────────

    [Serializable]
    private class DataPayload
    {
        public string time;
        public long timestamp;
        public string dv_input;
        public string nin_response;
        public float[] action_embedding;
        public string body_command_id;
        public string emotion_folder;
        public float[] obs_vector;
        public float[] act_vector;
        public float[] pose_before;
        public float[] pose_after;
        public string saved_pose_name;
        public float learning_weight;
        public float[] policy_act_vector;
        public bool guidance_applied;
        public float[] guidance_joint_mask;
        public string guidance_reason;
        public string reference_pose_name;
        public float dataset_match_score;
        public string intent_category;
        public string behavior_tag;
        public string turn_source;
        public string youtube_channel_id;
        public string youtube_display_name;
    }

    [Serializable]
    private class ResponsePayload
    {
        public bool success;
        public string message;
        public string summation;
        public string error;
    }

    /// <summary>
    /// /search 요청 바디
    /// </summary>
    [Serializable]
    private class SearchRequest
    {
        public string keyword;
        // 서버 hybrid_search 기본값과 맞춤 — 옛 기억·동의어 병합 결과 수
        public int top_k = 12;
    }

    [Serializable]
    private class ViewerSearchRequest
    {
        public string channel_id;
        public int top_k = 5;
    }

    /// <summary>
    /// /search 응답 바디 (단일 매치 항목)
    /// </summary>
    [Serializable]
    public class MemoryEntry
    {
        public string time;
        public string dv_input;
        public string nin_response;
        public string summation;
    }

    /// <summary>
    /// /search 응답 전체 래퍼
    /// Unity JsonUtility는 제네릭 리스트를 직접 파싱하지 못하므로
    /// matches 배열은 수동으로 파싱합니다(아래 ParseSearchResponse 참조).
    /// </summary>
    [Serializable]
    public class SearchResponse
    {
        public bool success;
        public bool found;
        public string keyword;
        public string reason;
        public string gpt_answer;
        // matches[]는 JsonUtility 한계로 별도 파싱
    }

    /// <summary>
    /// SearchMemory 콜백 결과
    /// </summary>
    public class SearchResult
    {
        public bool success;       // HTTP 통신 성공 여부
        public bool found;         // 관련 기억 존재 여부
        public string keyword;       // 검색어
        public string reason;        // 사람이 읽을 수 있는 이유 메시지
        public string gptAnswer;     // GPT가 정리한 핵심 정보 (found=false면 빈 문자열)
        public List<MemoryEntry> matches = new List<MemoryEntry>();
        public string errorMessage;  // 통신 실패 시 오류 내용
    }

    // ──────────────────────────────────────────────
    //  Inspector 필드
    // ──────────────────────────────────────────────

    [SerializeField] private string serverUrl = "http://127.0.0.1:5000";
    [SerializeField] private bool autoOpenDashboard = true;   // 서버 준비 후 브라우저 자동 실행

    // ──────────────────────────────────────────────
    //  내부 상태
    // ──────────────────────────────────────────────

    private Process pythonProcess;
    private bool serverRunning = false;
    private string pythonScriptPath;
    private string pythonExecutablePath;

    // ──────────────────────────────────────────────
    //  Unity 생명주기
    // ──────────────────────────────────────────────

    void Start()
    {
        pythonScriptPath = System.IO.Path.Combine(
            Application.dataPath,
            "AI", "ForChat", "DateServer", "Sever.py"
        );
        pythonExecutablePath = ResolvePythonExecutablePath();

        if (!System.IO.File.Exists(pythonScriptPath))
        {
            UnityEngine.Debug.LogError($"Python 스크립트를 찾을 수 없습니다: {pythonScriptPath}");
            return;
        }

        StartPythonServer();
    }

    void OnApplicationQuit()
    {
        StopPythonServer();
    }

    // ──────────────────────────────────────────────
    //  서버 시작 / 종료
    // ──────────────────────────────────────────────

    private void StartPythonServer()
    {
        try
        {
            if (pythonProcess != null && !pythonProcess.HasExited)
                return;

            pythonProcess = new Process();
            pythonProcess.StartInfo.FileName = pythonExecutablePath;
            pythonProcess.StartInfo.Arguments = $"\"{pythonScriptPath}\"";
            pythonProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(pythonScriptPath);
            pythonProcess.StartInfo.UseShellExecute = false;
            pythonProcess.StartInfo.RedirectStandardOutput = true;
            pythonProcess.StartInfo.RedirectStandardError = true;
            pythonProcess.StartInfo.CreateNoWindow = true;

            pythonProcess.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    UnityEngine.Debug.Log($"Python 서버: {e.Data}");
            };

            pythonProcess.ErrorDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;

                var message = $"Python stderr: {e.Data}";
                if (e.Data.Contains("Traceback") || e.Data.Contains("Error") ||
                    e.Data.Contains("Exception") || e.Data.Contains("failed") || e.Data.Contains("FAILED"))
                    UnityEngine.Debug.LogError(message);
                else
                    UnityEngine.Debug.LogWarning(message);
            };

            pythonProcess.Start();
            pythonProcess.BeginOutputReadLine();
            pythonProcess.BeginErrorReadLine();

            serverRunning = true;
            UnityEngine.Debug.Log($"Python 데이터 서버 시작 완료 (Python: {pythonExecutablePath})");

            StartCoroutine(WaitForServerReady());
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Python 서버 시작 실패: {e.Message}");
            serverRunning = false;
        }
    }

    private string ResolvePythonExecutablePath()
    {
        // DateServer 전용 가상환경 python을 우선 사용한다.
        string venvPython = Path.Combine(
            Application.dataPath,
            "AI", "ForChat", "DateServer", ".venv", "Scripts", "python.exe"
        );

        if (File.Exists(venvPython))
        {
            return venvPython;
        }

        // 가상환경이 없으면 시스템 python으로 fallback한다.
        return "python";
    }

    private void StopPythonServer()
    {
        try
        {
            if (pythonProcess != null && !pythonProcess.HasExited)
            {
                pythonProcess.Kill();
                pythonProcess.WaitForExit(5000);
                pythonProcess.Dispose();
                UnityEngine.Debug.Log("Python 서버 종료 완료");
            }
            serverRunning = false;
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"Python 서버 종료 오류: {e.Message}");
        }
    }

    private IEnumerator WaitForServerReady()
    {
        float timeout = 10f;
        float elapsedTime = 0f;

        while (elapsedTime < timeout)
        {
            UnityWebRequest request = UnityWebRequest.Get($"{serverUrl}/health");
            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                UnityEngine.Debug.Log("서버 준비 완료!");
                if (autoOpenDashboard)
                    OpenDashboard();
                yield break;
            }

            elapsedTime += 0.5f;
            yield return new WaitForSeconds(0.5f);
        }

        UnityEngine.Debug.LogWarning("서버 연결 타임아웃");
    }

    /// <summary>브라우저로 대시보드를 엽니다. 외부에서 직접 호출 가능.</summary>
    public void OpenDashboard()
    {
        string url = $"{serverUrl}/";
        UnityEngine.Debug.Log($"[Dashboard] 브라우저 실행: {url}");
        Application.OpenURL(url);
    }

    // ──────────────────────────────────────────────
    //  데이터 전송
    // ──────────────────────────────────────────────

    public void SendDataToServer(string time, long timestamp, string dvInput, string ninResponse, float[] actionEmbedding, string turnSource = "user")
    {
        SendDataToServer(time, timestamp, dvInput, ninResponse, actionEmbedding, turnSource, null, null);
    }

    public void SendDataToServer(
        string time,
        long timestamp,
        string dvInput,
        string ninResponse,
        float[] actionEmbedding,
        string turnSource,
        string youtubeChannelId,
        string youtubeDisplayName)
    {
        StartCoroutine(SendDataCoroutine(time, timestamp, dvInput, ninResponse, actionEmbedding, turnSource, youtubeChannelId, youtubeDisplayName));
    }

    private IEnumerator SendDataCoroutine(
        string time,
        long timestamp,
        string dvInput,
        string ninResponse,
        float[] actionEmbedding,
        string turnSource = "user",
        string youtubeChannelId = null,
        string youtubeDisplayName = null)
    {
        UnityWebRequest request = null;
        try
        {
            var payload = new DataPayload
            {
                time = time,
                timestamp = timestamp,
                dv_input = dvInput,
                nin_response = ninResponse,
                action_embedding = actionEmbedding,
                turn_source = turnSource ?? "user",
                youtube_channel_id = youtubeChannelId ?? string.Empty,
                youtube_display_name = youtubeDisplayName ?? string.Empty
            };
            string jsonData = JsonUtility.ToJson(payload);

            request = new UnityWebRequest($"{serverUrl}/data", "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.uploadHandler.contentType = "application/json";
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Content-Length", bodyRaw.Length.ToString());

            UnityEngine.Debug.Log($"서버 전송 POST {serverUrl}/data, length={bodyRaw.Length}, json={jsonData}");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"데이터 전송 오류: {e.Message}");
        }

        if (request == null) yield break;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            string responseText = TryGetResponseText(request);
            UnityEngine.Debug.LogError($"데이터 전송 실패: {request.error} ({request.responseCode}) {responseText}");
        }
        else
        {
            string responseText = TryGetResponseText(request);
            try
            {
                var response = JsonUtility.FromJson<ResponsePayload>(responseText);
                if (response != null && !string.IsNullOrEmpty(response.summation))
                    UnityEngine.Debug.Log($"데이터 전송 완료: {time} | 요약: {response.summation}");
                else
                    UnityEngine.Debug.Log($"데이터 전송 완료: {time}");
            }
            catch
            {
                UnityEngine.Debug.Log($"데이터 전송 완료: {time}");
            }
        }

        request.Dispose();
    }

    // ──────────────────────────────────────────────
    //  기억 검색  SearchMemory
    // ──────────────────────────────────────────────

    /// <summary>
    /// 키워드로 YYDate.Json의 대화 기억을 검색하고 결과를 직접 반환합니다.
    ///
    /// ── async/await 사용 예시 ──
    ///   SearchResult result = await serverCommunication.SearchMemory("이나 생일");
    ///   if (result.found)
    ///       Debug.Log(result.gptAnswer);
    ///   else
    ///       Debug.Log(result.reason);   // "관련 기억이 없습니다"
    ///
    /// ── Coroutine에서 사용 예시 ──
    ///   yield return serverCommunication.SearchMemoryCoroutine("이나 생일",
    ///       result => { if (result.found) Debug.Log(result.gptAnswer); });
    /// </summary>
    public Task<SearchResult> SearchMemory(string keyword)
    {
        var tcs = new TaskCompletionSource<SearchResult>();
        StartCoroutine(SearchMemoryCoroutine(keyword, result => tcs.SetResult(result)));
        return tcs.Task;
    }

    // Coroutine 직접 접근이 필요한 경우를 위해 public으로도 노출
    public IEnumerator SearchMemoryCoroutine(string keyword, Action<SearchResult> callback)
    {
        keyword = (keyword ?? string.Empty).Trim();
        var searchResult = new SearchResult { keyword = keyword };

        if (!serverRunning)
        {
            searchResult.success = false;
            searchResult.errorMessage = "서버가 실행 중이 아닙니다.";
            callback?.Invoke(searchResult);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            // 빈 검색어는 서버에 보내지 않고 즉시 반환한다(400 예방).
            searchResult.success = true;
            searchResult.found = false;
            searchResult.reason = "검색어가 비어 있습니다.";
            callback?.Invoke(searchResult);
            yield break;
        }

        // ── 요청 빌드 ──
        string jsonBody = JsonUtility.ToJson(new SearchRequest { keyword = keyword });
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

        var request = new UnityWebRequest($"{serverUrl}/search", "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.uploadHandler.contentType = "application/json";
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Accept", "application/json");
        request.SetRequestHeader("Content-Length", bodyRaw.Length.ToString());

        UnityEngine.Debug.Log($"[SearchMemory] 검색 요청: keyword=\"{keyword}\"");

        yield return request.SendWebRequest();

        // ── 통신 실패 ──
        if (request.result != UnityWebRequest.Result.Success)
        {
            string errorResponseText = TryGetResponseText(request);
            searchResult.success = false;
            searchResult.errorMessage = $"서버 통신 실패: {request.error} ({request.responseCode}) {errorResponseText}";
            UnityEngine.Debug.LogError($"[SearchMemory] {searchResult.errorMessage}");
            request.Dispose();
            callback?.Invoke(searchResult);
            yield break;
        }

        // ── 응답 파싱 ──
        string responseText = TryGetResponseText(request);
        request.Dispose();

        UnityEngine.Debug.Log($"[SearchMemory] 응답: {responseText}");

        try
        {
            var parsed = JsonUtility.FromJson<SearchResponse>(responseText);

            searchResult.success = parsed.success;
            searchResult.found = parsed.found;
            searchResult.keyword = parsed.keyword;
            searchResult.reason = parsed.reason;
            searchResult.gptAnswer = parsed.gpt_answer ?? "";

            // matches[] 수동 파싱 (JsonUtility는 중첩 배열을 지원하지 않음)
            searchResult.matches = ParseMatchesFromJson(responseText);

            // ── 로그 출력 ──
            if (searchResult.found)
            {
                UnityEngine.Debug.Log(
                    $"[SearchMemory] 찾음 → {searchResult.reason}\n" +
                    $"GPT 정리: {searchResult.gptAnswer}\n" +
                    $"매치 건수: {searchResult.matches.Count}"
                );
                for (int i = 0; i < searchResult.matches.Count; i++)
                {
                    var m = searchResult.matches[i];
                    UnityEngine.Debug.Log($"  [{i + 1}] time={m.time} | summation={m.summation}");
                }
            }
            else
            {
                UnityEngine.Debug.Log($"[SearchMemory] 기억 없음 → {searchResult.reason}");
            }
        }
        catch (Exception e)
        {
            searchResult.success = false;
            searchResult.errorMessage = $"응답 파싱 실패: {e.Message}";
            UnityEngine.Debug.LogError($"[SearchMemory] {searchResult.errorMessage}\n원문: {responseText}");
        }

        callback?.Invoke(searchResult);
    }

    /// <summary>
    /// YouTube channelId 기준 시청자 대화 기록 검색.
    /// </summary>
    public Task<SearchResult> SearchViewerHistoryAsync(string channelId, int topK = 5)
    {
        var tcs = new TaskCompletionSource<SearchResult>();
        StartCoroutine(SearchViewerHistoryCoroutine(channelId, topK, result => tcs.SetResult(result)));
        return tcs.Task;
    }

    public IEnumerator SearchViewerHistoryCoroutine(string channelId, int topK, Action<SearchResult> callback)
    {
        channelId = (channelId ?? string.Empty).Trim();
        var searchResult = new SearchResult { keyword = channelId };

        if (!serverRunning)
        {
            searchResult.success = false;
            searchResult.errorMessage = "서버가 실행 중이 아닙니다.";
            callback?.Invoke(searchResult);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(channelId))
        {
            searchResult.success = true;
            searchResult.found = false;
            searchResult.reason = "channel_id가 비어 있습니다.";
            callback?.Invoke(searchResult);
            yield break;
        }

        string jsonBody = JsonUtility.ToJson(new ViewerSearchRequest { channel_id = channelId, top_k = topK });
        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

        var request = new UnityWebRequest($"{serverUrl}/search_viewer", "POST");
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.uploadHandler.contentType = "application/json";
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Accept", "application/json");
        request.SetRequestHeader("Content-Length", bodyRaw.Length.ToString());

        UnityEngine.Debug.Log($"[SearchViewer] channel_id=\"{channelId}\"");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            string errorResponseText = TryGetResponseText(request);
            searchResult.success = false;
            searchResult.errorMessage = $"서버 통신 실패: {request.error} ({request.responseCode}) {errorResponseText}";
            UnityEngine.Debug.LogError($"[SearchViewer] {searchResult.errorMessage}");
            request.Dispose();
            callback?.Invoke(searchResult);
            yield break;
        }

        string responseText = TryGetResponseText(request);
        request.Dispose();
        UnityEngine.Debug.Log($"[SearchViewer] 응답: {responseText}");

        try
        {
            var response = JsonUtility.FromJson<SearchResponse>(responseText);
            if (response == null)
            {
                searchResult.success = false;
                searchResult.errorMessage = "응답 파싱 실패";
            }
            else
            {
                searchResult.success = response.success;
                searchResult.found = response.found;
                searchResult.reason = response.reason;
                searchResult.gptAnswer = response.gpt_answer;
                searchResult.matches = ParseMatchesFromJson(responseText);
            }
        }
        catch (Exception e)
        {
            searchResult.success = false;
            searchResult.errorMessage = $"응답 파싱 실패: {e.Message}";
            UnityEngine.Debug.LogError($"[SearchViewer] {searchResult.errorMessage}\n원문: {responseText}");
        }

        callback?.Invoke(searchResult);
    }

    // ──────────────────────────────────────────────
    //  summation 일괄 생성
    // ──────────────────────────────────────────────

    public void SendCustomData(string dvInputValue, string ninResponseValue, string turnSource = "user")
    {
        SendCustomData(dvInputValue, ninResponseValue, turnSource, null, null);
    }

    public void SendCustomData(
        string dvInputValue,
        string ninResponseValue,
        string turnSource,
        string youtubeChannelId,
        string youtubeDisplayName)
    {
        string currentTime = System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SendDataToServer(currentTime, timestamp, dvInputValue, ninResponseValue, null, turnSource, youtubeChannelId, youtubeDisplayName);
    }

    public void SendCustomData(string dvInputValue, string ninResponseValue, float[] actionEmbedding, long timestamp, string turnSource = "user")
    {
        SendCustomData(dvInputValue, ninResponseValue, actionEmbedding, timestamp, turnSource, null, null);
    }

    public void SendCustomData(
        string dvInputValue,
        string ninResponseValue,
        float[] actionEmbedding,
        long timestamp,
        string turnSource,
        string youtubeChannelId,
        string youtubeDisplayName)
    {
        string currentTime = System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        long safeTimestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SendDataToServer(currentTime, safeTimestamp, dvInputValue, ninResponseValue, actionEmbedding, turnSource, youtubeChannelId, youtubeDisplayName);
    }

    /// <summary>
    /// 대화 턴 + 모션 학습 필드를 YYDate leaf에 함께 저장한다.
    /// </summary>
    public void SendMotionTurnData(
        string dvInputValue,
        string ninResponseValue,
        string bodyCommandId,
        string emotionFolder,
        float[] obsVector,
        float[] actVector,
        float[] poseBefore,
        float[] poseAfter,
        string savedPoseName,
        float learningWeight,
        float[] actionEmbedding = null,
        long timestamp = 0,
        float[] policyActVector = null,
        bool guidanceApplied = false,
        float[] guidanceJointMask = null,
        string guidanceReason = null,
        string referencePoseName = null,
        float datasetMatchScore = 0f,
        string intentCategory = null,
        string behaviorTag = null,
        string turnSource = null,
        string youtubeChannelId = null,
        string youtubeDisplayName = null)
    {
        string currentTime = System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        long safeTimestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        StartCoroutine(SendMotionTurnCoroutine(
            currentTime,
            safeTimestamp,
            dvInputValue,
            ninResponseValue,
            bodyCommandId,
            emotionFolder,
            obsVector,
            actVector,
            poseBefore,
            poseAfter,
            savedPoseName,
            learningWeight,
            actionEmbedding,
            policyActVector,
            guidanceApplied,
            guidanceJointMask,
            guidanceReason,
            referencePoseName,
            datasetMatchScore,
            intentCategory,
            behaviorTag,
            turnSource,
            youtubeChannelId,
            youtubeDisplayName));
    }

    private IEnumerator SendMotionTurnCoroutine(
        string time,
        long timestamp,
        string dvInput,
        string ninResponse,
        string bodyCommandId,
        string emotionFolder,
        float[] obsVector,
        float[] actVector,
        float[] poseBefore,
        float[] poseAfter,
        string savedPoseName,
        float learningWeight,
        float[] actionEmbedding,
        float[] policyActVector,
        bool guidanceApplied,
        float[] guidanceJointMask,
        string guidanceReason,
        string referencePoseName,
        float datasetMatchScore,
        string intentCategory,
        string behaviorTag,
        string turnSource,
        string youtubeChannelId,
        string youtubeDisplayName)
    {
        UnityWebRequest request = null;
        try
        {
            var payload = new DataPayload
            {
                time = time,
                timestamp = timestamp,
                dv_input = dvInput,
                nin_response = ninResponse,
                action_embedding = actionEmbedding ?? new float[0],
                body_command_id = bodyCommandId ?? string.Empty,
                emotion_folder = emotionFolder ?? string.Empty,
                obs_vector = obsVector ?? new float[0],
                act_vector = actVector ?? new float[0],
                pose_before = poseBefore ?? new float[0],
                pose_after = poseAfter ?? new float[0],
                saved_pose_name = savedPoseName ?? string.Empty,
                learning_weight = learningWeight,
                policy_act_vector = policyActVector ?? new float[0],
                guidance_applied = guidanceApplied,
                guidance_joint_mask = guidanceJointMask ?? new float[0],
                guidance_reason = guidanceReason ?? string.Empty,
                reference_pose_name = referencePoseName ?? string.Empty,
                dataset_match_score = datasetMatchScore,
                intent_category = intentCategory ?? string.Empty,
                behavior_tag = behaviorTag ?? string.Empty,
                turn_source = turnSource ?? string.Empty,
                youtube_channel_id = youtubeChannelId ?? string.Empty,
                youtube_display_name = youtubeDisplayName ?? string.Empty
            };
            string jsonData = JsonUtility.ToJson(payload);
            request = new UnityWebRequest($"{serverUrl}/data", "POST");
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonData);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.uploadHandler.contentType = "application/json";
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Content-Length", bodyRaw.Length.ToString());
            UnityEngine.Debug.Log($"[MotionTurn] POST {serverUrl}/data len={bodyRaw.Length} cmd={bodyCommandId}");
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogError($"[MotionTurn] payload error: {e.Message}");
        }

        if (request == null)
        {
            yield break;
        }

        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            UnityEngine.Debug.LogWarning($"[MotionTurn] failed: {request.error}");
        }

        request.Dispose();
    }

    public void FillAllSummations()
    {
        StartCoroutine(FillSummationsCoroutine());
    }

    private IEnumerator FillSummationsCoroutine()
    {
        if (!serverRunning)
        {
            UnityEngine.Debug.LogWarning("서버가 실행 중이 아닙니다.");
            yield break;
        }

        UnityWebRequest request = UnityWebRequest.Get($"{serverUrl}/fill_summations");
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
            UnityEngine.Debug.Log($"모든 summation 채우기 완료: {request.downloadHandler.text}");
        else
            UnityEngine.Debug.LogError($"summation 채우기 실패: {request.error}");

        request.Dispose();
    }

    // ──────────────────────────────────────────────
    //  유틸리티
    // ──────────────────────────────────────────────

    public bool IsServerRunning => serverRunning;

    private static string TryGetResponseText(UnityWebRequest req)
    {
        try { return req.downloadHandler?.text ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>
    /// JsonUtility는 List&#60;T&#62;를 직접 파싱하지 못하므로
    /// 간단한 문자열 파싱으로 matches 배열을 추출합니다.
    /// 의존성 없이 동작하며, Newtonsoft.Json이 있으면 교체를 권장합니다.
    /// </summary>
    private static List<MemoryEntry> ParseMatchesFromJson(string json)
    {
        var result = new List<MemoryEntry>();
        try
        {
            // "matches" 키 이후의 배열 구간 추출
            int matchesStart = json.IndexOf("\"matches\"", StringComparison.Ordinal);
            if (matchesStart < 0) return result;

            int arrayStart = json.IndexOf('[', matchesStart);
            if (arrayStart < 0) return result;

            // 중괄호 깊이로 각 오브젝트 구간 찾기
            int depth = 0;
            int objStart = -1;

            for (int i = arrayStart; i < json.Length; i++)
            {
                char c = json[i];
                if (c == ']' && depth == 0) break; // 배열 끝

                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        string objJson = json.Substring(objStart, i - objStart + 1);
                        var entry = JsonUtility.FromJson<MemoryEntry>(objJson);
                        if (entry != null) result.Add(entry);
                        objStart = -1;
                    }
                }
            }
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"matches 파싱 경고: {e.Message}");
        }
        return result;
    }
}