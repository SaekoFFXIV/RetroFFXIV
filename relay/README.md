# SNES Relay

WebSocket fan-out hub for the FFXIV SNES emulator plugin. The host pushes one
H.264 + PCM stream in; the relay copies it verbatim to every spectator in the
room. No decoding, no re-encoding — a dumb pipe with room codes.

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

| Env var      | Default   | Description       |
|--------------|-----------|-------------------|
| `RELAY_HOST` | `0.0.0.0` | Bind address      |
| `RELAY_PORT` | `8765`    | Listen port       |

## Protocol

**Text (JSON) — control plane:**

| Direction          | Message                                      |
|--------------------|----------------------------------------------|
| Host → Relay       | `{"action": "create"}`                       |
| Relay → Host       | `{"type": "created", "room": "A1B2"}`        |
| Relay → Host       | `{"type": "viewers", "count": 3}`            |
| Spectator → Relay  | `{"action": "join", "room": "A1B2"}`         |
| Relay → Spectator  | `{"type": "joined", "room": "A1B2"}`         |
| Relay → Spectator  | `{"type": "closed"}`                         |
| Host → Relay       | `{"action": "close"}`                        |
| Relay → either     | `{"type": "error", "message": "..."}`        |

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
