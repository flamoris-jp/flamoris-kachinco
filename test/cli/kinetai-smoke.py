#!/usr/bin/env python3
"""Published Linux Kinetai: CJK text + actual Glyph fixture -> PNG/pair/timeline."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def main():
    binary = str(Path(sys.argv[1]).resolve())
    with tempfile.TemporaryDirectory(prefix="kinetai-cli-") as temporary:
        root = Path(temporary)
        def run(*args, ok=True):
            result = subprocess.run([binary, *map(str, args)], text=True, capture_output=True, timeout=90)
            if ok and result.returncode:
                raise AssertionError(result.stdout + result.stderr)
            if not ok and not result.returncode:
                raise AssertionError("Invalid request succeeded")
            return json.loads(result.stdout) if ok else result
        text = root / "text.ktproj"
        run("kinetai", "new", "--text", "赤い靴 / Akino", "--width", 320, "--height", 180,
            "--duration", ".1", "--output", text)
        run("kinetai", "validate", text)
        run("kinetai", "frame", text, "--time", "0", "--output", root / "text.png")
        assert (root / "text.png").read_bytes().startswith(b"\x89PNG")
        run("kinetai", "export", text, "--output", root / "pair")
        pair = json.loads((root / "pair/text.ktpair.json").read_text())
        assert pair["frameCount"] == "3" and pair["renderer"]["backend"] == "Pango-Cairo-CPU"
        bundle = root / "bundle"
        shutil.copytree(Path(__file__).resolve().parents[1] / "Kachinco.Tests/Fixtures/glyph-demo", bundle)
        document = root / "glyph.ktproj"
        run("kinetai", "import-glyph", bundle / "manifest.json", "--width", 320, "--height", 180, "--output", document)
        # Prove the .ktproj has no ongoing filesystem dependency on the source.
        shutil.rmtree(bundle)
        motion = root / "motion.py"
        motion.write_text("def animate(t, duration, i, n, seed):\n    return {'y': 6 * sin(t * 4 + i), 'scale': 1}\n")
        animated = root / "animated.ktproj"
        run("kinetai", "script", document, "--script", motion, "--output", animated)
        result = run("kinetai", "frame", animated, "--time", "1.5", "--output", root / "glyph.png")
        assert result["renderer"]["backend"] == "Pango-Cairo-CPU"
        rgba = subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(root / "glyph.png"), "-f", "rawvideo", "-pix_fmt", "rgba", "pipe:1"])
        assert any(rgba[3::4]), "Outlined Japanese Glyph frame is empty"
        project = root / "movie.fkproj"
        run("project", "new", "--duration", "10", "--output", project)
        edited = root / "edited.fkproj"
        run("kinetai", "add", project, "--composition", animated, "--output", edited)
        run("project", "validate", edited)
        run("render", "frame", edited, "--time", "1.5", "--output", root / "timeline.png")
        run("kinetai", "frame", animated, "--time", "1.5", "--output", root / "glyph.png", ok=False)
        print("Linux Kinetai text/Glyph/script/PNG/RGB-mask/native timeline smoke passed")


if __name__ == "__main__":
    main()
