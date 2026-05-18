FROM nvidia/cuda:12.4.1-cudnn-runtime-ubuntu22.04

RUN apt-get update && apt-get install -y \
    python3 \
    python3-pip \
    python3-dev \
    ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .

RUN python3 -m pip install --no-cache-dir --upgrade pip && \
    python3 -m pip install --no-cache-dir -r requirements.txt

COPY . .

EXPOSE 8000

#개발할 때는 CMD로 바로 워커 파일 실행 대신 -it 모드로 켜기
#CMD ["python3", "stt-worker.py"]

#빌드는 Dockerfile 있는 디렉토리에서 아래 명령어 대로
#docker build --tag stt-worker:1.0 .

#개발할 때는 docker run --rm -it --gpus all -p 8000:8000 -v ~/model:/app/model stt-worker:dev --env-file .env
#실행은 아래 명령어 대로
#docker run --rm --gpus all -p 8000:8000 ^
#  --env-file .env ^
#  -v ~/model:/app/model ^
#  stt-worker:1.0

