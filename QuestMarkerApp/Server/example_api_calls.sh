#!/bin/bash
# Example curl commands for Quest Marker App
# Usage: ./example_api_calls.sh [QUEST_IP] [QUEST_PORT]

QUEST_IP="${1:-192.168.1.100}"
QUEST_PORT="${2:-8080}"
BASE_URL="http://${QUEST_IP}:${QUEST_PORT}"

echo "Quest Marker App - API Examples"
echo "================================"
echo "Target: ${BASE_URL}"
echo ""

# 1. Health
echo "1. Health check..."
curl -s "${BASE_URL}/api/health" | python3 -m json.tool
echo ""

# 2. Status
echo "2. Full status..."
curl -s "${BASE_URL}/api/status" | python3 -m json.tool 2>/dev/null || echo "(status endpoint may be unavailable)"
echo ""

# 3. Single coordinate
echo "3. Sending single coordinate..."
curl -s -X POST "${BASE_URL}/api/coordinates" \
  -H "Content-Type: application/json" \
  -d '{
    "coordinates": [
      {
        "id": "point1",
        "x": 1.0,
        "y": 0.5,
        "z": 2.0,
        "label": "Σημείο 1",
        "color": "#FF0000",
        "scale": 0.1
      }
    ],
    "clearExisting": false
  }' | python3 -m json.tool
echo ""

# 4. Multiple coordinates
echo "4. Sending multiple coordinates..."
curl -s -X POST "${BASE_URL}/api/coordinates" \
  -H "Content-Type: application/json" \
  -d '{
    "coordinates": [
      {"id": "a", "x": 0.0, "y": 0.0, "z": 1.0, "label": "Origin", "color": "#00FF00"},
      {"id": "b", "x": 1.0, "y": 0.0, "z": 1.0, "label": "Right", "color": "#0000FF"},
      {"id": "c", "x": 0.0, "y": 1.0, "z": 1.0, "label": "Up", "color": "#FFFF00"},
      {"id": "d", "x": 0.0, "y": 0.0, "z": 2.0, "label": "Forward", "color": "#FF00FF"}
    ],
    "clearExisting": true
  }' | python3 -m json.tool
echo ""

# 5. List markers
echo "5. GET current markers..."
curl -s "${BASE_URL}/api/coordinates" | python3 -m json.tool
echo ""

# 6. Delete one marker
echo "6. DELETE marker 'a'..."
curl -s -X DELETE "${BASE_URL}/api/coordinates/a" | python3 -m json.tool
echo ""

# 7. Calibration status
echo "7. Calibration status..."
curl -s "${BASE_URL}/api/calibrate/status" | python3 -m json.tool
echo ""

# 8. Typed markers + layer
echo "8. Pin + zone markers..."
curl -s -X POST "${BASE_URL}/api/coordinates" \
  -H "Content-Type: application/json" \
  -d '{
    "coordinates": [
      {"id":"pin1","x":0.5,"y":0.2,"z":1.0,"label":"Pin","color":"#FF9500","markerType":"pin","layer":"survey"},
      {"id":"zone1","x":-0.5,"y":0,"z":1.5,"label":"Zone","color":"#5856D6","markerType":"zone","radius":0.75,"layer":"hazards"}
    ],
    "clearExisting": false
  }' | python3 -m json.tool
echo ""

# 9. Logs
echo "9. Request logs..."
curl -s "${BASE_URL}/api/logs" | python3 -m json.tool 2>/dev/null | head -40
echo ""

# 10. Session export
echo "10. Session export..."
curl -s "${BASE_URL}/api/session" | python3 -m json.tool 2>/dev/null | head -40
echo ""

echo "Other useful commands:"
echo "  curl -X POST ${BASE_URL}/api/clear"
echo "  curl -X POST ${BASE_URL}/api/undo"
echo "  curl -X POST ${BASE_URL}/api/calibrate/start"
echo "  curl -X POST ${BASE_URL}/api/calibrate/reset"
echo "  curl -X POST ${BASE_URL}/api/calibrate -H 'Content-Type: application/json' \\"
echo "       -d '{\"realX\":0,\"realY\":0,\"realZ\":0,\"quickCalibrate\":true}'"
echo "  curl -X POST ${BASE_URL}/api/layer -H 'Content-Type: application/json' -d '{\"layer\":\"hazards\"}'"
echo "  python coordinate_server.py --discover"
echo ""
echo "Done!"
