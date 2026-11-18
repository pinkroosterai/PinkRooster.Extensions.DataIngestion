import sys
import xml.etree.ElementTree as ET
from pathlib import Path


MINIMUM_PERCENT = 90
CORE_NAMESPACE = "PinkRooster.Extensions.DataIngestion.Chunkers."


def main(report_directory):
    reports = list(report_directory.rglob("core.cobertura.xml"))
    if len(reports) != 1:
        raise SystemExit(f"Expected one core Cobertura report, found {len(reports)} in {report_directory}")

    lines = {}
    for source_class in ET.parse(reports[0]).findall(".//class"):
        name = source_class.attrib["name"]
        if not name.startswith(CORE_NAMESPACE):
            raise SystemExit(f"Unexpected class in core report: {name}")

        filename = source_class.attrib["filename"]
        for line in source_class.findall("lines/line"):
            key = (filename, int(line.attrib["number"]))
            lines[key] = lines.get(key, False) or int(line.attrib["hits"]) > 0

    if not lines:
        raise SystemExit("The coverage report contains no core source lines.")

    covered = sum(lines.values())
    total = len(lines)
    print(f"Core line coverage: {covered}/{total} ({100 * covered / total:.2f}%), minimum {MINIMUM_PERCENT}%.")
    return 0 if covered * 100 >= total * MINIMUM_PERCENT else 1


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1])))
