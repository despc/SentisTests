"""Pull a hand-built grid out of the world save together with the grids attached to it.

Usage:  python tools/extract_group.py <DisplayName> <out.xml> [save.sbs]

A wheeled vehicle is several grids: the chassis and one grid per wheel, joined by the
suspension's <TopBlockId> (the id of the wheel block on the wheel grid). Rotors, pistons and
hinges work the same way. extract_authored.py takes one grid; this one follows every
<TopBlockId> from the named grid (and from what it reaches) and writes all of them as a
ship blueprint (MyObjectBuilder_Definitions/ShipBlueprints), the chassis first.

Entity ids stay, because the links are made of them; the scenario remaps them for every copy
(MyEntities.RemapObjectBuilderCollection). Saved transforms stay too. Ownership, names (the
save names every block after its id), velocities and physics go, as in extract_authored.py.
"""
import os
import re
import sys

DEFAULT_SAVE = r"C:\SE\Instance\Saves\Torch Server\SANDBOX_0_0_0_.sbs"
GRID_OPEN = '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
GRID_CLOSE = "</MyObjectBuilder_EntityBase>"
RUNTIME_TAGS = ["Name", "Owner", "BuiltBy", "LinearVelocity", "AngularVelocity",
                "LastLinearVelocity", "LastAngularVelocity", "Physics"]


def grid_around(src, index):
    start = src.rfind(GRID_OPEN, 0, index)
    end = src.find(GRID_CLOSE, index)
    if start < 0 or end < 0:
        sys.exit("unbounded grid element at %d" % index)
    return start, src[start:end + len(GRID_CLOSE)]


def clean(grid):
    body = grid[len(GRID_OPEN):-len(GRID_CLOSE)]
    for tag in RUNTIME_TAGS:
        body = re.sub(r"[ \t]*<%s\b[^>]*/>\s*" % tag, "", body)
        body = re.sub(r"[ \t]*<%s\b[^>]*>.*?</%s>\s*" % (tag, tag), "", body, flags=re.S)
    return "<CubeGrid>" + body + "</CubeGrid>"


def extract(display_name, out_path, save_path):
    src = open(save_path, encoding="utf-8-sig", errors="replace").read()
    at = src.find("<DisplayName>%s</DisplayName>" % display_name)
    if at < 0:
        sys.exit("grid %r not in the save" % display_name)

    seen, grids, queue = set(), [], [grid_around(src, at)]
    while queue:
        start, grid = queue.pop(0)
        if start in seen:
            continue
        seen.add(start)
        grids.append(grid)
        for top in re.findall(r"<TopBlockId>(\d+)</TopBlockId>", grid):
            i = src.find("<EntityId>%s</EntityId>" % top)
            if i < 0:
                sys.exit("top block %s not in the save" % top)
            queue.append(grid_around(src, i))

    xml = ('<?xml version="1.0" encoding="utf-8"?>\n'
           '<Definitions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
           '  <ShipBlueprints>\n'
           '    <ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">\n'
           '      <Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="%s" />\n'
           '      <CubeGrids>\n%s\n      </CubeGrids>\n'
           '    </ShipBlueprint>\n'
           '  </ShipBlueprints>\n'
           '</Definitions>\n') % (display_name, "\n".join(clean(g) for g in grids))
    open(out_path, "w", encoding="utf-8", newline="\n").write(xml)
    print("%s -> %s: %d grids, %d blocks" % (display_name, out_path, len(grids),
                                             len(re.findall(r"<MyObjectBuilder_CubeBlock\b", xml))))


if __name__ == "__main__":
    if len(sys.argv) not in (3, 4):
        sys.exit(__doc__)
    save = sys.argv[3] if len(sys.argv) == 4 else os.environ.get("SENTIS_SAVE", DEFAULT_SAVE)
    extract(sys.argv[1], sys.argv[2], save)
