"""扉 2 の出口のテスト: python -m unittest discover -s core/egress"""
import asyncio
import unittest

from egress_proxy import EgressProxy, allow_list, decide, parse_connect

ALLOW = allow_list(None)


class RuleTest(unittest.TestCase):
    def test_reads_only_connect(self):
        self.assertEqual(parse_connect(b"CONNECT bsky.social:443 HTTP/1.1\r\nHost: x\r\n\r\n"), ("bsky.social", 443))
        self.assertEqual(parse_connect(b"CONNECT BSKY.social.:443 HTTP/1.1\r\n\r\n"), ("bsky.social", 443))
        self.assertIsNone(parse_connect(b"GET http://bsky.social/ HTTP/1.1\r\n\r\n"))
        self.assertIsNone(parse_connect(b"CONNECT bsky.social HTTP/1.1\r\n\r\n"))
        self.assertIsNone(parse_connect(b"\xff\xfe\r\n\r\n"))

    def test_only_listed_names_on_443(self):
        self.assertIsNone(decide("generativelanguage.googleapis.com", 443, ALLOW))
        self.assertIsNone(decide("public.api.bsky.app", 443, ALLOW))
        self.assertIsNotNone(decide("bsky.social", 80, ALLOW))
        self.assertIsNotNone(decide("example.com", 443, ALLOW))
        self.assertIsNotNone(decide("evil.bsky.social", 443, ALLOW))     # 似た名前も通さない
        self.assertIsNotNone(decide("142.250.0.1", 443, ALLOW))
        self.assertIsNotNone(decide("[2001:db8::1]", 443, ALLOW))

    def test_allow_list_from_env(self):
        self.assertEqual(allow_list("A.example, b.example. ,"), frozenset({"a.example", "b.example"}))
        self.assertEqual(allow_list(""), ALLOW)


class TunnelTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        async def echo(reader, writer):
            writer.write(await reader.read(100))
            await writer.drain()
            writer.close()

        self.upstream = await asyncio.start_server(echo, "127.0.0.1", 0)
        up_port = self.upstream.sockets[0].getsockname()[1]
        self.dialed = []

        async def connect(address, port):
            self.dialed.append((address, port))
            return await asyncio.open_connection("127.0.0.1", up_port)

        self.addresses = ["104.18.1.1"]

        async def resolve(host):
            return self.addresses

        self.proxy = EgressProxy(ALLOW, resolve=resolve, connect=connect, idle_exit=0.2)
        self.server = await asyncio.start_server(self.proxy.handle, "127.0.0.1", 0)
        self.port = self.server.sockets[0].getsockname()[1]

    async def asyncTearDown(self):
        self.server.close()
        self.upstream.close()

    async def ask(self, head: bytes, body: bytes = b"") -> bytes:
        reader, writer = await asyncio.open_connection("127.0.0.1", self.port)
        writer.write(head + body)
        await writer.drain()
        data = await asyncio.wait_for(reader.read(1000), 2)
        if body and data.endswith(b"\r\n\r\n"):
            data += await asyncio.wait_for(reader.read(1000), 2)
        writer.close()
        return data

    async def test_listed_name_is_tunnelled(self):
        data = await self.ask(b"CONNECT bsky.social:443 HTTP/1.1\r\n\r\n", b"hello")
        self.assertTrue(data.startswith(b"HTTP/1.1 200"))
        self.assertTrue(data.endswith(b"hello"))
        self.assertEqual(self.dialed, [("104.18.1.1", 443)])

    async def test_other_names_and_plain_http_are_refused(self):
        self.assertTrue((await self.ask(b"CONNECT example.com:443 HTTP/1.1\r\n\r\n")).startswith(b"HTTP/1.1 403"))
        self.assertTrue((await self.ask(b"GET http://bsky.social/ HTTP/1.1\r\n\r\n")).startswith(b"HTTP/1.1 405"))
        self.assertEqual(self.dialed, [])

    async def test_name_pointing_inside_is_refused(self):
        self.addresses = ["10.0.0.5", "127.0.0.1"]
        self.assertTrue((await self.ask(b"CONNECT bsky.social:443 HTTP/1.1\r\n\r\n")).startswith(b"HTTP/1.1 403"))
        self.assertEqual(self.dialed, [])

    async def test_closes_itself_when_unused(self):
        self.proxy.last_activity = 100.0
        self.assertFalse(self.proxy.idle(100.1))
        self.assertTrue(self.proxy.idle(100.3))
        self.proxy.active = 1                                          # 使っている間は終わらない
        self.assertFalse(self.proxy.idle(1000.0))



class InternalPdsTest(unittest.TestCase):
    """社内（VPN）の PDS: EGRESS_INTERNAL に書いた名前だけ、社内の住所につないでよい。"""

    def test_only_listed_internal_names_reach_private_addresses(self):
        import asyncio
        from egress_proxy import EgressProxy, is_vpn
        self.assertTrue(is_vpn("10.8.0.5"))
        for bad in ("127.0.0.1", "169.254.169.254", "0.0.0.0", "8.8.8.8"):
            self.assertFalse(is_vpn(bad), bad)
        allow = frozenset({"pds.robomori.internal", "bsky.social"})

        async def attempt(proxy, host):
            reader = asyncio.StreamReader()
            reader.feed_data(f"CONNECT {host}:443 HTTP/1.1\r\n\r\n".encode())
            reader.feed_eof()
            sent = []

            class Writer:
                def write(self, data): sent.append(data)
                async def drain(self): pass
                def close(self): pass
            await proxy._handle(reader, Writer())
            return b"".join(sent)

        async def resolve(host):
            return ["10.8.0.5"]

        async def connect(address, port):
            raise OSError("この試験ではつながない")
        plain = EgressProxy(allow, resolve=resolve, connect=connect)
        self.assertIn(b"403", asyncio.run(attempt(plain, "pds.robomori.internal")), "既定では社内の住所は断る")
        opened = EgressProxy(allow, resolve=resolve, connect=connect, internal=frozenset({"pds.robomori.internal"}))
        self.assertIn(b"502", asyncio.run(attempt(opened, "pds.robomori.internal")), "許した名前は、つなぎに行く（ここではつながらないので 502）")
        self.assertIn(b"403", asyncio.run(attempt(opened, "bsky.social")), "ほかの名前が社内の住所を引いても断る")

if __name__ == "__main__":
    unittest.main()
