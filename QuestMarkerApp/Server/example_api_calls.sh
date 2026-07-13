#!/bin/bash
# Example curl commands for sending coordinates to Quest Marker App
# Replace QUEST_IP with your Quest's IP address

QUEST_IP="${1:-192.168.1.100}"
QUEST_PORT="${2:-8080}"
BASE_URL="http://${QUEST_IP}:${QUEST_PORT}"

echo "Quest Marker App - API Examples"
echo "================================"
echo "Target: ${BASE_URL}"
echo ""

# 1. Check health
echo "1. Checking Quest health status..."
curl -s "${BASE_URL}/api/health" | python3 -m json.tool
echo ""

# 2. Send single coordinate
echo "2. Sending single coordinate..."
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

# 3. Send multiple coordinates
echo "3. Sending multiple coordinates..."
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

# 4. Clear all markers
echo "4. To clear all markers, use:"
echo "   curl -X POST ${BASE_URL}/api/clear"
echo ""

echo "Done!"
