import asyncio
import json
import socket
import struct
import sys
import threading
import time
from urllib.parse import urlparse
import websockets

HEADER_BYTES = 6
UDP_TOKEN_BYTES = 8
PRIORITY_MANIFEST_OBJECT_TYPE = 17

def parse_binary_envelope_header(buf6):
    v = int.from_bytes(buf6, "big")
    version = (v >> 44) & 0xF
    type_id = (v >> 38) & 0x3F
    lane = (v >> 37) & 0x1
    seq = (v >> 21) & 0xFFFF
    payload_len = (v >> 5) & 0xFFFF
    return version, type_id, lane, seq, payload_len

def build_noop_header():
    v = 1 << 44  # version=1, type=0, lane=0, seq=0, payload_len=0
    return v.to_bytes(6, "big")

def udp_listen(host, port, token, duration, results):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(0.5)
    sock.sendto(struct.pack("<Q", token) + build_noop_header(), (host, port))
    deadline = time.monotonic() + duration
    while time.monotonic() < deadline:
        try:
            data, _ = sock.recvfrom(65535)
        except socket.timeout:
            continue
        if len(data) < UDP_TOKEN_BYTES + HEADER_BYTES:
            continue
        frame = data[UDP_TOKEN_BYTES:]
        header, body = frame[:HEADER_BYTES], frame[HEADER_BYTES:]
        _, type_id, _, _, payload_len = parse_binary_envelope_header(header)
        if type_id != PRIORITY_MANIFEST_OBJECT_TYPE:
            continue
        try:
            obj = json.loads(body[:payload_len].decode("utf-8"))
        except Exception:
            obj = None
        results.append(obj)
    sock.close()

async def main():
    url = sys.argv[1] if len(sys.argv) > 1 else "ws://localhost:4000/"
    timeout = float(sys.argv[2]) if len(sys.argv) > 2 else 60.0
    deadline = time.monotonic() + timeout
    host = urlparse(url).hostname or "localhost"

    async with websockets.connect(url) as ws:
        try:
            msg = await asyncio.wait_for(ws.recv(), timeout=timeout)
        except asyncio.TimeoutError:
            print(f"no priority_manifest received within {timeout:g}s")
            sys.exit(1)

        session = json.loads(msg)
        payload = session.get("payload", {})
        print(f"session_started: session_id={payload.get('session_id')}, player_id={payload.get('player_id')}")

        udp_results = []
        udp_thread = threading.Thread(
            target=udp_listen,
            args=(host, int(payload["udp_port"]), int(payload["udp_token"]), 20.0, udp_results),
            daemon=True,
        )
        udp_thread.start()
        print(f"udp bound: sent handshake token to {host}:{payload['udp_port']}")

        try:
            manifest_payload = None
            while True:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    print(f"no priority_manifest received within {timeout:g}s")
                    sys.exit(1)

                msg = await asyncio.wait_for(ws.recv(), timeout=remaining)
                envelope = json.loads(msg)

                if envelope["type"] == "priority_manifest":
                    manifest_payload = envelope["payload"]
                    print(
                        f"manifest_id={manifest_payload.get('manifest_id')}, "
                        f"total_input_objects={manifest_payload.get('total_input_objects')}, "
                        f"unique_objects={manifest_payload.get('unique_objects')}, "
                        f"reliable_count={manifest_payload.get('reliable_count')}, "
                        f"datagram_count={manifest_payload.get('datagram_count')}, "
                        f"deferred_count={manifest_payload.get('deferred_count')}"
                    )
                    break

                print(f"[{envelope.get('seq')}] type={envelope.get('type')}")

            expected_datagram = manifest_payload.get("datagram_count", 0)
            print(f"waiting for datagram-lane priority_manifest_object frames (expect {expected_datagram})...")
            udp_thread.join(timeout=10)
            got = len(udp_results)
            print(f"datagram_objects_received={got} expected={expected_datagram}")
            if udp_results:
                print("sample datagram object:")
                print(json.dumps(udp_results[0], indent=2))
        except asyncio.TimeoutError:
            print(f"no priority_manifest received within {timeout:g}s")
            sys.exit(1)
        except websockets.exceptions.ConnectionClosed:
            print("connection closed unexpectedly")
            sys.exit(1)

if __name__ == "__main__":
    asyncio.run(main())
