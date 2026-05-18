import os
import uvicorn
import io
import json
import asyncio
import numpy as np
import onnxruntime as ort
import httpx
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from faster_whisper import WhisperModel
from pydantic import BaseModel
from dotenv import load_dotenv

load_dotenv()
app = FastAPI()

# TTS Worker URL (기본값: localhost:8001)
TTS_WORKER_URL = os.getenv("TTS_WORKER_URL", "http://127.0.0.1:8001/process")

# 모델 로드 (Faster-Whisper & Silero VAD)
base_dir = os.path.dirname(os.path.abspath(__file__))
whisper_model_path = os.path.join(base_dir, "model", "whisper")
model = WhisperModel(whisper_model_path, device="cuda", compute_type="int8_float16", local_files_only=True)

# Silero VAD 모델 로드
vad_model_path = os.path.join(base_dir, "model", "silero_vad", "silero_vad.onnx")
try:
    vad_sess = ort.InferenceSession(vad_model_path, providers=['CPUExecutionProvider'])
except Exception as e:
    print(f"VAD Model not found or error at {vad_model_path}: {e}")
    vad_sess = None

class InterviewFeatures(BaseModel):
    speakingTime: float
    pauseCount: int
    averageVolume: float

# Silero VAD의 모델 RNN 상태값: [2, 1, 128] 형태의 제로 텐서
silero_state = np.zeros((2, 1, 128), dtype=np.float32)

def validate_voice(audio_bytes):
    """Silero VAD를 사용하여 음성 유무 판별 (16kHz, Mono 가정)"""
    global silero_state # 상태 유지를 위해 global 사용
    if vad_sess is None: return True

    try:
        # 데이터가 'RIFF'로 시작하면 WAV 헤더(44바이트) 제거
        if audio_bytes[:4] == b'RIFF':
            pcm_data = audio_bytes[44:]
        else:
            pcm_data = audio_bytes

        audio_int16 = np.frombuffer(pcm_data, dtype=np.int16)
        audio_float32 = audio_int16.astype(np.float32) / 32768.0

        # Silero VAD는 512
        # 청크 전체를 512 단위로 쪼개서 하나라도 음성이면 True 반환
        window_size = 512
        is_speech = False
        for i in range(0, len(audio_float32) - window_size + 1, window_size):
            input_data = audio_float32[i:i+window_size].reshape(1, -1)
            ort_inputs = {
                "input": input_data,
                "sr": np.array([16000], dtype=np.int64),
                "state": silero_state
            }
            out, new_state = vad_sess.run(None, ort_inputs)
            silero_state = new_state # 다음 청크를 위해 상태 업데이트

            prob = out[0][0]
            if prob > 0.4: # 하나라도 음성 구간이 있으면 True
                is_speech = True

        return is_speech
    except Exception as e:
        print(f"VAD Error: {e}")
        return True

@app.websocket("/ws/interview")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    print("WebSocket Connected (PCM Stream Mode)")

    audio_buffer = bytearray()
    consecutive_silence_count = 0
    # 0.3초 청크 기준 3번 연속 침묵 시 종료, 유니티(1.5초 failsafe)보다 더 빠른 능동적 종료 가능
    SILENCE_LIMIT = 3
    
    global silero_state

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                chunk = message["bytes"]

                # 오디오 데이터 오염 방지: 첫 청크만 헤더 포함, 나머지는 PCM만 병합
                if chunk[:4] == b'RIFF':
                    # 새 발화 시작 시 버퍼 초기화 (클라이언트가 발화 시작 때 헤더를 보냄)
                    if len(audio_buffer) > 0:
                        print("New header received. Resetting buffer.")
                    audio_buffer = bytearray(chunk)
                    silero_state = np.zeros((2, 1, 128), dtype=np.float32) # VAD 상태 초기화
                else:
                    audio_buffer.extend(chunk)

                # 실시간 VAD 체크 (전체 청크 분석)
                if validate_voice(chunk):
                    consecutive_silence_count = 0
                else:
                    consecutive_silence_count += 1

                # 서버 사이드 조기 종료 감지
                if consecutive_silence_count >= SILENCE_LIMIT:
                    if len(audio_buffer) > 32000: # 최소 1초 이상 데이터가 있을 때만
                        print(f"VAD detected {SILENCE_LIMIT} consecutive silences. Requesting end...")
                        await websocket.send_json({"type": "request_end"})
                        consecutive_silence_count = 0 

            elif "text" in message:
                print(f"Raw Unity JSON: {message}")
                data = json.loads(message["text"])
                
                if data.get("type") == "utterance_end":
                    if len(audio_buffer) > 0:
                        print(f"Utterance End received. Transcribing {len(audio_buffer)} bytes...")
                        
                        try:
                            # WAV 헤더를 제외한 순수 PCM 추출 및 float32 변환
                            raw_pcm = audio_buffer[44:] if audio_buffer[:4] == b'RIFF' else audio_buffer
                            audio_np = np.frombuffer(raw_pcm, dtype=np.int16).astype(np.float32) / 32768.0
                            
                            # Whisper 추론 (비동기 스레드에서 실행하여 소켓 블로킹 방지)
                            def transcribe_task(audio):
                                segments, _ = model.transcribe(audio, language="ko", beam_size=5, vad_filter=True)
                                return " ".join([segment.text for segment in segments]).strip()

                            final_text = await asyncio.to_thread(transcribe_task, audio_np)

                            features = data.get("features")
                            final_dto = {
                                "sttText": final_text,
                                "speakingTime": features["speakingTime"],
                                "pauseCount": features["pauseCount"],
                                "averageVolume": features["averageVolume"]
                            }
                            
                            print(f"Success: {final_text}")
                            
                            # 1. Unity에 STT 결과 JSON 전송 (UI 업데이트 및 피드백용)
                            await websocket.send_json({"type": "final", "data": final_dto})

                            # 2. TTS Worker에 요청하여 오디오 스트림 수신 및 패스스루
                            print(f"Requesting TTS for: {final_text}")
                            try:
                                async with httpx.AsyncClient(timeout=None) as client:
                                    async with client.stream("POST", TTS_WORKER_URL, json={"text": final_text}) as response:
                                        if response.status_code == 200:
                                            leftover = b""
                                            async for chunk in response.aiter_bytes():
                                                if leftover:
                                                    chunk = leftover + chunk
                                                    leftover = b""
                                                
                                                # 홀수 바이트 처리 (Byte Shift 방지)
                                                if len(chunk) % 2 != 0:
                                                    leftover = chunk[-1:]
                                                    chunk = chunk[:-1]
                                                
                                                if chunk:
                                                    await websocket.send_bytes(chunk)
                                            print("TTS Stream finished.")
                                        else:
                                            print(f"[Error] TTS Worker returned status: {response.status_code}")
                            except Exception as tts_e:
                                print(f"[Error] TTS Connection failed: {tts_e}")

                        except Exception as e:
                            print(f"Transcription/Processing Error: {e}")
                        
                        finally:
                            # 성공/실패 여부와 상관없이 Unity의 VAD 잠금을 해제하기 위해 종료 신호 전송
                            try:
                                await websocket.send_json({"type": "tts_end"})
                            except:
                                pass
                            
                            audio_buffer = bytearray()
                            consecutive_silence_count = 0
                            silero_state = np.zeros((2, 1, 128), dtype=np.float32) # VAD 상태 초기화

    except WebSocketDisconnect:
        print("WebSocket Disconnected")
    except Exception as e:
        print(f"Critical Error: {e}")
        try: await websocket.close()
        except: pass

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
