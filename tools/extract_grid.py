"""Pull one grid out of the world save and turn it into a shippable template.

Usage:  python tools/extract_grid.py <DisplayName> <out.xml> [save.sbs]

The save path can also come from the SENTIS_SAVE environment variable; the
default is the local Torch sandbox save. The save is the authority for anything
hand-built (block layout, orientations, conveyor lines, inventory flags).
Everything session-specific has to go, or the loaded template
collides with live state: identity ids, ownership, and the saved world transform (the
scenario places the grid itself).
"""
import os
import re
import sys

DEFAULT_SAVE = r"C:\SE\Instance\Saves\Torch Server\SANDBOX_0_0_0_.sbs"

RUNTIME_TAGS = [
    "EntityId", "Name", "Owner", "BuiltBy", "Id",
    "PositionAndOrientation", "Velocity", "AngularVelocity",
    "LastLinearVelocity", "LastAngularVelocity", "Physics",
]


def extract(display_name, out_path, save_path):
    src = open(save_path, encoding="utf-8-sig", errors="replace").read()

    at = src.find("<DisplayName>%s</DisplayName>" % display_name)
    if at < 0:
        sys.exit("grid %r not in the save" % display_name)

    # The save writes every entity as <MyObjectBuilder_EntityBase xsi:type="..."></MyObjectBuilder_EntityBase>;
    # the template loader wants the concrete root tag instead.
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

    for tag in RUNTIME_TAGS:
        # self-closing (<Tag ... />) and paired (<Tag>...</Tag>) forms both occur; the
        # non-greedy paired form must not cross into a sibling of the same name
        grid = re.sub(r"[ \t]*<%s\b[^>]*/>\s*" % tag, "", grid)
        grid = re.sub(r"[ \t]*<%s\b[^>]*>.*?</%s>\s*" % (tag, tag), "", grid, flags=re.S)

    open(out_path, "w", encoding="utf-8", newline="\n").write(grid + "\n")

    blocks = len(re.findall(r"<MyObjectBuilder_CubeBlock\b", grid))
    lines = len(re.findall(r"<MyObjectBuilder_ConveyorLine\b", grid))
    welders = len(re.findall(r'MyObjectBuilder_ShipWelder"', grid))
    items = len(re.findall(r"<MyObjectBuilder_InventoryItem>", grid))
    print("%s -> %s: %d blocks, %d conveyor lines, %d welders, %d inventory items"
          % (display_name, out_path, blocks, lines, welders, items))


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        sys.exit(__doc__)
    save = sys.argv[3] if len(sys.argv) == 4 else os.environ.get("SENTIS_SAVE", DEFAULT_SAVE)
    extract(sys.argv[1], sys.argv[2], save)
