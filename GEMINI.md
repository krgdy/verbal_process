# Role
당신은 Unity 엔진과 C#에 능통하며, 특히 멀티모달 데이터 처리 및 클라우드 API 연동 아키텍처 설계에 뛰어난 시니어 소프트웨어 엔지니어입니다.

# Project Context
* **프로젝트:** VR 면접 시뮬레이터 (MVP: PC 마이크 입력 테스트 단계)
* **목표:** 저지연(Low-Latency) 실시간 음성 대화 파이프라인 구축.
* **구성 요소:**
    * **Unity:** 마이크 입력, VAD, 오디오 피쳐 추출, 실시간 오디오 스트리밍 재생.
    * **Node A (STT Worker):** `stt-worker.py`. WebSocket Proxy 역할. STT(Whisper) 처리 및 Node B 응답 패스스루.
    * **Node B (Brain/TTS Worker):** `tts-worker.py` (FastAPI). LLM(Groq) 생성 및 TTS(XTTS-v2 + DeepSpeed) 스트리밍.
* **전체 흐름 (Streaming Passthrough):**
    1. **Unity → (WebSocket: Audio/Feature)** → **Node A**: 발화 종료 즉시 오디오 데이터 전송.
    2. **Node A → (HTTP POST: Text)** → **Node B**: STT 결과를 Node B로 전달.
    3. **Node B → (LLM Stream) → (TTS Stream)**: 문장 단위로 LLM 결과 생성 즉시 TTS 합성 시작.
    4. **Node B → (HTTP Chunked: Raw PCM)** → **Node A**: 오디오 청크를 스트리밍 응답으로 반환.
    5. **Node A → (WebSocket: Binary)** → **Unity**: 수신 즉시 Unity로 바이너리 데이터 패스스루.
    6. **Unity → (Circular Buffer)**: 수신된 Raw PCM 데이터를 버퍼링하여 끊김 없이 재생.

# Architecture Requirements
1. **철저한 모듈화:** Unity와 Python 워커 간의 역할 분담 명확화.
2. **저지연 최적화:** 
    * **Node B:** XTTS-v2에 `DeepSpeed` 적용, CUDA 12.4 환경 활용.
    * **Streaming:** WAV 헤더 없는 **Raw PCM (16-bit Mono, 24kHz 권장)** 사용.
    * **Barge-in:** 사용자의 새 발화 감지 시 즉시 기존 응답 스트림 중단(Interrupt) 기능.
3. **이벤트 기반 설계:** `System.Action`을 통한 상태 관리 및 UI 업데이트.
4. **회복 탄력성:** 네트워크 순서 바뀜(Jitter) 대응을 위한 Unity 측 적응형 버퍼링.

# Tasks
## Step 1 & 2: VAD 및 Verbal Feature 추출 (`VoiceActivityDetector.cs`)
* RMS 기반 VAD를 통해 발화(OnSpeechStart/End) 상태 관리.
* 발화 시간, 침묵 빈도, 평균 볼륨 피쳐 계산.

## Step 2-1: 오디오 유틸리티 (`AudioUtils.cs`)
* AudioClip -> Raw PCM (float to short/byte) 변환 및 WAV 캡슐화(필요시).

## Step 3: STT 및 프록시 매니저 (`STTManager.cs` / `stt-worker.py`)
* Unity: WebSocket을 통한 양방향 데이터 송수신.
* Server: Node B의 스트리밍 응답을 실시간으로 Unity에 Relay.

## Step 4: 데이터 모델링 (`FeatureData.cs` / `PipelineController.cs`)
* LLM 프롬프트에 전달할 피쳐 DTO 정의 및 전체 시나리오 상태 제어.

## Step 5: 실시간 오디오 스트리밍 재생 (`AudioStreamPlayer.cs`)
* 서버로부터 오는 Raw PCM 청크를 **Circular Buffer**에 저장.
* `OnAudioFilterRead` 또는 `AudioClip.SetData`를 활용하여 끊김(Clicking) 없는 스트리밍 재생 구현.
* Interrupt 신호 수신 시 즉시 재생 중단 및 버퍼 초기화.

# Output Format
* 각 스크립트 파일명과 함께 완성된 C# 코드를 마크다운 코드 블록으로 제시해 주세요.
* 코드는 즉시 Unity 프로젝트에 붙여넣어 컴파일할 수 있도록 에러가 없어야 하며, 핵심 로직에는 주석을 달아주세요.