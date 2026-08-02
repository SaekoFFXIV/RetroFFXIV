FROM python:3.13-slim

WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY server.py .

ENV RELAY_HOST=0.0.0.0
ENV RELAY_PORT=8765
EXPOSE 8765

HEALTHCHECK --interval=30s --timeout=3s \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8765/health')"

USER nobody
ENTRYPOINT ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "8765"]
