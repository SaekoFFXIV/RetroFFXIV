"""
Retro FFXIV streaming relay.

A WebSocket hub with three jobs:
  1. Player registration: a plugin that has logged in with XIVAuth
     registers its persistent_key; the relay issues a unique player ID
     (a short code like "K7QX-4MRT") and ties it to that identity,
     persisted across restarts.  Going live under an ID that isn't tied
     to your account is rejected.
  2. Identity-based streaming: a host goes live under their player ID;
     friends who know the ID subscribe — no room codes.
  3. Netplay: lockstep input routing between two players in a room.

The relay never decodes or re-encodes — it is a dumb pipe with routing.

Configuration (environment variables):
    RELAY_HOST      Bind address     (default 0.0.0.0)
    RELAY_PORT      Listen port      (default 8765)
    RELAY_REGISTRY  Player registry file (default ./registry.json)

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
import math
import os
import secrets
import string
import tempfile
from dataclasses import dataclass, field

from fastapi import FastAPI, WebSocket, WebSocketDisconnect

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("relay")

HOST = os.environ.get("RELAY_HOST", "0.0.0.0")
PORT = int(os.environ.get("RELAY_PORT", "8765"))
REGISTRY_PATH = os.environ.get("RELAY_REGISTRY", "registry.json")

app = FastAPI(title="Retro FFXIV Relay")

CODE_ALPHABET = string.ascii_uppercase + string.digits
CODE_LENGTH = 4

# Player IDs: unambiguous alphabet (no 0/O, 1/I/L), 8 chars as XXXX-XXXX.
PLAYER_ID_ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"
PLAYER_ID_LENGTH = 8


def generate_code() -> str:
    return "".join(secrets.choice(CODE_ALPHABET) for _ in range(CODE_LENGTH))


def normalize_player_id(value: str) -> str:
    """Player IDs are letters/digits with a dash in the middle; compare
    uppercased alphanumerics only."""
    return "".join(ch for ch in value.upper() if ch.isascii() and ch.isalnum())


def format_player_id(core: str) -> str:
    if len(core) <= 3:
        return core
    split = len(core) // 2
    return f"{core[:split]}-{core[split:]}"


def generate_player_id() -> str:
    core = "".join(secrets.choice(PLAYER_ID_ALPHABET) for _ in range(PLAYER_ID_LENGTH))
    return format_player_id(core)


@dataclass
class OnlinePlayer:
    uid: str
    player_id: str          # dash-formatted ID for display
    name: str
    ws: WebSocket


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
    uid: str                # XIVAuth persistent_key
    player_id: str          # normalized player ID (digits only)
    display_id: str         # dash-formatted ID for display
    name: str               # character name for display
    host: WebSocket
    subscribers: set[WebSocket] = field(default_factory=set)
    screen: dict | None = None


netplay_rooms: dict[str, NetplayRoom] = {}
live_channels: dict[str, LiveChannel] = {}       # uid → channel
channels_by_player_id: dict[str, LiveChannel] = {}  # normalized ID → channel
live_subscriptions: dict[WebSocket, str] = {}    # subscriber ws → uid they watch
netplay_connections: dict[WebSocket, NetplayRoom] = {}

# Player registry: uid → {"player_id": "XXXX-XXXX", "name": "..."}.
# Persisted to RELAY_REGISTRY_PATH so IDs survive restarts.
registry: dict[str, dict] = {}
registry_by_id: dict[str, str] = {}  # normalized player ID → uid

# Presence: registered players with an open presence connection.
online: dict[str, OnlinePlayer] = {}  # normalized player ID → player


def load_registry() -> None:
    try:
        with open(REGISTRY_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
    except FileNotFoundError:
        return
    except Exception:
        log.exception("could not read registry %s — starting empty", REGISTRY_PATH)
        return

    for uid, entry in data.get("players", {}).items():
        pid = normalize_player_id(entry.get("player_id", ""))
        if not pid or pid in registry_by_id:
            continue
        registry[uid] = {"player_id": format_player_id(pid), "name": entry.get("name", "")}
        registry_by_id[pid] = uid
    log.info("registry loaded: %d players", len(registry))


def save_registry() -> None:
    data = {"players": registry}
    try:
        dirname = os.path.dirname(os.path.abspath(REGISTRY_PATH))
        fd, tmp = tempfile.mkstemp(dir=dirname, suffix=".tmp")
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)
        os.replace(tmp, REGISTRY_PATH)
    except Exception:
        log.exception("could not save registry to %s", REGISTRY_PATH)


def register_player(uid: str, name: str) -> str:
    """Idempotent: returns the player ID tied to this uid, issuing one if new."""
    entry = registry.get(uid)
    if entry is not None:
        if name:
            entry["name"] = name
            save_registry()
        return entry["player_id"]

    pid = normalize_player_id(generate_player_id())
    while pid in registry_by_id:
        pid = normalize_player_id(generate_player_id())

    registry[uid] = {"player_id": format_player_id(pid), "name": name}
    registry_by_id[pid] = uid
    save_registry()
    log.info("registered %s → %s (%s)", uid[:8], format_player_id(pid), name or "anonymous")
    return format_player_id(pid)


load_registry()


async def _teardown_channel(uid: str, notify: bool = True) -> None:
    channel = live_channels.pop(uid, None)
    if channel is None:
        return
    if channels_by_player_id.get(channel.player_id) is channel:
        channels_by_player_id.pop(channel.player_id, None)
    for sub in list(channel.subscribers):
        if notify:
            asyncio.create_task(_safe_send_text(sub, {"type": "live_ended", "uid": uid}))
        live_subscriptions.pop(sub, None)


def remove_connection(ws: WebSocket) -> None:
    # Presence socket?
    online_pid = next((pid for pid, p in online.items() if p.ws is ws), None)
    if online_pid is not None:
        player = online.pop(online_pid)
        log.info("online %s left (%d online)", player.player_id, len(online))

    # Check live channel subscriptions first.
    sub_uid = live_subscriptions.pop(ws, None)
    if sub_uid is not None:
        channel = live_channels.get(sub_uid)
        if channel is not None:
            channel.subscribers.discard(ws)
            log.info("live %s subscriber left (%d remaining)",
                     channel.display_id, len(channel.subscribers))
        return

    # Check live channel hosts.
    live_uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if live_uid is not None:
        channel = live_channels[live_uid]
        log.info("live %s ended (host left)", channel.display_id)
        asyncio.create_task(_teardown_channel(live_uid))
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


@app.get("/health")
async def health():
    return {
        "registered_players": len(registry),
        "netplay_rooms": len(netplay_rooms),
        "live_channels": len(live_channels),
    }


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

    if action == "presence":
        uid = msg.get("uid", "")
        player_id = normalize_player_id(msg.get("player_id", ""))
        if not uid or not player_id:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid or player_id"})
            return
        await _handle_presence(ws, uid, player_id, msg.get("name", ""))
    elif action == "list_online":
        players = [
            {
                "player_id": p.player_id,
                "name": p.name,
                "live": p.uid in live_channels,
            }
            for p in online.values()
        ]
        await _safe_send_text(ws, {"type": "online", "online": players})
    elif action == "register":
        uid = msg.get("uid", "")
        if not uid:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid"})
            return
        player_id = register_player(uid, msg.get("name", ""))
        await _safe_send_text(ws, {
            "type": "registered", "uid": uid, "player_id": player_id,
        })
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
        player_id = normalize_player_id(msg.get("player_id", ""))
        if not uid:
            await _safe_send_text(ws, {"type": "error", "message": "Missing uid"})
            return
        if not player_id:
            await _safe_send_text(ws, {"type": "error", "message": "Missing player_id"})
            return
        await _handle_go_live(
            ws, uid, player_id, msg.get("player_id", ""), msg.get("name", ""), msg.get("screen"))
    elif action == "stop_live":
        await _handle_stop_live(ws)
    elif action == "screen_state":
        await _handle_screen_state(ws, msg.get("screen"))
    elif action == "subscribe":
        player_id = normalize_player_id(msg.get("player_id", ""))
        if not player_id:
            await _safe_send_text(ws, {"type": "error", "message": "Missing player_id"})
            return
        await _handle_subscribe(ws, player_id)
    elif action == "unsubscribe":
        await _handle_unsubscribe(ws)
    elif action == "sync_check":
        ids = [normalize_player_id(k) for k in msg.get("keys", []) if isinstance(k, str)]
        await _handle_sync_check(ws, ids)
    else:
        await _safe_send_text(ws, {"type": "error", "message": f"Unknown action: {action}"})


async def _handle_create_netplay(ws: WebSocket, uid: str) -> None:
    if netplay_connections.get(ws):
        await _safe_send_text(ws, {"type": "error", "message": "Already in a room"})
        return

    code = generate_code()
    while code in netplay_rooms:
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


async def _handle_presence(ws: WebSocket, uid: str, player_id: str, name: str) -> None:
    # Only registered identities may appear in the online list.
    entry = registry.get(uid)
    if entry is None or normalize_player_id(entry["player_id"]) != player_id:
        await _safe_send_text(ws, {"type": "error", "message": "Not registered"})
        return

    online[player_id] = OnlinePlayer(
        uid=uid, player_id=entry["player_id"],
        name=name or entry.get("name", ""), ws=ws)
    log.info("online %s joined (%d online)", entry["player_id"], len(online))
    await _safe_send_text(ws, {"type": "presence_ok"})


def _validate_screen_state(screen: object) -> dict | None:
    if screen is None:
        return None
    if not isinstance(screen, dict):
        raise ValueError("screen must be an object")

    position = screen.get("position")
    width = screen.get("width")
    if not isinstance(position, list) or len(position) not in (3, 6):
        raise ValueError("screen position must contain 3 or 6 values")
    if not all(isinstance(value, (int, float)) and math.isfinite(float(value)) for value in position):
        raise ValueError("screen position contains an invalid value")
    if not isinstance(width, (int, float)) or not math.isfinite(float(width)):
        raise ValueError("screen width is invalid")
    if not 0.5 <= float(width) <= 20.0:
        raise ValueError("screen width is out of range")

    return {
        "position": [float(value) for value in position],
        "width": float(width),
    }


async def _handle_go_live(ws: WebSocket, uid: str, player_id: str,
                          display_id: str, name: str, screen: object) -> None:
    # The ID must be the one tied to this account in the registry.
    entry = registry.get(uid)
    if entry is None:
        await _safe_send_text(ws, {"type": "error", "message": "Not registered"})
        return
    if normalize_player_id(entry["player_id"]) != player_id:
        await _safe_send_text(ws, {
            "type": "error",
            "message": "Player ID is not registered to this account",
        })
        return

    try:
        initial_screen = _validate_screen_state(screen)
    except ValueError as exc:
        await _safe_send_text(ws, {"type": "error", "message": str(exc)})
        return

    # Replacing an existing channel for this uid (reconnect).
    existing = live_channels.get(uid)
    if existing is not None:
        if existing.host is ws:
            await _safe_send_text(ws, {"type": "error", "message": "Already live"})
            return
        # Reconnect: move subscribers to the new socket.
        subscribers = existing.subscribers
        live_subscriptions.update({s: uid for s in subscribers})
        existing.host = ws
        existing.name = name or existing.name
        existing.screen = initial_screen
        log.info("live %s host reconnected (%d subscribers)",
                 existing.display_id, len(subscribers))
        await _safe_send_text(ws, {
            "type": "live_started",
            "uid": uid,
            "player_id": existing.display_id,
            "subscribers": len(subscribers),
        })
        return

    # Another connection live under this player ID: replace it (stale host).
    clash = channels_by_player_id.get(player_id)
    if clash is not None:
        log.info("live %s replaced by new host", clash.display_id)
        await _teardown_channel(clash.uid)

    channel = LiveChannel(uid=uid, player_id=player_id,
                          display_id=display_id or player_id, name=name, host=ws,
                          screen=initial_screen)
    live_channels[uid] = channel
    channels_by_player_id[player_id] = channel
    log.info("live %s started (%s)", channel.display_id, name or "anonymous")
    await _safe_send_text(ws, {
        "type": "live_started", "uid": uid,
        "player_id": channel.display_id, "subscribers": 0,
    })


async def _handle_stop_live(ws: WebSocket) -> None:
    uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if uid is None:
        await _safe_send_text(ws, {"type": "error", "message": "Not live"})
        return

    display_id = live_channels[uid].display_id
    await _teardown_channel(uid)
    log.info("live %s stopped", display_id)
    await _safe_send_text(ws, {"type": "live_stopped"})


async def _handle_screen_state(ws: WebSocket, screen: object) -> None:
    uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if uid is None:
        await _safe_send_text(ws, {"type": "error", "message": "Not live"})
        return

    try:
        state = _validate_screen_state(screen)
    except ValueError as exc:
        await _safe_send_text(ws, {"type": "error", "message": str(exc)})
        return

    channel = live_channels[uid]
    channel.screen = state
    for subscriber in list(channel.subscribers):
        asyncio.create_task(_safe_send_text(subscriber, {"type": "screen_state", "screen": state}))


async def _handle_subscribe(ws: WebSocket, player_id: str) -> None:
    channel = channels_by_player_id.get(player_id)
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
    live_subscriptions[ws] = channel.uid
    log.info("live %s subscriber joined (%d total)",
             channel.display_id, len(channel.subscribers))
    await _safe_send_text(ws, {
        "type": "subscribed",
        "uid": channel.uid,
        "player_id": channel.display_id,
        "name": channel.name,
        "screen": channel.screen,
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


async def _handle_sync_check(ws: WebSocket, ids: list[str]) -> None:
    # Return which of the given player IDs are currently live.
    live = []
    seen: set[str] = set()
    for pid in ids:
        if pid in seen:
            continue
        seen.add(pid)
        channel = channels_by_player_id.get(pid)
        if channel is not None:
            live.append({
                "player_id": channel.display_id,
                "name": channel.name,
                "viewers": len(channel.subscribers),
            })
    await _safe_send_text(ws, {"type": "sync_status", "live": live})


async def _handle_binary(ws: WebSocket, data: bytes) -> None:
    # Live channel: host fans out to subscribers.
    live_uid = next((u for u, ch in live_channels.items() if ch.host is ws), None)
    if live_uid is not None:
        channel = live_channels[live_uid]
        for sub in list(channel.subscribers):
            asyncio.create_task(_safe_send_bytes(sub, data))
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
