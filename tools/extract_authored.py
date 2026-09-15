"""Pull a hand-built grid out of the world save keeping the place the operator parked it.

Usage:  python tools/extract_authored.py <DisplayName> <out.xml> [save.sbs]

The save path can also come from the SENTIS_SAVE environment variable; the
default is the local Torch sandbox save.
Same trimming as extract_grid.py (identity ids, ownership, physics, velocity all go, or the
loaded copy fights live state), except the grid's own <PositionAndOrientation> survives: for
the mixed_weld bench the operator arranged platform and welding boat relative to each other by
hand, and that relationship - which side of the plate the tools come from - is the thing under
test, so the scenario reproduces the saved placement instead of inventing one.
"""
import os
import re
import sys

DEFAULT_SAVE = r"C:\SE\Instance\Saves\Torch Server\SANDBOX_0_0_0_.sbs"

RUNTIME_TAGS = [
    "EntityId", "Name", "Owner", "BuiltBy", "Id",
    "Velocity", "AngularVelocity",
    "LastLinearVelocity", "LastAngularVelocity", "Physics",
]


def extract(display_name, out_path, save_path):
    src = open(save_path, encoding="utf-8-sig", errors="replace").read()

    at = src.find("<DisplayName>%s</DisplayName>" % display_name)
    if at < 0:
        sys.exit("grid %r not in the save" % display_name)

    start = end = -1
    for m in re.finditer(r"<MyObjectBuilder_EntityBase\b[^>]*MyObjectBuilder_CubeGrid\">", src[:at]):
        start = m.start()
    if start >= 0:
        end = src.find("</MyObjectBuilder_EntityBase>", at)
    if start < 0 or end < 0:
        sys.exit("unbounded grid element around %r" % display_name)
    grid = src[start:end + len("</MyObjectBuilder_EntityBase>")]

    grid = re.sub(r"^<MyObjectBuilder_EntityBase\b[^>]*>",
                  '<?xml version="1.0" encoding="utf-8"?>\n'
                  '<MyObjectBuilder_CubeGrid xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">',
                  grid)
    grid = grid.replace("</MyObjectBuilder_EntityBase>", "</MyObjectBuilder_CubeGrid>")

    # The grid's own transform is the point of this extractor, so the strip list is applied only
    # inside the projector's embedded blueprint, where a transform is runtime noise: the projector
    # places its projection with ProjectionOffset, not with the blueprint's saved position.
    head, sep, tail = grid.partition("<ProjectedGrids>")
    body = sep + tail
    for tag in RUNTIME_TAGS + ["PositionAndOrientation"]:
        body = re.sub(r"[ \t]*<%s\b[^>]*/>\s*" % tag, "", body)
        body = re.sub(r"[ \t]*<%s\b[^>]*>.*?</%s>\s*" % (tag, tag), "", body, flags=re.S)
    for tag in RUNTIME_TAGS:
        head = re.sub(r"[ \t]*<%s\b[^>]*/>\s*" % tag, "", head)
        head = re.sub(r"[ \t]*<%s\b[^>]*>.*?</%s>\s*" % (tag, tag), "", head, flags=re.S)
    grid = head + body

    open(out_path, "w", encoding="utf-8", newline="\n").write(grid + "\n")

    m = re.search(r'<Position x="([-\d.]+)" y="([-\d.]+)" z="([-\d.]+)"\s*/>\s*'
                  r'<Forward x="([-\d.]+)" y="([-\d.]+)" z="([-\d.]+)"\s*/>\s*'
                  r'<Up x="([-\d.]+)" y="([-\d.]+)" z="([-\d.]+)"', grid)
    print("%s -> %s: %d blocks, %d welders, placement %s" % (
        display_name, out_path,
        len(re.findall(r"<MyObjectBuilder_CubeBlock\b", grid)),
        len(re.findall(r'MyObjectBuilder_ShipWelder"', grid)),
        m.groups() if m else "none"))


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        sys.exit(__doc__)
    save = sys.argv[3] if len(sys.argv) == 4 else os.environ.get("SENTIS_SAVE", DEFAULT_SAVE)
    extract(sys.argv[1], sys.argv[2], save)
