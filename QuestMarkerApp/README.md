# Quest Marker App 🥽

Εφαρμογή Unity για Meta Quest 3 που λαμβάνει συντεταγμένες X, Y, Z μέσω API κλήσεων και εμφανίζει markers στον χώρο VR/AR.

## Χαρακτηριστικά

- 📍 **Real-time Markers**: Εμφάνιση 3D markers σε πραγματικό χρόνο
- 🌐 **HTTP API**: Λήψη συντεταγμένων μέσω REST API (POST requests)
- 🎯 **Spatial Anchors**: Σταθερός προσανατολισμός στον χώρο
- 🎨 **Customizable**: Ρυθμιζόμενα χρώματα, labels και μεγέθη
- 📱 **Cross-platform**: Αποστολή δεδομένων από οποιαδήποτε συσκευή στο δίκτυο

## Απαιτήσεις

### Unity
- Unity 2022.3 LTS ή νεότερη
- Meta XR All-in-One SDK
- TextMeshPro

### Hardware
- Meta Quest 3 (ή Quest 2/Pro)
- PC για development
- Ίδιο WiFi δίκτυο μεταξύ Quest και server

## Οδηγίες Εγκατάστασης

### 1. Ρύθμιση Unity Project

```bash
# Άνοιγμα του project στο Unity Hub
# File -> Open Project -> QuestMarkerApp
```

### 2. Εγκατάσταση Meta XR SDK

1. Άνοιξε το Unity
2. **Window → Package Manager**
3. Κλικ στο **+** → **Add package by name**
4. Πρόσθεσε: `com.meta.xr.sdk.all`
5. Περίμενε να ολοκληρωθεί η εγκατάσταση

### 3. Ρύθμιση για Android/Quest

1. **File → Build Settings**
2. Επίλεξε **Android**
3. Κλικ **Switch Platform**
4. Ρυθμίσεις:
   - Texture Compression: ASTC
   - Minimum API Level: 29
   - Target API Level: 32
   - Scripting Backend: IL2CPP
   - Target Architectures: ARM64

### 4. Ρύθμιση XR

1. **Edit → Project Settings → XR Plug-in Management**
2. Tab Android: Ενεργοποίησε **Oculus**
3. **Oculus → Quest Features**:
   - Target Devices: Quest 3
   - Hand Tracking: Optional
   - Passthrough: Required

### 5. Δημιουργία Scene

1. **File → New Scene**
2. Διάγραψε το Main Camera
3. **Right-click Hierarchy → XR → OVR Camera Rig**
4. Δημιούργησε Empty GameObject: **"MarkerAppManager"**
5. Πρόσθεσε τα components:
   - `MarkerAppController`
   - `LocalAPIServer`
   - `MarkerManager`
   - `SpatialAnchorManager`
   - `UIManager`

### 6. Build και Deploy

```bash
# Σύνδεσε το Quest με USB
# Build Settings → Build and Run
```

## 📱 Mobile Preview (Android κινητό)

Για γρήγορη προεπισκόπηση **πώς φαίνεται η εφαρμογή σε κανονικό Android κινητό**
(χωρίς Meta Quest / VR headset), υπάρχει ξεχωριστό mode με **απλή 3D κάμερα**.

> Η Quest υλοποίηση παραμένει ανέπαφη. Το mobile mode ενεργοποιείται μέσω του
> scripting define `MOBILE_PREVIEW` και **δεν χρειάζεται το Meta XR SDK** για compile.

### Τι περιλαμβάνει

- 🎥 **MobileCameraController** — orbit / pan / zoom κάμερα
  - 1 δάχτυλο drag: περιστροφή
  - 2 δάχτυλα: pan / pinch zoom
  - (Editor: αριστερό κλικ = περιστροφή, δεξί = pan, scroll = zoom)
- 🟦 **GroundGrid** — grid εδάφους για αίσθηση κλίμακας (1 κελί = 1 μέτρο, άξονας X = κόκκινος, Z = μπλε)
- 🎯 **MobilePreviewController** — αγκυρώνει τα markers στο world origin, δημιουργεί demo markers και εμφανίζει on-screen κουμπιά (Demo / Clear / Info)
- Ο `LocalAPIServer` εξακολουθεί να τρέχει, οπότε μπορείς να στέλνεις και **πραγματικά coordinates** από άλλη συσκευή στο ίδιο δίκτυο (ίδιο API με το Quest).

### Ρύθμιση (μέσω Unity Editor menu)

1. **Quest Marker App → Mobile Preview → 1. Enable MOBILE_PREVIEW Define**
   (κάνει recompile· απενεργοποιεί τα OVR calls)
2. **Quest Marker App → Mobile Preview → 2. Setup Mobile Preview Scene**
   (δημιουργεί κάμερα + ground grid + manager + demo markers)
3. Πάτα **Play** για άμεσο preview στον Editor.

### Build για κινητό

1. **File → Build Settings → Android** → *Switch Platform*
2. **Edit → Project Settings → XR Plug-in Management**: στο tab **Android**
   **απενεργοποίησε** το Oculus / OpenXR (δεν χρειάζεται XR για απλό κινητό)
3. Σύνδεσε το κινητό (USB debugging ενεργό) → **Build and Run**

### Επιστροφή στο Quest

**Quest Marker App → Mobile Preview → Disable MOBILE_PREVIEW Define (back to Quest)**
και ξαναενεργοποίησε το Oculus στο XR Plug-in Management.

### Keyboard shortcuts (Editor / mobile build)

| Πλήκτρο | Λειτουργία |
|---------|------------|
| **C** | Καθαρισμός markers |
| **R** | Επαναβαθμονόμηση σημείου αναφοράς |
| **X** | Έναρξη calibration mode |

> ⚠️ Το mobile preview δείχνει τους markers σε **εικονικό 3D χώρο** (όχι AR/passthrough).
> Είναι για αξιολόγηση εμφάνισης & ροής — για AR στο κινητό θα χρειαζόταν AR Foundation.

## Χρήση API

### Endpoint: POST /api/coordinates

Στείλε συντεταγμένες στο Quest με HTTP POST request.

**URL**: `http://<QUEST_IP>:8080/api/coordinates`

### JSON Format

```json
{
  "coordinates": [
    {
      "id": "point1",
      "x": 1.0,
      "y": 0.5,
      "z": 2.0,
      "label": "Σημείο 1",
      "color": "#FF0000",
      "scale": 0.1
    },
    {
      "id": "point2",
      "x": -1.0,
      "y": 0.0,
      "z": 3.0,
      "label": "Σημείο 2",
      "color": "#00FF00",
      "scale": 0.15
    }
  ],
  "clearExisting": false
}
```

### Παράμετροι Coordinate

| Παράμετρος | Τύπος | Περιγραφή |
|------------|-------|-----------|
| `id` | string | Μοναδικό αναγνωριστικό (αυτόματο αν λείπει) |
| `x` | float | Συντεταγμένη X (μέτρα) |
| `y` | float | Συντεταγμένη Y - ύψος (μέτρα) |
| `z` | float | Συντεταγμένη Z (μέτρα) |
| `label` | string | Ετικέτα κειμένου (προαιρετικό) |
| `color` | string | Χρώμα: hex ("#FF0000" ή "FF0000") ή όνομα HTML ("red", "cyan") |
| `scale` | float | Μέγεθος marker σε μέτρα |

### Άλλα Endpoints

| Endpoint | Method | Περιγραφή |
|----------|--------|-----------|
| `/api/health` | GET | Health + uptime + marker count + calibration |
| `/api/status` | GET | Συνοπτικό status server/markers/calibration |
| `/api/coordinates` | GET | Λίστα ενεργών markers (JSON) |
| `/api/coordinates/{id}` | DELETE | Διαγραφή ενός marker |
| `/api/clear` | POST | Καθαρισμός όλων των markers |
| `/api/calibrate/start` | POST | Έναρξη calibration mode |
| `/api/calibrate` | POST | Αποστολή πραγματικών συντεταγμένων |
| `/api/calibrate/status` | GET | Κατάσταση βαθμονόμησης (live) |
| `/api/calibrate/reset` | POST | Επαναφορά calibration |

### Επιπλέον flags στο POST `/api/coordinates`

| Flag | Περιγραφή |
|------|-----------|
| `clearExisting` | `true` = καθάρισε όλα πριν την εισαγωγή |
| `removeMissing` | `true` = διέγραψε markers που λείπουν από το payload (sync) |

## Παραδείγματα

### Python

```python
import requests

quest_url = "http://192.168.1.100:8080/api/coordinates"

data = {
    "coordinates": [
        {"x": 1.0, "y": 0.5, "z": 2.0, "label": "Test", "color": "#FF0000"}
    ],
    "clearExisting": True
}

response = requests.post(quest_url, json=data)
print(response.json())
```

### JavaScript/Node.js

```javascript
const axios = require('axios');

const questUrl = 'http://192.168.1.100:8080/api/coordinates';

const data = {
  coordinates: [
    { x: 1.0, y: 0.5, z: 2.0, label: 'Test', color: '#FF0000' }
  ],
  clearExisting: true
};

axios.post(questUrl, data)
  .then(response => console.log(response.data))
  .catch(error => console.error(error));
```

### cURL

```bash
curl -X POST http://192.168.1.100:8080/api/coordinates \
  -H "Content-Type: application/json" \
  -d '{"coordinates": [{"x": 1, "y": 0.5, "z": 2, "label": "Test"}]}'
```

### C#

```csharp
using System.Net.Http;
using System.Text;
using System.Text.Json;

var client = new HttpClient();
var data = new {
    coordinates = new[] {
        new { x = 1.0, y = 0.5, z = 2.0, label = "Test", color = "#FF0000" }
    },
    clearExisting = true
};

var json = JsonSerializer.Serialize(data);
var content = new StringContent(json, Encoding.UTF8, "application/json");
var response = await client.PostAsync("http://192.168.1.100:8080/api/coordinates", content);
```

## Χειριστήρια Quest

| Κουμπί | Λειτουργία |
|--------|------------|
| **A Button** | Επαναβαθμονόμηση σημείου αναφοράς |
| **B Button** | Καθαρισμός όλων των markers |
| **X Button** | Έναρξη λειτουργίας calibration |
| **Trigger** | Καταγραφή σημείου (σε calibration mode) |
| **Thumbstick Click** | Εμφάνιση/Απόκρυψη UI |

## Calibration με Πραγματικές Συντεταγμένες

Για να χρησιμοποιήσεις πραγματικές συντεταγμένες (π.χ. μέτρα από γνωστό σημείο):

### Βήμα 1: Έναρξη Calibration

```bash
# Από τον server:
curl -X POST http://<QUEST_IP>:8080/api/calibrate/start

# Ή πάτα X Button στο Quest
```

### Βήμα 2: Δείξε ένα γνωστό σημείο

Στο Quest, δείξε με το χειριστήριο ένα σημείο που ξέρεις τις πραγματικές συντεταγμένες του (π.χ. γωνία δωματίου) και πάτα **Trigger**.

### Βήμα 3: Στείλε τις πραγματικές συντεταγμένες

```bash
curl -X POST http://<QUEST_IP>:8080/api/calibrate \
  -H "Content-Type: application/json" \
  -d '{
    "realX": 0.0,
    "realY": 0.0,
    "realZ": 0.0,
    "rotationY": 0,
    "quickCalibrate": true
  }'
```

### Calibration JSON Παράμετροι

| Παράμετρος | Τύπος | Περιγραφή |
|------------|-------|-----------|
| `realX` | float | Πραγματική συντεταγμένη X (μέτρα) |
| `realY` | float | Πραγματική συντεταγμένη Y - ύψος (μέτρα) |
| `realZ` | float | Πραγματική συντεταγμένη Z (μέτρα) |
| `rotationY` | float | Περιστροφή γύρω από τον άξονα Y (μοίρες) |
| `quickCalibrate` | bool | true = απλή βαθμονόμηση ενός σημείου |
| `coordinateSystem` | string | "local", "gps", ή "utm" |

### Παράδειγμα με Python Server

```bash
python coordinate_server.py --quest-ip 192.168.1.100 --interactive

>>> startcal
Calibration mode started on Quest!

>>> calibrate 10.0 0.0 5.0 45
Calibration sent: (10.0, 0.0, 5.0) rotation=45°

>>> add 12.5 1.0 7.0 MyPoint #FF0000
Added coordinate at (12.5, 1.0, 7.0)
```

## Σύστημα Συντεταγμένων

Οι συντεταγμένες είναι σχετικές με το **Σημείο Αναφοράς** (Reference Anchor):

```
     Y (πάνω)
     |
     |
     |_______ X (δεξιά)
    /
   /
  Z (μπροστά)
```

- **X**: Δεξιά (+) / Αριστερά (-)
- **Y**: Πάνω (+) / Κάτω (-)
- **Z**: Μπροστά (+) / Πίσω (-)
- Μονάδα: **μέτρα**

## Python Server (Προαιρετικό)

Μπορείς να χρησιμοποιήσεις τον Python server για διαχείριση συντεταγμένων:

```bash
cd Server
pip install -r requirements.txt
python coordinate_server.py --quest-ip 192.168.1.100 --interactive
```

### Interactive Mode Commands

- `add x y z [label] [color]` - Προσθήκη συντεταγμένης
- `list` - Λίστα συντεταγμένων
- `send` - Αποστολή στο Quest
- `clear` - Καθαρισμός
- `del <id>` - Διαγραφή ενός marker
- `status` - Έλεγχος σύνδεσης
- `calibrate x y z [rotation]` - Βαθμονόμηση με πραγματικές συντεταγμένες
- `startcal` - Έναρξη λειτουργίας calibration
- `calstatus` - Κατάσταση βαθμονόμησης
- `quit` - Έξοδος

## Βελτιώσεις v1.2 (full roadmap)

### Spatial Anchors
- OVRSpatialAnchor via reflection (με pose+UUID fallback)
- `GET /api/anchors` status

### Ασφάλεια & discovery
- Optional API key (`X-Api-Key` / Bearer) + rate limit
- UDP discovery beacon port **48080** (`NetworkDiscovery`)
- Python: `python coordinate_server.py --discover`

### Calibration UX
- Step messages + residual RMS (μέτρα)
- `CalibrationHUD` on-screen guide
- Live `/api/calibrate/status` με `step` + `residualRmsMeters`

### Marker UX
- Types: `sphere` | `pin` | `arrow` | `zone`
- Distance labels, LOD, ground line, controller ray highlight
- Layers: `layer` field + `POST /api/layer`

### Sessions
- `SessionManager`: export/import JSON & CSV, undo history
- `GET/POST /api/session`, `POST /api/session/new`, `POST /api/undo`
- Auto-save: η session αποθηκεύεται αυτόματα στο `persistentDataPath`
  (`autosave_session.json`) λίγα δευτερόλεπτα μετά από κάθε αλλαγή markers
  και επαναφέρεται στο επόμενο άνοιγμα της εφαρμογής. Απενεργοποίηση:
  `autoSave = false` στο `SessionManager` (Inspector).

### CRS / GPS
- `crs`, `latitude`/`longitude`/`altitude`, `POST /api/gps-origin`
- `CoordinateTransform` GPS→local meters

### Push
- Long-poll `GET /api/events` (Python) + `WebSocketPushClient` (Unity)
- Αν ο Python server τρέχει με `QUEST_API_KEY`, όρισε το ίδιο κλειδί στο
  `apiKey` του `WebSocketPushClient` (στέλνεται ως `Authorization: Bearer`)

### Observability
- `GET /api/logs` ring buffer
- Health/status enriched

### Editor / packaging
- **Quest Marker App → Play Mode Tools**
- **Android Build Settings (Quest)**
- AR Foundation stub + `AR_FOUNDATION` define

### Tests
```bash
cd Server
pip install -r requirements.txt
python -m unittest test_coordinate_math.py test_server_api.py -v
```

## API v1.2 (Quest device)

| Endpoint | Method | Περιγραφή |
|----------|--------|-----------|
| `/api/health` | GET | Health (open even with auth) |
| `/api/status` | GET | Markers + calibration + session |
| `/api/logs` | GET | Recent request log |
| `/api/coordinates` | GET/POST | List / add markers |
| `/api/coordinates/{id}` | DELETE | Delete one |
| `/api/clear` | POST | Clear all |
| `/api/undo` | POST | Undo last marker mutation |
| `/api/calibrate/*` | * | start / post / status / reset |
| `/api/session` | GET/POST | Export / import session JSON |
| `/api/session/new` | POST | New named session |
| `/api/gps-origin` | POST | Set GPS origin `{latitude,longitude,altitude}` |
| `/api/anchors` | GET | Spatial anchor status |
| `/api/layer` | POST | Filter visible layer `{"layer":"hazards"}` |

### Auth (optional)
On `LocalAPIServer` enable `requireApiKey` and set `apiKey`. Clients send:
```http
X-Api-Key: your-secret
```
or `Authorization: Bearer your-secret`.

> **Σημείωση ασφαλείας:** Το API είναι σχεδιασμένο για **τοπικό δίκτυο μόνο**
> (εργοτάξιο / LAN). Η επικοινωνία είναι plain HTTP χωρίς κρυπτογράφηση και το
> CORS είναι ανοιχτό — μην εκθέσετε τα ports 8080/5000 στο internet. Το API key
> προστατεύει από τυχαία χρήση στο LAN, όχι από αποφασισμένο επιτιθέμενο.

### Marker payload extras
```json
{
  "coordinates": [{
    "id": "p1",
    "x": 1, "y": 0.5, "z": 2,
    "label": "Hazard",
    "color": "#FF0000",
    "markerType": "pin",
    "layer": "hazards",
    "status": "critical",
    "crs": "local",
    "radius": 0.5
  }],
  "clearExisting": false,
  "removeMissing": false
}
```

### GPS example
```bash
# 1) Set origin
curl -X POST http://QUEST:8080/api/gps-origin \
  -H 'Content-Type: application/json' \
  -d '{"latitude":38.0,"longitude":23.7,"altitude":100}'

# 2) Send GPS point (converted to local meters on device)
curl -X POST http://QUEST:8080/api/coordinates \
  -H 'Content-Type: application/json' \
  -d '{"coordinates":[{"id":"g1","crs":"gps","latitude":38.0001,"longitude":23.7001,"altitude":100,"label":"GPS","markerType":"pin"}]}'
```

## Troubleshooting

### Δεν βρίσκω τη IP του Quest

1. Στο Quest: **Settings → Wi-Fi → Connected Network → IP Address**
2. Ή χρησιμοποίησε `adb shell ip route` με USB σύνδεση

### Αποτυχία σύνδεσης API

1. Βεβαιώσου ότι Quest και PC είναι στο ίδιο δίκτυο
2. Έλεγξε ότι η εφαρμογή τρέχει στο Quest
3. Δοκίμασε: `curl http://<QUEST_IP>:8080/api/health`

### Markers δεν εμφανίζονται σωστά

1. Πάτα **A Button** για νέα βαθμονόμηση
2. Έλεγξε ότι οι συντεταγμένες είναι σε λογική απόσταση (0-10 μέτρα)

## Δομή Project

```
QuestMarkerApp/
├── Assets/Scripts/
│   ├── API/
│   │   ├── CoordinateData.cs          # markers + session + GPS types
│   │   ├── CoordinateAPIService.cs    # remote polling + backoff
│   │   ├── LocalAPIServer.cs          # HTTP API + auth + logs
│   │   ├── ApiSecurity.cs
│   │   ├── RequestLog.cs
│   │   ├── NetworkDiscovery.cs        # UDP beacon :48080
│   │   └── WebSocketPushClient.cs     # long-poll push client
│   ├── Core/
│   │   ├── MarkerAppController.cs
│   │   ├── SpatialAnchorManager.cs    # OVR + UUID fallback
│   │   ├── CoordinateCalibration.cs   # residual + steps
│   │   ├── CoordinateTransform.cs     # CRS / GPS
│   │   └── SessionManager.cs          # JSON/CSV + undo
│   ├── Markers/
│   │   ├── Marker.cs                  # types, LOD, distance
│   │   └── MarkerManager.cs
│   ├── Mobile/
│   │   ├── MobileCameraController.cs
│   │   ├── MobilePreviewController.cs
│   │   ├── GroundGrid.cs
│   │   └── ARFoundationPreviewController.cs
│   ├── Editor/
│   │   ├── QuestMarkerAppSetup.cs
│   │   └── QuestMarkerPlayModeTools.cs
│   └── UI/
│       ├── UIManager.cs
│       └── CalibrationHUD.cs
├── Server/
│   ├── coordinate_server.py
│   ├── test_coordinate_math.py
│   ├── test_server_api.py
│   ├── requirements.txt
│   └── example_api_calls.sh
└── README.md
```

## Άδεια

MIT License

## Συντελεστές

Developed for Meta Quest 3 VR/AR applications.
