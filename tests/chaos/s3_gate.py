#!/usr/bin/env python3
"""CI-only S3 network gate. Forward a real PUT body prefix, then hang."""
import asyncio
from pathlib import Path

MARKER = Path("/signals/put-body-forwarded")


async def relay(reader, writer):
    try:
        while data := await reader.read(65536):
            writer.write(data)
            await writer.drain()
    except (OSError, ConnectionError):
        pass
    finally:
        writer.close()


async def serve(client_reader, client_writer):
    try:
        backend_reader, backend_writer = await asyncio.open_connection("seaweedfs", 8333)
    except OSError:
        client_writer.close()
        return
    upstream_reply = asyncio.create_task(relay(backend_reader, client_writer))
    try:
        headers = await asyncio.wait_for(client_reader.readuntil(b"\r\n\r\n"), 30)
        if len(headers) > 65536:
            raise ValueError("oversized headers")
        backend_writer.write(headers)
        await backend_writer.drain()
        if headers.startswith(b"PUT "):
            # A response relay handles HTTP 100 Continue, if needed.
            data = await asyncio.wait_for(client_reader.read(65536), 45)
            if not data:
                raise ValueError("missing PUT body")
            backend_writer.write(data[:min(len(data), 4096)])
            await backend_writer.drain()
            MARKER.write_text("body-forwarded\n", encoding="ascii")
            print("S3_CHAOS_BODY_FORWARDED", flush=True)
            await asyncio.Event().wait()  # never finish the in-flight PUT
        else:
            await relay(client_reader, backend_writer)
    except (asyncio.IncompleteReadError, asyncio.TimeoutError, ValueError, ConnectionError, OSError):
        pass
    finally:
        backend_writer.close()
        upstream_reply.cancel()
        await asyncio.gather(upstream_reply, return_exceptions=True)
        client_writer.close()


async def main():
    MARKER.unlink(missing_ok=True)
    server = await asyncio.start_server(serve, "0.0.0.0", 8334)
    print("S3_CHAOS_READY", flush=True)
    async with server:
        await server.serve_forever()


asyncio.run(main())
