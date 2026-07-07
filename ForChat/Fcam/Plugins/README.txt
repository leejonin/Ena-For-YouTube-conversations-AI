OpenCVSharp4 런타임 DLL (GitHub 업로드 제외 — 25MB 초과)

이 폴더의 DLL은 저장소에 포함되지 않습니다. Unity 사용 전 아래 방법으로 설치하세요.

필요 파일:
  - OpenCvSharp.dll
  - OpenCvSharpExtern.dll          (~65MB)
  - opencv_videoio_ffmpeg4130_64.dll (~27MB)
  - System.Runtime.CompilerServices.Unsafe.dll
  - System.Memory.dll
  - System.Buffers.dll
  - System.Numerics.Vectors.dll

설치 방법 (Windows x64):
  1. https://github.com/shimat/opencvsharp/releases
  2. NuGet: OpenCvSharp4 + OpenCvSharp4.runtime.win.x64 패키지에서 추출
  3. 위 DLL을 이 폴더(Fcam/Plugins/)에 복사

Unity:
  OpenCvSharpExtern.dll → Inspector → Platform Settings → Windows x64 체크

상세: Fcam/PhoneCameraStream.cs 상단 주석
