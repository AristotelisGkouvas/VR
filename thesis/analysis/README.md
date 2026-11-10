# Ανάλυση πειραμάτων

Κάθε αριθμός, πίνακας και γράφημα του κεφ. 6 βγαίνει από script αυτού του φακέλου. Τα αποτελέσματα γράφονται στο `../figures/generated/` και το report τα βρίσκει μόνο του: όσο δεν υπάρχουν, δείχνει πλαίσιο-υπενθύμιση.

```bash
cd thesis/analysis
python -m venv .venv && .venv\Scripts\activate      # μία φορά
pip install -r requirements.txt
```

| Script | Πείραμα (κεφ. 6) | Είσοδος | Έξοδος |
|---|---|---|---|
| `fieldtest_anchors.py` | Σταθερότητα και επανεντοπισμός anchors | `../data/fieldtest/*.csv` | `anchors_by_condition.tex`, `anchors_offset_by_condition.pdf`, `anchors_offset_vs_age.pdf` |
| `alignment_accuracy.py` | Ακρίβεια ευθυγράμμισης | `../data/accuracy/*.csv` | `alignment_by_k.tex`, `alignment_numbers.tex`, `alignment_error_vs_distance.pdf`, `alignment_pred_vs_meas.pdf` |
| `performance.py` | Απόδοση γραφικών | `../data/performance/perf-*.csv` (Καταγραφή απόδοσης) | `perf_by_scenario.tex`, `perf_numbers.tex`, `fps_vs_triangles.pdf` |

## fieldtest_anchors.py

Λήψη των CSV κατευθείαν από τα γυαλιά και ανάλυση (το κλειδί είναι στο κύριο μενού των γυαλιών):

```bash
python fieldtest_anchors.py --download --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ
```

ή, αν τα CSV είναι ήδη στο `../data/fieldtest/`:

```bash
python fieldtest_anchors.py
```

Τι μετρά:
- **Επανεντοπισμός**: από τις γραμμές `load` / `manual`, το ποσοστό `localized = yes` και ο διάμεσος χρόνος.
- **Απόκλιση**: από τις γραμμές `touch` (νέο άγγιγμα στο ίδιο σημείο), η οριζόντια `errH_cm_touch` (διάμεσος και 95ο εκατοστημόριο) και η απόλυτη κατακόρυφη `errV_cm_touch`.
- Γραφήματα: boxplot ανά συνθήκη, και απόκλιση συναρτήσει ηλικίας anchor με διάκριση «αλλαγές εργοταξίου».

## Επόμενα scripts

Ακολουθούν το ίδιο μοτίβο (είσοδος στο `../data/<πείραμα>/`, έξοδος στο `../figures/generated/`):
- `interpolation_loocv.py`: Delaunay έναντι IDW με leave-one-out.
- `user_study.py`: SUS, NASA-TLX, χρόνοι, Wilcoxon.

## alignment_accuracy.py

Τα CSV της λειτουργίας «Σημεία ελέγχου ακρίβειας» (README της εφαρμογής). Λήψη και ανάλυση μαζί:

```bash
python alignment_accuracy.py --download --quest-ip 192.168.1.30 --api-key ΚΛΕΙΔΙ
```

Τι μετρά: ανά k, το σφάλμα των ανεξάρτητων σημείων (διάμεσος, 95%), τον λόγο μετρημένου/προβλεπόμενου, και την επαναληψιμότητα του αγγίγματος σ από επαναλαμβανόμενα αγγίγματα του ίδιου σημείου. Οι αριθμοί περνούν στο κείμενο ως μακροεντολές (`\AccSigmaHcm` κ.λπ.) από το `alignment_numbers.tex`. Αγνοεί τις αναιρεμένες μετρήσεις και το έργο εξάσκησης (εκτός με `--include-practice`).
