/*
 * [OpenCVSharp4 DLL 설치 안내]
 * OpenCVSharp4 DLL은 .\Fcam\Plugins\ 에 배치되어 있습니다.
 *
 * 필요 파일 (Plugins 폴더):
 *   - OpenCvSharp.dll
 *   - OpenCvSharpExtern.dll
 *   - opencv_videoio_ffmpeg4130_64.dll
 *   - System.Runtime.CompilerServices.Unsafe.dll
 *   - System.Memory.dll
 *   - System.Buffers.dll
 *   - System.Numerics.Vectors.dll
 *
 * 다운로드:
 *   https://github.com/shimat/opencvsharp/releases
 *   NuGet 패키지 OpenCvSharp4 + OpenCvSharp4.runtime.win.x64 에서 추출
 *
 * Unity Inspector 설정:
 *   OpenCvSharpExtern.dll → Platform Settings → Windows x64 체크
 *
 * [핸드폰 설정]
 *   Play 스토어 "IP Webcam" 앱 설치 → 서버 시작
 *   PC와 같은 WiFi 연결 후 표시된 IP를 phoneIP 필드에 입력
 *   스트림 URL 예: http://192.168.0.10:8080/video
 */

using System;
using System.Collections.Concurrent;
using System.Threading;
using OpenCvSharp;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// WiFi MJPEG 스트림(IP Webcam)을 OpenCVSharp로 수신하여 Unity RawImage에 표시합니다.
/// </summary>
public class PhoneCameraStream : MonoBehaviour
{
    [Header("핸드폰 연결 설정")]
    [Tooltip("IP Webcam 앱에 표시된 핸드폰 IP 주소")]
    public string phoneIP = "192.168.0.10";

    [Tooltip("IP Webcam 기본 포트")]
    public int port = 8080;

    [Tooltip("MJPEG 경로 (IP Webcam 기본: videofeed)")]
    public string streamPath = "videofeed";

    [Header("표시 대상")]
    [Tooltip("카메라 영상을 출력할 UI RawImage (없어도 Vision 캡처는 동작)")]
    public RawImage targetRawImage;

    [Header("성능 설정")]
    [Tooltip("JPEG 인코딩 품질 (1~100, 낮을수록 CPU/대역폭 절약)")]
    [Range(1, 100)]
    public int jpegQuality = 75;

    [Tooltip("큐에 쌓을 수 있는 최대 프레임 수 (초과 시 오래된 프레임 폐기)")]
    public int maxQueueSize = 2;

    [Tooltip("연결 실패 시 재시도 간격 (초)")]
    public float reconnectIntervalSec = 3f;

    [Header("Vision 영상 버퍼")]
    [Tooltip("AI에 보낼 연속 프레임 수 (영상 클립)")]
    [Range(2, 8)]
    public int visionFrameCount = 4;

    [Tooltip("버퍼에 프레임을 쌓는 간격 (초)")]
    public float visionSampleIntervalSec = 0.4f;

    // 백그라운드 캡처 스레드
    private Thread captureThread;
    private volatile bool isRunning;

    // 메인 스레드로 전달할 JPEG 프레임 큐
    private ConcurrentQueue<byte[]> frameQueue;

    // Unity 텍스처 (재사용하여 GC 부담 감소)
    private Texture2D displayTexture;

    // OpenCV 캡처 객체 (스레드 전용)
    private VideoCapture videoCapture;

    // Vision API용 최신 JPEG (메인 스레드에서 갱신)
    private volatile byte[] latestJpegBytes;
    private volatile bool streamConnected;

    // Vision용 연속 프레임 링 버퍼 (시간순 영상 클립)
    private byte[][] visionFrameRing;
    private int visionRingWriteIndex;
    private int visionRingCount;
    private float lastVisionSampleTime;

    /// <summary>스트림 연결 및 프레임 수신 여부</summary>
    public bool IsStreamConnected => streamConnected && latestJpegBytes != null && latestJpegBytes.Length > 0;

    /// <summary>디버그용 상태 문자열</summary>
    public string StatusMessage { get; private set; } = "대기 중";

    /// <summary>
    /// IP Webcam MJPEG 스트림 URL 생성
    /// </summary>
    private string StreamUrl
    {
        get
        {
            string path = string.IsNullOrWhiteSpace(streamPath) ? "videofeed" : streamPath.Trim().TrimStart('/');
            return $"http://{phoneIP}:{port}/{path}";
        }
    }

    /// <summary>연결 실패 시 videofeed / video 순서로 URL 후보 생성</summary>
    private static string[] BuildStreamUrlCandidates(string phoneIP, int port, string streamPath)
    {
        var list = new System.Collections.Generic.List<string>();
        string primary = string.IsNullOrWhiteSpace(streamPath) ? "videofeed" : streamPath.Trim().TrimStart('/');
        list.Add($"http://{phoneIP}:{port}/{primary}");
        if (!primary.Equals("videofeed", System.StringComparison.OrdinalIgnoreCase))
            list.Add($"http://{phoneIP}:{port}/videofeed");
        if (!primary.Equals("video", System.StringComparison.OrdinalIgnoreCase))
            list.Add($"http://{phoneIP}:{port}/video");
        return list.ToArray();
    }

    private void Start()
    {
        frameQueue = new ConcurrentQueue<byte[]>();
        displayTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        int ringSize = Mathf.Clamp(visionFrameCount, 2, 8);
        visionFrameRing = new byte[ringSize][];

        if (targetRawImage != null)
        {
            targetRawImage.texture = displayTexture;
            // 알파 0이면 화면에 안 보일 수 있음
            if (targetRawImage.color.a < 0.01f)
                targetRawImage.color = Color.white;
        }
        else
        {
            Debug.LogWarning("[PhoneCameraStream] targetRawImage 미연결 — Vision만 사용, 화면 표시 없음.");
        }

        isRunning = true;
        captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "PhoneCameraCapture"
        };
        captureThread.Start();
    }

    /// <summary>
    /// 백그라운드 스레드: VideoCapture로 프레임을 읽어 JPEG 큐에 적재 (실패 시 URL 재시도)
    /// </summary>
    private void CaptureLoop()
    {
        int reconnectMs = Math.Max(1000, (int)(reconnectIntervalSec * 1000f));

        while (isRunning)
        {
            string[] urls = BuildStreamUrlCandidates(phoneIP, port, streamPath);
            bool opened = false;

            foreach (string url in urls)
            {
                if (!isRunning)
                    return;

                ReleaseCapture();
                videoCapture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);

                if (videoCapture.IsOpened())
                {
                    opened = true;
                    streamConnected = true;
                    StatusMessage = "연결됨: " + url;
                    Debug.Log("[PhoneCameraStream] 스트림 연결 성공: " + url);
                    ReadFramesUntilDisconnect(url);
                    break;
                }

                Debug.LogWarning("[PhoneCameraStream] 연결 실패: " + url);
            }

            if (!opened)
            {
                streamConnected = false;
                StatusMessage = "연결 실패 — IP/Webcam 서버 확인 (" + phoneIP + ":" + port + ")";
                Debug.LogError("[PhoneCameraStream] 모든 URL 연결 실패. IP Webcam '서버 시작' 확인.");
            }

            if (isRunning)
                Thread.Sleep(reconnectMs);
        }
    }

    private void ReadFramesUntilDisconnect(string activeUrl)
    {
        var encodeParams = new int[]
        {
            (int)ImwriteFlags.JpegQuality,
            jpegQuality
        };

        int failCount = 0;
        using (var frame = new Mat())
        {
            while (isRunning && videoCapture != null && videoCapture.IsOpened())
            {
                if (!videoCapture.Read(frame) || frame.Empty())
                {
                    failCount++;
                    if (failCount > 100)
                    {
                        Debug.LogWarning("[PhoneCameraStream] 프레임 수신 중단: " + activeUrl);
                        streamConnected = false;
                        StatusMessage = "프레임 수신 중단 — 재연결 중";
                        break;
                    }
                    Thread.Sleep(10);
                    continue;
                }

                failCount = 0;
                byte[] jpegBytes = frame.ToBytes(".jpg", encodeParams);
                if (jpegBytes == null || jpegBytes.Length == 0)
                    continue;

                while (frameQueue.Count >= maxQueueSize && frameQueue.TryDequeue(out _))
                {
                }

                frameQueue.Enqueue(jpegBytes);
            }
        }
    }

    private void ReleaseCapture()
    {
        if (videoCapture == null)
            return;
        videoCapture.Release();
        videoCapture.Dispose();
        videoCapture = null;
    }

    /// <summary>
    /// 메인 스레드: 큐에서 프레임을 꺼내 Texture2D → RawImage 갱신
    /// </summary>
    private void Update()
    {
        if (frameQueue == null || displayTexture == null)
            return;

        byte[] latestFrame = null;
        while (frameQueue.TryDequeue(out byte[] frameBytes))
        {
            latestFrame = frameBytes;
        }

        if (latestFrame == null)
            return;

        if (displayTexture.LoadImage(latestFrame))
        {
            latestJpegBytes = latestFrame;
            StatusMessage = "수신 중 (" + displayTexture.width + "x" + displayTexture.height + ")";

            if (targetRawImage != null)
            {
                targetRawImage.texture = displayTexture;
                targetRawImage.enabled = true;
            }

            PushVisionFrameToRing();
        }
    }

    /// <summary>
    /// 일정 간격으로 Vision 링 버퍼에 프레임 적재 (연속 영상 클립)
    /// </summary>
    private void PushVisionFrameToRing()
    {
        if (visionFrameRing == null || visionFrameRing.Length == 0)
            return;

        if (Time.time - lastVisionSampleTime < visionSampleIntervalSec)
            return;

        if (!TryEncodeDisplayTexture(out byte[] jpeg, 512, 60))
            return;

        lastVisionSampleTime = Time.time;
        visionFrameRing[visionRingWriteIndex] = jpeg;
        visionRingWriteIndex = (visionRingWriteIndex + 1) % visionFrameRing.Length;
        if (visionRingCount < visionFrameRing.Length)
            visionRingCount++;
    }

    /// <summary>
    /// Vision API용 연속 프레임 배열 (시간순). GPT는 MP4 대신 다중 프레임으로 영상을 이해한다.
    /// </summary>
    public bool TryGetVisionFrameBatch(out byte[][] frames, int maxEdge = 512, int quality = 60)
    {
        frames = null;
        if (!IsStreamConnected || displayTexture == null || displayTexture.width <= 2)
            return false;

        if (visionRingCount > 0)
        {
            int count = visionRingCount;
            frames = new byte[count][];
            int startIndex = visionRingCount < visionFrameRing.Length
                ? 0
                : visionRingWriteIndex;

            for (int i = 0; i < count; i++)
            {
                int idx = (startIndex + i) % visionFrameRing.Length;
                frames[i] = visionFrameRing[idx];
            }

            return frames[0] != null && frames[0].Length > 0;
        }

        if (TryGetLatestJpeg(out byte[] single, maxEdge, quality))
        {
            frames = new byte[][] { single };
            return true;
        }

        return false;
    }

    private bool TryEncodeDisplayTexture(out byte[] jpeg, int maxEdge, int quality)
    {
        jpeg = null;
        if (displayTexture == null || displayTexture.width <= 2)
            return false;

        int w = displayTexture.width;
        int h = displayTexture.height;
        float scale = maxEdge / (float)Mathf.Max(w, h);

        if (scale >= 1f)
        {
            jpeg = displayTexture.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
            return jpeg != null && jpeg.Length > 0;
        }

        int nw = Mathf.Max(1, Mathf.RoundToInt(w * scale));
        int nh = Mathf.Max(1, Mathf.RoundToInt(h * scale));

        RenderTexture rt = RenderTexture.GetTemporary(nw, nh);
        Graphics.Blit(displayTexture, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D scaled = new Texture2D(nw, nh, TextureFormat.RGB24, false);
        scaled.ReadPixels(new UnityEngine.Rect(0, 0, nw, nh), 0, 0);
        scaled.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        jpeg = scaled.EncodeToJPG(Mathf.Clamp(quality, 1, 100));
        Destroy(scaled);
        return jpeg != null && jpeg.Length > 0;
    }

    /// <summary>
    /// Vision API 전송용 JPEG 스냅샷 (단일 프레임, 하위 호환)
    /// </summary>
    public bool TryGetLatestJpeg(out byte[] jpeg, int maxEdge = 512, int quality = 60)
    {
        jpeg = null;
        if (!IsStreamConnected || displayTexture == null || displayTexture.width <= 2)
            return false;

        return TryEncodeDisplayTexture(out jpeg, maxEdge, quality);
    }

    /// <summary>
    /// 종료 시 스레드 및 OpenCV 리소스 안전 해제
    /// </summary>
    private void OnDestroy()
    {
        isRunning = false;

        if (captureThread != null && captureThread.IsAlive)
        {
            captureThread.Join(1000);
        }

        ReleaseCapture();

        if (displayTexture != null)
        {
            Destroy(displayTexture);
            displayTexture = null;
        }

        frameQueue = null;
        visionFrameRing = null;
    }

    private void OnApplicationQuit()
    {
        isRunning = false;
    }
}
