#!/usr/bin/env bash
# Self-test the shared expected-failure baseline contract. Real gates normally have no stale entry, so their green
# path cannot prove that a future unexpected PASS will redden rather than becoming an advisory line nobody reads.
source "$(cd -- "$(dirname -- "$0")/../.." && pwd -P)/scripts/lib.sh"

declare -A PROBE_XFAIL=(
	[still-broken]='calibration: a listed failure that remains broken is tolerated'
	[now-fixed]='calibration: a listed failure that passes must stale the baseline'
)

work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
xfail_diff selftest PROBE_XFAIL still-broken new-regression >"$work/verdict"
out="$(<"$work/verdict")"
[[ "$out" == *'XFAIL     selftest:still-broken'* ]] || die "xfail_diff did not classify the surviving expected failure"
[[ "$out" == *'NEW-FAIL  selftest:new-regression'* ]] || die "xfail_diff did not classify the new failure"
[[ "$out" == *'FIXED     selftest:now-fixed'* ]] || die "xfail_diff did not classify the stale baseline entry"
[[ "${XFAIL_NEW[*]}" == 'selftest:new-regression' ]] || die "new failure was not recorded for the final verdict"
[[ "${XFAIL_FIXED[*]}" == 'selftest:now-fixed' ]] || die "fixed entry was not recorded for the final verdict"
if xfail_gate_is_clean; then die "NEW + FIXED state was accepted as a clean gate"; fi

XFAIL_NEW=(); XFAIL_FIXED=()
xfail_gate_is_clean || die "an empty differential was rejected"
echo "xfail-policy: GREEN (XFAIL tolerated; NEW and FIXED both enforce a red final verdict)"
