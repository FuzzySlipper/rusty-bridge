"""Writes a copy of timeline.html with a recording embedded, so it opens ready to step.

    python3 viewer/embed.py local/recordings/fight.rbrec local/fight-stepper.html
"""
import base64
import pathlib
import sys

rec, out = map(pathlib.Path, sys.argv[1:3])
page = (pathlib.Path(__file__).parent / "timeline.html").read_text()
tag = '<script id="recording" type="application/octet-stream" data-name=""></script>'
assert tag in page, "timeline.html has no recording slot"
data = base64.b64encode(rec.read_bytes()).decode()
page = page.replace(tag, f'<script id="recording" type="application/octet-stream" data-name="{rec.name}">{data}</script>')
out.write_text(page)
print(f"{out}: {out.stat().st_size // 1024} KiB")
