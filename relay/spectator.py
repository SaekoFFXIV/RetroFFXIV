"""
Standalone spectator — watch a stream in a window, no sound.

Usage:
    python spectator.py <PLAYER_ID> [relay_url]

Examples:
    python spectator.py 1234-5678
    python spectator.py 1234-5678 wss://relay.nekomail.cc

Requires:  pip install opencv-python av websockets
"""

import asyncio
import json
import sys
import threading

import av
import cv2
import numpy as np
import websockets

RELAY = "wss://relay.nekomail.cc"

# Latest decoded frame, shared between the async receiver and the
# OpenCV display loop (which runs on the main thread).
frame_lock = threading.Lock()
latest_frame: np.ndarray | None = None
stream_info: dict = {}
connected = False


decode_errors = 0
frames_received = 0

def decode_and_display(h264_data: bytes, codec: av.CodecContext) -> None:
    global latest_frame, decode_errors, frames_received
    frames_received += 1
    try:
        packet = av.Packet(h264_data)
        for frame in codec.decode(packet):
            img = frame.to_ndarray(format="bgr24")
            with frame_lock:
                latest_frame = img
    except Exception as e:
        decode_errors += 1
        if decode_errors <= 5:
            print(f"  decode error #{decode_errors}: {e} (data={len(h264_data)}B, first4={h264_data[:4].hex()})")


async def receive_loop(player_id: str, relay_url: str) -> None:
    global connected
    url = relay_url.rstrip("/") + "/ws"
    print(f"Connecting to {url} ...")

    async with websockets.connect(url) as ws:
        # Subscribe to the player's live stream by ID.
        await ws.send(json.dumps({"action": "subscribe", "player_id": player_id}))
        r = json.loads(await ws.recv())
        if r.get("type") != "subscribed":
            print(f"Failed to subscribe: {r}")
            return

        connected = True
        name = r.get("name") or player_id
        print(f"Watching {name} — waiting for frames (Ctrl+C to quit)")

        codec = av.CodecContext.create("h264", "r")

        async for message in ws:
            if isinstance(message, bytes) and len(message) > 1:
                msg_type = message[0]
                payload = message[1:]

                if msg_type == 0x03:  # stream info
                    info = json.loads(payload)
                    stream_info.update(info)
                    w, h = info.get("width", 768), info.get("height", 672)
                    print(f"Stream: {w}x{h} @ {info.get('fps', 30)}fps")

                elif msg_type == 0x01:  # video
                    decode_and_display(payload, codec)
                    if frames_received % 150 == 1:
                        print(f"  video pkt #{frames_received}: {len(payload)}B, decoded_frames={latest_frame is not None}, errors={decode_errors}")

            elif isinstance(message, str):
                msg = json.loads(message)
                if msg.get("type") == "live_ended":
                    print("Player stopped streaming.")
                    break


def main() -> None:
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)

    player_id = sys.argv[1]
    relay_url = sys.argv[2] if len(sys.argv) > 2 else RELAY

    # Start the async receiver in a background thread.
    loop = asyncio.new_event_loop()
    t = threading.Thread(target=loop.run_until_complete,
                         args=(receive_loop(player_id, relay_url),),
                         daemon=True)
    t.start()

    # OpenCV display loop on the main thread.
    window_name = f"SNES Stream — {player_id}"
    print("Opening window... (it may take a few seconds for the first frame)")

    try:
        while True:
            with frame_lock:
                img = latest_frame.copy() if latest_frame is not None else None

            if img is not None:
                cv2.imshow(window_name, img)
            else:
                # Blank frame while waiting.
                blank = np.zeros((448, 512, 3), dtype=np.uint8)
                cv2.putText(blank, "Waiting for frames...", (120, 230),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (100, 100, 100), 2)
                cv2.imshow(window_name, blank)

            if cv2.waitKey(16) & 0xFF == ord("q"):
                break

            if not t.is_alive() and connected:
                break
    except KeyboardInterrupt:
        pass
    finally:
        cv2.destroyAllWindows()
        print("Closed.")


if __name__ == "__main__":
    main()
