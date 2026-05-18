# 1. CUDA + Ubuntu
FROM nvidia/cuda:12.4.1-devel-ubuntu22.04

# 시스템 패키지
RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-dev \
    ffmpeg \
    espeak-ng \
    libsndfile1-dev \
    ninja-build \
    git \
    build-essential \
    portaudio19-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# 환경 변수
ENV TTS_HOME=/app/model
ENV PYTHONPATH=/app

# requirements 먼저 복사 (캐시 최적화)
COPY requirements.txt .

# pip 업데이트
RUN python3 -m pip install --no-cache-dir --upgrade pip

# PyTorch
RUN python3 -m pip install --no-cache-dir \
    torch torchaudio \
    --index-url https://download.pytorch.org/whl/cu124

# requirements
RUN python3 -m pip install --no-cache-dir -r requirements.txt

# Fish Speech 설치
RUN git clone https://github.com/fishaudio/fish-speech.git /opt/fish-speech && \
    cd /opt/fish-speech && \
    git checkout 58046ea && \
    pip install -e .

ENV PYTHONPATH=/app:/opt/fish-speech

# 앱 복사
COPY . .

EXPOSE 8001

CMD ["python3", "tts-worker.py"]