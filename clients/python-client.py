"""pip install websockets"""
import argparse
import asyncio
import json
import websockets

async def main(token: str, port: int, cwd: str, shell: str) -> None:
    uri = f"ws://127.0.0.1:{port}/ws?token={token}"
    async with websockets.connect(uri, max_size=4 * 1024 * 1024) as ws:
        print(await ws.recv())
        await ws.send(json.dumps({
            "action": "create", "requestId": "create-1",
            "shell": shell, "cwd": cwd, "cols": 120, "rows": 30
        }))
        created = json.loads(await ws.recv())
        session_id = created["session"]["id"]
        print("session:", session_id)
        await ws.send(json.dumps({
            "action": "write", "requestId": "write-1",
            "sessionId": session_id, "data": "Get-ChildItem\r"
        }))
        async for raw in ws:
            message = json.loads(raw)
            if message.get("type") == "output" and message.get("sessionId") == session_id:
                print(message.get("data", ""), end="", flush=True)
            else:
                print("\n", message)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--token", required=True)
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--cwd", default=".")
    parser.add_argument("--shell", choices=["pwsh", "powershell", "cmd"], default="pwsh")
    args = parser.parse_args()
    asyncio.run(main(args.token, args.port, args.cwd, args.shell))
