#!/usr/bin/env bash
# A newly extracted SDK owns one clean project graph. Subsequent consumers reuse that graph's producer outputs.

nunit_prepare_cohort() {
	local root="$1" cache="$2" configuration="$3" project name
	shift 3
	# Keep SDK resolution and restore on the same private cache even when the caller exports another NuGet root.
	export NUGET_PACKAGES="$cache"
	if [[ -d "$cache" ]]; then
		if ! find "$cache" -mindepth 1 -maxdepth 1 -type d -iname 'dotkt.*' -exec rm -rf -- {} +; then
			echo 'NUnit cohort: could not clear the previous DotKt package cohort' >&2
			return 1
		fi
	fi
	# Restore FIRST: clean must import the newly extracted SDK's compiler-state cleanup targets, not missing or
	# previous-cohort package imports. Restore all roots before cleaning any outputs used by their shared graph.
	for project in "$@"; do
		name="${project//\//-}"
		if ! dotnet restore "$root/$project" -p:Configuration="$configuration" -v:q --nologo \
			> "$root/build/nunit-$name.restore.log" 2>&1; then
			echo "NUnit cohort: RESTORE FAIL — $project" >&2
			tail -25 "$root/build/nunit-$name.restore.log" >&2
			return 1
		fi
	done
	# Clean is transitive through ProjectReference. Complete ALL clean operations before the first build;
	# cleaning a later consumer after an earlier build would discard the producer outputs we intend to share.
	for project in "$@"; do
		name="${project//\//-}"
		if ! dotnet clean "$root/$project" -c "$configuration" -v:q --nologo \
			> "$root/build/nunit-$name.clean.log" 2>&1; then
			echo "NUnit cohort: CLEAN FAIL — $project" >&2
			tail -25 "$root/build/nunit-$name.clean.log" >&2
			return 1
		fi
	done
}

nunit_build_project() {
	local root="$1" configuration="$2" project="$3" name
	name="${project//\//-}"
	dotnet build "$root/$project" -c "$configuration" --no-restore -m:1 -v:q --nologo \
		> "$root/build/nunit-$name.build.log" 2>&1
}
