import os
import re
import json
import asyncio
import torch
import numpy as np
import uvicorn
import librosa
from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.responses import StreamingResponse
from contextlib import asynccontextmanager
from pydantic import BaseModel
from openai import OpenAI
from dotenv import load_dotenv
from threading import Lock

from fish_speech.models.vqgan.modules.firefly import FireflyArchitecture
from fish_speech.models.text2semantic.llama import DualARTransformer
from fish_speech.models.text2semantic.inference import generate_long, decode_one_token_ar
from fish_speech.models.vqgan.inference import load_model as load_vqgan

# 환경 변수 로드
load_dotenv()

tts_lock = Lock()

device = "cuda" if torch.cuda.is_available() else "cpu"

# 정밀도 설정
if torch.cuda.is_available():
    torch.backends.cuda.matmul.allow_tf32 = True
    torch.backends.cudnn.allow_tf32 = True
    precision = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
else:
    precision = torch.float32

print(f"[Init] Loading Fish Speech 1.5 models on {device} with {precision}...")

base_dir = os.path.dirname(os.path.abspath(__file__))
fs_model_path = os.path.join(base_dir, "model", "fish-speech-1.5")

# VQGAN 로드
print("[Init] Loading VQGAN...")
vqgan_model = load_vqgan(   
    config_name="firefly_gan_vq",
    checkpoint_path=f"{fs_model_path}/firefly-gan-vq-fsq-8x1024-21hz-generator.pth",
    device=device,
)
vqgan_model.eval()

# LLaMA 로드
print("[Init] Loading LLaMA Text2Semantic model...")
llama_model = DualARTransformer.from_pretrained(fs_model_path, load_weights=True)
llama_model.to(device=device, dtype=precision)
llama_model.eval()

# KV 캐시 초기화
print("[Init] Setting up LLaMA caches...")
with torch.device(device):
    llama_model.setup_caches(
        max_batch_size=1,
        max_seq_len=llama_model.config.max_seq_len,
        dtype=precision,
    )

# 컴파일 최적화
print("[Init] Compiling decode function for extreme speed...")
import torch._inductor.config
torch._inductor.config.coordinate_descent_tuning = True
torch._inductor.config.triton.unique_kernel_names = True

compiled_decode = torch.compile(
    decode_one_token_ar,
    backend="inductor",
    mode="default"
)

speaker_wav_path = os.path.join(base_dir, "speaker.wav")
prompt_tokens = None
prompt_texts = os.environ.get("REFERENCE_DIALOGUE", "왜 우리 회사에 지원했나요? 예상치 못한 문제에 봉착했을 때, 해결했던 경험은? 동료와 갈등이 발생했을 때, 어떻게 해결했나요? 입사 후 1년 내에 달성하고 싶은 목표는? 마지막으로 하고 싶은 말이나 질문 있나요?")

# 안정적인 librosa 기반 로딩
if os.path.exists(speaker_wav_path):
    print(f"[Init] Pre-computing prompt tokens with librosa...")
    audio_data, sr = librosa.load(speaker_wav_path, sr=vqgan_model.spec_transform.sample_rate)
    audio = torch.from_numpy(audio_data).float()
    
    if audio.dim() == 1:
        audio = audio.unsqueeze(0).unsqueeze(0)
    elif audio.dim() == 2:
        audio = audio.unsqueeze(0)

    audio = audio.to(device=device)
    audio_lengths = torch.tensor([audio.shape[-1]], device=device)

    with torch.inference_mode():
        indices, _ = vqgan_model.encode(audio, audio_lengths)
        prompt_tokens = indices[0]
else:
    print(f"[Warning] speaker.wav not found. Using default voice.")

if device == "cuda":
    torch.cuda.empty_cache()

print("[Init] Fish Speech 1.5 models loaded and ready.")

client = OpenAI(api_key=os.environ.get("GROQ_API_KEY"), base_url="https://api.groq.com/openai/v1")

class TTSRequest(BaseModel):
    text: str

def synthesize_audio(text: str):
    with tts_lock:
        try:
            print(f"[TTS] Synthesizing: {text}")
            with torch.inference_mode():
                token_generator = generate_long(
                    model=llama_model,
                    device=device,
                    text=text,
                    prompt_text=prompt_texts if prompt_tokens is not None else None,
                    prompt_tokens=prompt_tokens,
                    max_new_tokens=1024,
                    temperature=0.6,
                    top_p=0.9,
                    decode_one_token=compiled_decode
                )

                all_codes = []
                for response in token_generator:
                    if response.action == "sample":
                        all_codes.append(response.codes.detach().clone())

                if not all_codes: return b""

                acoustic_tokens = torch.cat(all_codes, dim=-1).unsqueeze(0).to(device)
                feature_lengths = torch.tensor([acoustic_tokens.shape[-1]], device=device)

                audio_tensor, _ = vqgan_model.decode(indices=acoustic_tokens, feature_lengths=feature_lengths)
                return audio_tensor.squeeze().cpu().float().numpy().astype(np.float32).tobytes()
        except Exception as e:
            print(f"[Error] Synthesis failed: {e}")
            return b""

async def response_generator(user_text: str):
    try:
        with client.chat.completions.create(
            model=os.environ.get("MODEL_NAME"),
            messages=[{"role": "system", "content": os.environ.get("SYSTEM_PROMPT")}, {"role": "user", "content": user_text}],
            stream=True
        ) as response:
            sentence_buffer = ""
            for chunk in response:
                if not chunk.choices: continue
                content = chunk.choices[0].delta.content
                if content:
                    sentence_buffer += content
                    if any(ch in content for ch in ".!?\n"):
                        sentences = [ s for s in re.split(r'(?<=[.!?\n])',sentence_buffer) if s ]
                        for i in range(len(sentences) - 1):
                            sentence = sentences[i].strip()
                            if sentence:
                                audio_chunk = await asyncio.to_thread(synthesize_audio, sentence)
                                if audio_chunk: yield audio_chunk
                        sentence_buffer = sentences[-1]
            if sentence_buffer.strip():
                audio_chunk = await asyncio.to_thread(synthesize_audio, sentence_buffer.strip())
                if audio_chunk: yield audio_chunk
    except Exception as e:
        print(f"[Error] response_generator failed: {e}")

@asynccontextmanager
async def lifespan(app: FastAPI):
    """
    서버 시작 시 자동으로 실행되는 웜업(Warm-up) 함수입니다.
    torch.compile의 JIT 컴파일 과정을 미리 수행하여 첫 사용자 요청의 지연을 없앱니다.
    """
    print("="*60)
    print("[Warm-up] Starting model warm-up sequence...")
    print("[Warm-up] THIS MAY TAKE 5~10 MINUTES. DO NOT INTERRUPT.")
    print("="*60)

    # 웜업용 더미 텍스트
    # (실제 요청과 유사한 길이나 특수문자를 포함하면 좋습니다)
    dummy_text = "안녕하세요. 시스템 초기화를 위한 더미 테스트입니다."

    try:
        # synthesize_audio 함수 강제로 한 번 실행
        # 비동기 환경이므로 asyncio.to_thread 사용
        await asyncio.to_thread(synthesize_audio, dummy_text)
        print("="*60)
        print("[Warm-up] Compilation and warm-up successful!")
        print("[Warm-up] Server is now ready to accept requests.")
        print("="*60)
    except Exception as e:
        print(f"[Warm-up Error] Warm-up failed: {e}")
        print("Please check the error logs.")

    yield

    print("tts 서버 종료")

app = FastAPI(lifespan=lifespan)

@app.post("/process")
async def process_text_to_audio(request: TTSRequest):
    if not request.text:
        raise HTTPException(status_code=400, detail="Text is empty")
    return StreamingResponse(
        response_generator(request.text),
        media_type="application/octet-stream" 
    )

@app.websocket("/ws/tts")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    print("[WS] Persistent client connection accepted.")
    try:
        while True:
            # 클라이언트로부터 JSON 텍스트 수신 (예: {"text": "..."})
            data = await websocket.receive_text()
            message = json.loads(data)
            text = message.get("text")
            
            if text:
                # response_generator를 통해 생성되는 오디오 바이트 청크를 실시간 전송
                async for audio_chunk in response_generator(text):
                    await websocket.send_bytes(audio_chunk)
                
                # 합성 완료 신호 전송 (연결을 끊지 않고 스트림 종료만 통보)
                await websocket.send_json({"type": "end"})
    except WebSocketDisconnect:
        print("[WS] Client disconnected")
    except Exception as e:
        print(f"[WS] Error: {e}")
    finally:
        try:
            await websocket.close()
            print("[WS] Connection closed.")
        except:
            pass

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8001)
