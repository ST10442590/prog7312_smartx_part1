\# Smart-X IoT Mesh Ecosystem — Part 1



Data ingestion and validation gateway for the Smart-X hybrid IoT ecosystem.

An ASP.NET Core Minimal API receives multi-typed telemetry from a mesh of

ESP32 nodes; a Blazor WebAssembly console renders mesh health as a

\*\*Sensor Health Pulse Grid\*\*, where severity is carried by colour, motion and

opacity rather than by numbers.



\*\*Module:\*\* PROG7312 — Programming 3B 

\*\*Student:\*\* Thabang Thamae (ST10442590)



\---



\## Requirements



| Requirement | Version |

|---|---|

| .NET SDK | 10.0 or later |

| Browser | Any modern browser with WebAssembly support |



Check your SDK:



```bash

dotnet --list-sdks

```



If no `10.x` entry appears, install the .NET 10 SDK from

<https://dotnet.microsoft.com/download>.



\---



\## Quick start



```bash

git clone https://github.com/ST10442590/prog7312\_smartx\_part1.git

cd prog7312\_smartx\_part1

dotnet restore

dotnet build

```



The API and the client are \*\*separate processes\*\* and both must be running.

Open two terminals.



\*\*Terminal 1 — the gateway API:\*\*



```bash

dotnet run --project SmartX.Api

```



Listens on `http://localhost:5145`. On startup it registers 24 simulated

ESP32 nodes and begins publishing telemetry every 800 ms.



\*\*Terminal 2 — the dashboard client:\*\*



```bash

dotnet run --project SmartX.Client

```



Listens on `http://localhost:5006` and opens a browser automatically.



Stop either with `Ctrl+C`.



> \*\*Start the API first.\*\* The client polls the gateway every 900 ms and will

> show "Gateway unreachable" until the API answers. It reconnects on its own

> once the API is up — no client restart needed.



\---



\## Solution structure



```

SmartX/

├── SmartX.Shared/     Class library referenced by both other projects

│   ├── Telemetry/     TelemetryPacket<T>, SensorReading, health windows, DTOs

│   ├── Sensors/       Registration profiles, categories, units, health states

│   ├── Collections/   RingBuffer<T>, jagged-array batch buffer

│   └── Topology/      Deployment tree and recursive validator

├── SmartX.Api/        Minimal API gateway

│   └── Services/      Device registry, mesh simulator, topology builder

└── SmartX.Client/     Blazor WebAssembly console

&#x20;   ├── Components/    PulseTile

&#x20;   ├── Pages/         Dashboard, sensor registry

&#x20;   └── Services/      Typed API client

```



Domain types live in `SmartX.Shared` so that the same validation logic runs

in the browser and on the server. Registration errors a user sees while

typing are produced by the identical method the API runs on submission, so

the two can never drift apart.



\---



\## API reference



| Method | Route | Purpose |

|---|---|---|

| `GET` | `/api/health` | Liveness probe |

| `POST` | `/api/telemetry` | Ingest one packet |

| `POST` | `/api/telemetry/batch` | Ingest a buffered batch |

| `GET` | `/api/devices` | List registered sensors |

| `GET` | `/api/devices/{id}` | One sensor profile |

| `POST` | `/api/devices` | Register or update a sensor |

| `POST` | `/api/devices/{id}/attachments` | Upload a config file, photo or log |

| `GET` | `/api/devices/{id}/attachments` | List attachments |

| `GET` | `/api/mesh/health` | Pulse Grid tiles, worst first |

| `GET` | `/api/mesh/summary` | Mesh-wide counters |

| `GET` | `/api/topology/tree` | Deployment tree |

| `GET` | `/api/topology/validate` | Recursive validation report |

| `GET` | `/api/topology/path/{deviceId}` | Recursive ancestry lookup |



OpenAPI document (development only): `http://localhost:5145/openapi/v1.json`



\### Posting a packet manually



```bash

curl -X POST http://localhost:5145/api/telemetry \\

&#x20; -H "Content-Type: application/json" \\

&#x20; -d "{\\"deviceId\\":\\"ESP32-A00\\",\\"kind\\":0,\\"value\\":21.6,\\"category\\":0,\\"unit\\":1}"

```



`kind` is `0` float, `1` integer, `2` boolean.



\---



\## The engagement strategy: Sensor Health Pulse Grid



The dashboard implements \*\*pre-attentive anomaly encoding\*\*. Three visual

channels carry state simultaneously:



\- \*\*Hue\*\* encodes deviation from each node's own rolling baseline

\- \*\*A ring pulse\*\* fires when a packet arrives, so throughput is felt as motion

\- \*\*Opacity and a counter\*\* mark silence, so a dropped node grows more visually

&#x20; distinct over time rather than quietly vanishing



Scoring is per device, not global. A greenhouse humidity sensor at 85% and a

server-room sensor at 30% are both normal for where they are, so a single

global threshold would either spam false alarms or miss real ones. Each node

is instead scored by z-score — how many standard deviations its latest

reading sits from its own recent mean — which puts every channel on one

comparable scale.



| Band | Condition |

|---|---|

| Nominal | \\|z\\| < 1.5 |

| Drift | 1.5 ≤ \\|z\\| < 3 |

| Spike | \\|z\\| ≥ 3 |

| Disconnected | No packet for 30 s |



Every tile also carries an ARIA live region with the same state in words, so

the information reaches screen-reader users and not only those watching the

colour.



\---



\## Advanced OOP concepts



\### Generics — `TelemetryPacket<T>`



A reusable wrapper constrained to `where T : struct`, carrying a float for

soil moisture, an int for power wattage or a bool for a valve state.



Widening a generic value to a double is normally done with

`Convert.ToDouble(value)`, which \*\*boxes\*\*: the struct is copied to the heap,

an object header is allocated, and the GC must later collect it. At thousands

of packets per second that is a per-packet heap allocation.

`NumericProjector<T>` instead caches one delegate per closed generic type and

uses `Unsafe.As` to reinterpret the value in place, so no allocation occurs.

`ITelemetryPacket` deliberately exposes `NumericValue` as a `double` rather

than `object Value`, since the latter would reintroduce boxing wherever a

heterogeneous collection was read.



\### Operator overloading — `SensorReading`



A `readonly struct` with arithmetic defined directly on it:



```csharp

SensorReading total = meter1 + meter2;   // aggregate load

SensorReading drift = current - baseline; // delta against baseline

if (current > threshold) RaiseSpike();

```



`+` tracks a `SourceCount` so `Mean` stays correct after repeated

aggregation. Both `+` and `-` throw when units differ, because adding watts

to degrees Celsius produces a number that looks valid and means nothing.

`==` is exact structural equality, keeping it consistent with `GetHashCode`;

`ApproximatelyEquals` provides the tolerance-based comparison that floating

point usually needs.



\### Arrays — jagged and multi-dimensional



`TelemetryBatchBuffer` stages raw telemetry in a jagged `double\[]\[]` before

draining into a `List<SensorReading>` sized once to the exact capacity

needed. Jagged rather than rectangular because batch sizes vary — one zone

may send 12 readings while another sends 4,000, and a rectangular array would

pad every short batch to the longest. The zone-by-category tally alongside it

is a genuinely rectangular `int\[,]`, since every zone has the same three

category slots.



\### Recursion — `RecursiveTopologyValidator`



Walks the deployment tree (Facility → Zone → Sub-Zone → Device) verifying

tier ordering, MAC presence, and that any device requiring mains power sits

inside an ancestry that supplies it. Three guards keep it safe on deep or

malformed input: the natural leaf base case, a hard depth ceiling of 64, and

a visited set that reports cycles instead of following them forever.

`FindPath` recursively resolves a device's ancestry with backtracking on

dead-end branches.



\### Collections — `RingBuffer<T>`



A custom fixed-capacity circular buffer. `List<T>` would grow without bound

and `Queue<T>` reallocates as it grows; this buffer allocates its array once

at construction and never again, however many packets pass through. Since one

window exists per device across the whole mesh, that property is what keeps

memory flat. Enumeration uses a struct enumerator, so iterating a window

allocates nothing on the heap.



\---



\## Data seeding



`MeshSimulator` is a `BackgroundService` that registers 24 nodes across

two zones and four sub-zones — environmental, power and actuator channels —

then publishes readings every 800 ms.



Values are generated with Gaussian noise via Box-Muller so the baseline looks

like real sensor scatter rather than a uniform band. Faults are injected at

low probability: spikes of six to ten times normal noise, and dropouts where

the node simply stops publishing. Nothing announces a dropout — the dashboard

infers it from silence, which is how a real mesh behaves when a node loses

power.



The random seed is fixed, so every run produces the same sequence.



Tuning constants are at the top of `SmartX.Api/Services/MeshSimulator.cs`:



```csharp

private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(800);

private const double SpikeChance = 0.004;

private const double DropoutChance = 0.002;

```



\---



\## File upload



Config files, deployment photos and hardware logs attach to a sensor profile

from the Sensor Registry page. Accepted: `.json`, `.yaml`, `.yml`, `.txt`,

`.log`, `.csv`, `.cfg`, `.ini`, `.png`, `.jpg`, `.jpeg`, `.webp`, up to 25 MB.



The upload streams to disk with `CopyToAsync` rather than buffering the file

into a byte array, and the stored filename is \*\*generated server-side\*\* — a

client-supplied name such as `../../appsettings.json` would otherwise escape

the upload directory. A SHA-256 hash is recorded for integrity checking.



Files are written to `SmartX.Api/uploads/{deviceId}/`, which is git-ignored.



\---



\## Troubleshooting



\*\*"Gateway unreachable" on the dashboard\*\*

The API is not running, or is on a different port. Confirm the API terminal

shows `Now listening on: http://localhost:5145`.



\*\*CORS errors in the browser console\*\*

The API allows only `http://localhost:5006` and `https://localhost:7296`. If

your client started on a different port, update `WithOrigins` in

`SmartX.Api/Program.cs` to match.



\*\*Port already in use\*\*

Change `applicationUrl` in the relevant `Properties/launchSettings.json`, and

update the CORS origins if it was the client that moved.



\*\*Build fails on `Unsafe.As`\*\*

`SmartX.Shared.csproj` must contain

`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.



\---



\## Scope



Part 1 implements the first architectural pillar, Sensor Data Ingestion and

Telemetry. The other two appear in the navigation as disabled, so the

intended architecture is legible from the first screen:



\- \*\*Real-Time Command Stream and History\*\* — Part 2

\- \*\*Network Topology and Mesh Routing\*\* — final PoE



\---



\## References



Ancker, J.S., Edwards, A., Nosal, S., Hauser, D., Mauer, E. \& Kaushal, R.

2017\. Effects of workload, work complexity, and repeated alerts on alert

fatigue in a clinical decision support system. \*BMC Medical Informatics and

Decision Making\*, 17(36):1–9.



Endsley, M.R. 1995. Toward a theory of situation awareness in dynamic

systems. \*Human Factors\*, 37(1):32–64.



Healey, C.G. \& Enns, J.T. 2012. Attention and visual memory in visualization

and computer graphics. \*IEEE Transactions on Visualization and Computer

Graphics\*, 18(7):1170–1188.



Koivisto, J. \& Hamari, J. 2019. The rise of motivational information systems:

a review of gamification research. \*International Journal of Information

Management\*, 45:191–210.



Stelea, G.A., Sangeorzan, L. \& Enache-David, N. 2025. Accessible IoT

dashboard design with AI-enhanced descriptions for visually impaired users.

\*Future Internet\*, 17(7):274.



Zong, J., Lee, C., Lundgard, A., Jang, J., Hajas, D. \& Satyanarayan, A. 2022.

Rich screen reader experiences for accessible data visualization. \*Computer

Graphics Forum\*, 41(3):15–27.

