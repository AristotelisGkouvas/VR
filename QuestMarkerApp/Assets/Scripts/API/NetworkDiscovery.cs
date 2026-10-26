using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace QuestMarkerApp.API
{
    /// <summary>
    /// Lightweight UDP beacon so PCs can discover the Quest API on the LAN
    /// without typing the IP. Broadcasts every interval on port 48080.
    /// Protocol: JSON line {"service":"quest-marker","port":8080,"name":"..."}
    /// </summary>
    public class NetworkDiscovery : MonoBehaviour
    {
        public const int DefaultDiscoveryPort = 48080;

        [SerializeField] private bool enableBeacon = true;
        [SerializeField] private int discoveryPort = DefaultDiscoveryPort;
        [SerializeField] private float broadcastInterval = 2f;
        [SerializeField] private int apiPort = 8080;
        [SerializeField] private string serviceName = "quest-marker";
        [SerializeField] private string deviceName = "Quest3";

        private UdpClient udp;
        private Thread thread;
        private volatile bool running;
        private volatile byte[] payload;
        // Signalled by StopBeacon: wakes the beacon thread at once instead of after its sleep.
        // Each start gets its own event, so a thread from a previous start can never keep
        // running alongside a new one after a quick stop/start (app pause/resume).
        private ManualResetEvent stopSignal;

        public void Configure(int httpPort, string name = null)
        {
            apiPort = httpPort;
            if (!string.IsNullOrEmpty(name)) deviceName = name;
            RebuildPayload();
        }

        private void Start()
        {
            if (!enableBeacon) return;
            RebuildPayload();
            StartBeacon();
        }

        private void OnDestroy()
        {
            StopBeacon();
        }

        private void RebuildPayload()
        {
            string json =
                "{\"service\":\"" + Escape(serviceName) + "\"," +
                "\"port\":" + apiPort + "," +
                "\"name\":\"" + Escape(deviceName) + "\"," +
                "\"version\":\"1.2.0\"}";
            payload = Encoding.UTF8.GetBytes(json);
        }

        public void StartBeacon()
        {
            if (running) return;
            running = true;
            try
            {
                udp = new UdpClient();
                udp.EnableBroadcast = true;
                stopSignal = new ManualResetEvent(false);
                UdpClient client = udp;
                ManualResetEvent stop = stopSignal;
                thread = new Thread(() => BeaconLoop(client, stop)) { IsBackground = true, Name = "QuestMarkerDiscovery" };
                thread.Start();
                Debug.Log($"[Discovery] UDP beacon on port {discoveryPort} advertising API {apiPort}");
            }
            catch (Exception e)
            {
                running = false;
                try { udp?.Close(); } catch { /* ignore */ }
                udp = null;
                Debug.LogWarning($"[Discovery] Failed to start: {e.Message}");
            }
        }

        public void StopBeacon()
        {
            running = false;
            try { stopSignal?.Set(); } catch { /* ignore */ }
            try { thread?.Join(500); } catch { /* ignore */ }
            try { udp?.Close(); } catch { /* ignore */ }
            udp = null;
            thread = null;
            stopSignal = null; // not disposed: a thread that missed the join may still wait on it
        }

        private void OnApplicationPause(bool paused)
        {
            // Standalone use (no LocalAPIServer driving it): stay quiet while asleep.
            if (paused) StopBeacon();
            else if (enableBeacon) StartBeacon();
        }

        private void BeaconLoop(UdpClient client, ManualResetEvent stop)
        {
            var endpoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
            // The interval is read once per start; Configure() changes only the payload.
            int sleepMs = Math.Max(500, (int)(broadcastInterval * 1000));
            do
            {
                try
                {
                    byte[] data = payload;
                    if (data != null)
                        client.Send(data, data.Length, endpoint);
                }
                catch (ObjectDisposedException)
                {
                    return; // closed by StopBeacon
                }
                catch
                {
                    // ignore transient network errors
                }
            } while (!stop.WaitOne(sleepMs));
        }

        private static string Escape(string s) => JsonText.Escape(s);
    }
}
