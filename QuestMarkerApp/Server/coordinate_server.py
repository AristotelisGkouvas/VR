#!/usr/bin/env python3
"""
Example server for sending coordinates to Quest Marker App
This script demonstrates how to send coordinate data to the Quest 3 headset

Usage:
    1. Start the Quest app and note the displayed IP address
    2. Run this script: python coordinate_server.py
    3. Enter coordinates in the interactive mode, or use the API
"""

from flask import Flask, jsonify, request
from flask_cors import CORS
import json
import time
import threading
import requests
import argparse

app = Flask(__name__)
CORS(app)

# Storage for coordinates
coordinates_store = []

# Quest headset connection settings
QUEST_IP = "192.168.1.100"  # Replace with your Quest's IP
QUEST_PORT = 8080

def get_quest_url():
    return f"http://{QUEST_IP}:{QUEST_PORT}/api/coordinates"


# ==================== API Endpoints ====================

@app.route('/api/health', methods=['GET'])
def health():
    """Health check endpoint"""
    return jsonify({
        "status": "healthy",
        "timestamp": int(time.time() * 1000)
    })


@app.route('/api/coordinates', methods=['GET'])
def get_coordinates():
    """Get all stored coordinates"""
    return jsonify({
        "coordinates": coordinates_store,
        "clearExisting": False
    })


@app.route('/api/coordinates', methods=['POST'])
def add_coordinates():
    """Add new coordinates and optionally send to Quest"""
    data = request.get_json()
    
    if not data:
        return jsonify({"error": "No JSON data provided"}), 400
    
    coords = data.get('coordinates', [])
    clear = data.get('clearExisting', False)
    send_to_quest = data.get('sendToQuest', True)
    
    if clear:
        coordinates_store.clear()
    
    # Add new coordinates
    for coord in coords:
        # Add ID and timestamp if not present
        if 'id' not in coord:
            coord['id'] = f"coord_{int(time.time() * 1000)}_{len(coordinates_store)}"
        if 'timestamp' not in coord:
            coord['timestamp'] = int(time.time() * 1000)
        
        coordinates_store.append(coord)
    
    # Optionally send to Quest
    if send_to_quest:
        try:
            response = send_to_quest_headset(coords, clear)
            return jsonify({
                "success": True,
                "stored": len(coords),
                "quest_response": response
            })
        except Exception as e:
            return jsonify({
                "success": True,
                "stored": len(coords),
                "quest_error": str(e)
            })
    
    return jsonify({
        "success": True,
        "stored": len(coords)
    })


@app.route('/api/coordinates/clear', methods=['POST'])
def clear_coordinates():
    """Clear all stored coordinates"""
    coordinates_store.clear()
    
    # Also clear on Quest
    try:
        requests.post(
            f"http://{QUEST_IP}:{QUEST_PORT}/api/clear",
            timeout=5
        )
    except:
        pass
    
    return jsonify({"success": True, "message": "All coordinates cleared"})


@app.route('/api/send-to-quest', methods=['POST'])
def send_stored_to_quest():
    """Send all stored coordinates to Quest"""
    try:
        response = send_to_quest_headset(coordinates_store, True)
        return jsonify({
            "success": True,
            "sent": len(coordinates_store),
            "quest_response": response
        })
    except Exception as e:
        return jsonify({
            "success": False,
            "error": str(e)
        }), 500


@app.route('/api/quest-status', methods=['GET'])
def quest_status():
    """Check Quest headset connection status"""
    try:
        response = requests.get(
            f"http://{QUEST_IP}:{QUEST_PORT}/api/health",
            timeout=5
        )
        return jsonify({
            "connected": True,
            "quest_ip": QUEST_IP,
            "quest_port": QUEST_PORT,
            "response": response.json()
        })
    except Exception as e:
        return jsonify({
            "connected": False,
            "quest_ip": QUEST_IP,
            "quest_port": QUEST_PORT,
            "error": str(e)
        })


# ==================== Calibration Endpoints ====================

@app.route('/api/calibrate/start', methods=['POST'])
def start_calibration():
    """Start calibration mode on Quest - user will point at a known location"""
    try:
        response = requests.post(
            f"http://{QUEST_IP}:{QUEST_PORT}/api/calibrate/start",
            timeout=5
        )
        return jsonify({
            "success": True,
            "message": "Calibration mode started on Quest. Point controller at known location and press trigger.",
            "quest_response": response.json()
        })
    except Exception as e:
        return jsonify({
            "success": False,
            "error": str(e)
        }), 500


@app.route('/api/calibrate', methods=['POST'])
def calibrate():
    """
    Send real-world coordinates for calibration point.
    After user points at a location on Quest, send the real coordinates here.
    
    JSON body:
    {
        "realX": 10.5,      # Real-world X coordinate (meters)
        "realY": 0.0,       # Real-world Y (height)
        "realZ": 5.0,       # Real-world Z coordinate (meters)
        "rotationY": 0,     # Optional: Y-axis rotation offset (degrees)
        "quickCalibrate": true,  # Optional: single-point quick calibration
        "coordinateSystem": "local"  # "local", "gps", or "utm"
    }
    """
    data = request.get_json()
    
    if not data:
        return jsonify({"error": "No JSON data provided"}), 400
    
    # Validate required fields
    if 'realX' not in data or 'realZ' not in data:
        return jsonify({"error": "Missing realX or realZ coordinates"}), 400
    
    calibration_data = {
        "realX": float(data.get('realX', 0)),
        "realY": float(data.get('realY', 0)),
        "realZ": float(data.get('realZ', 0)),
        "rotationY": float(data.get('rotationY', 0)),
        "quickCalibrate": bool(data.get('quickCalibrate', True)),
        "coordinateSystem": data.get('coordinateSystem', 'local')
    }
    
    try:
        response = requests.post(
            f"http://{QUEST_IP}:{QUEST_PORT}/api/calibrate",
            json=calibration_data,
            headers={"Content-Type": "application/json"},
            timeout=10
        )
        return jsonify({
            "success": True,
            "message": "Calibration data sent to Quest",
            "calibration": calibration_data,
            "quest_response": response.json()
        })
    except Exception as e:
        return jsonify({
            "success": False,
            "error": str(e)
        }), 500


@app.route('/api/calibrate/status', methods=['GET'])
def calibration_status():
    """Check calibration status on Quest"""
    try:
        response = requests.get(
            f"http://{QUEST_IP}:{QUEST_PORT}/api/calibrate/status",
            timeout=5
        )
        return jsonify({
            "success": True,
            "quest_response": response.json()
        })
    except Exception as e:
        return jsonify({
            "success": False,
            "error": str(e)
        })


# ==================== Helper Functions ====================

def send_to_quest_headset(coordinates, clear_existing=False):
    """Send coordinates to the Quest headset"""
    payload = {
        "coordinates": coordinates,
        "clearExisting": clear_existing
    }
    
    response = requests.post(
        get_quest_url(),
        json=payload,
        headers={"Content-Type": "application/json"},
        timeout=10
    )
    
    return response.json()


def interactive_mode():
    """Interactive command-line mode for adding coordinates"""
    print("\n" + "="*50)
    print("Quest Marker App - Interactive Mode")
    print("="*50)
    print("\nCommands:")
    print("  add x y z [label] [color]  - Add a coordinate")
    print("  list                        - List all coordinates")
    print("  send                        - Send all to Quest")
    print("  clear                       - Clear all coordinates")
    print("  status                      - Check Quest connection")
    print("  calibrate x y z [rotation]  - Quick calibrate with real coords")
    print("  startcal                    - Start calibration mode on Quest")
    print("  quit                        - Exit")
    print("\nExample: add 1.5 0.5 2.0 Point1 #FF0000")
    print("Example: calibrate 10.0 0.0 5.0 45")
    print("="*50 + "\n")
    
    while True:
        try:
            cmd = input(">>> ").strip()
            if not cmd:
                continue
            
            parts = cmd.split()
            action = parts[0].lower()
            
            if action == "quit" or action == "exit":
                print("Goodbye!")
                break
            
            elif action == "add":
                if len(parts) < 4:
                    print("Usage: add x y z [label] [color]")
                    continue
                
                try:
                    x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                    label = parts[4] if len(parts) > 4 else None
                    color = parts[5] if len(parts) > 5 else "#00FFFF"
                    
                    coord = {
                        "id": f"coord_{int(time.time() * 1000)}",
                        "x": x,
                        "y": y,
                        "z": z,
                        "label": label,
                        "color": color,
                        "scale": 0.1,
                        "timestamp": int(time.time() * 1000)
                    }
                    
                    coordinates_store.append(coord)
                    print(f"Added coordinate at ({x}, {y}, {z})")
                    
                    # Immediately send to Quest
                    try:
                        send_to_quest_headset([coord], False)
                        print("  -> Sent to Quest")
                    except Exception as e:
                        print(f"  -> Quest send failed: {e}")
                        
                except ValueError:
                    print("Invalid coordinates. Use numbers for x, y, z")
            
            elif action == "list":
                if not coordinates_store:
                    print("No coordinates stored")
                else:
                    for i, c in enumerate(coordinates_store):
                        print(f"  {i+1}. ({c['x']}, {c['y']}, {c['z']}) - {c.get('label', 'No label')}")
            
            elif action == "send":
                try:
                    send_to_quest_headset(coordinates_store, True)
                    print(f"Sent {len(coordinates_store)} coordinates to Quest")
                except Exception as e:
                    print(f"Failed to send: {e}")
            
            elif action == "clear":
                coordinates_store.clear()
                try:
                    requests.post(f"http://{QUEST_IP}:{QUEST_PORT}/api/clear", timeout=5)
                    print("Cleared all coordinates (local and Quest)")
                except:
                    print("Cleared local coordinates (Quest clear failed)")
            
            elif action == "status":
                try:
                    r = requests.get(f"http://{QUEST_IP}:{QUEST_PORT}/api/health", timeout=5)
                    print(f"Quest connected: {QUEST_IP}:{QUEST_PORT}")
                    print(f"Response: {r.json()}")
                except Exception as e:
                    print(f"Quest not reachable: {e}")
            
            elif action == "calibrate":
                if len(parts) < 4:
                    print("Usage: calibrate x y z [rotation_y]")
                    print("  x, y, z = real-world coordinates in meters")
                    print("  rotation_y = optional Y-axis rotation in degrees")
                    continue
                
                try:
                    x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
                    rotation = float(parts[4]) if len(parts) > 4 else 0
                    
                    calibration_data = {
                        "realX": x,
                        "realY": y,
                        "realZ": z,
                        "rotationY": rotation,
                        "quickCalibrate": True,
                        "coordinateSystem": "local"
                    }
                    
                    response = requests.post(
                        f"http://{QUEST_IP}:{QUEST_PORT}/api/calibrate",
                        json=calibration_data,
                        timeout=10
                    )
                    print(f"Calibration sent: ({x}, {y}, {z}) rotation={rotation}°")
                    print(f"Response: {response.json()}")
                except ValueError:
                    print("Invalid coordinates. Use numbers.")
                except Exception as e:
                    print(f"Calibration failed: {e}")
            
            elif action == "startcal":
                try:
                    response = requests.post(
                        f"http://{QUEST_IP}:{QUEST_PORT}/api/calibrate/start",
                        timeout=5
                    )
                    print("Calibration mode started on Quest!")
                    print("Point controller at a known location and press trigger.")
                    print("Then use: calibrate x y z [rotation]")
                except Exception as e:
                    print(f"Failed to start calibration: {e}")
            
            else:
                print(f"Unknown command: {action}")
                
        except KeyboardInterrupt:
            print("\nGoodbye!")
            break
        except Exception as e:
            print(f"Error: {e}")


# ==================== Main ====================

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Quest Marker App Coordinate Server")
    parser.add_argument("--port", type=int, default=5000, help="Server port")
    parser.add_argument("--quest-ip", type=str, default="192.168.1.100", help="Quest IP address")
    parser.add_argument("--quest-port", type=int, default=8080, help="Quest server port")
    parser.add_argument("--interactive", action="store_true", help="Start in interactive mode")
    
    args = parser.parse_args()
    
    QUEST_IP = args.quest_ip
    QUEST_PORT = args.quest_port
    
    print(f"\n{'='*50}")
    print("Quest Marker App - Coordinate Server")
    print(f"{'='*50}")
    print(f"Server running at: http://localhost:{args.port}")
    print(f"Quest target: http://{QUEST_IP}:{QUEST_PORT}")
    print(f"{'='*50}\n")
    
    if args.interactive:
        # Start Flask in a background thread
        flask_thread = threading.Thread(
            target=lambda: app.run(host="0.0.0.0", port=args.port, debug=False),
            daemon=True
        )
        flask_thread.start()
        
        # Run interactive mode
        interactive_mode()
    else:
        app.run(host="0.0.0.0", port=args.port, debug=True)
