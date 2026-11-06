"""Union executed source lines across local suites; never average duplicate module reports."""
from pathlib import Path
import os
import sys
import xml.etree.ElementTree as ET

PROJECTS = {"Niobium.Notification.Contracts", "Niobium.Notification.Core", "Niobium.Notification.Server"}


def collect(paths):
    result = {project: {} for project in PROJECTS}
    for path in paths:
        root = ET.parse(path).getroot()
        for package in root.findall("./packages/package"):
            name = package.attrib["name"]
            if name not in PROJECTS:
                continue
            for cls in package.findall("./classes/class"):
                filename = cls.attrib["filename"].replace("\\", "/")
                marker = f"src/{name}/"
                if marker not in filename or "/obj/" in filename or "/bin/" in filename:
                    continue  # Only authored repository application source, not generated regex/SDK code.
                relative = marker + filename.split(marker, 1)[1]
                for line in cls.findall("./lines/line"):
                    key = (relative, int(line.attrib["number"]))
                    result[name][key] = result[name].get(key, False) or int(line.attrib["hits"]) > 0
    return result


def main(paths):
    projects = collect(paths)
    output = ["| Application module | Covered / executable lines | Line coverage |", "| --- | --- | --- |"]
    failed = False
    for name, lines in sorted(projects.items()):
        covered = sum(lines.values())
        percent = 100 * covered / len(lines) if lines else 0
        output.append(f"| {name} | {covered} / {len(lines)} | {percent:.2f}% |")
        failed |= percent < 80
    total = sum(len(lines) for lines in projects.values())
    covered = sum(sum(lines.values()) for lines in projects.values())
    percent = 100 * covered / total if total else 0
    output.append(f"| Combined | {covered} / {total} | {percent:.2f}% |")
    failed |= percent < 90
    report = "\n".join(output) + "\n\nRequired: at least 90% combined and 80% in each module. Generated obj/bin sources are excluded; no authored files are excluded.\n"
    print(report)
    if "GITHUB_STEP_SUMMARY" in os.environ:
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write(report)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
