import re
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPTS = ROOT / "fieldlab" / "scripts"

ORCHESTRATOR = "Invoke-NativeValheimCutoverScenario.ps1"
CLIENT = "Invoke-NativeValheimClient.ps1"
C7_CELLS = "Invoke-C7ColdJoinNegativeCells.ps1"

# (caller, callee, parameter) for every value a caller accepts from an operator and
# then hands straight to a callee's like-named parameter. A caller can only promise
# what its callee will accept, so the caller's ValidateRange must be a subset of the
# callee's -- otherwise the caller advertises a range that aborts mid-run on
# parameter validation, after the earlier steps have already changed remote state.
# That is exactly how -WaitSeconds 1800 died at the i5 queue-smoke step on
# 2026-07-31 (fieldlab/runs/native-valheim/native-20260731-c8-full25/).
FORWARDED = [
    (ORCHESTRATOR, CLIENT, "WaitSeconds"),
    (ORCHESTRATOR, CLIENT, "HoldSeconds"),
    (C7_CELLS, CLIENT, "WaitSeconds"),
]

VALIDATE_RANGE = re.compile(
    r"\[ValidateRange\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)\]\s*"
    r"(?:\[[^\]]+\]\s*)+\$(\w+)"
)
PARAM_NAME = re.compile(r"^\s*(?:\[[^\]]+\]\s*)*\[[^\]]+\]\s*\$(\w+)", re.MULTILINE)


def _script_param_block(name):
    """Return the top-level param(...) block, so function-scoped params are ignored."""
    text = (SCRIPTS / name).read_text(encoding="utf-8-sig")
    match = re.search(r"^param\(", text, re.MULTILINE)
    if match is None:
        raise AssertionError(f"{name} has no top-level param( block")
    depth = 0
    for index in range(match.end() - 1, len(text)):
        if text[index] == "(":
            depth += 1
        elif text[index] == ")":
            depth -= 1
            if depth == 0:
                return text[match.end() : index]
    raise AssertionError(f"{name} has an unbalanced top-level param( block")


def _ranges(name):
    block = _script_param_block(name)
    return {
        parameter: (int(low), int(high))
        for low, high, parameter in VALIDATE_RANGE.findall(block)
    }


def _declared(name):
    return set(PARAM_NAME.findall(_script_param_block(name)))


class ForwardedParameterRangeTests(unittest.TestCase):
    def test_forwarded_parameters_are_declared_on_both_sides(self):
        for caller, callee, parameter in FORWARDED:
            for script in (caller, callee):
                self.assertIn(
                    parameter,
                    _declared(script),
                    f"{script} no longer declares -{parameter}; "
                    f"the {caller} -> {callee} forwarding table in {Path(__file__).name} is stale",
                )

    def test_caller_range_is_a_subset_of_callee_range(self):
        violations = []
        for caller, callee, parameter in FORWARDED:
            caller_range = _ranges(caller).get(parameter)
            callee_range = _ranges(callee).get(parameter)
            if callee_range is None:
                continue
            if caller_range is None:
                violations.append(
                    f"{caller} -{parameter} is unbounded but {callee} requires "
                    f"{callee_range[0]}..{callee_range[1]}"
                )
                continue
            if caller_range[0] < callee_range[0] or caller_range[1] > callee_range[1]:
                violations.append(
                    f"{caller} -{parameter} accepts {caller_range[0]}..{caller_range[1]} "
                    f"but forwards to {callee} which accepts "
                    f"{callee_range[0]}..{callee_range[1]}"
                )
        self.assertEqual(
            [],
            violations,
            "Callers advertise ranges their callees reject:\n" + "\n".join(violations),
        )


if __name__ == "__main__":
    unittest.main()
