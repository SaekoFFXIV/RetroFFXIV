# Retro FFXIV Relay

WebSocket hub for the RetroFFXIV emulator plugin. Streaming is
identity-based: a player who has logged in with XIVAuth **registers**
their account and the relay issues them a unique **player ID** (a short
code like `K7QX-4MRT`) tied permanently to that identity. Hosts go live
under their ID; friends subscribe by that ID — no room codes. Going live
under an ID that isn't tied to your account is rejected. The relay also
routes lockstep netplay inputs. No decoding, no re-encoding — a dumb
pipe with routing.

## Quick start (local dev)

```bash
pip install -r requirements.txt
python server.py
# → listening on 0.0.0.0:8765
```

## Docker (homeserver or VPS)

```bash
docker compose up -d
```

Binds to `127.0.0.1:8765` on the host by default (edit `docker-compose.yml` to
expose publicly, or put a reverse proxy / Cloudflare Tunnel in front).

## Cloudflare Tunnel (no open ports)

On the homeserver, after `docker compose up -d`:

```bash
cloudflared tunnel --url http://127.0.0.1:8765
```

Or for a persistent named tunnel with your own domain:

```bash
cloudflared tunnel create snes-relay
cloudflared tunnel route dns snes-relay snes.yourdomain.com
# config.yml:
#   tunnel: <TUNNEL_ID>
#   ingress:
#     - hostname: snes.yourdomain.com
#       service: http://127.0.0.1:8765
#     - service: http_status:404
cloudflared tunnel run snes-relay
```

Friends then connect to `wss://snes.yourdomain.com/ws`.

## VPS deployment

Same Docker flow — open port 8765 (or 443 with a reverse proxy) and point the
plugin's relay URL at the VPS IP/domain. No Cloudflare Tunnel needed when the
relay is on a rented VPS; the VPS IP is the only one exposed, and it is not
anyone's home address.

## Configuration

| Env var          | Default        | Description                        |
|------------------|----------------|------------------------------------|
| `RELAY_HOST`     | `0.0.0.0`      | Bind address                       |
| `RELAY_PORT`     | `8765`         | Listen port                        |
| `RELAY_REGISTRY` | `registry.json`| Player ID registry file (persisted)|

## Streaming protocol

**Text (JSON) — control plane:**

| Direction          | Message                                                                  |
|--------------------|--------------------------------------------------------------------------|
| Client → Relay     | `{"action": "register", "uid": "<persistent_key>", "name": "Character Name"}` |
| Relay → Client     | `{"type": "registered", "uid": "…", "player_id": "K7QX-4MRT"}`          |
| Host → Relay       | `{"action": "go_live", "uid": "<persistent_key>", "player_id": "K7QX-4MRT", "name": "Character Name"}` |
| Relay → Host       | `{"type": "live_started", "uid": "…", "player_id": "K7QX-4MRT", "subscribers": 0}` |
| Relay → Host       | `{"type": "viewers", "count": 3}`                                        |
| Host → Relay       | `{"action": "stop_live"}`                                               |
| Relay → Host       | `{"type": "live_stopped"}`                                              |
| Spectator → Relay  | `{"action": "subscribe", "player_id": "K7QX-4MRT"}`                     |
| Relay → Spectator  | `{"type": "subscribed", "uid": "…", "player_id": "K7QX-4MRT", "name": "…"}` |
| Spectator → Relay  | `{"action": "unsubscribe"}`                                             |
| Relay → Spectator  | `{"type": "live_ended", "uid": "…"}`                                    |
| Client → Relay     | `{"action": "sync_check", "keys": ["K7QX-4MRT", …]}`                    |
| Relay → Client     | `{"type": "sync_status", "live": [{"player_id": "K7QX-4MRT", "name": "…", "viewers": 2}]}` |
| Relay → either     | `{"type": "error", "message": "..."}`                                   |

`uid` is the XIVAuth persistent_key (channel identity, used for
reconnects). `player_id` is the short public code friends exchange.
**Registration:** `register` is idempotent — the relay generates an ID
from an unambiguous alphabet (no 0/O/1/I/L), ties it to the uid, and
persists the mapping to the registry file; re-registering returns the
same ID. `go_live` is rejected unless the ID is registered to that uid.
Player IDs compare case-insensitively on their letters/digits
(`K7QX-4MRT` == `k7qx4mrt`). Reconnecting host with the same uid
reclaims the channel and keeps its subscribers; a new host going live
under the same player ID replaces a stale one.

**Binary — data plane (host → relay → spectators):**

| Byte 0 | Payload                          |
|--------|----------------------------------|
| `0x01` | H.264 access unit (one frame)    |
| `0x02` | Audio chunk (int16 stereo PCM)   |
| `0x03` | Stream info (JSON: width, height, fps, sample_rate) |

## Netplay protocol

Lockstep input sync through the relay (no P2P). Each player runs the same
deterministic core (bsnes) with the same ROM; the relay routes inputs.

**Text (JSON) — control plane:**

| Direction          | Message                                                        |
|--------------------|----------------------------------------------------------------|
| Player → Relay     | `{"action": "create_netplay", "uid": "<uuid>"}`                |
| Relay → Player     | `{"type": "netplay_created", "room": "A1B2", "slot": 0}`      |
| Player → Relay     | `{"action": "join_netplay", "room": "A1B2", "uid": "<uuid>"}`  |
| Relay → Player     | `{"type": "netplay_joined", "room": "A1B2", "slot": 1}`        |
| Relay → All        | `{"type": "netplay_players", "players": [{"uid":"…","slot":0}, …]}` |
| Relay → All        | `{"type": "netplay_player_left", "uid": "…", "slot": 1}`       |

Each plugin instance has a persistent UUID (`PlayerUid` in config) sent as
`uid`. Reconnecting with the same UID reclaims the player's slot.

**Binary — input packets (routed to all OTHER players):**

| Offset | Size | Content                        |
|--------|------|--------------------------------|
| 0      | 1    | `0x04` (netplay input)         |
| 1      | 1    | Sender slot (0 or 1)           |
| 2      | 4    | Frame number (uint32 LE)       |
| 6      | 2    | Joypad state (uint16 LE)       |

8 bytes per frame per player. At 60fps that's ~960 B/s — negligible.
