import http.server, os, sys

ROOT = os.path.dirname(os.path.abspath(__file__))
REPORT = os.path.join(ROOT, "report.txt")

class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode("utf-8", "replace")
        with open(REPORT, "w", encoding="utf-8") as f:
            f.write(body)
        self.send_response(204)
        self.end_headers()
        print("REPORT RECEIVED", len(body), "bytes", flush=True)

    def log_message(self, fmt, *args):
        sys.stderr.write("HTTP %s\n" % (fmt % args))

if __name__ == "__main__":
    http.server.HTTPServer(("127.0.0.1", 8765), H).serve_forever()
