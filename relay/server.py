"""
Retro FFXIV streaming relay.

A WebSocket fan-out hub with three modes:
  1. Room-based spectating (legacy): host creates a room code, spectators join.
  2. Identity-based streaming: host goes live with their XIVAuth persistent_key,
     friends subscribe by key — no room code needed.
  3. Netplay: lockstep input routing between two players in a room.

The relay never decodes or re-encodes — it is a dumb pipe with routing.

Configuration (environment variables):
    RELAY_HOST  Bind address   (default 0.0.0.0)
    RELAY_PORT  Listen port    (default 8765)

Local dev:
    uvicorn server:app --host 127.0.0.1 --port 8765

Docker (homeserver or VPS):
    docker compose up -d

Behind Cloudflare Tunnel (homeserver, no open ports):
    cloudflared tunnel --url http://127.0.0.1:8765
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import secrets
import string
from dataclasses import dataclass, field

from fastapi import FastAPI, WebSocket, WebSocketDisconnect

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("relay")

HOST = os.environ.get("RELAY_HOST", "0.0.0.0")
PORT = int(os.environ.get("RELAY_PORT", "8765"))

app = FastAPI(title="Retro FFXIV Relay")

CODE_ALPHABET = string.ascii_uppercase + string.digits
CODE_LENGTH = 4


def generate_code() -> str:
    return "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))


@dataclass
class Room:
    code: str
    host: WebSocket
    spectators: set[WebSocket] = field(default_factory=set)


@dataclass
class NetplayPlayer:
    uid: str            # persistent unique ID (UUID from the plugin config)
    ws: WebSocket
    slot: int           # emulator port (0, 1, …)


@dataclass
class NetplayRoom:
    code: str
    max_players: int = 2
    players: dict[str, NetplayPlayer] = field(default_factory=dict)  # uid → player


@dataclass
class LiveChannel:
    uid: str                # XIVAuth persistent_key (or legacy UUID)
    name: str               # character name for display
    host: WebSocket
    subscribers: set[WebSocket] = field(default_factory=set)


rooms: dict[str, Room] = {}
netplay_rooms: dict[str, NetplayRoom] = {}
live_channels: dict[str, LiveChannel] = {}   # uid → channel
live_subscriptions: dict[WebSocket, str] = {} # subscriber ws → uid they watch
# Reverse lookups.
connections: dict[WebSocket, Room] = {}
netplay_connections: dict[WebSocket, NetplayRoom] = {}


def remove_connection(ws: WebSocket) -> None:
    # Check live channel subscriptions first.
    sub_uid = live_subscriptions.pop(ws, None)
    if sub_uid is not None:
        channel = live_channels.get(sub_uid)
        if channel is not None:
            channel.subscribers.discard(ws)
            log.info("live %s subscriber left (%d remaining)", sub_uid[:8], len(channel.subscribers))
        return

    # Check live channel hosts.
    live_uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if live_uid is not None:
        channel = live_channels.pop(live_uid)
        for sub in list(channel.subscribers):
            asyncio.create_task(_safe_send_text(sub, {"type": "live_ended", "uid": live_uid}))
            live_subscriptions.pop(sub, None)
        log.info("live %s ended (host left)", live_uid[:8])
        return

    # Check spectate rooms.
    room = connections.pop(ws, None)
    if room is not None:
        if ws is room.host:
            for spec in list(room.spectators):
                asyncio.create_task(_safe_send_text(spec, {"type": "closed"}))
                connections.pop(spec, None)
                asyncio.create_task(spec.close())
            rooms.pop(room.code, None)
            log.info("room %s closed (host left)", room.code)
        else:
            room.spectators.discard(ws)
            log.info("room %s spectator left (%d remaining)", room.code, len(room.spectators))
            asyncio.create_task(_notify_viewers(room))
        return

    # Check netplay rooms.
    np_room = netplay_connections.pop(ws, None)
    if np_room is not None:
        uid = next((u for u, p in np_room.players.items() if p.ws is ws), None)
        if uid is not None:
            player = np_room.players.pop(uid)
            log.info("netplay room %s player %s (slot %d) left (%d remaining)",
                     np_room.code, uid[:8], player.slot, len(np_room.players))
            for other in np_room.players.values():
                asyncio.create_task(_safe_send_text(other.ws, {
                    "type": "netplay_player_left",
                    "uid": uid,
                    "slot": player.slot,
                }))
        if not np_room.players:
            netplay_rooms.pop(np_room.code, None)
            log.info("netplay room %s closed (empty)", np_room.code)


async def _safe_send_text(ws: WebSocket, obj: dict) -> None:
    try:
        await ws.send_text(json.dumps(obj))
    except Exception:
        pass


async def _safe_send_bytes(ws: WebSocket, data: bytes) -> None:
    try:
        await ws.send_bytes(data)
    except Exception:
        pass


async def _notify_viewers(room: Room) -> None:
    await _safe_send_text(room.host, {"type": "viewers", "count": len(room.spectators)})


@app.get("/health")
async def health():
    return {"rooms": len(rooms), "netplay_rooms": len(netplay_rooms), "live_channels": len(live_channels)}


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await ws.accept()
    log.info("connection opened")

    try:
        while True:
            message = await ws.receive()

            if message.get("type") == "websocket.disconnect":
                break

            if "text" in message and message["text"] is not None:
                await _handle_text(ws, message["text"])
            elif "bytes" in message and message["bytes"] is not None:
                await _handle_binary(ws, message["bytes"])

    except WebSocketDisconnect:
        pass
    except Exception:
        log.exception("connection error")
    finally:
        remove_connection(ws)
        log.info("connection closed")


async def _handle_text(ws: WebSocket, raw: str) -> None:
    try:
        msg = json.loads(raw)
    except json.JSONDecodeError:
        await _safe_send_text(ws, {"type": "error", "message": "Invalid JSON"})
        return

    action = msg.get("action")

    if action == "create":
        await _handle_create(ws)
    elif action == "join":
        await _handle_join(ws, msg.get("room", ""))
    elif action == "close":
        await _handle_close(ws)
    elif action == "create_netplay":
        uid = msg.get("uid", "")
        if not uid:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid"})
            return
        await _handle_create_netplay(ws, uid)
    elif action == "join_netplay":
        uid = msg.get("uid", "")
        if not uid:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid"})
            return
        await _handle_join_netplay(ws, msg.get("room", ""), uid)
    elif action == "go_live":
        uid = msg.get("uid", "")
        if not uid:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid"})
            return
        await _handle_go_live(ws, uid, msg.get("name", ""))
    elif action == "stop_live":
        await _handle_stop_live(ws)
    elif action == "subscribe":
        uid = msg.get("uid", "")
        if not uid:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid"})
            return
        await _handle_subscribe(ws, uid)
    elif action == "unsubscribe":
        await _handle_unsubscribe(ws)
    elif action == "sync_check":
        keys = msg.get("keys", [])
        await _handle_sync_check(ws, keys)
    else:
        await _safe_send_text(ws, {"type": "error", "message": f"Unknown action: {action}"})


async def _handle_create(ws: WebSocket) -> None:
    # A connection can only host one room at a time.
    existing = connections.get(ws)
    if existing is not None:
        await _safe_send_text(ws, {"type": "error", "message": "Already in a room"})
        return

    code = generate_code()
    while code in rooms:
        code = generate_code()

    room = Room(code=code, host=ws)
    rooms[code] = room
    connections[ws] = room
    log.info("room %s created", code)
    await _safe_send_text(ws, {"type": "created", "room": code})


async def _handle_join(ws: WebSocket, code: str) -> None:
    code = code.upper().strip()
    room = rooms.get(code)
    if room is None:
        await _safe_send_text(ws, {"type": "error", "message": "Room not found"})
        return

    if ws in room.spectators or ws is room.host:
        await _safe_send_text(ws, {"type": "error", "message": "Already in this room"})
        return

    # Leave any previous room first.
    remove_connection(ws)

    room.spectators.add(ws)
    connections[ws] = room
    log.info("room %s spectator joined (%d total)", code, len(room.spectators))
    await _safe_send_text(ws, {"type": "joined", "room": code})
    await _notify_viewers(room)


async def _handle_close(ws: WebSocket) -> None:
    room = connections.get(ws)
    if room is None or ws is not room.host:
        await _safe_send_text(ws, {"type": "error", "message": "Not hosting a room"})
        return

    for spec in list(room.spectators):
        await _safe_send_text(spec, {"type": "closed"})
        connections.pop(spec, None)
        asyncio.create_task(spec.close())

    rooms.pop(room.code, None)
    connections.pop(ws, None)
    log.info("room %s closed by host", room.code)


async def _handle_create_netplay(ws: WebSocket, uid: str) -> None:
    if connections.get(ws) or netplay_connections.get(ws):
        await _safe_send_text(ws, {"type": "error", "message": "Already in a room"})
        return

    code = generate_code()
    while code in rooms or code in netplay_rooms:
        code = generate_code()

    np_room = NetplayRoom(code=code)
    np_room.players[uid] = NetplayPlayer(uid=uid, ws=ws, slot=0)
    netplay_rooms[code] = np_room
    netplay_connections[ws] = np_room
    log.info("netplay room %s created by %s (slot 0)", code, uid[:8])
    await _safe_send_text(ws, {
        "type": "netplay_created", "room": code, "slot": 0, "uid": uid,
    })


async def _handle_join_netplay(ws: WebSocket, code: str, uid: str) -> None:
    code = code.upper().strip()
    np_room = netplay_rooms.get(code)
    if np_room is None:
        await _safe_send_text(ws, {"type": "error", "message": "Room not found"})
        return

    # Reconnection: if this UID is already in the room, replace the socket.
    if uid in np_room.players:
        old = np_room.players[uid]
        netplay_connections.pop(old.ws, None)
        old.ws = ws
        netplay_connections[ws] = np_room
        log.info("netplay room %s player %s reconnected (slot %d)",
                 code, uid[:8], old.slot)
        await _safe_send_text(ws, {
            "type": "netplay_joined", "room": code, "slot": old.slot, "uid": uid,
        })
        return

    if len(np_room.players) >= np_room.max_players:
        await _safe_send_text(ws, {"type": "error", "message": "Room is full"})
        return

    # Leave any previous room.
    remove_connection(ws)

    # Assign the next available slot.
    used_slots = {p.slot for p in np_room.players.values()}
    slot = 0
    while slot in used_slots:
        slot += 1

    np_room.players[uid] = NetplayPlayer(uid=uid, ws=ws, slot=slot)
    netplay_connections[ws] = np_room
    log.info("netplay room %s player %s joined (slot %d, %d total)",
             code, uid[:8], slot, len(np_room.players))

    await _safe_send_text(ws, {
        "type": "netplay_joined", "room": code, "slot": slot, "uid": uid,
    })
    # Broadcast the full roster so every client knows who's who.
    roster = [
        {"uid": p.uid, "slot": p.slot}
        for p in sorted(np_room.players.values(), key=lambda p: p.slot)
    ]
    for p in np_room.players.values():
        asyncio.create_task(_safe_send_text(p.ws, {
            "type": "netplay_players", "players": roster,
        }))


async def _handle_go_live(ws: WebSocket, uid: str, name: str) -> None:
    # Replacing an existing channel for this uid (reconnect).
    existing = live_channels.get(uid)
    if existing is not None:
        if existing.host is ws:
            await _safe_send_text(ws, {"type": "error", "message": "Already live"})
            return
        # Reconnect: move subscribers to the new socket.
        subscribers = existing.subscribers
        live_subscriptions_update = {s: uid for s in subscribers}
        live_subscriptions.update(live_subscriptions_update)
        existing.host = ws
        existing.name = name or existing.name
        log.info("live %s host reconnected (%d subscribers)", uid[:8], len(subscribers))
        await _safe_send_text(ws, {"type": "live_started", "uid": uid, "subscribers": len(subscribers)})
        return

    channel = LiveChannel(uid=uid, name=name, host=ws)
    live_channels[uid] = channel
    log.info("live %s started (%s)", uid[:8], name or "anonymous")
    await _safe_send_text(ws, {"type": "live_started", "uid": uid, "subscribers": 0})


async def _handle_stop_live(ws: WebSocket) -> None:
    uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if uid is None:
        await _safe_send_text(ws, {"type": "error", "message": "Not live"})
        return

    channel = live_channels.pop(uid)
    for sub in list(channel.subscribers):
        asyncio.create_task(_safe_send_text(sub, {"type": "live_ended", "uid": uid}))
        live_subscriptions.pop(sub, None)
    log.info("live %s stopped", uid[:8])
    await _safe_send_text(ws, {"type": "live_stopped"})


async def _handle_subscribe(ws: WebSocket, uid: str) -> None:
    channel = live_channels.get(uid)
    if channel is None:
        await _safe_send_text(ws, {"type": "error", "message": "Player is not live"})
        return

    # Unsubscribe from any previous channel.
    old_uid = live_subscriptions.pop(ws, None)
    if old_uid is not None:
        old_channel = live_channels.get(old_uid)
        if old_channel is not None:
            old_channel.subscribers.discard(ws)

    channel.subscribers.add(ws)
    live_subscriptions[ws] = uid
    log.info("live %s subscriber joined (%d total)", uid[:8], len(channel.subscribers))
    await _safe_send_text(ws, {
        "type": "subscribed", "uid": uid, "name": channel.name,
    })
    # Notify the host of the new viewer count.
    await _safe_send_text(channel.host, {
        "type": "viewers", "count": len(channel.subscribers),
    })


async def _handle_unsubscribe(ws: WebSocket) -> None:
    uid = live_subscriptions.pop(ws, None)
    if uid is None:
        return
    channel = live_channels.get(uid)
    if channel is not None:
        channel.subscribers.discard(ws)
        await _safe_send_text(channel.host, {
            "type": "viewers", "count": len(channel.subscribers),
        })
    await _safe_send_text(ws, {"type": "unsubscribed"})


async def _handle_sync_check(ws: WebSocket, keys: list) -> None:
    # Return which of the given keys are currently live.
    live = []
    for key in keys:
        channel = live_channels.get(key)
        if channel is not None:
            live.append({"uid": key, "name": channel.name, "viewers": len(channel.subscribers)})
    await _safe_send_text(ws, {"type": "sync_status", "live": live})


async def _handle_binary(ws: WebSocket, data: bytes) -> None:
    # Live channel: host fans out to subscribers.
    live_uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if live_uid is not None:
        channel = live_channels[live_uid]
        for sub in list(channel.subscribers):
            asyncio.create_task(_safe_send_bytes(sub, data))
        return

    # Spectate room: host fans out to spectators.
    room = connections.get(ws)
    if room is not None:
        if ws is not room.host:
            return
        for spec in list(room.spectators):
            asyncio.create_task(_safe_send_bytes(spec, data))
        return

    # Netplay room: route to all OTHER players.
    np_room = netplay_connections.get(ws)
    if np_room is not None:
        for p in list(np_room.players.values()):
            if p.ws is not ws:
                asyncio.create_task(_safe_send_bytes(p.ws, data))


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host=HOST, port=PORT)
