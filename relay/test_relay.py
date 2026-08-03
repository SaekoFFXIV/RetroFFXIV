"""
Local integration test for the Retro FFXIV relay — no FFXIV, no audio, no plugin.

Tests:
  1. Live streaming: host goes live under a player ID → sync_check shows
     them live → spectator subscribes by ID → receives video/audio →
     stop_live notifies the spectator.
  2. Netplay: two players create/join → exchange input packets → both
     receive each other's inputs.

Usage:
  1. Start the relay:  python server.py
  2. Run this:         python test_relay.py

Exit code 0 = all passed.
"""

import asyncio
import json
import struct
import sys

import websockets

RELAY = sys.argv[1] if len(sys.argv) > 1 else "ws://127.0.0.1:8765/ws"
if not RELAY.endswith("/ws"):
    RELAY = RELAY.rstrip("/") + "/ws"
PASS = 0
FAIL = 0


def ok(name: str, cond: bool, detail: str = ""):
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ✓ {name}")
    else:
        FAIL += 1
        print(f"  ✗ {name}  {detail}")


async def test_live_streaming():
    print("\n── Live streaming (identity-based) ──")

    host = await websockets.connect(RELAY)
    spec = await websockets.connect(RELAY)
    checker = await websockets.connect(RELAY)

    uid = "test-persistent-key-aaaa"
    player_id = "1234-5678"

    # Host goes live.
    await host.send(json.dumps({
        "action": "go_live", "uid": uid,
        "player_id": player_id, "name": "Test Player",
    }))
    r = json.loads(await host.recv())
    ok("host goes live", r.get("type") == "live_started" and r.get("player_id") == player_id, str(r))

    # sync_check reports the channel as live.
    await checker.send(json.dumps({"action": "sync_check", "keys": [player_id, "9999-9999"]}))
    r = json.loads(await checker.recv())
    live = r.get("live", [])
    ok("sync_check shows live", r.get("type") == "sync_status"
       and len(live) == 1 and live[0]["player_id"] == player_id
       and live[0]["name"] == "Test Player", str(r))

    # Spectator subscribes by player ID.
    await spec.send(json.dumps({"action": "subscribe", "player_id": player_id}))
    r = json.loads(await spec.recv())
    ok("spectator subscribes", r.get("type") == "subscribed"
       and r.get("player_id") == player_id and r.get("name") == "Test Player", str(r))

    # Host gets viewer count.
    r = json.loads(await host.recv())
    ok("host sees 1 viewer", r.get("type") == "viewers" and r.get("count") == 1, str(r))

    # Host sends fake stream info + video + audio.
    fake_video = bytes([0x01]) + b"\x00\x00\x00\x01fake-h264-nal"
    fake_audio = bytes([0x02]) + struct.pack("<4h", 100, -100, 200, -200)
    fake_info  = bytes([0x03]) + json.dumps({"width": 768, "height": 672, "fps": 30, "sample_rate": 32000}).encode()

    await host.send(fake_info)
    await host.send(fake_video)
    await host.send(fake_audio)

    got_info  = await spec.recv()
    got_video = await spec.recv()
    got_audio = await spec.recv()

    ok("spectator gets stream info", isinstance(got_info, bytes) and got_info[0] == 0x03)
    ok("spectator gets video",       isinstance(got_video, bytes) and got_video == fake_video)
    ok("spectator gets audio",       isinstance(got_audio, bytes) and got_audio == fake_audio)

    # Spectator's binary should be ignored (only the host fans out).
    await spec.send(bytes([0x01]) + b"should-be-ignored")

    # Subscribing to a player who is not live fails.
    other = await websockets.connect(RELAY)
    await other.send(json.dumps({"action": "subscribe", "player_id": "9999-9999"}))
    r = json.loads(await other.recv())
    ok("subscribe to offline player fails", r.get("type") == "error", str(r))
    await other.close()

    # Host stops streaming; spectator is notified.
    await host.send(json.dumps({"action": "stop_live"}))
    r = json.loads(await host.recv())
    ok("host stops live", r.get("type") == "live_stopped", str(r))
    r = json.loads(await spec.recv())
    ok("spectator notified of live_ended", r.get("type") == "live_ended", str(r))

    # sync_check is now empty.
    await checker.send(json.dumps({"action": "sync_check", "keys": [player_id]}))
    r = json.loads(await checker.recv())
    ok("sync_check empty after stop", r.get("type") == "sync_status" and r.get("live") == [], str(r))

    # Room codes are gone: the old actions are unknown.
    await checker.send(json.dumps({"action": "create"}))
    r = json.loads(await checker.recv())
    ok("room mode removed", r.get("type") == "error", str(r))

    await host.close()
    await spec.close()
    await checker.close()


async def test_netplay():
    print("\n── Netplay ──")

    p1 = await websockets.connect(RELAY)
    p2 = await websockets.connect(RELAY)

    uid1 = "test-player-aaaa"
    uid2 = "test-player-bbbb"

    # Player 1 creates.
    await p1.send(json.dumps({"action": "create_netplay", "uid": uid1}))
    r = json.loads(await p1.recv())
    ok("p1 creates netplay room", r.get("type") == "netplay_created" and r.get("slot") == 0, str(r))
    room = r["room"]

    # Player 2 joins.
    await p2.send(json.dumps({"action": "join_netplay", "room": room, "uid": uid2}))
    r = json.loads(await p2.recv())
    ok("p2 joins netplay room", r.get("type") == "netplay_joined" and r.get("slot") == 1, str(r))

    # Both get roster.
    r1 = json.loads(await p1.recv())
    r2 = json.loads(await p2.recv())
    ok("p1 gets roster", r1.get("type") == "netplay_players" and len(r1.get("players", [])) == 2, str(r1))
    ok("p2 gets roster", r2.get("type") == "netplay_players" and len(r2.get("players", [])) == 2, str(r2))

    # Exchange input packets: [0x04][slot][frame u32][input u16]
    def make_input(slot: int, frame: int, buttons: int) -> bytes:
        return struct.pack("<BBIH", 0x04, slot, frame, buttons)

    # P1 sends frame 0 with buttons=0x00FF, P2 sends frame 0 with buttons=0xFF00.
    await p1.send(make_input(0, 0, 0x00FF))
    await p2.send(make_input(1, 0, 0xFF00))

    # P1 should receive P2's input, P2 should receive P1's.
    d1 = await p1.recv()
    d2 = await p2.recv()

    ok("p1 receives p2 input", isinstance(d1, bytes) and d1 == make_input(1, 0, 0xFF00), d1.hex() if isinstance(d1, bytes) else str(d1))
    ok("p2 receives p1 input", isinstance(d2, bytes) and d2 == make_input(0, 0, 0x00FF), d2.hex() if isinstance(d2, bytes) else str(d2))

    # Reconnection: P2 disconnects and rejoins with same UID.
    await p2.close()
    await asyncio.sleep(0.2)

    # P1 should get player-left notification.
    r = json.loads(await p1.recv())
    ok("p1 notified of p2 leaving", r.get("type") == "netplay_player_left" and r.get("uid") == uid2, str(r))

    p2b = await websockets.connect(RELAY)
    await p2b.send(json.dumps({"action": "join_netplay", "room": room, "uid": uid2}))
    r = json.loads(await p2b.recv())
    ok("p2 reconnects with same UID", r.get("type") == "netplay_joined" and r.get("slot") == 1, str(r))

    # Room full: a third player should be rejected.
    p3 = await websockets.connect(RELAY)
    await p3.send(json.dumps({"action": "join_netplay", "room": room, "uid": "test-player-cccc"}))
    r = json.loads(await p3.recv())
    ok("third player rejected (room full)", r.get("type") == "error" and "full" in r.get("message", "").lower(), str(r))

    await p1.close()
    await p2b.close()
    await p3.close()


async def main():
    print("Retro FFXIV Relay integration test")
    print(f"Target: {RELAY}")

    try:
        await test_live_streaming()
        await test_netplay()
    except ConnectionRefusedError:
        print("\n✗ Cannot connect to relay — is it running?  (python server.py)")
        sys.exit(1)

    print(f"\n{'='*40}")
    print(f"  {PASS} passed, {FAIL} failed")
    print(f"{'='*40}")
    sys.exit(1 if FAIL else 0)


if __name__ == "__main__":
    asyncio.run(main())
