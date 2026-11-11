# MoodyBlues Wire Protocol Spec

Protocol version **3** (`Serializer.ProtocolVersion`). Version 3 shrank `BeginSession` to 19
bytes (16-bit float ranges, Section 6.1) and `Log` to 32 bytes (Section 6.7), and spawns now
cover the clone's whole subtree (Section 6.2). Version 2 added the `Log` event. The backend
accepts version 3 only and refuses older clients.

This document describes the binary protocol streamed by the Unity client over a WebSocket
connection, plus the HTTP handshake and scene export that precede it (Section 13). It is intended
to be sufficient, on its own, to write an independent decoder for every byte the client sends.

Implementing files: `Assets/Scripts/Wire/` (`Serializer`, `SerializerDecode`,
`TransformEventDispatcher`, `RingBufferAccumulator`, `MoodyBluesStreamer`) and
`Assets/Scripts/Runtime/` (`MoodyBluesSessionManager`, `MoodyBluesMaintenanceSystem`,
`MoodyBluesRuntimeManager`, `MoodyBluesLog`).

**If you change the wire format, update this file in the same change.** The constants in
Section 12 are mechanically derived from the source; if they disagree with the code, the code is
right and this document is stale.

(This file is plain ASCII on purpose -- an earlier version of one of the source files had its doc
comments corrupted by a non-UTF-8 save, so special characters are spelled out below: "+/-" instead
of the plus-minus symbol, "deg" instead of the degree symbol.)

---

## 1. The governing model

Everything else in this document follows from two rules:

> **Transform state is polled.** Every tracked object's local position/rotation/scale is sampled
> once per capture tick, and an event is emitted only for what actually changed.
>
> **Everything else is injected.** Spawns, deletes, reparents, visibility changes and session
> boundaries are written the moment they happen, each carrying its own `TimeStamp`.

The second rule is why there is no runtime watching for those things. The client exposes
sanctioned wrappers (`MoodyBluesRuntimeManager.InstantiateObject` / `DeleteObject` /
`ObjectSetActive` / `SetObjectParent`) which perform the GameObject operation *and* emit the
matching event, so no per-frame scanning is needed to notice that they happened.

The practical consequence for a decoder: a `TimeStamp` applies to every event after it until the
next `TimeStamp`. A tick with transform changes emits one `TimeStamp` followed by that tick's
transform events. An injected event emits its own `TimeStamp` immediately before itself, so it is
correctly placed in time even though it did not happen on a tick boundary.

---

## 2. Transport

- WebSocket, binary messages only (`WebSocketMessageType.Binary`).
- The client connects to whatever `webSocketUrl` the handshake returns (Section 13.1). It is
  opaque to the client, not a fixed URL. The upgrade request carries the handshake's
  `streamToken` in the `X-MoodyBlues-Stream-Token` header (not in the URL, so it stays out of
  proxy logs). A reconnect within the same recording sends the same token.
- The event stream is **client-to-server only**. The server's only traffic back is a one-byte
  `KeepAlive` message (type 30) every 10 s, which exists because a proxy in front of the server
  (Cloudflare) resets connections whose server side stays silent, after about 60 s. The client keeps
  a receive loop pending, so `ClientWebSocket` can answer server PINGs and drain those keepalives,
  and discards anything that arrives.
- Each message is a self-contained run of zero or more back-to-back **events**. There is no outer
  header or length field: WebSocket framing delimits messages, and **a message boundary always
  falls between two whole events**, never inside one.

---

## 3. Byte order and alignment

All multi-byte fields are **little-endian**. The client asserts this at startup (`Serializer`'s
static constructor throws if `BitConverter.IsLittleEndian` is ever false), so a big-endian target
fails loudly rather than silently emitting an incompatible stream.

Fields are packed with no padding. Every event's size is exactly the sum of its field sizes.

---

## 4. Event envelope

Every event **except** those in Section 6.1 starts with a 3-byte envelope:

| Offset | Size | Field     | Type   |
|-------:|-----:|-----------|--------|
| 0      | 1    | EventType | byte   |
| 1      | 2    | ObjectID  | ushort |

`EventType` selects the payload that follows immediately. Section 6 lists every type, its id, and
its total size.

There is no length field and no reserved bits. A decoder that meets an unknown type byte cannot
skip the event, because it cannot know its length -- see Section 10.3.

---

## 5. Object IDs

- `ObjectID` is a `ushort`, assigned from a single sequence starting at **1**.
- **0 is never a valid ObjectID.** It is reserved as the ring buffer's internal padding marker
  (Section 8.3) and will never appear on the wire.
- **IDs are permanent and never reused** within a session, even after the object is destroyed. A
  decoder can treat an ObjectID as a stable key for the session's lifetime.
- The client refuses to allocate past 65535 rather than wrapping. Objects spawned after exhaustion
  are not recorded; the session continues. (A ushort wrap would land on 0, the padding marker, and
  corrupt the stream.)

IDs are assigned two ways:

1. **Initial scene walk**, once, during `Awake` (`SceneWalk`). The client depth-first-walks every
   root GameObject of the active scene and assigns the next ID to **every** Transform it visits,
   static or not. This includes the inactive prefab template instances (Section 6.6). Two
   subtrees are skipped because they exist in only one of Editor and Player, and would shift
   every later ID: the build-time scene-identity holder (Player only), and objects tagged
   `EditorOnly` (stripped from Players).
2. **Runtime spawn** (`InstantiateObject`), which assigns the clone's root and each of its
   descendants the next IDs, as one consecutive block in walk order (Section 6.2).

**The event stream never carries geometry.** A decoder is expected to hold a separate static
export of the scene (Section 13.3) that assigns the *same* IDs to the *same* objects by walking in
the same deterministic order. The stream only carries transform changes and lifecycle events for
IDs the decoder already knows.

### 5.1 A session is bound to one scene revision

IDs are **positional**: they follow walk order. Inserting, deleting, or reordering one GameObject
renumbers everything after it. A session recorded before such an edit is therefore not replayable
against the scene after it -- the IDs still decode, they simply refer to different objects now.

**This is by design, not a limitation to be worked around.** A recording describes what happened
in one specific revision of one specific scene. Pairing it with a later export is not a goal: the
protocol gains its compactness precisely from IDs being a dense positional index rather than a
durable identity, and a translation layer between revisions would be both a cost and a new source
of silently-wrong playback.

What the protocol provides instead is **detection**, not reconciliation: `sceneHash`
(Section 13.2) changes whenever the tracked object set, hierarchy, or transforms change, so a
receiver can tell that a stored export no longer matches the scene and act accordingly -- by
retaining the export each session was recorded against, or by declining to replay. What it must
not do is replay an old session against a new export and present the result as accurate.

The scene-identity holder object that the build pipeline injects into Player builds is
deliberately **excluded** from the walk and the export, so Editor and Player builds of the same
scene produce identical IDs. That is a different concern: it makes one revision self-consistent
across launch modes, rather than making two revisions interchangeable.

---

## 6. Event catalog

Every size below is the **total** event size including the envelope, unless stated otherwise.
Quantization is defined in Section 9.

### 6.1 Session maintenance events (no envelope)

These describe the stream rather than an object, so they carry no ObjectID.

#### TimeStamp -- id 24, 9 bytes

| Offset | Size | Field          | Type   |
|-------:|-----:|----------------|--------|
| 0      | 1    | EventType (24) | byte   |
| 1      | 8    | Seconds        | double |

`Seconds` is Unity's `Time.timeAsDouble`: seconds of **game** time since startup. It is affected
by `Time.timeScale` and does not advance while the game is paused, which is intentional -- a
replay should show a pause as a pause. Repeated identical timestamps therefore mean time is not
advancing, not that the stream is broken.

Applies to every following event until the next `TimeStamp`.

#### BeginSession -- id 29, 19 bytes

**The first event on every connection, including after a reconnect.** It declares the protocol
version and every tunable quantization range, so a decoder never has to assume them.

| Offset | Size | Field                       | Type    |
|-------:|-----:|-----------------------------|---------|
| 0      | 1    | EventType (29)              | byte    |
| 1      | 2    | ProtocolVersion             | ushort  |
| 3      | 2    | CaptureHz                   | float16 |
| 5      | 2    | PositionRange               | float16 |
| 7      | 2    | ScaleMin                    | float16 |
| 9      | 2    | ScaleMax                    | float16 |
| 11     | 2    | DeltaPositionRange          | float16 |
| 13     | 2    | DeltaRotationComponentRange | float16 |
| 15     | 2    | DeltaSingleAxisAngleRange   | float16 |
| 17     | 2    | DeltaScaleRange             | float16 |

`float16` is IEEE-754 half precision (`Mathf.FloatToHalf`), little-endian. It keeps about 3
significant digits and tops out at 65504, which the config inspector enforces. The client applies
the **decoded** half values to its own serializer before encoding anything, so sender and receiver
quantize with bit-identical ranges.

Versions 1 and 2 sent the same fields as 4-byte floats (35 bytes total).

A decoder **MUST** reject a stream whose `ProtocolVersion` it does not implement.

It is sent directly on the socket ahead of any buffered data, so it cannot be reordered behind
transform events that were produced while the connection was still being established.

#### KeepAlive -- id 30, 1 byte

A single type byte, preceded by its own `TimeStamp`. Emitted every
`KeepAliveIntervalSeconds` of **real** time (unscaled), whether or not there is data to send.

This exists because a decoder's inactivity timeout can only be re-armed by a fully decoded
message; WebSocket protocol-level pings do not reset it. Real time rather than game time, because
a paused game still has to hold its connection open.

#### EndSession -- id 31, 18 bytes

| Offset | Size | Field             | Type   |
|-------:|-----:|-------------------|--------|
| 0      | 1    | EventType (31)    | byte   |
| 1      | 1    | Reason            | byte   |
| 2      | 4    | DeltaPromotions   | uint32 |
| 6      | 4    | RotationWidenings | uint32 |
| 10     | 4    | HardClamps        | uint32 |
| 14     | 4    | DroppedEvents     | uint32 |

`Reason`: 0 = StoppedByHost, 1 = ApplicationQuit, 2 = SceneUnloaded, 3 = ClientError.

**Its absence is meaningful.** A stream that stops without `EndSession` was truncated -- crash,
process kill, or dropped connection -- and a decoder should record it as such rather than treating
it as a clean finish.

The counters are facts only the client can know:

- `DeltaPromotions` -- times a delta exceeded its range and an absolute event was sent instead.
  Non-zero means the configured delta ranges are tighter than the game's motion. Costs bytes, not
  correctness.
- `RotationWidenings` -- times an axis-aligned rotation was too large for the 2-byte single-axis
  encoding and used the 3-byte form.
- `HardClamps` -- times a value exceeded even the widest encoding. **Non-zero means some recorded
  transforms are wrong**; no promotion can fix it, and the absolute ranges need raising.
- `DroppedEvents` -- events discarded because the socket could not keep up (Section 8.4).

### 6.2 Lifecycle events

#### ShowObject -- id 25, 3 bytes  /  HideObject -- id 26, 3 bytes

Envelope only. The object was set active / inactive.

**These are subtree operations.** See Section 10.1.

#### InstantiateObject -- id 27, 23 bytes

The envelope's ObjectID is the **newly assigned** ID of the spawned instance.

| Offset | Size | Field            | Type                                        |
|-------:|-----:|------------------|---------------------------------------------|
| 0-2    | 3    | Envelope         | Section 4                                    |
| 3      | 2    | TemplateObjectID | ushort                                       |
| 5      | 2    | ParentObjectID   | ushort (0 = scene root)                      |
| 7      | 6    | Position         | Vector3, 3x16-bit (Section 9.3), PositionRange |
| 13     | 4    | Rotation         | Quaternion, smallest-three (Section 9.5)     |
| 17     | 6    | Scale            | Vector3, 3x16-bit, ScaleMin..ScaleMax        |

Position / Rotation / Scale use byte-identical encoding to `TrueTransform`, and are **local** to
`ParentObjectID`. This transform becomes the new object's baseline for subsequent deltas.

`TemplateObjectID` identifies the prefab template (Section 6.6) this instance was cloned from, so
the event can say "new object N is a clone of already-known template M" without re-sending
geometry.

**The event creates the whole subtree (version 3).** Let the template's subtree, walked depth
first exactly as in Section 5, be `T0` (the template itself), `T1`, ..., `Tk`. The client
registers the clone's matching nodes as ObjectIDs `N`, `N+1`, ..., `N+k`, so a decoder creates a
copy of the template's subtree and gives node `Ti` ObjectID `N+i`. Each child starts with its
template node's local transform and active state; the event itself carries only the root's.

- A child whose pose or active state already differs from its template node at spawn (a script
  changed it while the clone was created) gets a `TrueTransform` and/or `ShowObject` /
  `HideObject` straight after the `InstantiateObject`, under the same `TimeStamp`.
- If the clone's hierarchy differs from the template's (a script added or removed children in
  `Awake`), the client registers the root only, logs an error, and the next ID after `N` goes to
  whatever is registered next. A decoder cannot tell this case from the wire, which is why the
  client refuses rather than registering a block that does not line up.

The block is always consecutive: if the ID space cannot hold all `k+1` IDs, nothing is
registered and the spawn is not recorded.

#### DeleteObject -- id 28, 3 bytes

Envelope only. **This is a subtree operation** -- see Section 10.1. The ID is never reused.

#### ReparentObject -- id 32, 21 bytes

| Offset | Size | Field             | Type                     |
|-------:|-----:|-------------------|--------------------------|
| 0-2    | 3    | Envelope          | Section 4                 |
| 3      | 2    | NewParentObjectID | ushort (0 = scene root)   |
| 5      | 6    | Position          | as InstantiateObject      |
| 11     | 4    | Rotation          | as InstantiateObject      |
| 15     | 6    | Scale             | as InstantiateObject      |

Carries the full new **local** transform, not just the parent id. Unity's `SetParent` recomputes
local position/rotation/scale to preserve world placement, so the instant the parent changes a
decoder's accumulated local transform becomes meaningless -- it was relative to the old parent.
This event therefore **re-baselines** the object rather than merely announcing a hierarchy change.

A decoder **MUST** apply the transform, not only the parentage.

### 6.3 Single-property transform events

| Event | Id | Total | Encoding | Range |
|---|---|---|---|---|
| TruePosition | 1 | 9 | 3x16-bit (9.3) | +/-PositionRange |
| TrueRotation | 2 | 7 | smallest-three (9.5) | component +/-0.70711 |
| TrueRotationSingleAxis | 3 | 5 | axis+14-bit angle (9.6) | 0..360 deg |
| TrueScale | 4 | 9 | 3x16-bit (9.3) | ScaleMin..ScaleMax |
| TrueUniformScale | 5 | 5 | 1x16-bit (9.2) | ScaleMin..ScaleMax |
| DeltaPosition | 6 | 7 | packed 11/10/11 (9.7) | +/-DeltaPositionRange |
| DeltaRotation | 7 | 6 | drop-W, 3x8-bit (9.8) | component +/-DeltaRotationComponentRange |
| DeltaRotationSingleAxis | 8 | 5 | axis+14-bit angle (9.6) | +/-DeltaSingleAxisAngleRange |
| DeltaScale | 9 | 6 | 3x8-bit (9.4) | +/-DeltaScaleRange |
| DeltaUniformScale | 10 | 4 | 1x8-bit (9.4) | +/-DeltaScaleRange |

### 6.4 Combined transform events

These bundle properties that changed together so the envelope is paid once.

| Event | Id | Total | Contains |
|---|---|---|---|
| TrueTransform | 11 | 19 | Position(6) + Rotation(4) + Scale(6) |
| TruePositionRotation | 12 | 13 | Position(6) + Rotation(4) |
| TrueRotationScale | 13 | 13 | Rotation(4) + Scale(6) |
| TruePositionScale | 14 | 15 | Position(6) + Scale(6) |
| TruePositionRotationUniformScale | 15 | 15 | Position(6) + Rotation(4) + UniformScale(2) |
| TruePositionRotationSingleAxis | 16 | 11 | Position(6) + RotationSingleAxis(2) |
| DeltaTransform | 17 | 13 | Position(4) + Rotation(3) + Scale(3) |
| DeltaPositionRotation | 18 | 10 | Position(4) + Rotation(3) |
| DeltaRotationScale | 19 | 9 | Rotation(3) + Scale(3) |
| DeltaPositionScale | 20 | 10 | Position(4) + Scale(3) |
| DeltaPositionRotationSingleAxis | 21 | 9 | Position(4) + RotationSingleAxis(2) |
| DeltaPositionUniformScale | 22 | 8 | Position(4) + UniformScale(1) |
| DeltaRotationUniformScale | 23 | 7 | Rotation(3) + UniformScale(1) |

Fields appear in the order listed, back to back after the envelope.

**Applying them:** `True*` events are absolute -- they replace the named properties. `Delta*`
events are cumulative: position and scale **add**, rotation **post-multiplies**
(`state = state * delta`, Hamilton product). Properties an event does not carry are unchanged.

### 6.5 Which event is chosen

Not "first tick True, every tick after Delta". Because a decoder already has every scene object's
starting transform from the separate export (Section 5), sending a redundant absolute event the
moment polling begins would waste bytes.

**Ordinary case -- Delta, gated on actual change.** Each tracked object's position/rotation/scale
are compared against the last value *sent* for it, using the epsilons in Section 9.1. If nothing
changed, nothing is emitted for that object this tick. Otherwise the smallest combined `Delta*`
event covering exactly the changed properties is used, applying the uniform-scale and
single-axis-rotation special cases wherever they save bytes.

An ordinary scene object's first poll sends nothing at all unless it is already moving: its
baseline is what the receiver already holds after the start-of-recording snapshot (Section 8.5),
which is the scene export's pose unless the snapshot re-sent it.

**Absolute, in five situations:**

1. **Newly spawned objects** get `InstantiateObject`, which already carries a full transform.
2. **Reactivated objects.** Hidden objects are not polled, so the client cannot know whether one
   moved while hidden and cannot trust its stale baseline. On reactivation it compares the current
   transform against the last sent value and, only if they differ, emits `ShowObject` immediately
   followed by an absolute event in the same batch. If it did not move, only `ShowObject` is sent.
3. **Range overflow (promotion).** If a delta cannot be represented in its range, the whole event
   is promoted to the `True*` variant covering the same properties, rather than clamped. See
   Section 10.2 -- this is a correctness requirement, not an optimisation.
4. **Recovery from dropped events.** If the client had to discard buffered events (Section 8.4),
   the delta chain is broken for every affected object, so absolute transforms are re-sent for all
   tracked objects once the socket is healthy again.
5. **Start-of-recording snapshot.** Objects that moved since the scene loaded get one
   `TrueTransform` at the start of each recording (Section 8.5).

**Rotation has a three-rung ladder.** An axis-aligned rotation delta uses the 2-byte single-axis
form if the angle fits `DeltaSingleAxisAngleRange`; otherwise the 3-byte drop-W form (one extra
byte, reported in `RotationWidenings`); otherwise absolute. The single-axis range is the tightest
in the protocol and axis-aligned spin is the most common rotation there is, so this rung matters:
without it, a cardinal-axis spin faster than the range would be recorded at the range's ceiling
speed.

### 6.6 Prefab template library

On startup, before the initial scene walk, the client instantiates one inactive instance per
configured prefab under a `PrefabInstanceLibrary` GameObject, so each template receives a normal,
permanent ObjectID like any other scene object. Templates are never shown, moved, or otherwise
referenced on the wire -- only as the `TemplateObjectID` inside `InstantiateObject`.

A decoder needs them (as geometry) from the scene export precisely so `InstantiateObject` can
reference them. The editor-side export (Section 13.3) builds the same library the same way, so its
templates get the same ObjectIDs as in the running game.

### 6.7 Log -- id 33, 32 bytes (no envelope)

A developer log line, written with `MoodyBluesLog.Info` / `Warning` / `Error`. These are the only
events a replay viewer lists for the user; everything else drives playback. Unity's own
`Debug.Log` output is **not** forwarded.

| Offset | Size | Field          | Type                         |
|-------:|-----:|----------------|------------------------------|
| 0      | 1    | EventType (33) | byte                         |
| 1      | 1    | Style          | byte                         |
| 2      | 30   | Text           | UTF-8, zero-padded           |

Version 2 carried 33 text bytes (35 bytes total).

`Style` packs two fields:

- bits 6-7 (the top two bits): **level**. `00` = continuation of the previous log, `01` = Info,
  `10` = Warning, `11` = Error.
- bits 0-5: **colour** index. 0-15 are defined below; 16-63 are reserved and shown as gray.

| Index | Colour | Index | Colour |
|------:|--------|------:|--------|
| 0     | Gray   | 8     | Cyan   |
| 1     | White  | 9     | Sky    |
| 2     | Red    | 10    | Blue   |
| 3     | Orange | 11    | Indigo |
| 4     | Yellow | 12    | Violet |
| 5     | Lime   | 13    | Pink   |
| 6     | Green  | 14    | Rose   |
| 7     | Teal   | 15    | Brown  |

The client's `LogColor.Default` is never sent: it resolves to Gray for Info, Yellow for Warning
and Red for Error.

`Text` ends at the first zero byte, or after all 30 bytes. The client strips NUL characters from
messages so this is unambiguous.

**Long messages span several events.** The client writes one `TimeStamp`, then the first event
with the level and colour, then as many continuation events (`Style` = 0) as the text needs.
Text is split on UTF-8 character boundaries, so every event's text is valid UTF-8 on its own. A
decoder reassembles a log by appending each continuation to the log before it; a non-zero level
starts a new log. A continuation with no log before it (its first event was dropped under
backpressure) should be kept as a new Info log rather than discarded.

Limits: a single log is truncated at 16 KB of UTF-8. Logs written while nothing is recording are
dropped; logs written while a recording is starting are held and sent right after the
start-of-recording snapshot, at the snapshot's time.

---

## 7. Packet bytes

A byte-level walkthrough of a real connection, using default configuration. All values
little-endian; `|` separates fields for readability only and is not in the stream.

### 7.1 First message: BeginSession, alone

Sent immediately on connect, before anything buffered.

```
1D | 03 00 | 00 51 | D0 5F | 00 BC | 00 49 | 00 3E | 00 38 | 00 40 | 00 3C

1D      EventType 29 (BeginSession)
03 00   ProtocolVersion = 3
00 51   CaptureHz                   = 40.0   (half 0x5100)
D0 5F   PositionRange               = 500.0  (half 0x5FD0)
00 BC   ScaleMin                    = -1.0   (half 0xBC00)
00 49   ScaleMax                    = 10.0   (half 0x4900)
00 3E   DeltaPositionRange          = 1.5    (half 0x3E00)
00 38   DeltaRotationComponentRange = 0.5    (half 0x3800)
00 40   DeltaSingleAxisAngleRange   = 2.0    (half 0x4000)
00 3C   DeltaScaleRange             = 1.0    (half 0x3C00)
```

19 bytes. Every quantized value later in the stream is interpreted using these ranges.

### 7.2 A typical capture tick

One `TimeStamp` then one event per changed object. Here: game time 12.5 s, object 1 moved,
object 4660 rotated about Y by 1 degree.

```
18 | 00 00 00 00 00 00 29 40 | 06 | 01 00 | 22 CC 8F 88 | 08 | 34 12 | <2 bytes>

18                        EventType 24 (TimeStamp)
00 00 00 00 00 00 29 40   Seconds = 12.5  (IEEE-754 double)

06                        EventType 6 (DeltaPosition)
01 00                     ObjectID 1
22 CC 8F 88               packed uint32 0x888FCC22:
                            bits  0-10 = 1058 -> x = +0.0500 (11 bits over +/-1.5)
                            bits 11-20 =  505 -> y = -0.0200 (10 bits)
                            bits 21-31 = 1092 -> z = +0.1001 (11 bits)

08                        EventType 8 (DeltaRotationSingleAxis)
34 12                     ObjectID 4660
<2 bytes>                 bits 0-1 = axis (1 = Y), bits 2-15 = 14-bit angle over +/-2 deg
```

Total 9 + 7 + 5 = 21 bytes for that tick. A tick in which nothing moved emits **nothing at all**,
not even a `TimeStamp`.

### 7.3 An injected event mid-tick

A spawn does not wait for a tick boundary; it stamps its own time and follows immediately:

```
18 | <8 bytes: Seconds> | 1B | <2: new ObjectID> | <2: TemplateObjectID> |
     <2: ParentObjectID> | <6: Position> | <4: Rotation> | <6: Scale>
```

9 + 23 = 32 bytes. The `TimeStamp` is what places it correctly between two ticks.

### 7.4 Message framing

A WebSocket message is simply a run of such events concatenated. The client cuts messages at up to
`PacketSize` bytes and **only ever on an event boundary**, so a decoder can always parse a message
start to finish without buffering across messages. `PacketSize` is the config's *Message Size*:
8, 32 or 128 KB (default 8 KB).

Note that one message may contain several ticks' worth of events (the buffer coalesces), and one
very busy tick may span several messages. So "first event of a message is a `TimeStamp`" is the
common case but **not** guaranteed -- a decoder must carry its time cursor across messages.

---

## 8. Session flow, batching and flushing

### 8.1 Two independent systems

Connection upkeep and bulk data movement are separate, because "is the connection alive" and "is
there data worth sending" are different questions.

- **Maintenance** owns the connection's lifetime: connect, `BeginSession`, a `KeepAlive` every
  `KeepAliveIntervalSeconds`, reconnect with backoff, and finally `EndSession`.
- **Flush** moves buffered bytes out, and only fires once a full packet has accumulated.

Conflating them is a trap worth naming: with flushing gated solely on a full packet, a quiet scene
takes longer to fill one than a decoder's inactivity timeout, so a perfectly healthy session gets
closed as idle. The fixed-interval keepalive is what decouples the two.

### 8.2 Flush triggers

- **Buffer full** -- once `PacketSize` bytes have accumulated, drained in `PacketSize`-capped,
  event-boundary-aligned chunks, one WebSocket message per chunk.
- **Maintenance tick** -- also pushes whatever tail is pending, however small. This bounds what a
  crash or force-quit can lose to one keepalive interval rather than everything still sitting
  below the packet threshold.

All sends funnel through a single pump, because `ClientWebSocket` does not permit concurrent sends
and there are now two independent triggers.

### 8.3 The ring buffer

Events are serialized directly into a circular buffer of `max(256 KB, 4 x PacketSize)` bytes. An
event is never
allowed to straddle the end of the buffer: when one would, the leftover tail is stamped with a
**skip marker** (byte `0`) and the write cursor wraps. The reader steps over that padding.

The marker is safe precisely because **no event type is 0** (they start at 1). Padding never
reaches the wire.

### 8.4 Backpressure

If the buffer cannot accommodate an event -- socket down, or sends backing up -- the client drops
the **oldest** buffered events to make room and counts them in `DroppedEvents`. It never throws,
and never blocks the game.

Dropping has a consequence a decoder should understand: it breaks the `Delta*` chain for every
affected object. The client therefore re-sends absolute transforms for all tracked objects once
the socket recovers (Section 6.5, case 4), which re-anchors everything the gap desynchronised.

### 8.5 Recording lifecycle and the start-of-recording snapshot

The scene's ObjectIDs, capture set and scene export live for the whole time the scene is loaded.
A **recording** is one backend session on top of them, and a scene can hold several in a row.
Each recording gets a fresh handshake (new `sessionId`), a fresh connection, and its own
`BeginSession` ... `EndSession`.

**Modes** (`MoodyBluesClientConfig.RecordingMode`):

- *Start Recording on Load* -- a recording starts in `Start` and ends when the component is
  disabled or destroyed.
- *Custom Recording Control* -- nothing records until game code calls
  `MoodyBluesSessionManager.StartRecording`, and it ends at `StopRecording`. Recommended on Quest
  and mobile, calling `StopRecording` from `OnApplicationPause(true)`, because the OS may kill a
  backgrounded app without `OnApplicationQuit` running.

In both modes a recording also ends on `OnDisable`, `OnDestroy` and `OnApplicationQuit`; those
paths stop synchronously because Unity will not wait for anything asynchronous.

**Starting.** `StartRecording` handshakes, then creates the transport and writes the snapshot
below into its buffer, then connects. It has `StartTimeout` (60 s) to complete the handshake and
open the WebSocket; past that, the start fails and nothing is recorded.

**Stopping.** `StopRecording` writes `EndSession`, sends everything still buffered, and performs
the WebSocket close handshake, all without blocking. If the connection is down and reconnecting
at that moment, it waits for the reconnect (starting one if none is running) and writes
`EndSession` once connected, all within the 10 s stop timeout. Its callback reports `Completed`
only once the server has acknowledged the close; the server acknowledges only after storing every
earlier message.

**While not recording,** the capture tick only maintains the capture set: no diffing, no events.
The sanctioned wrappers in `MoodyBluesRuntimeManager` still spawn, delete, reparent and toggle
objects; they simply emit nothing.

**The snapshot.** Every session's receiver starts from the scene export, which describes the
scene as it was in `Start`. The snapshot is what takes it from there to the scene as it is when
the recording starts. It is a single `TimeStamp` (which becomes the session's time 0) followed
by, in this order:

1. `DeleteObject` for every object tracked at `Start` that no longer exists -- topmost only, as
   in Section 10.1.
2. `InstantiateObject` for every live spawn root, in ascending ObjectID order, each followed by
   its children's corrections as in Section 6.2 (children are created by their root's event and
   never get one of their own). A spawn whose parent is a later spawn is created at the scene root
   with its world transform, and reparented in step 3.
3. `ReparentObject` for those spawns, for spawn children whose parent is no longer their template
   parent, and for objects tracked at `Start` whose parent has changed. Then `DeleteObject` for
   spawn children destroyed since they spawned.
4. `TrueTransform` for objects tracked at `Start` whose local transform has changed (objects
   reparented in step 3 already carried theirs).
5. `ShowObject` / `HideObject` for objects tracked at `Start` whose active state changed, and
   `HideObject` for inactive spawn roots.

After the snapshot each object's delta baseline is what the receiver now holds: the encoded value
for objects the snapshot sent, the export pose for the rest. A recording started straight after
`Start` therefore has an empty snapshot apart from its `TimeStamp`.

The snapshot is written before the socket is open, so the ring buffer has to hold it (Section
12); a scene with more changed objects than fit falls back to the backpressure rules of Section
8.4.

---

## 9. Quantization reference

All quantization maps a float range `[min, max]` onto an unsigned integer of `bits` bits by linear
remap, round-to-nearest, and clamp; dequantization is the exact inverse remap. Lossy but
deterministic and bounded.

Ranges marked *tunable* are declared per session in `BeginSession` -- the values shown are the
defaults.

### 9.1 "Did this change" epsilons (informational)

A property is only re-sent once it moves by more than: position 0.0001 units, scale 0.0001 units,
rotation quaternion dot >= 0.999999 (about 0.08 deg). Uniform-scale detection uses 0.0005;
single-axis alignment uses 0.25 deg.

These affect *when* the client sends, not how to decode.

### 9.2 Scalar, 16-bit

`ushort`, remap `[min,max]` -> `[0, 65535]`.

### 9.3 Vector3, 3x16-bit

Each component independently per 9.2, written x then y then z.

### 9.4 Scalar / Vector3, 8-bit

`byte` per component, remap `[min,max]` -> `[0, 255]`.

### 9.5 Quaternion, smallest-three (4 bytes, absolute rotations)

1. Find the component (x/y/z/w) with the largest absolute value -- the "dropped index". If it is
   negative, negate all four components first (q and -q are the same rotation, so this forces a
   canonical hemisphere).
2. Pack a `uint32`: bits 0-1 = dropped index (0=x, 1=y, 2=z, 3=w); then the other three
   components in ascending index order, 10 bits each, over +/-0.70711.
3. To decode: read the three stored components, dequantize, then reconstruct the dropped one as
   `sqrt(max(0, 1 - sumOfSquares))` -- always non-negative, consistent with step 1.

0.70711 is `1/sqrt(2)`, the largest a non-largest component of a unit quaternion can be, so it is
already the tightest correct bound. Not tunable.

### 9.6 Rotation, single axis (2 bytes)

`ushort`: bits 0-1 = axis (0=X, 1=Y, 2=Z), bits 2-15 = 14-bit angle in degrees. Range 0..360 for
absolute rotations, +/-`DeltaSingleAxisAngleRange` (*tunable*, default 2.0) for deltas.

### 9.7 Delta position, packed 11/10/11 (4 bytes)

A `uint32`: x in bits 0-10 (11 bits), y in bits 11-20 (10 bits), z in bits 21-31 (11 bits), each
over +/-`DeltaPositionRange` (*tunable*, default 1.5).

Y gets one bit fewer because the three fields have to fit 32 bits; vertical motion is usually the
least precision-critical of the three.

### 9.8 Quaternion, drop-W (3 bytes, delta rotations)

Like 9.5 but always drops W specifically -- forcing W's hemisphere non-negative rather than the
largest component's -- and stores only x/y/z as 8-bit values over
+/-`DeltaRotationComponentRange` (*tunable*, default 0.5).

Decode: dequantize x/y/z, then `w = sqrt(max(0, 1 - x*x - y*y - z*z))`.

---

## 10. Decoder requirements

### 10.1 Show, Hide and Delete are subtree operations

In Unity, deactivating or destroying a GameObject affects its entire subtree. The protocol follows
that, and emits **one event for the topmost affected object only**. A decoder **MUST** cascade the
effect to that object's descendants.

- `DeleteObject` deletes the identified object and its whole subtree at the time of the event.
- `HideObject` hides the subtree.
- `ShowObject` shows the subtree, except descendants that are individually hidden -- which matches
  both Unity's `activeInHierarchy` and glTF/three.js visibility propagation, so the natural
  implementation is also the correct one.

Where a destroyed subtree's root is itself untracked (only possible below a spawned clone whose
hierarchy did not match its template, Section 6.2), the client emits one `DeleteObject` for each
**topmost tracked** object in that subtree.

**This requires the decoder to maintain a live hierarchy**, which is derivable from what it
already has:

- initial parentage from the scene export's node hierarchy (Section 13.3),
- `InstantiateObject.ParentObjectID` for runtime spawns, plus the template's own hierarchy for
  the spawned children,
- `ReparentObject` for every change thereafter.

The export can contain nodes without an `objectId` (UnityGLTF inserts one between a Camera or
Light node and its children, see 13.3), so a decoder should walk up from a tracked node to its nearest tracked
ancestor rather than assume the direct parent is tracked. A flat `objectId -> transform` map is
**not** sufficient; parentage must be retained.

### 10.2 Clamping versus promotion

Quantization ranges are deliberately tight, so precision is concentrated where motion actually
lives. Out-of-range values are **promoted to a wider encoding**, not clamped.

The asymmetry that forces this: clamping an *absolute* value is self-healing -- the object is
wrong for one frame and recovers. Clamping a *delta* is integrated by the decoder and never
recovered, so the object is permanently displaced. A projectile whose delta clipped for twenty
ticks ends up metres behind where it should be, forever.

`EndSession`'s `HardClamps` counter reports the one case promotion cannot fix: a value outside
even the widest absolute range.

### 10.3 Unknown event types

A decoder that meets an unrecognised type byte **cannot** skip the event: events have no length
field, so their size is only knowable from a type table. The only safe responses are to reject the
message or to abort the stream.

This is why `BeginSession` carries `ProtocolVersion` and why a decoder must check it up front:
version negotiation is the only defence available. Adding an event type is a breaking change.

### 10.4 Quaternion composition

`Delta*` rotations post-multiply using the Hamilton product: `state = state * delta`. Unity and
`System.Numerics` agree on this convention, so a .NET decoder needs no adjustment. A decoder in
another language must verify its quaternion multiply matches, and should re-normalize periodically
to shed accumulated magnitude error.

---

## 11. Precision guarantee

The reconstruction error for any property is **bounded by one quantization step of whichever
encoding carried it, independent of how long the session runs**.

This holds because the client encodes deltas against *the value it last actually transmitted* --
decoded back out of the bytes it wrote -- rather than against the true float. The sender therefore
tracks exactly what the decoder reconstructs, and per-tick rounding error cannot compound.

| Quantity | Encoding | Step size | Max per-tick change |
|---|---|---|---|
| Absolute position | 16 bits over +/-500 | 0.015259 units | n/a |
| Absolute scale | 16 bits over -1..10 | 0.00016785 units | n/a |
| Absolute rotation | 10 bits/component over +/-0.70711 | 0.001382 | n/a |
| Absolute single-axis angle | 14 bits over 0..360 | 0.021974 deg | n/a |
| Delta position x,z | 11 bits over +/-1.5 | 0.0014656 units | 1.5 units |
| Delta position y | 10 bits over +/-1.5 | 0.0029326 units | 1.5 units |
| Delta rotation component | 8 bits over +/-0.5 | 0.0039216 | about 60 deg |
| Delta single-axis angle | 14 bits over +/-2 | 0.00024416 deg | 2 deg |
| Delta scale | 8 bits over +/-1 | 0.0078431 units | 1.0 units |

At the default 40 Hz capture rate, the per-tick limits correspond to roughly 60 units/s of
translation, 2400 deg/s of rotation via drop-W, 80 deg/s via the single-axis form, and 40 units/s
of scale change. Exceeding any of them triggers promotion (Section 10.2) rather than error.

Absolute position is the one hard ceiling: nothing is wider than +/-`PositionRange`, so objects
outside it cannot be recorded correctly and are reported in `HardClamps`.

---

## 12. Constants summary

| Constant | Value | Source |
|---|---|---|
| Protocol version | 3 (the backend refuses any other) | `Serializer.ProtocolVersion` |
| Ring buffer capacity | max(256 KB, 4 x message size) | `MoodyBluesStreamer` |
| Max packet (message) size | 8, 32 or 128 KB (config Message Size; default 8 KB) | `MoodyBluesClientConfig.MessageSizeBytes` |
| Max single event size | 32 bytes (`Log`) | `Serializer.MaxEventSize` |
| BeginSession size | 19 bytes | `Serializer.BeginSessionSize` |
| Log text per event | 30 bytes | `Serializer.LogTextBytes` |
| Max log length | 16 KB UTF-8 | `MoodyBluesLog.MaxMessageBytes` |
| Capture rate | 40 Hz, configurable | `MoodyBluesSessionManager.DefaultCaptureHz` |
| Keepalive interval | 5 s real time | `MoodyBluesMaintenanceSystem.KeepAliveIntervalSeconds` |
| Start timeout (handshake + connect) | 60 s | `MoodyBluesSessionManager.StartTimeout` |
| Handshake / connect retry backoff | 1s / 2s / 4s / 8s, then 8s, until the start timeout | `MoodyBluesHandshakeClient`, `MoodyBluesMaintenanceSystem` |
| Reconnect backoff (after connecting) | 1s / 2s / 4s / 8s | `MoodyBluesMaintenanceSystem` |
| Close handshake timeout (synchronous stop) | 2 s | `MoodyBluesMaintenanceSystem.CloseTimeout` |
| Stop timeout (`StopRecording`) | 10 s | `MoodyBluesMaintenanceSystem.AsyncStopTimeout` |
| First ObjectID | 1 (0 reserved) | `MoodyBluesSessionManager` |
| Max ObjectID | 65535, then refused | `MoodyBluesSessionManager.RegisterTransform` |
| Ring buffer skip marker | byte 0 | `RingBufferAccumulator.SkipMarker` |

Capture rate is sampled in `LateUpdate` on an accumulator, at most once per frame. It runs after
animation and all `Update` calls, so it observes the transforms the frame actually rendered,
including physics interpolation. Below the configured rate the effective rate degrades to the
frame rate.

---

## 13. Handshake and scene export

Each recording (Section 8.5) starts with an HTTP handshake that returns its WebSocket URL and says
whether the backend needs a fresh scene export. The scene can also be checked and uploaded on its
own, ahead of any recording (Section 13.5).

### 13.1 Handshake

Sequence: `MoodyBluesSessionManager.Awake` walks the scene and assigns ObjectIDs (no network
access). `Start` records every tracked object's load-time state and computes the scene hash
(Section 13.2) in the same frame. It does not export the scene, except with *Always Export at
Runtime* (Section 13.3). Each `StartRecording` then sends the request below.

The component runs at `[DefaultExecutionOrder(1000)]`, which pins both moments: the walk sees the
scene after every other `Awake`, and `Start` samples transforms after every other `Start`.

```
POST {BackendBaseUrl}/handshake
Content-Type: application/json
X-MoodyBlues-Ingest-Key: mbk_...

{
  "sceneId":     "string",   // Unity's scene-asset GUID, 32 lowercase hex chars
  "sceneHash":   "string",   // 64 lowercase hex chars, SHA-256 -- Section 13.2
  "sessionId":   "string"    // fresh GUID per recording, not persisted
}
```

```
200 OK
{
  "webSocketUrl":        "ws://host:port/stream",    // opaque to the client
  "streamToken":         "string",                   // sent as a header on the upgrade (Section 2)
  "sceneUploadRequired": true,
  "sceneUploadUrl":      "http://host:port/path..."  // only when sceneUploadRequired
}
```

**Connection.** The base URL and the ingest key come from one **connection string**, set once
for the whole Unity project in *MoodyBlues > Settings* (Project Settings > MoodyBlues), stored in
`Assets/Resources/MoodyBluesProjectSettings.asset` and loaded from Resources at runtime, so
builds carry it. It is copied from the project's page in the dashboard and has the form

```
https://mbk_<key>@host[:port][/path]
```

The client splits it: `https://host[:port][/path]` is the base URL, and the user part
(`mbk_...`) is the ingest key. The URL with the key in it is never requested. Without a valid
connection string the recorder stays inert (no scene walk, no backend calls) and logs one
warning. *Test Connection* checks it (Section 13.6).

**Ingest key.** Every backend call carries the key in `X-MoodyBlues-Ingest-Key`, and the key
alone identifies the project: no developer ID is sent. The backend stores its SHA-256 (indexed,
unique) for the lookup and an encrypted copy so the dashboard can show the connection string at
any time. It allows uploads only, never reads. It ships inside builds, so anyone holding a build
can extract it; rotating it in the dashboard locks out every build that has the old key. A wrong
or missing key gets 401, which the client does not retry.

**Scene config.** The scene's `MoodyBluesClientConfig` asset, on its `MoodyBluesSessionManager`,
is optional. It carries the recording mode, scene export settings, the tunable quantization
ranges and a master `recordingEnabled` switch; without one the defaults apply. `sceneId` is
resolved via `AssetDatabase` in the Editor, or read from the build-time scene-identity holder in a
Player.

On HTTP failure the client retries with 1s/2s/4s/8s backoff (then every 8s) until the 60 s start
timeout. A 4xx response other than 408 or 429 is not retried, since the request itself is wrong.

### 13.2 Scene hash

The hash answers one question: "is the export the backend holds valid for this scene?" It is
computed without exporting anything, and combines up to three parts (`SceneHasher.Combine`):

```
sceneHash = SHA-256( "content:" + contentHash + "\n" +
                     "structure:" + structuralHash + "\n" +
                     "export:" + exportHash )            lowercase hex
```

**Content hash** (`SceneContentHash`). SHA-256 over, for every asset in
`AssetDatabase.GetDependencies(scenePath, recursive: true)` sorted by path (ordinal), the path and
`AssetDatabase.GetAssetDependencyHash(path)`, each followed by `\n`. Scripts (`.cs`, `.dll`,
`.asmdef`, `.asmref`) are left out, so a code change alone is not a new version. This catches
mesh, material, texture, shader and prefab edits that move nothing. Only the Editor can compute
it: Play mode does so live, and a build bakes it into the scene-identity holder of each scene. A
Player without a baked value logs a warning and uses an empty string.

**Structural hash** (`SceneHasher.ComputeStructuralHash`), over the same object set and order as
the initial walk, so both sides agree on ordering without communicating it:

1. UTF-8 of the active scene's name, then one `0x00`.
2. For every ObjectID in walk order:
   a. UTF-8 of the object's **hierarchy path** -- `"Root[0]/Child[2]/Leaf[0]"`, each step's name
      followed by its sibling index -- then one `0x00`.
   b. ObjectID, 2 bytes.
   c. `localPosition` 3x float32, `localRotation` 4x float32, `localScale` 3x float32.
3. SHA-256 of the buffer, lowercase hex.

The hierarchy path rather than the bare name matters: two identically named siblings that swap
order keep the same (name, id) pairs, so a name-only hash would call the scene unchanged. Objects
that cannot be resolved leave their 40-byte transform slot zeroed rather than shifting every later
field. At runtime it is sampled in `Start`, after every other `Awake` and `Start`; the editor
export samples the scene as saved, with the template library built. **A script that creates,
destroys or moves objects in `Awake` or `Start` therefore makes the running scene hash differ
from the editor export**, and such scenes need runtime export.

**Export hash.** SHA-256 of the exported `.glb`, used only with *Always Export at Runtime*, for
scenes whose meshes or materials are generated by code and can differ between runs with identical
assets. Empty otherwise.

### 13.3 Scene export (glTF)

`SceneGltfExporter` writes the scene as a self-contained `.glb`. There are three ways an export
reaches the backend, chosen per scene on its config:

- **Export Scene on Build** (default on). Before a Player build, the Editor opens each recorded
  scene in the build, builds the prefab template library exactly as
  `MoodyBluesRuntimeManager.Awake` does, assigns IDs with the same walk, exports, deletes the
  templates and restores the scenes that were open. The export is saved under
  `Library/MoodyBlues/Exports/` and uploaded if the backend lacks that version. A failure only
  warns; saved exports can be retried with *MoodyBlues > Upload Pending Exports*.
- **Export Scene on Runtime Start.** If the backend lacks the version, the running game exports
  and uploads it. Needs Read/Write on the scene's models (*MoodyBlues > Make Scene Meshes
  Readable*, also run before builds). In Editor Play mode only with *Export Scene from Editor*.
  The check starts in `Start`, and the export happens only once the backend says it is needed, so
  objects may have moved by then: the export writes every object's `Start` pose and visibility,
  and leaves out objects spawned since. If an object present at `Start` was destroyed or
  reparented in the meantime, the client does not export (the result could not describe the
  scene as it loaded) and logs a warning instead.
- **Always Export at Runtime** (under the previous setting). Exports in `Start` on every run and
  folds the export into the hash (Section 13.2).
- **By hand**, *MoodyBlues > Export This Scene / Export All Scenes*, same steps as the
  build.

If the backend lacks the version and this run may not export it, **recording carries on**: the
backend keeps those sessions waiting until that version is uploaded, then compiles them.

Export rules:

- Exactly the transforms that have an ObjectID are exported, with `ExportDisabledGameObjects =
  true` (inactive objects and the template library are part of the scene), camera-based layer
  culling off and animation export off. EditorOnly objects and the scene-identity holder are not
  exported.
- `ObjectIdExportPlugin` writes `node.extras` on every exported node:
  - `objectId` (ushort) -- **a decoder MUST key off this, never node index or name**;
  - `isHidden` (bool, only when true) -- startup visibility, which the stream deliberately never
    carries because it is static scene state;
  - `invertLook` (bool, only when true) -- the GameObject has a Camera or Light component, so
    UnityGLTF multiplied the exported node rotation by a 180 deg turn about Y (its
    `InvertDirection`). A receiver converting streamed rotations into the GLB's space must apply
    the same turn for these nodes (see 13.4).
- UnityGLTF inserts an extra node (`<name>-flipped`) between a Camera or Light node and its
  children. It has no extras.
- Meshes that cannot be exported are left off their node and counted: in a Player, meshes without
  Read/Write; at runtime, statically batched renderers (their mesh is the whole combined batch).
  The count goes to the backend in the `X-MoodyBlues-Skipped-Meshes` header. Editor exports
  read every mesh and are never batched.
- Mesh compression: UnityGLTF's `KHR_draco_mesh_compression` support is import-only, so the `.glb`
  is uncompressed at the mesh level. The upload is gzipped instead.
- Skinned meshes: UnityGLTF writes a glTF skin for a `SkinnedMeshRenderer` when every one of its
  bones is exported, which the walk guarantees (bones are ordinary Transforms with ObjectIDs, and
  are recorded like any other object). Models imported with *Optimize Game Objects* have no bone
  Transforms, so they export and replay in bind pose; *Validate Scenes* warns about them.
- Saved editor exports hold only the scene, its hash and the GLB. Uploading uses the project's
  connection at upload time, so exports saved before the project was connected, or before the key
  was rotated, still go through.

```
POST {sceneUploadUrl}
Content-Type: model/gltf-binary
Content-Encoding: gzip
X-MoodyBlues-Ingest-Key: mbk_...
X-MoodyBlues-Skipped-Meshes: 3        (only when non-zero)

<gzip-compressed .glb bytes>
```

### 13.4 Coordinate spaces

The wire stream carries **Unity** local transforms (left-handed). The exported GLB is in
**glTF** space (right-handed), converted by UnityGLTF. A receiver that animates GLB nodes with
streamed values must apply the same conversion UnityGLTF applied at export:

- position `(x, y, z)` becomes `(-x, y, z)`;
- rotation `(x, y, z, w)` becomes `(x, -y, -z, w)`, after first post-multiplying by
  `(0, -1, 0, 0)` for nodes flagged `invertLook`;
- scale is unchanged.

The rotation map is conjugation by the X axis, so it preserves quaternion products: a receiver
can keep accumulating deltas in Unity space exactly as Section 6.4 describes and convert the
result. Seeding a receiver's state from the GLB needs the inverse map (the same position and
component flips, then post-multiplying by `(0, 1, 0, 0)` for `invertLook` nodes).

### 13.5 Scene check

`MoodyBluesSessionManager.SendScene` makes sure the backend holds this scene revision without
starting a recording, for example during a loading screen:

```
POST {BackendBaseUrl}/scenes/check
Content-Type: application/json
X-MoodyBlues-Ingest-Key: mbk_...

{ "sceneId": "string", "sceneHash": "string" }
```

```
200 OK
{
  "sceneUploadRequired": true,
  "sceneUploadUrl":      "http://host:port/path..."  // only when sceneUploadRequired
}
```

Same validation, ingest key and rate limit as `/handshake`. The client uploads
(Section 13.3) only when `sceneUploadRequired` is true, and its callback reports `Uploaded`,
`AlreadyOnServer`, `NotExported` (the backend needs the version but this run may not export it)
or `Failed`. At most one check or upload runs at a time, and once the backend has confirmed it
holds the scene the in-memory export is released.

### 13.6 Connection test

*MoodyBlues > Test Connection* and the button in *MoodyBlues > Settings* send one request, with no
retries and a 15 s timeout:

```
GET {BackendBaseUrl}/connection
X-MoodyBlues-Ingest-Key: mbk_...
```

```
200 OK
{ "projectName": "string" }
```

401 means the key is missing, wrong or rotated. Any other answer (404 from something that is not
a MoodyBlues server, a network error) is reported as it is. The editor shows the result; nothing
else depends on it.

---

## 14. Rejected alternatives

Recorded so they are not re-proposed.

**Durable per-object identity (edit-time GUIDs) to survive scene edits.** Rejected on design
grounds, not cost: see Section 5.1. A recording belongs to the scene revision it was made in, and
making old sessions replayable against new exports is explicitly not a goal. A GUID scheme would
have had to be the identity model from the beginning to be coherent; bolted alongside positional
ObjectIDs it buys only a translation layer for a feature nobody wants, and introduces a new way
for playback to be confidently wrong. `sceneHash` already provides the detection that is actually
needed.

**`Transform.hasChanged` as a change filter.** Unity never resets this flag automatically, and it
is set by any operation that *could* change the matrix without comparing old and new values -- so
it yields false positives. Worse, it is shared global state any script can write: a library that
cleared it would silently break the host game's own use of it. Change detection compares values
instead.

**Clamping out-of-range deltas.** See Section 10.2. The error is permanent rather than
single-frame, so the range is widened per-event instead.

**Storing the true float as the delta baseline.** This is what produces unbounded drift: the
sender and decoder disagree a little every tick and never reconverge. The sender stores the value
it actually transmitted instead (Section 11).

**Splitting a partially-overflowing combined event** into a narrow delta plus a separate absolute
event. A single promoted `TrueTransform` is 19 bytes against 17 for the split, and the selection
logic stays something a person can follow.

**WebSocket ping/pong as keepalive.** A decoder's inactivity timer is re-armed by decoded
messages, not protocol frames, so pings would not prevent the idle-timeout failure. Keepalive has
to be an application-level event.

**Emitting `DeleteObject` per descendant.** The hierarchy is already known to the decoder from the
export plus `InstantiateObject`/`ReparentObject`, so the cascade is derivable. One event per
developer action, with the decoder deriving consequences, keeps the stream a log of *what
happened* rather than of its effects.
