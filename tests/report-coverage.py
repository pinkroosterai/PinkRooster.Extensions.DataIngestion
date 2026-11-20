import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main(report_directory, report_name):
    reports = list(report_directory.rglob(report_name))
    if len(reports) != 1:
        raise SystemExit(f"Expected one {report_name} report, found {len(reports)} in {report_directory}")

    lines = {}
    for source_class in ET.parse(reports[0]).findall(".//class"):
        filename = source_class.attrib["filename"]
        for line in source_class.findall("lines/line"):
            key = (filename, int(line.attrib["number"]))
            lines[key] = lines.get(key, False) or int(line.attrib["hits"]) > 0

    if not lines:
        raise SystemExit("The coverage report contains no source lines.")

    covered = sum(lines.values())
    print(f"Line coverage of {report_name}: {covered}/{len(lines)} ({100 * covered / len(lines):.2f}%), reported only.")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: report-coverage.py <report-directory> <report-file-name>")
    main(Path(sys.argv[1]), sys.argv[2])
