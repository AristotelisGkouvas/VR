# Quest Marker App 🥽

Εφαρμογή Unity για Meta Quest 3 που λαμβάνει συντεταγμένες X, Y, Z μέσω API κλήσεων και εμφανίζει markers στον χώρο VR/AR.

## Χαρακτηριστικά

- 📍 **Real-time Markers**: Εμφάνιση 3D markers σε πραγματικό χρόνο
- 🌐 **HTTP API**: Λήψη συντεταγμένων μέσω REST API (POST requests)
- 🎯 **Spatial Anchors**: Σταθερός προσανατολισμός στον χώρο
- 🎨 **Customizable**: Ρυθμιζόμενα χρώματα, labels και μεγέθη
- 📱 **Cross-platform**: Αποστολή δεδομένων από οποιαδήποτε συσκευή στο δίκτυο
- 📈 **Κορδέλες ιστορικού**: μικρό 3D διάγραμμα 30 ημερών δίπλα σε κάθε αισθητήρα με ιστορικό (Μενού › Εμφάνιση)
- 🔔 **Ζωντανοί συναγερμοί**: ερώτηση του Terraweb (`/api/alerts/events/?active=true`) κάθε 20 s, αλλαγή εικονιδίου, ήχος και μήνυμα όταν αλλάζει η κατάσταση (Μενού › Έργο & σύνδεση)
- 🚶 **Διαδρομή επιθεώρησης**: διαδρομή από αισθητήρα σε αισθητήρα με βέλη στο δάπεδο, τικ σε κάθε επίσκεψη, αναφορά στο τέλος σε `persistentDataPath/reports/` (tile «Επιθεώρηση» στο μενού, B = παράλειψη, Μενού = διακοπή)
- 🎤 **Φωνητικές εντολές**: «βρες Κ5», «δείξε συναγερμούς», «χρονομηχανή», «μενού», «κλείσε», «διαδρομή επιθεώρησης»… με κλικ στο stick του άλλου χεριού. Χωρίς Meta Voice SDK η εντολή πληκτρολογείται· για φωνή βλ. παρακάτω.

- 🏗️ **Μοντέλο 3D πάνω στην κατασκευή**: OBJ, DXF (και IFC μέσω μετατροπής στο PC) εμφανίζονται ημιδιαφανή με ακμές, τοποθετημένα μέσω της ίδιας ευθυγράμμισης με τους αισθητήρες (Μενού › Μοντέλο 3D)

### Μοντέλο 3D (OBJ / DXF / IFC)

Το αρχείο στέλνεται από το PC στα γυαλιά και φορτώνεται αμέσως:

```
python Server/send_model.py κτήριο.obj --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ
python Server/send_model.py τοπογραφικό.dxf --quest-ip ... --api-key ...
python Server/send_model.py κτήριο.ifc --quest-ip ... --api-key ...      (μετατρέπεται σε OBJ στο PC)
python Server/send_model.py --list --quest-ip ... --api-key ...
```

- Το μοντέλο πρέπει να είναι στο ίδιο σύστημα με τους αισθητήρες (ΕΓΣΑ87 E, N, H). Τότε κάθεται μόνο του στη θέση του μόλις γίνει η ευθυγράμμιση, είτε φορτώθηκε πριν είτε μετά.
- Χωρίς ευθυγράμμιση (π.χ. στο γραφείο) εμφανίζεται ως **μακέτα** περίπου 1 m μπροστά σου. Μενού › Μοντέλο 3D › Προβολή: Αυτόματα (στο έργο όταν υπάρχει ευθυγράμμιση, αλλιώς μακέτα) / Μακέτα / 1:1 γύρω μου. Με το grip του αριστερού χειριστηρίου (του δεξιού για αριστερόχειρες) τη μετακινείς και τη γυρίζεις· «Νέα θέση μπροστά μου» την ξαναφέρνει μπροστά σου.
- **Κίνηση μέσα στο 1:1 χωρίς περπάτημα**: στο «1:1 γύρω μου» μένεις ακίνητος στον πραγματικό χώρο και κινείσαι μέσα στο μοντέλο με τα sticks, ώστε να μη χτυπήσεις σε τοίχους ή έπιπλα. Stick του δεύτερου χεριού (αριστερό για δεξιόχειρες): κίνηση μπρος/πίσω/πλάγια προς όπου κοιτάς, με σταθερή ταχύτητα βαδίσματος. Stick του κύριου χεριού αριστερά/δεξιά: στροφή 30° ανά κίνηση. Δεν λειτουργεί όταν είναι ανοιχτό μενού ή λίστα, στην ευθυγράμμιση ή στη χρονομηχανή, ούτε όταν το μοντέλο είναι πάνω στο έργο (εκεί πρέπει να μένει στη θέση του πάνω στο πραγματικό κτήριο). «Νέα θέση μπροστά μου» σε επαναφέρει στην αρχή.
- Μονάδες: μέτρα από προεπιλογή, `--units mm|cm|ft|in` για άλλες. Στο DXF διαβάζεται και το `$INSUNITS`.
- Άξονας: τα αρχεία CAD/τοπογραφίας έχουν Z πάνω (προεπιλογή). Εξαγωγές από Blender ή SketchUp συχνά Y πάνω: `--up y`.
- IFC: χρειάζεται `pip install ifcopenshell`. Με `IfcMapConversion` το μοντέλο τοποθετείται σε συντεταγμένες χάρτη, αλλιώς δώσε `--origin E N H`.
- DXF: γραμμές, πολυγραμμές (και τόξα), κύκλοι, τόξα, ελλείψεις, 3DFACE, SOLID, MESH, polyface, blocks (INSERT). Κείμενα, διαγραμμίσεις και διαστάσεις παραλείπονται.
- Μέγεθος: μέχρι 300 MB ανά αρχείο. Πάνω από περίπου 500 χιλιάδες τρίγωνα το Quest αρχίζει να κουράζεται.
- Χρόνος αποστολής: το upload ακυρώνεται αν δεν έρθουν δεδομένα για 30 s ή αν κρατήσει πάνω από 15 λεπτά συνολικά.
- Το `--api-key` μπορεί να παραλειφθεί αν έχεις ορίσει τη μεταβλητή περιβάλλοντος `QUEST_KEY` (το κλειδί από το κύριο μενού των γυαλιών).

#### Φωτογραμμετρία και βαριά OBJ (MeshLab)

Ένα OBJ με υφές ή πάνω από 400 χιλιάδες τρίγωνα προετοιμάζεται αυτόματα στο PC πριν σταλεί (χρειάζεται `pip install pymeshlab pillow`):

1. απλοποίηση με το MeshLab (quadric edge collapse που κρατά την υφή) στα `--faces` τρίγωνα (προεπιλογή 400 000),
2. σμίκρυνση κάθε υφής στα `--texture-size` pixels (προεπιλογή 2048) και μετατροπή σε JPG,
3. προαιρετικά μεταφορά στον κάναβο των αισθητήρων με `--match σημεία.csv`,
4. πακέτο zip με OBJ, MTL και εικόνες, που τα γυαλιά αποσυμπιέζουν.

```
python Server/send_model.py konitsa.obj --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ --match konitsa_points.csv
```

Το `σημεία.csv` έχει μία γραμμή ανά κοινό σημείο, με συντεταγμένες στο μοντέλο και στον κάναβο των αισθητήρων (οι τιμές παρακάτω είναι μόνο παράδειγμα μορφής):

```
name,model_x,model_y,model_z,e,n,h
A1,-12.41,8.30,551.20,1008.4459,4982.3774,21.3001
A10,15.02,30.11,546.85,1038.0968,5019.4186,16.2079
P5,-30.77,-20.46,560.02,1001.2,4960.1,12.98
```

Με 3 και πάνω σημεία φαίνεται και το σφάλμα κάθε σημείου. `--fit 3d` αν το μοντέλο δεν είναι οριζοντιωμένο, `--allow-scale` αν διαφέρει και η κλίμακα. Το `--keep-package` κρατά το zip δίπλα στο OBJ.

### Φωνή μέσω Wit.ai (προαιρετικό)

Το Meta Voice SDK αναγνωρίζει ομιλία μέσω Wit.ai (cloud, δωρεάν λογαριασμός, υποστηρίζει ελληνικά). Βήματα:

1. `Packages/manifest.json`: πρόσθεσε `"com.meta.xr.sdk.voice"` με την ίδια έκδοση με το `com.meta.xr.sdk.core`.
2. Στο https://wit.ai φτιάξε app με γλώσσα Greek και αντίγραψε το Server Access Token.
3. Unity: Oculus › Voice SDK › Settings, επικόλλησε το token για να δημιουργηθεί WitConfiguration. Ονόμασέ το `WitConfiguration` και βάλ' το στο `Assets/Resources/`.
4. Project Settings › Player › Scripting Define Symbols (Android): πρόσθεσε `META_VOICE_SDK`.
5. Build. Η εφαρμογή ζητά άδεια μικροφώνου την πρώτη φορά. Λεπτομέρειες στο `Assets/Scripts/UI/VoiceRecognizers.cs`.

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
> Είναι για αξιολόγηση εμφάνισης & ροής. Για πραγματικό AR με την κάμερα, δες την
> επόμενη ενότητα.

## 📷 AR με την κάμερα του κινητού (AR Foundation)

Πλήρες AR mode: η **κάμερα της συσκευής** είναι το background και τα markers
κάθονται πάνω στον πραγματικό χώρο, αγκυρωμένα σε ένα σημείο αναφοράς που ορίζεις
πατώντας σε μια επιφάνεια που εντόπισε η συσκευή.

Το AR runtime ζει σε **ξεχωριστό assembly** (`QuestMarkerApp.AR`) με
`defineConstraint: AR_FOUNDATION`. Χωρίς τα AR packages το assembly απλώς δεν
μεταγλωττίζεται — το Quest build και το mobile preview μένουν ανέπαφα.

### Απαιτήσεις

- Android κινητό με **ARCore support** ([λίστα συσκευών](https://developers.google.com/ar/devices))
  και εγκατεστημένο το *Google Play Services for AR*
- Android 8.0+ (API 26)

### Ρύθμιση — 4 βήματα από το Unity menu

| Βήμα | Menu | Τι κάνει |
|------|------|----------|
| 1 | **Quest Marker App → AR (Camera) → 1. Install AR Packages** | Εγκαθιστά `com.unity.xr.arfoundation` + `com.unity.xr.arcore` (τελευταία συμβατή έκδοση) |
| 2 | **… → 2. Enable AR Mode** | Ενεργοποιεί `MOBILE_PREVIEW` + `AR_FOUNDATION` (Android/Standalone/iOS) |
| 3 | **… → 3. Setup AR Camera Scene** | Φτιάχνει AR Session + XR Origin (AR) + `ARMarkerSession` + `MarkerAppManager` |
| 4 | **… → 4. Android Player Settings for AR** | Min API 26, IL2CPP, ARM64, OpenGLES3, ενεργοποιεί τον ARCore loader και βγάζει τον Oculus |

Μετά το βήμα 3 σώσε τη σκηνή (π.χ. `Assets/Scenes/ARCamera.unity`) και βάλ' την στα build scenes.

> **Project Validation:** το βήμα 4 προσφέρεται να ανοίξει το
> **Project Settings → XR Plug-in Management → Project Validation** (tab Android).
> Πάτα **Fix All** αν εμφανιστούν issues. Το menu
> **AR (Camera) → Open XR Project Validation** το ανοίγει οποτεδήποτε.
>
> Σε AR Foundation 6.x το URP camera background δουλεύει αυτόματα — δεν χρειάζεται
> χειροκίνητη προσθήκη του *AR Background Renderer Feature* όπως στην 4.x. Μόνο αν
> αλλάξεις σε **Vulkan** θα ζητηθεί το `ARCommandBufferSupportRendererFeature`.

Μετά: σύνδεσε το κινητό με USB debugging → **File → Build Settings → Build and Run**.

### Χρήση στη συσκευή

1. Κούνησε αργά το κινητό προς το πάτωμα — εμφανίζονται μπλε ημιδιάφανες επιφάνειες
2. Το σημείο αναφοράς **(0,0,0)** μπαίνει αυτόματα στην πρώτη επιφάνεια που θα βρεθεί
3. Πάτα **Origin** και μετά κάπου στην οθόνη για να το ξαναορίσεις εκεί που θες
4. Ο `+Z` άξονας κοιτά προς την κατεύθυνση που κοιτούσες τη στιγμή της τοποθέτησης

| Κουμπί οθόνης | Λειτουργία |
|---------------|------------|
| **Demo** | Δείγμα markers γύρω από το origin |
| **Clear** | Καθαρισμός όλων |
| **Origin** | Επανατοποθέτηση σημείου αναφοράς με tap |
| **Info / Hide** | Status overlay (κατάσταση AR, markers, επιφάνειες, API URL) |

Ο `LocalAPIServer` τρέχει κανονικά, οπότε στέλνεις πραγματικές συντεταγμένες από
άλλη συσκευή στο ίδιο δίκτυο **με το ίδιο API όπως στο Quest**:

```bash
curl -X POST http://<IP_ΚΙΝΗΤΟΥ>:8080/api/coordinates   -H 'Content-Type: application/json'   -d '{"coordinates":[{"x":1,"y":0.5,"z":2,"label":"Test","color":"#FF0000"}]}'
```

### Επιστροφή στο Quest

**Quest Marker App → AR (Camera) → Disable AR Mode (back to Quest)** και
ξαναενεργοποίησε το Oculus στο XR Plug-in Management → Android.

### Περιορισμοί

- Το AR **δεν τρέχει σε Editor Play mode** χωρίς XR Simulation — χρειάζεται build στη συσκευή
- Το origin τοποθετείται σε world space, όχι σε `ARAnchor`. Σε μεγάλους χώρους ή
  μετά από παρατεταμένο tracking, το ARCore μπορεί να διορθώσει το world frame και
  τα markers να μετατοπιστούν λίγα εκατοστά — πάτα **Origin** για επαναφορά.
- Δεν έχει ρυθμιστεί iOS/ARKit· το `com.unity.xr.arkit` προστίθεται με τον ίδιο τρόπο

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
| `/api/fieldtest` | GET | Λίστα των CSV της δοκιμής πεδίου anchors |
| `/api/fieldtest/{file}` | GET | Λήψη ενός CSV (text/csv) |

### Κωδικοί απάντησης

Κάθε αίτημα θέλει το κλειδί της συσκευής (`X-Api-Key` ή `Authorization: Bearer`). Τα λάθη επιστρέφονται ως JSON:

| Κωδικός | Πότε |
|---------|------|
| `400` | Άδειο ή χαλασμένο JSON, ή μη έγκυρες τιμές (NaN, lat/lon εκτός ορίων) |
| `401` | Λείπει ή είναι λάθος το κλειδί |
| `405` | Γνωστό endpoint με λάθος method |
| `413` | Σώμα πάνω από 512 KB (για JSON) |
| `429` | Πολλά αιτήματα (60/λεπτό χωρίς σωστό κλειδί, 600/λεπτό με κλειδί) ή κλείδωμα μετά από 10 λάθος κλειδιά· δες `Retry-After` |
| `503` | Τα γυαλιά δεν απάντησαν εγκαίρως (π.χ. σε αναμονή) ή η ουρά εντολών γέμισε· δες `Retry-After` |

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

## Απόδοση (Μενού › Ρυθμίσεις)

- **Ρυθμός ανανέωσης** 72 / 90 Hz (προεπιλογή 72). Τα 90 Hz κάνουν πιο σταθερή την επικάλυψη markers και μοντέλου πάνω στον πραγματικό χώρο και την κίνηση με stick στο 1:1, αλλά καταναλώνουν περισσότερη μπαταρία και ζεσταίνουν τα γυαλιά. Αν η εφαρμογή δεν προλαβαίνει τα 90, η εικόνα τρεμοπαίζει περισσότερο από τα σταθερά 72: μέτρησε με το OVR Metrics Tool. Αν η οθόνη δεν άλλαξε, η τιμή δείχνει «90 Hz · τώρα 72».
- **Foveated rendering** Ανενεργό / Χαμηλό / Μεσαίο / Υψηλό (προεπιλογή Ανενεργό). Μικρότερη ανάλυση στις άκρες του πεδίου, που το μάτι δεν ξεχωρίζει· ελευθερώνει χρόνο στην κάρτα γραφικών για τα 90 Hz και για μεγάλα ημιδιάφανα μοντέλα. Είναι δυναμικό: χαμηλώνει μόνο του όταν η κάρτα γραφικών δεν πιέζεται.
- Προτεινόμενη δοκιμή: το βαρύτερο σενάριο (όλοι οι αισθητήρες, ετικέτες, μεγάλο μοντέλο) πρώτα στα 72 Hz χωρίς foveation, μετά 90 Hz + Υψηλό.

## Δοκιμή πεδίου anchors (Φάση 0)

Εργαλείο μέτρησης, όχι η αυτόματη επαναευθυγράμμιση (βλ. `docs/proposals/auto-realignment.md` §9). Δεν αλλάζει τίποτα στην ευθυγράμμιση: ο χρήστης ακουμπά πάντα τα σημεία ελέγχου και επιβεβαιώνει. Απλώς καταγράφει αν τα spatial anchors ανά σημείο ελέγχου ξαναβρίσκονται σε επόμενες επισκέψεις, πόσο γρήγορα και πόσο έχουν μετακινηθεί.

**Ενεργοποίηση:** Μενού › Ευθυγράμμιση › «Δοκιμή πεδίου anchors» (προεπιλογή ανενεργή). Στο ίδιο σημείο:
- **Συνθήκες**: ηλιοφάνεια, συννεφιά, εσωτερικός χώρος, πρωί, μεσημέρι, βράδυ. Τη ρυθμίζεις με το χέρι πριν από κάθε μέτρηση (η εφαρμογή δεν «βλέπει» το φως) και μπαίνει σε κάθε γραμμή του CSV.
- **Αλλαγές στο εργοτάξιο**: Ναι/Όχι (εκσκαφές, σκαλωσιές, μηχανήματα από την προηγούμενη φορά).
- **Μέτρηση τώρα**: ξαναψάχνει όλα τα anchors του έργου. Η τιμή της γραμμής δείχνει το αποτέλεσμα, π.χ. «3/4 εντοπίστηκαν · 2.1 s · max 4.2 cm».
- **Διαγραφή anchors δοκιμής**: σβήνει τα anchors του έργου από τα γυαλιά. Τα CSV μένουν.

Όσο είναι ενεργή, κάθε άγγιγμα σημείου ελέγχου καρφώνει και ένα anchor εκεί (έως 12 ανά έργο). Ένα νέο άγγιγμα στο ίδιο σημείο το αντικαθιστά, και το παλιό σβήνεται από τη συσκευή. Στο άνοιγμα του έργου σε επόμενη συνεδρία, τα anchors φορτώνονται και εντοπίζονται αυτόματα (έως 20 s).

**Πρωτόκολλο**
1. Διάλεξε **3 σημεία ελέγχου σε απόσταση 5–30 m** μεταξύ τους, σε σημεία που θα μείνουν προσβάσιμα.
2. **Μέρα 1:** ευθυγράμμιση κανονικά (άγγιγμα και στα 3), όρισε συνθήκες, κλείσε τη λίστα ευθυγράμμισης και φύγε.
3. **Επιστροφή μετά από 10 λεπτά, την επόμενη μέρα και μετά από μία εβδομάδα:** άνοιξε το έργο, περίμενε τη μέτρηση (ή «Μέτρηση τώρα»), δες την τιμή, και μετά ευθυγράμμισε κανονικά ξανά με άγγιγμα στα ίδια σημεία. Κάθε άγγιγμα γράφει την απόκλιση anchor ↔ άγγιγμα. Όταν κλείσεις τη λίστα, γράφεται και η απόκλιση anchor ↔ νέα λύση. Το νέο άγγιγμα ανανεώνει το anchor, οπότε η στήλη `anchor_age_h` δείχνει πόσο παλιό ήταν το anchor που μετρήθηκε.
4. Επανέλαβε με **διαφορετικό φως** (πρωί/μεσημέρι, ήλιος/συννεφιά) και **μετά από αλλαγές στο εργοτάξιο** (με «Αλλαγές στο εργοτάξιο: Ναι»).
5. **Περπάτησε 50 m μακριά και γύρνα** χωρίς να κλείσεις την εφαρμογή, μετά «Μέτρηση τώρα».

**Λήψη CSV** (ένα αρχείο ανά έργο ανά ημέρα στο `persistentDataPath/fieldtest/`, μία γραμμή ανά anchor ανά μέτρηση):

```bash
curl -H "X-Api-Key: <κλειδί>" http://<IP του Quest>:8080/api/fieldtest
curl -H "X-Api-Key: <κλειδί>" -O http://<IP του Quest>:8080/api/fieldtest/<αρχείο>.csv
```

Στήλες: `timestamp, session_id, project, point_id, condition, site_changed, app_version, phase` (load / manual / touch / solution / create / undo / erase), `anchor_uuid, anchor_age_h, localized, seconds` (από την αρχή της μέτρησης), `tracked, dist_from_original_m, errH_cm_solution, errV_cm_solution, errH_cm_touch, errV_cm_touch, pair_errH_cm, pair_errV_cm` (χειρότερη διαφορά απόστασης από τα άλλα anchors σε σχέση με το έργο, χωρίς νέο άγγιγμα), `headset_dist_m, note`. Κενή στήλη σημαίνει «άγνωστο». Το `dist_from_original_m` είναι γνωστό μόνο όταν ξαναβρέθηκε και το ίδιο reference anchor του χώρου (άρα είναι σχετική μετακίνηση δύο anchors) ή μέσα στην ίδια συνεδρία. Το κατακόρυφο σφάλμα έχει πρόσημο (anchor μείον αναφορά).

## Χειριστήρια Quest

Για δεξιόχειρες: κύριο χέρι το δεξί (A/B), δεύτερο το αριστερό (X/Y). Για αριστερόχειρες οι ρόλοι αντιστρέφονται. Η σελίδα Βοήθεια › Χειρισμοί στο μενού δείχνει πάντα την τρέχουσα αντιστοίχιση.

| Κουμπί | Λειτουργία |
|--------|------------|
| **Menu** (αριστερό) | Κλείνει ό,τι είναι ανοιχτό μπροστά σου· αλλιώς σταματά τη διαδρομή επιθεώρησης· αλλιώς ανοίγει/κλείνει το μενού. Χωρίς αριστερό χειριστήριο: κλικ στο stick του δεξιού |
| **X** | Ανοίγει το κύριο μενού (κλείνει το panel ευθυγράμμισης αν είναι ανοιχτό) |
| **Trigger** (κύριο) | Κλικ σε panel · καρτέλα του αισθητήρα που σημαδεύεις · καταγραφή σημείου στην ευθυγράμμιση |
| **A** | Επιλογή στο μενού |
| **B** | Πίσω · σταματά την πλοήγηση, μετά τη χρονομηχανή, μετά παραλείπει στάση της διαδρομής |
| **Stick ↑↓** | Πλοήγηση στο μενού και στις λίστες |
| **Grip** (κύριο) | Μετακίνηση παραθύρου |
| **Grip** (δεύτερο) | Μετακίνηση μοντέλου 3D |
| **Stick** (δεύτερο) / **Stick ←→** (κύριο) | Στο μοντέλο «1:1 γύρω μου»: κίνηση μέσα στο μοντέλο / στροφή 30° |
| **Stick click** (δεύτερο, κράτα πατημένο) | Φωνητική εντολή (αν είναι ενεργή η φωνή) |

Μόνο **χωρίς φορτωμένο έργο** λειτουργούν και οι συντομεύσεις: **A** = νέο σημείο αναφοράς, **B** = καθαρισμός όλων των markers, **Y** = αναίρεση. Η παλιά βαθμονόμηση με laser ξεκινά μόνο μέσω `POST /api/calibrate/start`.

## Calibration με Πραγματικές Συντεταγμένες

Για να χρησιμοποιήσεις πραγματικές συντεταγμένες (π.χ. μέτρα από γνωστό σημείο):

> ⚠️ Η βαθμονόμηση αποθηκεύεται **σε σχέση με το Σημείο Αναφοράς** (Reference Anchor). Αν το ξαναδημιουργήσεις (κουμπί A), οι βαθμονομημένοι markers μετακινούνται μαζί του. Βαθμονομήσεις που αποθηκεύτηκαν πριν από αυτή την αλλαγή (όταν αποθηκεύονταν σε απόλυτες θέσεις) θα βγαίνουν μετατοπισμένες: κάνε τις ξανά. Η ευθυγράμμιση έργου (ΕΓΣΑ87) δεν επηρεάζεται.

### Βήμα 1: Έναρξη Calibration

```bash
# Από τον server:
curl -X POST http://<QUEST_IP>:8080/api/calibrate/start

# (το X στο Quest ανοίγει πλέον το μενού· η βαθμονόμηση με laser ξεκινά μόνο από εδώ)
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

### Κλειδιά

- `--quest-key` / `QUEST_KEY`: το κλειδί των γυαλιών (κύριο μενού), που στέλνει ο server στο Quest.
- `--api-key` / `QUEST_API_KEY`: το κλειδί που πρέπει να στέλνουν οι πελάτες αυτού του server. Χωρίς `--quest-key`, στέλνεται αυτό και στο Quest (παλιά συμπεριφορά).
- Με WebSocket push (`useWebSocketPush` στο `MarkerAppController`), τα γυαλιά στέλνουν στον server το `remoteApiKey` (το `--api-key` του server). Αν μείνει κενό, δεν στέλνεται κλειδί — το κλειδί των γυαλιών δεν στέλνεται ποτέ αυτόματα.
- Τα `fake_scenarios.py --api-key ΚΛΕΙΔΙ` και `example_api_calls.sh [IP] [PORT] [ΚΛΕΙΔΙ]` διαβάζουν επίσης το `QUEST_KEY` αν δεν δοθεί κλειδί.

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
- AR Foundation (κάμερα κινητού) σε ξεχωριστό assembly + `AR_FOUNDATION` define

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
│   │   └── GroundGrid.cs
│   ├── AR/                            # ξεχωριστό asmdef, defineConstraint AR_FOUNDATION
│   │   ├── QuestMarkerApp.AR.asmdef
│   │   └── ARMarkerSession.cs         # AR Foundation: κάμερα + planes + origin placement
│   ├── Editor/
│   │   ├── QuestMarkerAppSetup.cs
│   │   ├── QuestMarkerARSetup.cs      # AR install / defines / scene / player settings
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
