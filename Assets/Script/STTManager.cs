using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VerbalProcess
{
    /// <summary>
    /// WebSocket을 통해 실시간 STT 및 Feature 데이터를 전송하는 매니저
    /// </summary>
    public class STTManager : MonoBehaviour
    {
        [SerializeField] private string wsUrl = "ws://127.0.0.1:8000/ws/interview";

        public Action OnServerRequestEnd; // 서버에서 발화 종료를 감지했을 때 발생
        public Action<FinalResponse> OnTranscriptionReceived; // 최종 결과 수신
        public Action<byte[]> OnAudioChunkReceived; // 서버로부터 오디오 청크(Raw PCM) 수신 시 발생
        public Action OnAudioStreamEnded; // 서버에서 모든 오디오 스트리밍이 완료되었을 때 발생

        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private bool _isFirstChunk = true;

        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1); // 웹소켓 연결에 따른 세마포어
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1); // 데이터 전송에 따른 세마포어

        private async void Start()
        {
            await ConnectAsync();
        }

        private void OnDestroy()
        {
            _cts?.Cancel();
            _webSocket?.Dispose();
        }

        public async Task ConnectAsync()
        {
            // 이미 연결된 경우 빠른 탈출 (락 없이)
            if (_webSocket?.State == WebSocketState.Open) return;

            await _connectLock.WaitAsync();
            try
            {
                // 락 획득 후 재확인 (선행 호출이 이미 연결했을 수 있음)
                if (_webSocket?.State == WebSocketState.Open) return;

                _cts?.Cancel();
                _webSocket?.Dispose();

                _webSocket = new ClientWebSocket();
                _cts = new CancellationTokenSource();

                await _webSocket.ConnectAsync(new Uri(wsUrl), _cts.Token);
                Debug.Log("[STT] WebSocket Connected!");
                _ = ReceiveLoop();
            }
            catch (Exception e)
            {
                Debug.LogError($"[STT] Connection Error: {e.Message}");
            }
            finally
            {
                _connectLock.Release(); // 예외 발생해도 반드시 해제
            }
        }

        /// <summary>
        /// 발화가 새로 시작됨을 알림 (헤더 전송 준비)
        /// </summary>
        public void ResetUtteranceState()
        {
            _isFirstChunk = true;
        }

        /// <summary>
        /// 오디오 청크를 바이너리로 전송 (첫 청크만 헤더 포함)
        /// </summary>
        public async Task SendAudioChunkAsync(AudioClip clip)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                await ConnectAsync();
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                byte[] audioBytes;
                if (_isFirstChunk)
                {
                    audioBytes = AudioUtils.GetWavBytes(clip);
                    _isFirstChunk = false;
                }
                else
                {
                    audioBytes = AudioUtils.GetRawPcmBytes(clip);
                }

                await _webSocket.SendAsync(
                    new ArraySegment<byte>(audioBytes),
                    WebSocketMessageType.Binary, true, _cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// 발화 종료 신호와 Feature 데이터를 전송
        /// </summary>
        public async Task SendEndUtteranceAsync(FeatureData features)
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync();
            try
            {
                string json = $"{{\"type\":\"utterance_end\",\"features\":{{" +
                            $"\"speakingTime\":{features.speakingTime:F2}," +
                            $"\"pauseCount\":{features.pauseCount}," +
                            $"\"averageVolume\":{features.averageVolume}}}}}";

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text, true, _cts.Token);
                
                _isFirstChunk = true;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task ReceiveLoop()
        {
            byte[] buffer = new byte[1024 * 32];
            using (var ms = new System.IO.MemoryStream())
            {
                while (_webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = null;
                    try
                    {
                        ms.SetLength(0); // 매 메시지마다 스트림 초기화
                        do
                        {
                            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token);
                            break;
                        }

                        if (ms.Length == 0) continue;

                        byte[] data = ms.ToArray();

                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            // 바이너리 데이터는 TTS Worker에서 온 오디오 청크(Raw PCM)로 간주
                            OnAudioChunkReceived?.Invoke(data);
                        }
                        else if (result.MessageType == WebSocketMessageType.Text)
                        {
                            string message = Encoding.UTF8.GetString(data);
                            Debug.Log($"[STT] Received JSON: {message}");

                            try
                            {
                                ServerMessage msg = JsonUtility.FromJson<ServerMessage>(message);
                                if (msg.type == "request_end")
                                {
                                    OnServerRequestEnd?.Invoke();
                                }
                                else if (msg.type == "final")
                                {
                                    FinalResponse response = JsonUtility.FromJson<FinalResponse>(message);
                                    OnTranscriptionReceived?.Invoke(response);
                                }
                                else if (msg.type == "tts_end")
                                {
                                    OnAudioStreamEnded?.Invoke();
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogWarning($"Failed to parse server message: {e.Message}");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (_webSocket.State == WebSocketState.Open)
                            Debug.LogWarning($"WebSocket Receive Error: {e.Message}");
                        break;
                    }
                }
            }
        }

        [Serializable]
        public class ServerMessage {
            public string type;
        }

        [Serializable]
        public class FinalResponse {
            public string type;
            public TranscriptionData data;
        }

        [Serializable]
        public class TranscriptionData {
            public string sttText;
            public float speakingTime;
            public int pauseCount;
            public float averageVolume;
        }
    }
}
