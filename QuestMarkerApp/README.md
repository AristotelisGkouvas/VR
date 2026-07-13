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
| `color` | string | Χρώμα σε hex format (π.χ. "#FF0000") |
| `scale` | float | Μέγεθος marker σε μέτρα |

### Άλλα Endpoints

| Endpoint | Method | Περιγραφή |
|----------|--------|-----------|
| `/api/health` | GET | Έλεγχος σύνδεσης |
| `/api/coordinates` | GET | Status |
| `/api/clear` | POST | Καθαρισμός όλων των markers |

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
- `status` - Έλεγχος σύνδεσης
- `calibrate x y z [rotation]` - Βαθμονόμηση με πραγματικές συντεταγμένες
- `startcal` - Έναρξη λειτουργίας calibration
- `quit` - Έξοδος

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
├── Assets/
│   ├── Scripts/
│   │   ├── API/
│   │   │   ├── CoordinateData.cs
│   │   │   ├── CoordinateAPIService.cs
│   │   │   └── LocalAPIServer.cs
│   │   ├── Core/
│   │   │   ├── MarkerAppController.cs
│   │   │   ├── SpatialAnchorManager.cs
│   │   │   └── CoordinateCalibration.cs   # Βαθμονόμηση πραγματικών συντεταγμένων
│   │   ├── Markers/
│   │   │   ├── Marker.cs
│   │   │   └── MarkerManager.cs
│   │   └── UI/
│   │       └── UIManager.cs
│   ├── Prefabs/
│   ├── Scenes/
│   └── Materials/
├── Server/
│   ├── coordinate_server.py
│   ├── requirements.txt
│   └── example_api_calls.sh
├── ProjectSettings/
└── README.md
```

## Άδεια

MIT License

## Συντελεστές

Developed for Meta Quest 3 VR/AR applications.
