"""Check the local viewer's HTTP preflight without starting Docker or opening accounts."""
import threading
import unittest
import urllib.request
from http.server import ThreadingHTTPServer

import pds


class ViewerHeadersTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), pds.Handler)
        cls.worker = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.worker.start()
        cls.url = "http://127.0.0.1:%s/xrpc/com.atproto.server.describeServer" % cls.server.server_port

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.worker.join(timeout=2)

    def test_local_file_preflight(self):
        request = urllib.request.Request(self.url, method="OPTIONS", headers={
            "Origin": "null", "Access-Control-Request-Private-Network": "true"})
        with urllib.request.urlopen(request, timeout=2) as response:
            self.assertEqual(response.headers["Access-Control-Allow-Private-Network"], "true")
            self.assertIn("GET", response.headers["Access-Control-Allow-Methods"])

    def test_local_viewer_response(self):
        with urllib.request.urlopen(self.url, timeout=2) as response:
            self.assertEqual(response.status, 200)
            self.assertEqual(response.headers["Access-Control-Allow-Private-Network"], "true")


if __name__ == "__main__":
    unittest.main()
