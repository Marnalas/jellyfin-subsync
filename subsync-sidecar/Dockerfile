# Lightweight sidecar for ffsubsync. No GPU/CUDA - the default webrtc VAD
# ffsubsync uses is CPU-only, so there's nothing for a GPU base image to
# accelerate right now. If you switch to GPU-accelerated silero/fused VAD
# later, that's the point to revisit this Dockerfile (nvidia/cuda base,
# torch, --gpus all in compose) - deliberately not carrying that weight
# until it's actually used.
FROM dhi/python:3.14-alpine-sfw-dev

ENV PYTHONUNBUFFERED=1

RUN apk add --no-cache ffmpeg

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py .

EXPOSE 8000

# Exec form, and urllib rather than curl: there's no curl in this base image and
# adding one just for a healthcheck would pull a package into a hardened image
# for no other reason. urlopen raises on a connection error or a non-2xx status,
# which exits non-zero - exactly the "unhealthy" signal wanted.
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 CMD ["python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/health', timeout=4).read()"]

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
