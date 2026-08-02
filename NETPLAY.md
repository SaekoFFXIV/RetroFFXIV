# SNES Emulator Netplay — Roadmap

Goal: let friends **play together** in the in-game SNES emulator (not just watch). This document
outlines how, in buildable phases.

---

## How it works (the short version)

bsnes is a **deterministic** emulator: given the same ROM and the same sequence of controller
inputs, it produces the *exact* same output, every time, on every machine. Netplay exploits this.

Instead of streaming video, **every player runs the game locally** and the network only carries
**controller inputs**. As long as everyone applies the same inputs on the same frame, every
player's game stays perfectly in sync.

Consequences:
- **Cheap:** inputs are a couple of bytes per player per frame. The relay barely works.
- **Crisp:** each machine renders its own frame, so there's no video compression artifacts or
  stream latency on the picture.
- **Requirement:** everyone must run the **same core version** and the **same ROM** (hash-checked).

> Netplay (input sync, everyone plays) is different from **spectating** (one-way video stream for
> people who aren't playing). Netplay is the focus here; spectating can be layered on later by
> streaming the host's frame to non-player viewers.

---

## Two sync strategies

| | Lockstep (Phase 2 target) | Rollback (stretch goal) |
|---|---|---|
| Idea | Each frame, wait for every player's input, then advance. | Predict remote inputs, simulate ahead; if wrong, rewind + resimulate. |
| Feel | Input latency = slowest player's ping. | Near-local responsiveness over distance. |
| Complexity | Simple, rock-solid. | Needs fast save states + a lot more code. |
| Good for | Low-ping friend groups (LAN / Tailscale). | Playing over the wider internet. |

Start with **lockstep**. It's simple and, for a friend group on low ping (e.g. over Tailscale),
plays well. Add rollback later only if distance latency becomes a problem.

---

## Architecture

```
 Player A (plugin) ──┐
 Player B (plugin) ──┼──► Relay (homeserver, behind Cloudflare Tunnel) ── forwards messages only
 Player C (plugin) ──┘
```

- **Each player** runs the plugin (bsnes core + the same ROM).
- A **relay** on the homeserver, fronted by **Cloudflare Tunnel** (no port forwarding, no exposed
  IPs — the plan from the spectating design). The relay only forwards messages between room
  members; it never runs the game.
- **Rooms** are identified by a short **code**. The host creates a room; friends join with the code.

Because the relay only forwards tiny input messages, the homeserver + 1000/1000 fiber handles it
trivially.

---

## Wire protocol (messages over WebSocket)

| Message | Payload | Purpose |
|---|---|---|
| `hello` | playerName, romHash | Join a room; tell the host which ROM we have. |
| `welcome` | playerId, playerId list, room state | Server assigns an ID and introduces the room. |
| `rom_check` | romHash, ok | Host confirms everyone's ROM matches before starting. |
| `start` | frame=0, initial settings | Host starts the game for everyone simultaneously. |
| `input` | frame, playerId, buttons (ushort) | Sent every frame — the core of netplay. |
| `reset` | — | Host resets the game for everyone. |
| `state` | frame, save-state blob | Sync a late joiner, or resync after a desync. |
| `bye` | playerId | Someone left. |

The SNES joypad is 12 buttons, which already fits in a `ushort` (we pack it this way in
`InputManager`). So each `input` message is just `{ frame, playerId, ushort }`.

---

## Phases

### Phase 2a — Relay + rooms (networking foundation)
- [ ] Build the **relay server** (WebSocket) to run on the homeserver. It routes messages between
      members of a room and nothing else.
- [ ] **Cloudflare Tunnel** config so friends connect via a hostname (no port forwarding, no
      exposed IPs).
- [ ] **Room system:** create a room (get a code), join by code, assign player IDs, list members.
- [ ] **Plugin netplay client:** connect to the relay, create/join a room, show room members.
- [ ] A small netplay UI in the control deck (a "Netplay" tab): host/join, room code, player list,
      ready state.

### Phase 2b — Lockstep netplay (the actual multiplayer)
- [ ] **ROM hash verification:** before starting, the host confirms every player has the same ROM
      (reject mismatches).
- [ ] **Per-frame input exchange:** each frame, broadcast our input; collect every other player's
      input for that frame.
- [ ] **Lockstep advance:** run the core for frame N only once all players' inputs for frame N have
      arrived. The host is the clock.
- [ ] **Input delay buffer:** a 1–2 frame input delay smooths network jitter (standard for lockstep
      netplay).
- [ ] **Host-authoritative start/reset:** the host starts and resets the game for everyone.
- [ ] **Drop handling:** if a player disconnects, pause or continue with the remaining players.
- [ ] **Late join:** send a save state so a joiner catches up to the current frame (or require
      joining before the game starts — simpler first version).
- [ ] **Desync detection:** periodically compare state hashes; if they diverge, resync from the
      host's save state.

### Phase 2c — Rollback netplay (stretch goal)
- [ ] Save state every frame into a ring buffer (~10 frames).
- [ ] Predict remote inputs and simulate ahead.
- [ ] On receiving the real inputs, if a prediction was wrong, rewind to the last good state and
      resimulate.
- [ ] Tune the prediction window and input delay.

---

## Technical notes

- **Input format:** one `ushort` per player per frame (the 12-button joypad bitmask we already
  build in `InputManager`).
- **Frame timing:** the host is the clock. Frame N advances when all inputs for N are in. A small
  input delay hides jitter.
- **Save states:** bsnes exposes `retro_serialize` / `retro_unserialize`. We need to **wire these
  up** in `RetroCore` (expose `SaveState()` / `LoadState()` returning/consuming a byte[]). Needed
  for late-join sync, desync recovery, and (later) rollback. bsnes states are a few hundred KB, so
  a ~10-frame ring buffer is manageable.
- **Determinism caveats:** identical core version + identical ROM (hash-checked) on every client,
  and no nondeterministic core settings. If any client diverges, the desync-detection hash catches
  it and we resync from the host.
- **Threading:** the lockstep loop replaces the free-running emulation thread while netplay is
  active — the core advances only when the network says all inputs are ready.

---

## Risks / open questions

- **Lockstep latency over distance** — input lag equals the slowest ping. Mitigate with a low-ping
  friend group (LAN / Tailscale) and a small input delay; rollback (Phase 2c) is the real fix for
  the open internet.
- **Save-state size/rate** — a few hundred KB per state; a 10-frame ring buffer is fine, but
  serializing every frame has a CPU cost. Measure it.
- **Relay reliability** — the relay is simple (message forwarding), but if it drops, the session
  ends. Keep the relay stateless and restartable.
- **Desyncs** — rare with a deterministic core + identical ROM, but handle them (detect via state
  hash, resync from host) rather than assuming they never happen.

---

## What we already have to build on

- The **bsnes core** (deterministic) running on its own thread in `RetroCore`.
- Input already packed as a **`ushort` joypad bitmask** in `InputManager`.
- The **libretro P/Invoke layer** in `Emulation/Libretro.cs` (add `retro_serialize` /
  `retro_unserialize` bindings for save states).
- The **homeserver + Cloudflare Tunnel** plan and 1000/1000 fiber from the spectating design — the
  relay reuses that exactly.

---

## Suggested order of work

1. Wire up **save states** in `RetroCore` (`SaveState`/`LoadState`) — needed by everything later.
2. Build the **relay + rooms** (Phase 2a) and get two plugin clients exchanging a dummy message.
3. Add **ROM hash check + lockstep input exchange** (Phase 2b) — first playable netplay.
4. Polish: drop handling, late join, desync recovery.
5. **Rollback** (Phase 2c) only if distance latency demands it.
