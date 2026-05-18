using System;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// VAD, STT, Feature 추출의 전체 파이프라인 흐름을 제어하는 컨트롤러 클래스
    /// </summary>
    public class PipelineController : MonoBehaviour
    {
        [SerializeField] private VoiceActivityDetector vad;
        [SerializeField] private STTManager sttManager;
        [SerializeField] private Speaker speaker;

        private void OnEnable()
        {
            if (vad != null)
            {
                vad.OnUtteranceEnded += HandleUtteranceEnded;
                vad.OnAudioChunkCaptured += HandleOnAudioChunkCaptured;
                vad.OnSpeakingStarted += HandleSpeakingStarted;
            }
            else
            {
                Debug.LogWarning("PipelineController: VoiceActivityDetector is not assigned!");
            }

            if (sttManager != null)
            {
                sttManager.OnServerRequestEnd += HandleServerRequestEnd;
                sttManager.OnTranscriptionReceived += HandleTranscriptionReceived;
                sttManager.OnAudioStreamEnded += HandleAudioStreamEnded;
                
                if (speaker != null)
                {
                    sttManager.OnAudioChunkReceived += speaker.HandleAudioChunkReceived;
                    speaker.OnPlaybackFinished += HandlePlaybackFinished;
                }
            }
        }

        private void OnDisable()
        {
            if (vad != null)
            {
                vad.OnUtteranceEnded -= HandleUtteranceEnded;
                vad.OnAudioChunkCaptured -= HandleOnAudioChunkCaptured;
                vad.OnSpeakingStarted -= HandleSpeakingStarted;
            }

            if (sttManager != null)
            {
                sttManager.OnServerRequestEnd -= HandleServerRequestEnd;
                sttManager.OnTranscriptionReceived -= HandleTranscriptionReceived;
                sttManager.OnAudioStreamEnded -= HandleAudioStreamEnded;

                if (speaker != null)
                {
                    sttManager.OnAudioChunkReceived -= speaker.HandleAudioChunkReceived;
                    speaker.OnPlaybackFinished -= HandlePlaybackFinished;
                }
            }
        }

        private void HandleSpeakingStarted()
        {
            if (sttManager != null)
            {
                // 새 발화가 시작될 때 STT 매니저의 상태(헤더 전송 여부)를 초기화
                sttManager.ResetUtteranceState();
            }

            if (speaker != null)
            {
                // 사용자가 다시 말을 시작하면 기존 출력 중이던 오디오 중단 (Barge-in)
                speaker.StopAndClear();
            }
        }

        private void HandleServerRequestEnd()
        {
            if (vad != null)
            {
                vad.ForceEnd();
            }
        }

        private void HandleTranscriptionReceived(STTManager.FinalResponse response)
        {
            Debug.Log($"<color=cyan>[Pipeline] Final STT Result: {response.data.sttText}</color>");
            Debug.Log($"[Pipeline] Stats - Time: {response.data.speakingTime:F2}s, Pauses: {response.data.pauseCount}, Vol: {response.data.averageVolume:F4}");
            
            // STT 결과는 UI 업데이트 등에 활용하며, VAD 재활성화는 오디오 재생 종료 시(HandlePlaybackFinished) 수행합니다.
        }

        private void HandleAudioStreamEnded()
        {
            if (speaker != null)
            {
                // 서버에서 더 이상 오디오가 오지 않음을 Speaker에 알림
                speaker.SetEndOfStream();
            }
        }

        private void HandlePlaybackFinished()
        {
            // AI의 모든 답변 재생이 끝났을 때 VAD를 다시 활성화하여 다음 입력을 대기합니다.
            if (vad != null)
            {
                vad.enabled = true;
                Debug.Log("[Pipeline] Speaker finished. VAD re-enabled.");
            }
        }

        private async void HandleOnAudioChunkCaptured(AudioClip Clip)
        {
            if (sttManager == null) return;
            try
            {
                // WebSocket을 통해 실시간 오디오 데이터 전송
                await sttManager.SendAudioChunkAsync(Clip);
            }
            catch (Exception e)
            {
                Debug.LogError($"Pipeline Error (Chunk): {e.Message}");
            }
        }

        private async void HandleUtteranceEnded(VoiceActivityDetector.VoiceFeatures features)
        {
            if (sttManager == null) return;

            try
            {
                Debug.Log("Pipeline: Utterance ended. Sending Feature via WebSocket...");
                
                // 데이터 패키징
                FeatureData featureData = new FeatureData(features);

                // WebSocket을 통해 발화 종료 알림 및 feature 데이터 전송
                await sttManager.SendEndUtteranceAsync(featureData);
            }
            catch (Exception e)
            {
                Debug.LogError($"Pipeline Error (End): {e.Message}");
            }
        }
    }
}
