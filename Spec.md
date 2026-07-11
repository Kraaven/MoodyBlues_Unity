# MoodyBlues Wire Protocol Spec

This document describes the binary protocol streamed by the Unity client (`BluesStreamer`,
`RingBufferAccumulator`, `Serializer`, `TransformEventDispatcher` in `Assets/Scripts/Wire`;
`BluesSessionManager`, `BluesRuntimeManager` in `Assets/Scripts/Runtime`) over a WebSocket
connection, plus the HTTP handshake and scene export that precede it (Section 9). It is intended
to be sufficient, on its own, for an independent backend implementation to decode every byte the
Unity client sends.

If you change the wire format in the Unity project, update this file in the same change.

(This file is plain ASCII on purpose -- an earlier version of one of the source files had its
doc comments corrupted by a non-UTF-8 save at some point, so special characters are spelled out
below, e.g. "+/-" instead of the plus-minus symbol and "deg" instead of the degree symbol.)

## 1. Transport

- Protocol: WebSocket, binary messages only (`WebSocketMessageType.Binary`).
- Client connects to the backend at whatever `webSocketUrl` the handshake response returns
  (Section 9.1) -- it is not a fixed/hardcoded URL, and Unity treats it as opaque.
- The client never expects to *receive* data on this connection today -- this spec only covers
  the client-to-server direction.
- Each WebSocket message is a self-contained sequence of zero or more back-to-back **Events**
  (see Section 3). There is no outer message header/length field -- the WebSocket framing itself
  delimits messages, and each message boundary always falls between two whole events, never in
  the middle of one.

## 2. Byte order and alignment

All multi-byte integer/float fields are written in the **host's native byte order**, which for
every currently-supported Unity build target (x86/x64/ARM, desktop/mobile) is **little-endian**.
The client asserts this at startup (`Serializer`'s static constructor throws if
`BitConverter.IsLittleEndian` is ever false) so a big-endian target would fail loudly instead of
silently producing an incompatible stream. Treat every field below as little-endian.

Fields are packed with no padding -- there is no struct alignment to account for, just the exact
byte counts listed per event.

## 3. Event envelope

Every event except **TimeStamp** (Section 5.1) starts with a 3-byte envelope:

| Offset | Size | Field     | Type   |
|-------:|-----:|-----------|--------|
| 0      | 1    | EventType | byte   |
| 1      | 2    | ObjectID  | ushort |

`EventType` selects which payload (if any) follows immediately after the envelope; see the event
catalog in Section 5 for each event's total size and payload layout.

## 4. Object IDs

- `ObjectID` is a `ushort` (0-65535).
- IDs are assigned by the client in a single global sequence starting at **1** and incrementing
  forever (`0` is never assigned to an object -- it is reserved internally by the client's ring
  buffer as an "empty padding" marker and will never appear as an ObjectID on the wire).
- **IDs are permanent and never reused**, even after the object they identify is permanently
  destroyed (`DeleteObject`, Section 5.1). A backend can safely treat an ObjectID as a stable,
  unique key for the lifetime of the session.
- IDs are assigned in two ways:
  1. **Initial scene walk** (once, at session start): the client depth-first-walks every root
     GameObject in the active scene and assigns the next ID to every non-`static`-flagged
     Transform it visits (it still recurses into a static object's children, since a static
     parent can have a moving child). This includes the inactive prefab template instances
     created by `BluesRuntimeManager` (see Section 5.1) -- those templates run their setup
     *before* this walk happens specifically so they get included in it.
  2. **Runtime spawn** (`InstantiateObject`, Section 5.1): each newly spawned instance gets the
     next ID at the moment it's spawned.
- **The live event stream never transmits geometry/mesh data.** The backend is expected to have
  its own static export of the scene (produced out-of-band, not over this socket) that assigns
  the *same* IDs to the *same* objects (by walking the scene in the same deterministic order).
  This stream only ever carries transform changes and lifecycle events for IDs the backend is
  assumed to already know about from that export -- including the inactive prefab templates,
  which the backend needs to know about (as meshes) precisely so `InstantiateObject` can just say
  "new object N is a clone of already-known template M" without re-sending M's geometry.

## 5. Event catalog

Every size below includes the 3-byte envelope unless stated otherwise. All quantization is
described in Section 7.

### 5.1 Session / lifecycle events

#### TimeStamp -- EventType 24 (9 bytes, **no envelope**)

The only event with no ObjectID -- it describes the stream itself, not an object.

| Offset | Size | Field   | Type   |
|-------:|-----:|---------|--------|
| 0      | 1    | EventType (24) | byte |
| 1      | 8    | Seconds | double |

`Seconds` is Unity's `Time.timeAsDouble` (seconds since the game started, monotonically
increasing for the session). The client writes exactly one `TimeStamp` event at the start of
every `FixedUpdate` tick's batch of events, before any other event for that tick, so it is always
the first event physically written for that batch. Under normal load one tick's batch fits in a
single WebSocket message, so in practice this also means: **the first event of a WebSocket
message is a `TimeStamp` event**, unless a single tick's batch was large enough to span multiple
messages, in which case only the first of those messages starts with one.

#### ShowObject -- EventType 25 (3 bytes, envelope only)

ObjectID = the object that was just set active. No payload.

#### HideObject -- EventType 26 (3 bytes, envelope only)

ObjectID = the object that was just set inactive. No payload.

#### InstantiateObject -- EventType 27 (21 bytes)

ObjectID (in the envelope) = the **newly assigned** ID of the spawned instance.

| Offset | Size | Field            | Type                                              |
|-------:|-----:|------------------|----------------------------------------------------|
| 0-2    | 3    | Envelope         | see Section 3                                       |
| 3      | 2    | TemplateObjectID | ushort                                              |
| 5      | 6    | Position         | Vector3, quantized (Section 7.4, range +/-500)      |
| 11     | 4    | Rotation         | Quaternion, smallest-three (Section 7.5, component range +/-0.70711) |
| 15     | 6    | Scale            | Vector3, quantized (Section 7.4, range -1..10)      |

(Total 21 bytes = 3 envelope + 2 TemplateObjectID + 6 position + 4 rotation + 6 scale. Position /
Rotation / Scale here use exactly the same encoding as the `TrueTransform` event, Section 5.3.)

`TemplateObjectID` is the ObjectID of the `PrefabInstanceLibrary` template (Section 5.4) this
instance was cloned from -- the backend uses it to know which already-known mesh/prefab to
instantiate.

#### DeleteObject -- EventType 28 (3 bytes, envelope only)

ObjectID = the object permanently destroyed. This ID is never reused (Section 4). No payload.

### 5.2 Single-property events

All positions below are `Vector3`; all rotations are `Quaternion`.

| Event | ID | Payload size | Total | Encoding | Range |
|---|---|---|---|---|---|
| TruePosition | 1 | 6 | 9 | 3x ushort (Section 7.4) | +/-500 |
| TrueRotation | 2 | 4 | 7 | smallest-three (Section 7.5) | component +/-0.70711 |
| TrueRotationSingleAxis | 3 | 2 | 5 | axis+angle (Section 7.6) | 0-360 deg |
| TrueScale | 4 | 6 | 9 | 3x ushort (Section 7.4) | -1..10 |
| TrueUniformScale | 5 | 2 | 5 | 1x ushort (Section 7.2) | -1..10 |
| DeltaPosition | 6 | 4 | 7 | packed x11/y10/z11 (Section 7.7) | +/-1.5 |
| DeltaRotation | 7 | 3 | 6 | drop-W, 3x byte (Section 7.8) | component +/-0.5 |
| DeltaRotationSingleAxis | 8 | 2 | 5 | axis+angle (Section 7.6) | +/-2.0 deg |
| DeltaScale | 9 | 3 | 6 | 3x byte (Section 7.3) | +/-1.0 |
| DeltaUniformScale | 10 | 1 | 4 | 1x byte (Section 7.3) | +/-1.0 |

### 5.3 Combined "hot path" events

These bundle 2-3 properties that changed together in one event so the receiver doesn't pay
envelope overhead per property.

| Event | ID | Payload size | Total | Contains |
|---|---|---|---|---|
| TrueTransform | 11 | 16 | 19 | Position(6) + Rotation(4) + Scale(6) |
| TruePositionRotation | 12 | 10 | 13 | Position(6) + Rotation(4) |
| TrueRotationScale | 13 | 10 | 13 | Rotation(4) + Scale(6) |
| TruePositionScale | 14 | 12 | 15 | Position(6) + Scale(6) |
| TruePositionRotationUniformScale | 15 | 12 | 15 | Position(6) + Rotation(4) + UniformScale(2) |
| TruePositionRotationSingleAxis | 16 | 8 | 11 | Position(6) + RotationSingleAxis(2) |
| DeltaTransform | 17 | 10 | 13 | Position(4) + Rotation(3) + Scale(3) |
| DeltaPositionRotation | 18 | 7 | 10 | Position(4) + Rotation(3) |
| DeltaRotationScale | 19 | 6 | 9 | Rotation(3) + Scale(3) |
| DeltaPositionScale | 20 | 7 | 10 | Position(4) + Scale(3) |
| DeltaPositionRotationSingleAxis | 21 | 6 | 9 | Position(4) + RotationSingleAxis(2) |
| DeltaPositionUniformScale | 22 | 5 | 8 | Position(4) + UniformScale(1) |
| DeltaRotationUniformScale | 23 | 4 | 7 | Rotation(3) + UniformScale(1) |

Fields within a combined event are always written in the order listed (e.g. `DeltaTransform` is
Position bytes, then Rotation bytes, then Scale bytes, back to back after the envelope).

**Which event gets picked, and when, is NOT simply "first tick = True, every tick after =
Delta".** Since the backend is assumed to already have every scene object's starting transform
from the separate scene export (Section 4), sending a redundant True* event the moment a scene
object starts being polled would waste bytes for no reason. Instead:

1. **Ordinary case (the vast majority of ticks): Delta, gated on "did it actually change".**
   Every tracked object's position/rotation/scale are compared to the last value *sent* for that
   object with a small epsilon (Section 7.1). If none changed, no event is sent for this object
   this tick at all. Otherwise, the smallest combined **Delta*** event that covers exactly the
   properties that changed is sent (e.g. only rotation changed -> `DeltaRotation`, or
   `DeltaRotationSingleAxis` if that rotation is aligned with a cardinal axis; position+scale
   changed but not rotation -> `DeltaPositionScale`, etc). Uniform-scale and single-axis-rotation
   special cases are applied wherever they'd save bytes. This is also what happens on an ordinary
   scene object's very first poll after session start: its "last sent" baseline is seeded from
   its actual transform at that moment (matching what the scene export already recorded), so
   nothing is sent at all unless it's already moving.
2. **True, but only when the client has no trustworthy Delta baseline to send instead:**
   - **Newly instantiated objects** don't get a generic True* event -- they get the dedicated
     `InstantiateObject` event (Section 5.1), which already carries a full true transform in its
     own payload. That event's transform becomes the new object's baseline for subsequent Deltas.
   - **Reactivated objects** (shown again via `BluesRuntimeManager.ObjectSetActive` after being
     hidden): hidden objects are never polled while inactive (see
     `BluesSessionManager.FixedUpdate`'s `activeInHierarchy` check), so the client has no way to
     know whether/how much a hidden object moved while hidden -- it can't trust its own stale
     "last sent" state to compute a correct Delta*. So immediately when `ObjectSetActive`
     reactivates one -- not deferred to the next tick, since this can be called from `Update` or
     anywhere else, same as `InstantiateObject`/`DeleteObject` -- the client checks whether the
     object's current transform actually differs from what it last sent, and only if so emits a
     `ShowObject` event immediately followed by a True* event (`TrueTransform`, 19 bytes, or
     `TruePositionRotationUniformScale`, 15 bytes, if the object's scale is uniform) in the same
     batch. If it didn't move while hidden, only `ShowObject` is sent -- no True* event follows.

### 5.4 Prefab template library

See `BluesRuntimeManager`. On startup, one inactive instance per configured prefab is created
under a `PrefabInstanceLibrary` GameObject *before* the initial scene walk (Section 4) runs, so
each template receives a normal, permanent ObjectID exactly like any other scene object.
Templates are never shown, moved, or otherwise referenced on the wire except as the
`TemplateObjectID` inside future `InstantiateObject` events that clone them.

## 6. Session-level batching and flushing

- The client accumulates events into a 24KB ring buffer (`RingBufferAccumulator`,
  `RingBufferCapacity`) as they're produced during a `FixedUpdate` tick.
- Once per tick, after all of that tick's events have been written (not per-event), the client
  drains the buffer to the socket in chunks of at most 8KB (`PacketSize`) each -- one WebSocket
  message per chunk. A chunk is only ever cut at an event boundary, never in the middle of an
  event, even if that means sending fewer than 8KB in a given message.
- If the buffer fills up mid-tick (should not happen in practice at the ~500-object scale this is
  sized for -- 24KB comfortably covers several ticks' worth of worst-case data), the client
  forces an out-of-band flush to make room before continuing to write that tick's remaining
  events, rather than dropping data or throwing.
- Practical implication for the backend: expect roughly one WebSocket message per `FixedUpdate`
  tick (Unity's default fixed timestep is 50Hz, i.e. every 20ms) containing that tick's
  `TimeStamp` event followed by zero or more object events, occasionally split into more than one
  consecutive message if an unusually large batch (e.g. many simultaneous first-tick spawns)
  exceeded 8KB.

## 7. Quantization reference

All quantization maps a float range `[min, max]` to an unsigned integer of `bits` bits via linear
remap + round + clamp, and dequantizes with the exact inverse remap (see
`Serializer.QuantizeToBits` / `DequantizeFromBits`). This is lossy but deterministic and bounded.

### 7.1 "Did this change" epsilons (informational -- doesn't affect decoding)

The client only re-sends a property once it moves by more than: position 0.0001 units, scale
0.0001 units, rotation ~0.08 deg (quaternion dot >= 0.999999), uniform-scale-detection 0.0005,
single-axis-rotation-alignment 0.25 deg. These only affect *when* the client sends an update, not
how to decode one -- included here for completeness.

### 7.2 16-bit float (used for True* position/scale)

`ushort`, remap `[min,max]` -> `[0, 65535]`.

### 7.3 8-bit float (used for Delta* scale/uniform-scale)

`byte`, remap `[min,max]` -> `[0, 255]`.

### 7.4 Vector3 -> 3x16-bit or 3x8-bit

Each component independently quantized per Section 7.2/7.3, written x then y then z.

### 7.5 Quaternion, smallest-three (True* rotation, 4 bytes)

1. Find the component (x/y/z/w) with the largest absolute value ("dropped index"); if it's
   negative, negate all 4 components first (quaternions q and -q represent the same rotation, so
   this forces a canonical hemisphere).
2. Pack a `uint32`: bits [0-1] = dropped index (0=x,1=y,2=z,3=w); then the other 3 components in
   ascending index order, each 10 bits, quantized to range +/-0.70711 (`1/sqrt(2)`, the max
   possible magnitude of a non-largest component of a unit quaternion).
3. To decode: read the 3 stored 10-bit components, dequantize, then reconstruct the dropped
   component as `sqrt(max(0, 1 - sumOfSquares))` (always non-negative, consistent with step 1's
   hemisphere normalization).

### 7.6 Rotation, single-axis (2 bytes)

`ushort`: bits [0-1] = axis id (0=X,1=Y,2=Z), bits [2-15] = 14-bit quantized angle in degrees
(range 0-360 for True*, +/-2.0 for Delta*).

### 7.7 Delta position, packed x11/y10/z11 (4 bytes)

A `uint32` with x in bits [0-10] (11 bits), y in bits [11-20] (10 bits), z in bits [21-31] (11
bits), each independently quantized to range +/-1.5 (tune to your game's actual max per-tick
displacement if this ever clips).

### 7.8 Quaternion, drop-W (Delta* rotation, 3 bytes)

Like Section 7.5 but always drops W specifically (forcing W's hemisphere non-negative rather than
the largest component's), and only stores X/Y/Z, each as an 8-bit quantized value, range +/-0.5.
Decode: dequantize x/y/z, then `w = sqrt(max(0, 1 - x*x - y*y - z*z))`.

## 8. Constants summary

| Constant | Value | Where |
|---|---|---|
| Ring buffer capacity | 24 KB | `BluesStreamer.RingBufferCapacity` |
| Max packet (message) size | 8 KB | `BluesStreamer.PacketSize` |
| Max single event size | 21 bytes (`InstantiateObject`) | `Serializer.MaxEventSize` |
| WebSocket URL | dynamic, from handshake response (Section 9.1) | `BluesStreamer`'s constructor argument |
| Handshake retries | 3 attempts, 1s/2s/4s backoff | `BluesHandshakeClient` |
| First ObjectID | 1 | `BluesSessionManager._nextObjectId` |
| Unity fixed timestep (typical) | 20ms / 50Hz | Project Time settings |

## 9. Backend handshake and scene export

Before any wire event (Section 3-6) is sent, the client performs an HTTP handshake to learn its
WebSocket URL and let the backend decide whether it needs a fresh scene export. Until the
handshake response arrives, `BluesSessionManager.FixedUpdate` is a no-op -- no data is lost,
since every tracked object's `LastSent*` stays seeded at its startup transform (Section 4), so
the first real delta after connecting just diffs against that baseline.

### 9.1 Handshake

- Sequence: `BluesSessionManager.Awake()` walks the scene and assigns ObjectIDs exactly as today
  (Section 4, no network access) -- then `Start()` calls
  `BluesHandshakeClient.PerformHandshakeAndConnect`, which builds and sends the request below.
- `developerId` and the backend's base URL come from the singleton config asset
  `BluesClientConfig` (`Assets/Scripts/Config/BluesClientConfig.cs`, asset at
  `Assets/Resources/BluesClientConfig.asset`).
- `sceneId` is Unity's own scene-asset GUID (32 lowercase hex chars) -- resolved live via
  `AssetDatabase.AssetPathToGUID` when running in the Editor, or read from a `BakedSceneId`
  component stamped into the scene at build time by `SceneIdentityBuildProcessor`
  (`IProcessSceneWithReport`) otherwise. See `Assets/Scripts/Handshake/SceneIdentity.cs`. Either
  way this needs zero manual developer bookkeeping.
- `sceneHash` is computed by `SceneHasher` -- see Section 9.2.
- `sessionId` is a fresh GUID generated per run, never persisted.

```
POST {BackendBaseUrl}/handshake
Content-Type: application/json

{
  "developerId": "string",
  "sceneId": "string",     // Unity's built-in scene-asset GUID, 32 lowercase hex chars
  "sceneHash": "string",   // 64 lowercase hex chars, SHA-256 -- see Section 9.2
  "sessionId": "string"    // fresh GUID, generated by Unity per run, not persisted
}
```

Response:

```
200 OK
{
  "webSocketUrl": "ws://host:port/path...",     // opaque to Unity -- connect as-is
  "sceneUploadRequired": true,
  "sceneUploadUrl": "http://host:port/path..."   // present only if sceneUploadRequired == true
}
```

On success the client connects `BluesStreamer` to `webSocketUrl` (see
`BluesSessionManager.AttachStreamer`). If the HTTP request itself fails (not a parse/contract
error), the client retries up to 3 attempts total with 1s/2s/4s backoff between attempts, then
gives up for the session and logs an error -- there is no further retry within that run.

### 9.2 Scene hash algorithm

Computed by `SceneHasher.ComputeSceneHash`, over the exact same object set and order as the
initial scene walk (Section 4), so both sides agree on ordering without needing to communicate it
separately:

1. UTF-8 bytes of the active scene's name, then one `0x00` separator byte.
2. For every ObjectID assigned during the initial scene walk, in assignment order: UTF-8 bytes of
   that object's GameObject name, one `0x00` separator byte, then the ObjectID as 2 bytes
   little-endian.
3. `sceneHash` = SHA-256 of the whole buffer, encoded as a 64-character lowercase hex string.

This folds in both the scene's own name and every tracked object's (name, ID) pair, so it catches
a scene rename as well as any ID-mapping drift between the client and the backend's static export.

### 9.3 Scene export (GLTF)

When the handshake response has `sceneUploadRequired == true`, the client exports the active
scene to a self-contained binary `.glb` and uploads it -- concurrently with normal event
streaming; export/upload does not block `FixedUpdate`.

- `SceneGltfExporter` (`Assets/Scripts/SceneExport/SceneGltfExporter.cs`) exports every root
  GameObject of the active scene via UnityGLTF (`org.khronos.unitygltf`), with
  `ExportDisabledGameObjects = true` so the inactive `PrefabInstanceLibrary` templates
  (Section 5.4) are included in the export.
- `ObjectIdExportPlugin` (`Assets/Scripts/SceneExport/ObjectIdExportPlugin.cs`) hooks
  `ExportContext.AfterNodeExport` to write `node.Extras = { "objectId": <ushort> }` on every glTF
  node that corresponds to a tracked Transform (i.e. has a permanent ObjectID). Nodes without a
  tracked ID have no `extras`. **The backend must key off `extras.objectId` to identify objects,
  never node array index or name.**
- Mesh compression: UnityGLTF's `KHR_draco_mesh_compression` support is import-only as of this
  writing -- there is no export-side hook (confirmed against UnityGLTF's own source/README,
  which lists Draco under "Import only"). `com.unity.cloud.draco` is installed for
  forward-compatibility, but the exported `.glb` is currently uncompressed at the mesh level.
- `SceneUploadClient` (`Assets/Scripts/SceneExport/SceneUploadClient.cs`) gzip-compresses the
  `.glb` bytes and `POST`s them to `sceneUploadUrl`:

```
POST {sceneUploadUrl}
Content-Type: model/gltf-binary
Content-Encoding: gzip

<gzip-compressed .glb bytes>
```
