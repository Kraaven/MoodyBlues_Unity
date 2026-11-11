# MoodyBlues Unity Client

The Unity client is the capture side of MoodyBlues. A developer adds it to a scene, pastes the
project's connection string once, and plays. Recordings are streamed to the backend, and can later be scrubbed
through as a 3D replay in the browser, with no video, no game build and no Unity installed.

The client sends two things, and nothing else:

1. **The scene once**, as a GLB, and only when that exact scene revision has not been uploaded
   before.
2. **A compact binary event stream** while recording: quantized transform changes for every
   tracked object, lifecycle events (spawn, delete, show, hide, reparent), and developer logs
   written with `MoodyBluesLog`.

Companion documents: `Spec.md` (the byte-level wire protocol, which is authoritative),
`MoodyBlues_Backend/README.md` (what receives it) and
`MoodyBlues_FrontEnd/DOCUMENTATION.md` (what plays it back).

---

## 1. Where it sits

```
 This client                                Backend                              Browser
 -----------                                -------                              -------
 Awake: walk scene, assign ObjectIDs
 Start: hash scene (no export needed)
 [build / MoodyBlues menu] export GLB in Editor ▶ uploaded if the backend lacks this revision
 [optional] SendScene: POST /scenes/check ▶ "do you have this revision?"
 StartRecording: POST /handshake ─────────▶ creates the session
                    ◀── webSocketUrl, streamToken, uploadRequired?
 POST /scenes/{id} (gzip GLB) if needed ──▶ stores this revision (by hash)
   and runtime export is on
 connect WebSocket, BeginSession ─────────▶ appends to the session's raw log
 snapshot of changes since Start ─────────▶       "
 40 Hz: transform deltas ─────────────────▶       "
 spawn/delete/hide/show/reparent, logs ───▶       "
 StopRecording: EndSession ───────────────▶ compiles curves, events, logs ─────▶ replay viewer
```

The client never talks to the browser. Everything the viewer shows comes from the GLB this client
exported plus the events it streamed, which the backend compiles into a replay file.

## 2. The core idea: shared ObjectIDs

Geometry is sent once, out of band. The stream carries only small transform deltas keyed by a
16-bit **ObjectID**. Both sides agree on what each ID means because of three things:

- **Assignment.** At `Awake`, `MoodyBluesSessionManager` walks the scene depth-first
  (`SceneWalk`) and gives every Transform the next ID, starting at 1, static or not. Objects
  tagged `EditorOnly` are skipped, because Player builds strip them.
- **Export.** The GLB exporter writes that same ID into each node's `extras.objectId`. The backend
  and the viewer find nodes only by this ID, never by name or index. Editor exports use the same
  walk, so they produce the same IDs as the running game.
- **Detection.** The scene hash changes whenever the scene's content or layout does. It combines
  a content hash (Unity's dependency hashes of every asset the scene uses, scripts excluded) with
  a structural hash (hierarchy paths, IDs and local transforms at `Start`). Neither needs an
  export. A new hash means a new revision is uploaded, and old sessions keep replaying against
  the revision they were recorded in.

IDs are positional and never reused within a session. A recording belongs to one scene revision
by design (Spec 5.1).

## 3. Components and runtime flow

Add to the scene:

| Component | Role |
|---|---|
| `MoodyBluesSessionManager` | Scene walk, capture loop, the tracked set, the recording API, handshake and connection. Runs at `[DefaultExecutionOrder(1000)]`, so it sees the scene after every other `Awake`/`Start`. |
| `MoodyBluesRuntimeManager` | The sanctioned API for anything that changes the hierarchy at runtime. Holds the prefab template list. |

**Connect the project once.** Open *MoodyBlues > Settings* (the same page as Project Settings >
MoodyBlues), paste the **connection string** from the project's page in the dashboard, and click
*Test Connection*. It answers with the project's name, or says what is wrong.

- The string looks like `https://mbk_...@your-domain`: the server address with the project's
  ingest key in front. It is set for the whole Unity project, not per scene, and is saved in
  `Assets/Resources/MoodyBluesProjectSettings.asset`, which builds include automatically. Commit
  it, so everyone on the team records to the same project.
- Player builds need an `https://` server.
- The key allows uploads only, but it ships inside builds, so rotate it in the dashboard if a
  build leaks. Rotating locks out every build that has the old key; paste the new string and
  rebuild.
- Without a connection string nothing is recorded, and the scene config inspector, the build and
  *Validate Scenes* say so.

**Per-scene settings are optional.** A scene that needs different settings gets a config asset
(Assets > Create > MoodyBlues > Scene Config, `MoodyBluesClientConfig`) in the `Config` field of
its `MoodyBluesSessionManager`. Without one the defaults are used. It holds:

- `recordingEnabled`, a master switch that makes everything inert
- `recordingMode`: *Start Recording on Load* or *Custom Recording Control* (Section 4)
- `messageSize`: the largest WebSocket message, 8, 32 or 128 KB
- the **Scene Export** settings (Section 5):
  - *Export Scene on Build* (default on)
  - *Export Scene on Runtime Start*, with *Always Export at Runtime* under it
  - *Export Scene from Editor*: lets the runtime export also run in Play mode
  - turning both export settings off shows a dialog: the scene then has to be exported by hand
- the tunable quantization ranges, hidden behind a "Quantization Ranges" foldout with a
  "Reset to defaults" button. They are sent as 16-bit floats, so they are rounded to about 3
  significant digits and capped at 65504.

What happens when the game runs:

1. **Prefab library.** `RuntimeManager.Awake` instantiates one inactive copy of every listed prefab
   under `PrefabInstanceLibrary`, so each template gets a normal ObjectID. These are what
   spawns are cloned from, in the game and in the viewer.
2. **Scene walk** (`SessionManager.Awake`) assigns IDs.
3. **Load state** (`Start`). In one frame the client records every tracked object's pose, parent
   and active state, and computes the scene hash. Nothing is exported here, unless the config
   has *Always Export at Runtime*.
4. **Recording start** (`StartRecording`, automatic in *Start Recording on Load*). Sends
   `POST /handshake` with `{sceneId (scene asset GUID), sceneHash, sessionId}` and
   the `X-MoodyBlues-Ingest-Key` header, retrying with 1, 2, 4 and 8 s backoff. A 401 (wrong or
   missing key) is not retried. The handshake and the WebSocket connect together have
   60 s; past that the start fails.
5. **Scene upload**, only when the backend lacks this revision and runtime export is on (Section
   5). Otherwise the client logs a warning and keeps recording; the backend holds those sessions
   until the revision is uploaded.
6. **Snapshot.** The first events of each recording describe every change since `Start`: deleted
   objects, live spawns, reparents, moved objects and visibility changes. That makes a recording
   started late, or a second recording in the same scene, replay correctly (Spec 8.5).
7. **Streaming** (`Wire/MoodyBluesStreamer`, `Runtime/MoodyBluesMaintenanceSystem`).
   - **Connect.** The upgrade request carries the handshake's stream token in the
     `X-MoodyBlues-Stream-Token` header. `BeginSession` (19 bytes) goes first, carrying the
     protocol version, capture rate and every quantization range.
   - **Capture.** At 40 Hz in `LateUpdate`, a Burst job diffs every tracked transform against the
     last value *sent* and emits the smallest event that covers the change.
   - **Lifecycle.** Lifecycle events and logs are written the moment they happen, each with its
     own timestamp.
   - **Keepalive.** A `KeepAlive` goes out every 5 s of real time.
   - **Reconnect.** Backoff of 1, 2, 4 and 8 s, reusing the same URL and token. The backend
     appends to the same session.
   - **End.** `EndSession` reports the reason and four counters: promotions, rotation widenings,
     hard clamps and dropped events.

While nothing is recording, the capture tick only maintains the tracked set, and the runtime
wrappers below still work but emit nothing.

## 4. What developers must call

Transform changes are picked up automatically. Hierarchy changes are not, because the client
does not scan for them. Use `MoodyBluesRuntimeManager.Instance`:

| Call | Instead of | Emits |
|---|---|---|
| `InstantiateObject("PrefabName", pos, rot, scale, parent, startActive)` | `Instantiate` | `InstantiateObject` (template id, parent, full local transform). The clone's root and all its children are tracked, as consecutive IDs; the viewer builds the children from the template. |
| `DeleteObject(go)` | `Destroy` | `DeleteObject` for the topmost tracked objects. The backend deletes their subtrees. |
| `ObjectSetActive(go, active)` | `SetActive` | `ShowObject` / `HideObject`, plus an absolute transform if it moved while hidden |
| `SetObjectParent(go, parent, worldPositionStays)` | `SetParent` | `ReparentObject` with the new local transform |

In the Editor, a parent change that bypasses `SetObjectParent` logs a warning, because it would
otherwise replay wrong with no visible symptom.

**Recording control** (`MoodyBluesSessionManager.Instance`). In *Start Recording on Load* mode
none of this is needed. In *Custom Recording Control* mode, which Quest and mobile builds should
use:

| Call | Does | Callback |
|---|---|---|
| `SendScene(onDone)` | Uploads the scene only if the backend's copy is missing or stale. Optional; `StartRecording` does the same check. Useful during a loading screen. | `SceneSendResult`: `Uploaded`, `AlreadyOnServer`, `NotExported` (needed, but this run may not export it), `Failed` |
| `StartRecording(onStarted)` | Starts a new backend session. | `true` once connected; `false` if the backend was not reached within 60 s, the start was cancelled, or a recording is already running |
| `StopRecording(onDone)` | Writes `EndSession`, sends the rest, closes. Never blocks. | `StopResult`: `Completed` (the server stored everything), `Incomplete`, `NotRecording` |
| `IsRecording` | True from `StartRecording` until the recording stops or fails to start. | |

On Quest, call `StopRecording` from `OnApplicationPause(true)`: the OS can kill a backgrounded app
without `OnApplicationQuit` running. In both modes a recording also ends when the component is
disabled or destroyed and on quit; those paths stop synchronously, blocking for at most 2 s.
`StopRecording` during a reconnect waits for the connection to come back (up to 10 s) so the
recording still ends cleanly.

**Logs** (`MoodyBluesLog`). These are the only events the replay viewer lists for the user:

```csharp
MoodyBluesLog.Info("Checkpoint reached");
MoodyBluesLog.Warning("Low health");                     // yellow by default
MoodyBluesLog.Error("Fell out of the world", LogColor.Violet);
```

Info defaults to gray, Warning to yellow, Error to red; the optional `LogColor` overrides it
(16 colours: Gray, White, Red, Orange, Yellow, Lime, Green, Teal, Cyan, Sky, Blue, Indigo, Violet,
Pink, Rose, Brown). Safe from any thread. Logs are dropped while nothing is recording; a log is
capped at 16 KB. `Debug.Log` output is not forwarded.

## 5. Scene export

The backend can only replay a session once it has the GLB of the scene revision it was recorded
in. Each config chooses how that GLB gets there:

| Setting | When it exports | Cost |
|---|---|---|
| *Export Scene on Build* (default) | Before every Player build, in the Editor, for each recorded scene in the build. Uploaded if the backend lacks it. | Build time only. No Read/Write needed. |
| *Export Scene on Runtime Start* | In the running game, only when the backend lacks this revision. Play mode too with *Export Scene from Editor*. | Read/Write on the scene's models, which keeps a CPU copy of every mesh (roughly double mesh memory on Quest). Statically batched meshes cannot be exported at runtime. |
| *Always Export at Runtime* | In `Start` on every load, folded into the scene hash. For scenes that generate meshes or materials in code. | An export on every load. |
| Neither | Never. Export by hand from the MoodyBlues menu. | |

The editor export opens the scene as saved, builds the prefab template library the same way the
game does, exports, removes the templates and reopens the scenes you had. The GLB is saved to
`Library/MoodyBlues/Exports/` until the backend confirms it has that revision. A failed export or
upload only logs a warning; it never fails the build.

A runtime export describes the scene as it was at `Start`: poses and visibility are written from
the load state, and spawned objects are left out. If an object present at `Start` has since been
destroyed or reparented, the client cannot export the scene as it loaded, so it logs a warning
and keeps recording.

**The MoodyBlues menu** (the same tools are buttons in *MoodyBlues > Settings*):

| Item | Does |
|---|---|
| Settings... | Opens Project Settings > MoodyBlues: the connection string, Test Connection, and these tools as buttons. |
| Test Connection | Asks the server which project the connection string belongs to (`GET /connection`), and shows the answer. |
| Export This Scene | Exports and uploads the active scene. |
| Export All Scenes | Every scene under `Assets/` with a `MoodyBluesSessionManager`. |
| Upload Pending Exports | Retries saved exports the backend does not have yet. |
| Make Scene Meshes Readable | Enables Read/Write on the models used by scenes with runtime export, after showing the memory it adds. Builds do this automatically. |
| Validate Scenes | Lists a missing or invalid connection string, models imported with *Optimize Game Objects* (their bones are not recorded), renderers whose material count differs from their submesh count (UnityGLTF then maps materials differently from Unity), models a runtime export cannot read, Batching Static objects in runtime-export scenes, and scenes with export switched off. |

**When the hash at runtime differs from the editor export.** The structural hash is sampled after
every `Awake` and `Start`. A script that creates, destroys or moves objects there makes the
running scene differ from the saved scene the Editor exported, so the backend never sees a
matching upload from the build. Use runtime export for such scenes.

## 6. Accuracy guarantees the backend relies on

- **No drift.** Each delta is encoded against the value the client last *transmitted*, decoded
  back out of its own bytes (`SerializerDecode.TryApplyTransformEvent`). The backend uses the same
  arithmetic, so error stays within one quantization step however long the session runs
  (Spec 11).
- **No silent clipping.** A delta that does not fit its range is promoted to an absolute event
  instead of being clamped. Only a value beyond the absolute range is clamped; that is counted in
  `HardClamps`, and the dashboard shows a warning.
- **Coordinate spaces.** The stream carries Unity local transforms (left-handed); the GLB is glTF
  (right-handed). The backend converts the same way UnityGLTF does: X position and the Y/Z
  rotation components are negated, and nodes flagged `invertLook` first get UnityGLTF's 180° turn
  about Y. That flag exists because UnityGLTF flips any GameObject with a Camera or Light
  component, and the receiver cannot reliably infer that from the GLB alone (Spec 13.4).

## 7. Code layout

```
Assets/Scripts/
  Config/MoodyBluesClientConfig.cs        optional per-scene config: mode, message size, export settings, ranges
  Config/MoodyBluesProjectSettings.cs     project-wide connection string (Resources asset) and its parser
  Handshake/                              handshake / scene-check client and DTOs, SceneHasher, SceneContentHash,
                                          SceneIdentity (scene GUID, baked holder)
  Runtime/
    MoodyBluesSessionManager.cs           scene walk, 40 Hz capture, tracked set, subtree registration
    MoodyBluesSessionManager.Recording.cs recording API, start snapshot, scene check / runtime export / upload
    SceneWalk.cs                          the one ObjectID walk, shared with the editor export
    MoodyBluesLog.cs                      developer log API (LogLevel, LogColor)
    MoodyBluesSessionExportUtility.cs     node extras: isHidden, invertLook
    MoodyBluesRuntimeManager.cs           Instantiate / Delete / SetActive / SetParent wrappers, template library
    MoodyBluesMaintenanceSystem.cs        connect, KeepAlive, reconnect, EndSession
    TransformDiffJob.cs, TransformTracking.cs   Burst diff job and tracked-state structs
  Wire/                                   Serializer (33 event types), decode-feedback, event selection,
                                          ring buffer, WebSocket streamer
  SceneExport/                            UnityGLTF export, objectId plugin, gzip upload
  Editor/
    SceneIdentityBuildProcessor.cs        bakes the scene GUID and content hash into Player builds
    SceneContentHashEditor.cs             content hash; registered for Play mode
    MoodyBluesSceneExport.cs              editor export, saved exports, upload
    MoodyBluesBuildExport.cs              pre-build: Read/Write, export, upload
    MoodyBluesMeshReadability.cs          Read/Write tool and memory estimate
    MoodyBluesMenu.cs                     the MoodyBlues menu
    MoodyBluesSettingsProvider.cs         Project Settings > MoodyBlues: connection, Test Connection, tool buttons
    MoodyBluesClientConfigEditor.cs       config inspector
  Testing/                                BallsSpawner, CubeMovement, SampleSceneController samples
Assets/Tests/EditMode/                    drift, round-trip, event-selection and ring-buffer tests
Spec.md                                   the wire protocol
```

## 8. Known gaps

- **Skinned meshes.** Bones are tracked and recorded like any other transform, but replaying
  them as a deforming mesh is not supported yet (planned for the next iteration).
- **Reconnects.** These work across short network drops, within the 15 s backoff. If the client
  gives up, the rest of that recording is lost; a new `StartRecording` starts a fresh one.
- **Spawned children reparented out of a clone** whose root is then destroyed are missing from
  the snapshot of a later recording, because the receiver could only create them through the
  root.
- **Snapshot size.** The snapshot is buffered before the socket opens. The 256 KB buffer holds
  roughly 13,000 changed objects; beyond that the oldest events are dropped.
- **EditMode tests.** These have been compiled but have never been run (see ProjectState.md).
- **Optimized rigs.** Models imported with *Optimize Game Objects* have no bone Transforms, so
  they replay in bind pose. Other skinned meshes are recorded through their bones, which are
  ordinary tracked Transforms (every bone at 40 Hz; only moving bones cost bytes).
- **The ingest key is in every build.** Anyone with a build can extract it and upload junk
  recordings to the project. It cannot read anything. Rotate it from the dashboard if that
  happens.
