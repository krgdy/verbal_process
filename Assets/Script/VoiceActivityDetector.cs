using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// 마이크 입력을 캡처하고 RMS 기반으로 발화 상태(Speaking/Silence)를 감지하며 특징점을 추출합니다.
    /// 침묵이 감지될 때마다(Pause) 부분적인 음성 청크를 이벤트로 발생시킵니다.
    /// </summary>
    public class VoiceActivityDetector : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private float threshold = 0.02f;
        [SerializeField] private float silenceDurationThreshold = 1.5f;
        [SerializeField] private float chunkSendInterval = 0.3f; // 0.3초마다 청크 전송
        [SerializeField] private int sampleRate = 16000; //whisper는 16khz를 사용함
        [SerializeField] private int bufferLengthSeconds = 300;
        //최대 샘플 수는 sampleRate * bufferLengthSeconds
        [Header("Events")]
        public Action<VoiceFeatures> OnUtteranceEnded; //응답 종료 플래그와 피쳐 값 전달용 이벤트
        public Action<AudioClip> OnAudioChunkCaptured; // 부분 음성 전달용 이벤트
        public Action OnSpeakingStarted;

        private AudioClip micClip;
        private string micDevice;
        private bool isSpeaking = false;
        private float silenceTimer = 0f;
        private float chunkTimer = 0f;

        private int lastChunkEndSample = 0; // 마지막으로 보낸 청크의 끝 지점
        private float utteranceStartTime;
        private int silenceCount = 0;
        private List<float> rmsSamples = new List<float>();

        private int lastSamplePosition = 0;
        private float[] reusableSampleBuffer; // GC 최적화를 위한 재사용 버퍼

        public struct VoiceFeatures
        {
            public float speakingTime;
            public int silenceCount;
            public float averageVolume;
        }

        void Start()
        {
            InitializeMicrophone();
            // 최대 발생 가능한 샘플 수만큼 버퍼 미리 할당 (예: 0.1초 분량이면 충분)
            reusableSampleBuffer = new float[sampleRate / 10];
        }

        private void InitializeMicrophone()
        {
            if (Microphone.devices.Length > 0)
            {
                micDevice = Microphone.devices[0];
                micClip = Microphone.Start(micDevice, true, bufferLengthSeconds, sampleRate);

                if (micClip == null)
                {
                    Debug.LogError("Failed to initialize Microphone Clip!");
                    return;
                }
                Debug.Log($"Microphone started: {micDevice}");
            }
            else
            {
                Debug.LogError("No microphone detected!");
            }
        }

        void Update()
        {
            if (micClip == null || !Microphone.IsRecording(micDevice)) return;

            int currentPosition = Microphone.GetPosition(micDevice);
            if (currentPosition < 0 || currentPosition == lastSamplePosition) return;

            ProcessMicSamples(currentPosition);
            lastSamplePosition = currentPosition;
        }

        private void ProcessMicSamples(int currentPosition)
        {
            int sampleCount = (currentPosition - lastSamplePosition + micClip.samples) % micClip.samples;
            if (sampleCount <= 0) return;

            // 실제 오디오 샘플 개수를 기반으로 한 정확한 시간(초) 계산
            float audioDuration = (float)sampleCount / sampleRate;

            // 버퍼 크기 부족 시 재할당 (드문 경우)
            if (reusableSampleBuffer.Length < sampleCount)
            {
                reusableSampleBuffer = new float[sampleCount];
            }

            micClip.GetData(reusableSampleBuffer, lastSamplePosition);

            float sum = 0f;
            for (int i = 0; i < sampleCount; i++)
            {
                sum += reusableSampleBuffer[i] * reusableSampleBuffer[i];
            }
            float rms = Mathf.Sqrt(sum / sampleCount);

            ProcessVAD(rms, audioDuration, currentPosition);
        }

        private void ProcessVAD(float rms, float duration, int currentPosition)
        {
            if (rms > threshold)
            {
                if (!isSpeaking) {
                    StartSpeaking(currentPosition);
                }
                else
                {
                    // 말하는 도중 주기적으로 청크 전송
                    chunkTimer += duration;
                    if (chunkTimer >= chunkSendInterval)
                    {
                        SendChunk(currentPosition);
                        lastChunkEndSample = currentPosition;
                        chunkTimer = 0f;
                    }
                }
                silenceTimer = 0f;
                rmsSamples.Add(rms);
            }
            else if (isSpeaking)
            {
                // 침묵이 시작되는 첫 프레임에서도 청크 전송 (남은 부분)
                if (silenceTimer == 0f)
                {
                    silenceCount++;
                    SendChunk(currentPosition);
                    lastChunkEndSample = currentPosition;
                    chunkTimer = 0f;
                }

                silenceTimer += duration;
                if (silenceTimer >= silenceDurationThreshold)
                {
                    EndSpeaking(currentPosition);
                }
            }
        }

        private void StartSpeaking(int currentPosition)
        {
            isSpeaking = true;
            utteranceStartTime = Time.time;
            lastChunkEndSample = currentPosition; // 청크 시작 지점 초기화
            silenceCount = 0;
            rmsSamples.Clear();
            OnSpeakingStarted?.Invoke();
            Debug.Log("Speaking Started");
        }

        private void SendChunk(int currentPosition)
        {
            AudioClip trimmedClip = AudioUtils.TrimAudio(micClip, lastChunkEndSample, currentPosition);
            OnAudioChunkCaptured?.Invoke(trimmedClip);
        }

        /// <summary>
        /// 서버의 요청 등에 의해 발화를 강제로 종료합니다.
        /// </summary>
        public void ForceEnd()
        {
            if (isSpeaking)
            {
                Debug.Log("[VAD] Forced End triggered by external signal.");
                EndSpeaking(Microphone.GetPosition(micDevice));
                // 타이머 리셋하여 중복 종료 방지
                silenceTimer = 0f;
            }
        }

        private void OnEnable()
        {
            // 다시 활성화될 때, 비활성 기간 동안의 오디오를 무시하기 위해 포인터를 현재 위치로 동기화
            if (micClip != null && Microphone.IsRecording(micDevice))
            {
                lastSamplePosition = Microphone.GetPosition(micDevice);
                lastChunkEndSample = lastSamplePosition;
                silenceTimer = 0f;
                chunkTimer = 0f;
                isSpeaking = false;
                Debug.Log("[VAD] Re-enabled. Syncing sample position.");
            }
        }

        private void EndSpeaking(int currentPosition)
        {
            isSpeaking = false;
            
            // 침묵 임계값만큼 이전이 실제 발화가 종료된 시점
            int silenceSamples = (int)(silenceDurationThreshold * sampleRate);
            int utteranceEndSample = (currentPosition - silenceSamples + micClip.samples) % micClip.samples;

            // AudioUtils를 사용하여 실제 발화 구간만 추출
            AudioClip trimmedClip = AudioUtils.TrimAudio(micClip, lastChunkEndSample, utteranceEndSample);

            float duration = Time.time - utteranceStartTime - silenceDurationThreshold;
            float avgRms = rmsSamples.Count > 0 ? rmsSamples.Average() : 0f;

            VoiceFeatures features = new VoiceFeatures
            {
                speakingTime = Mathf.Max(0, duration),
                silenceCount = silenceCount,
                averageVolume = avgRms
            };

            OnUtteranceEnded?.Invoke(features);
            Debug.Log($"Speaking Ended. Trimmed Clip Length: {trimmedClip?.length}s, Avg Volume: {features.averageVolume}");

            // 발화가 끝나면 다음 입력을 막기 위해 스스로를 비활성화 (Barge-in 미사용 시)
            this.enabled = false;
        }
    }
}
