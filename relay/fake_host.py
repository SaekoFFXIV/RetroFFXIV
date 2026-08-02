"""
Fake host — generates a test pattern and streams it through the relay.
Use this to verify the spectator window works without FFXIV.

Usage:
    python fake_host.py [relay_url]

Then in another terminal:
    python spectator.py <ROOM_CODE> [relay_url]

The room code is printed when the host starts.

Requires:  pip install av websockets
"""

import asyncio
import json
import math
import sys
import time
from fractions import Fraction

import av
import numpy as np
import websockets

RELAY = "wss://relay.nekomail.cc"
WIDTH, HEIGHT, FPS = 768, 672, 30
BITRATE = 2_000_000


def make_test_frame(t: float) -> np.ndarray:
    """Colour bars + a bouncing white square + frame counter."""
    img = np.zeros((HEIGHT, WIDTH, 3), dtype=np.uint8)

    # Colour bars (top 80%).
    bar_h = int(HEIGHT * 0.8)
    colours = [
        (255, 255, 255),  # white
        (255, 255, 0),    # yellow
        (0, 255, 255),    # cyan
        (0, 255, 0),      # green
        (255, 0, 255),    # magenta
        (255, 0, 0),      # red
        (0, 0, 255),      # blue
        (0, 0, 0),        # black
    ]
    bar_w = WIDTH // len(colours)
    for i, (r, g, b) in enumerate(colours):
        img[:bar_h, i * bar_w:(i + 1) * bar_w] = (b, g, r)  # BGR

    # Bouncing white square.
    sq = 60
    period = 3.0
    x = int((WIDTH - sq) * (0.5 + 0.5 * math.sin(2 * math.pi * t / period)))
    y = int((bar_h - sq) * (0.5 + 0.5 * math.cos(2 * math.pi * t / (period * 1.3))))
    img[y:y + sq, x:x + sq] = (255, 255, 255)

    # Frame counter text (bottom bar).
    frame_num = int(t * FPS)
    text = f"SNES RELAY TEST  |  frame {frame_num}  |  {WIDTH}x{HEIGHT} @ {FPS}fps"
    import cv2
    cv2.putText(img, text, (20, HEIGHT - 40),
                cv2.FONT_HERSHEY_SIMPLEX, 0.8, (200, 200, 200), 2)

    return img


async def main():
    relay_url = sys.argv[1] if len(sys.argv) > 1 else RELAY
    url = relay_url.rstrip("/") + "/ws"

    print(f"Connecting to {url} ...")
    async with websockets.connect(url) as ws:
        # Create a spectate room.
        await ws.send(json.dumps({"action": "create"}))
        r = json.loads(await ws.recv())
        if r.get("type") != "created":
            print(f"Failed: {r}")
            return

        room = r["room"]
        print(f"Room: {room}")
        print(f"Watch with:  python spectator.py {room} {relay_url}")
        print("Streaming test pattern... (Ctrl+C to stop)")

        # Send stream info.
        info = json.dumps({"width": WIDTH, "height": HEIGHT, "fps": FPS, "sample_rate": 32000})
        await ws.send(bytes([0x03]) + info.encode())

        # Set up H.264 encoder via PyAV.
        codec = av.CodecContext.create("libx264", "w")
        codec.width = WIDTH
        codec.height = HEIGHT
        codec.pix_fmt = "yuv420p"
        codec.bit_rate = BITRATE
        codec.framerate = Fraction(FPS, 1)
        codec.time_base = Fraction(1, FPS)
        # Low-latency settings.
        codec.options = {
            "preset": "ultrafast",
            "tune": "zerolatency",
            "g": str(FPS * 2),  # keyframe every 2 seconds
        }
        codec.open()

        frame_idx = 0
        start = time.monotonic()

        while True:
            target = start + frame_idx / FPS
            now = time.monotonic()
            if now < target:
                await asyncio.sleep(target - now)

            t = frame_idx / FPS
            img = make_test_frame(t)

            # BGR (OpenCV) → RGB → YUV420P for the encoder.
            frame = av.VideoFrame.from_ndarray(img[:, :, ::-1], format="rgb24")
            frame = frame.reformat(format="yuv420p")
            frame.pts = frame_idx
            frame.time_base = Fraction(1, FPS)

            for packet in codec.encode(frame):
                data = bytes(packet)
                if data:
                    await ws.send(bytes([0x01]) + data)

            frame_idx += 1
            if frame_idx % (FPS * 5) == 0:
                print(f"  sent {frame_idx} frames ({t:.0f}s)")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
