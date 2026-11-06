import re
import sys
from pathlib import Path

output = Path(sys.argv[1]).read_text()
errors = re.findall(r"^.*NegativeProbe\.kt:\d+:\d+: error: (.*)$", output, re.MULTILINE)
expected = ("type argument is not within its bounds", "value class cannot extend classes",
            "nullable receiver", "missing return statement", "is not a subtype of the return type")
assert len(errors) == len(expected), errors
for marker in expected:
    assert sum(marker in error for error in errors) == 1, (marker, errors)
assert output.count("kotc finished: COMPILATION_ERROR") == 1, output
assert "INTERNAL_ERROR" not in output, output
print("source-array diagnostic components reject invalid types, declarations, expressions and control flow: OK")
