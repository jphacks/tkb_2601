"""扉 2 の出口。door2 がインターネットに出るときに必ず通る、許可した宛先だけを通す中継（HTTPS の CONNECT だけ）。

- door2 は外への道がない door2net にだけいる。外へ出られるのは、door2net と egress の両方にいるこの中継だけ。
- 通すのは「CONNECT 許可した名前:443」だけ。IP アドレスの直書き、443 以外、普通の HTTP、許可にない名前は 403。
- 名前を引いた先が内側の住所（127.0.0.1 や 192.168.x.x など）なら通さない（名前のすり替えで内側を狙うのを防ぐ）。
- 中身は TLS のまま流すだけで、読まない（API キーやパスワードはこの中継から見えない）。記録するのは宛先の名前だけ。
- しばらく使われなければ自分で終わる（閉じるのは自動）。PC 側の PSoC 6 中継も、door2 が終わると消す。

使い方（コンテナの中）: python egress_proxy.py   設定: EGRESS_ALLOW（カンマ区切り）、EGRESS_PORT、EGRESS_IDLE_EXIT、
EGRESS_INTERNAL（社内の PDS など、社内・VPN の住所につないでよい名前。EGRESS_ALLOW にも入れる）
"""
from __future__ import annotations

import asyncio
import ipaddress
import os
import socket
import sys
import time
from datetime import datetime

DEFAULT_ALLOW = ("generativelanguage.googleapis.com", "bsky.social", "public.api.bsky.app")
HEADER_LIMIT = 8192
HEADER_TIMEOUT = 10.0
CONNECT_TIMEOUT = 10.0
TUNNEL_IDLE = 60.0          # 中継の中で、この秒数なにも流れなければ切る
MAX_TUNNELS = 16


def say(message: str) -> None:
    print(f"{datetime.now():%H:%M:%S} egress {message}", flush=True)


def allow_list(text: str | None) -> frozenset[str]:
    names = [n.strip().lower().rstrip(".") for n in (text or "").split(",")]
    return frozenset(n for n in names if n) or frozenset(DEFAULT_ALLOW)


def parse_connect(head: bytes) -> tuple[str, int] | None:
    """リクエストの最初の行が「CONNECT 名前:ポート HTTP/1.x」なら (名前, ポート)。それ以外は None。"""
    try:
        line = head.split(b"\r\n", 1)[0].decode("ascii")
    except UnicodeDecodeError:
        return None
    parts = line.split(" ")
    if len(parts) != 3 or parts[0] != "CONNECT" or not parts[2].startswith("HTTP/1."):
        return None
    host, sep, port = parts[1].rpartition(":")
    if not sep or not port.isdigit():
        return None
    return host.lower().rstrip("."), int(port)


def decide(host: str, port: int, allow: frozenset[str]) -> str | None:
    """通してよければ None、だめなら理由。"""
    if port != 443:
        return "443 以外のポート"
    try:
        ipaddress.ip_address(host.strip("[]"))
        return "IP アドレスの直書き"
    except ValueError:
        pass
    if host not in allow:
        return "許可にない宛先"
    return None


def is_public(address: str) -> bool:
    return ipaddress.ip_address(address).is_global


def is_vpn(address: str) -> bool:
    """社内（VPN）の住所。自分自身・リンクローカル（169.254.x、クラウドの管理用）・未指定は含めない。"""
    ip = ipaddress.ip_address(address)
    return ip.is_private and not (ip.is_loopback or ip.is_link_local or ip.is_unspecified or ip.is_multicast)


class EgressProxy:
    def __init__(self, allow: frozenset[str], resolve=None, public=is_public, idle_exit: float = 30.0, connect=None,
                 internal: frozenset[str] = frozenset()):
        self.allow = allow
        self.internal = internal   # 社内の PDS など、社内（VPN）の住所につないでよい名前（allow にも入っていること）
        self.resolve = resolve or self._resolve
        self.public = public
        self.connect = connect or asyncio.open_connection
        self.idle_exit = idle_exit
        self.active = 0
        self.last_activity = time.monotonic()

    @staticmethod
    async def _resolve(host: str) -> list[str]:
        infos = await asyncio.get_running_loop().getaddrinfo(host, 443, type=socket.SOCK_STREAM)
        return [info[4][0] for info in infos]

    async def handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
        self.active += 1
        self.last_activity = time.monotonic()
        try:
            await self._handle(reader, writer)
        finally:
            self.active -= 1
            self.last_activity = time.monotonic()
            writer.close()

    async def _handle(self, reader, writer) -> None:
        try:
            head = await asyncio.wait_for(reader.readuntil(b"\r\n\r\n"), HEADER_TIMEOUT)
        except (asyncio.IncompleteReadError, asyncio.LimitOverrunError, asyncio.TimeoutError, ConnectionError):
            return
        target = parse_connect(head)
        if target is None:
            say("拒否: CONNECT ではない（普通の HTTP は通さない）")
            return await self._refuse(writer, 405, "Method Not Allowed")
        host, port = target
        reason = decide(host, port, self.allow) if self.active <= MAX_TUNNELS else "同時に使える数を超えた"
        if reason:
            say(f"拒否: {host}:{port}（{reason}）")
            return await self._refuse(writer, 403, "Forbidden")
        try:
            addresses = await asyncio.wait_for(self.resolve(host), CONNECT_TIMEOUT)
        except (OSError, asyncio.TimeoutError):
            say(f"失敗: {host} の名前を引けなかった")
            return await self._refuse(writer, 502, "Bad Gateway")
        public = [a for a in addresses if self.public(a) or (host in self.internal and is_vpn(a))]
        if not public:
            say(f"拒否: {host}（引いた先が内側の住所）")
            return await self._refuse(writer, 403, "Forbidden")
        try:
            up_reader, up_writer = await asyncio.wait_for(self.connect(public[0], port), CONNECT_TIMEOUT)
        except (OSError, asyncio.TimeoutError):
            say(f"失敗: {host}:{port} につながらなかった")
            return await self._refuse(writer, 502, "Bad Gateway")
        say(f"許可: {host}:{port}")
        writer.write(b"HTTP/1.1 200 Connection established\r\n\r\n")
        await writer.drain()
        try:
            await asyncio.gather(self._pipe(reader, up_writer), self._pipe(up_reader, writer))
        finally:
            up_writer.close()

    async def _pipe(self, reader, writer) -> None:
        try:
            while True:
                data = await asyncio.wait_for(reader.read(65536), TUNNEL_IDLE)
                if not data:
                    break
                self.last_activity = time.monotonic()
                writer.write(data)
                await writer.drain()
        except (ConnectionError, asyncio.TimeoutError):
            pass
        finally:
            if writer.can_write_eof():
                try:
                    writer.write_eof()
                except OSError:
                    pass

    @staticmethod
    async def _refuse(writer, code: int, text: str) -> None:
        writer.write(f"HTTP/1.1 {code} {text}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n".encode("ascii"))
        try:
            await writer.drain()
        except ConnectionError:
            pass

    def idle(self, now: float) -> bool:
        return self.active == 0 and now - self.last_activity > self.idle_exit


async def serve(proxy: EgressProxy, host: str, port: int) -> None:
    server = await asyncio.start_server(proxy.handle, host, port, limit=HEADER_LIMIT)
    say(f"待ち受け {host}:{port}。通す宛先: {', '.join(sorted(proxy.allow))}（443 だけ）")
    async with server:
        while not proxy.idle(time.monotonic()):
            await asyncio.sleep(1.0)
    say(f"{proxy.idle_exit:.0f} 秒使われなかったので終わる（扉 2 の出口を閉じた）")


def main() -> int:
    allow = allow_list(os.environ.get("EGRESS_ALLOW"))
    internal = frozenset(n.strip().lower().rstrip(".") for n in os.environ.get("EGRESS_INTERNAL", "").split(",") if n.strip()) & allow
    proxy = EgressProxy(allow, idle_exit=float(os.environ.get("EGRESS_IDLE_EXIT", "30")), internal=internal)
    asyncio.run(serve(proxy, "0.0.0.0", int(os.environ.get("EGRESS_PORT", "3128"))))
    return 0


if __name__ == "__main__":
    sys.exit(main())
