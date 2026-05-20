using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace VerbalProcess
{
    [RequireComponent(typeof(AudioSource))]
    public class Speaker : MonoBehaviour
    {
        [Header("Audio Settings")]
        [SerializeField] private int serverSampleRate = 44100;
        [SerializeField] private float volume = 1.0f;
        [SerializeField] private int bufferThresholdChunks = 3; // 큐에 쌓일 청크 개수 기준

        public Action OnPlaybackFinished; // 모든 버퍼 재생이 완료되었을 때 발생

        private AudioSource _audioSource;

        // float 하나가 아니라 배열(청크) 단위로 관리하여 성능 극대화
        private ConcurrentQueue<float[]> _audioChunkQueue = new ConcurrentQueue<float[]>();

        // 현재 읽고 있는 청크의 인덱스
        private float[] _currentChunk = null;
        private int _chunkIndex = 0;

        private int _outputSampleRate;
        private float _lastSample = 0;
        private float _currentSample = 0;
        private float _t = 0;
        private bool _hasCurrentSample = false;

        private bool _isEndOfStream = false;
        private bool _playbackFinishedEventFired = true; // 시작 시에는 완료된 상태로 간주

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = true; // 더미 클립 무한 반복

            _outputSampleRate = AudioSettings.outputSampleRate;

            // OnAudioFilterRead를 작동시키기 위한 무음 더미 클립 생성
            _audioSource.clip = AudioClip.Create("DummyStream", _outputSampleRate, 1, _outputSampleRate, false);
            _audioSource.Play(); // 스트림 대기 상태로 항시 켜둠
        }

        private void Update()
        {
            // 메인 스레드에서 재생 종료 이벤트 처리
            if (_isEndOfStream && !_playbackFinishedEventFired && _audioChunkQueue.IsEmpty && _currentChunk == null)
            {
                _playbackFinishedEventFired = true;
                _isEndOfStream = false;
                OnPlaybackFinished?.Invoke();
                Debug.Log("[Speaker] All audio playback finished. VAD can be re-enabled.");
            }
        }

        public void HandleAudioChunkReceived(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length < 4) return;

            // 최적화: 개별 형변환 대신 BlockCopy로 메모리 통째로 복사 (초고속)
            int sampleCount = pcmData.Length / 4;
            float[] floatArray = new float[sampleCount];
            Buffer.BlockCopy(pcmData, 0, floatArray, 0, pcmData.Length);

            _audioChunkQueue.Enqueue(floatArray);
            _playbackFinishedEventFired = false;
            _isEndOfStream = false;

            // 초기 버퍼링 확인 (로그 출력용)
            if (_audioChunkQueue.Count == bufferThresholdChunks)
            {
                Debug.Log($"[Speaker] Buffer threshold reached. Audio streaming stabilized.");
            }
        }

        /// <summary>
        /// 서버로부터 더 이상 오디오 청크가 오지 않음을 설정합니다.
        /// </summary>
        public void SetEndOfStream()
        {
            _isEndOfStream = true;
            _playbackFinishedEventFired = false; // 오디오가 아예 없었더라도 재생 완료 이벤트를 발생시키기 위해 false로 설정
            Debug.Log("[Speaker] End of stream signaled from server.");
        }

        public void StopAndClear()
        {
            // 큐 비우기
            while (_audioChunkQueue.TryDequeue(out _)) { }

            _currentChunk = null;
            _chunkIndex = 0;
            _lastSample = 0;
            _currentSample = 0;
            _t = 0;
            _hasCurrentSample = false;
            _isEndOfStream = false;
            _playbackFinishedEventFired = true;
            Debug.Log("[Speaker] Audio buffer cleared.");
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_outputSampleRate == 0) return;

            float resampleRatio = (float)serverSampleRate / _outputSampleRate;

            for (int i = 0; i < data.Length; i += channels)
            {
                while (_t >= 1.0f || !_hasCurrentSample)
                {
                    // 청크 단위 데이터 수급 로직
                    if (_currentChunk == null || _chunkIndex >= _currentChunk.Length)
                    {
                        if (_audioChunkQueue.TryDequeue(out _currentChunk))
                        {
                            _chunkIndex = 0;
                        }
                        else
                        {
                            // 버퍼 언더런 (데이터 부족)
                            _currentChunk = null; // 현재 청크 완료 표시
                            _hasCurrentSample = false;
                            break;
                        }
                    }

                    float nextSample = _currentChunk[_chunkIndex++];

                    if (!_hasCurrentSample)
                    {
                        _currentSample = nextSample;
                        _lastSample = nextSample;
                        _hasCurrentSample = true;
                        _t = 0;
                    }
                    else
                    {
                        _lastSample = _currentSample;
                        _currentSample = nextSample;
                        _t -= 1.0f;
                        if (_t < 0) _t = 0;
                    }
                }

                // 부드러운 보간 계산
                float interpolatedSample = 0f;
                if (_hasCurrentSample)
                {
                    interpolatedSample = Mathf.Lerp(_lastSample, _currentSample, _t);
                    _t += resampleRatio;
                }
                else
                {
                    // 팝핑 노이즈 방지: 파형이 0으로 확 떨어지지 않고 아주 빠르게 감쇠(Fade-out)
                    _lastSample = Mathf.Lerp(_lastSample, 0, 0.1f);
                    interpolatedSample = _lastSample;
                }

                // 채널 복사 (모노 스트림을 스테레오 스피커로 출력 시 양쪽 복사)
                for (int c = 0; c < channels; c++)
                {
                    data[i + c] = interpolatedSample * volume;
                }
            }
        }
    }
}