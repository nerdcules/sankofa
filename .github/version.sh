#!/bin/sh
# The release version: from the tag (v1.2.3 -> 1.2.3), else the one compiled in. On a tag it is
# written into Store.cs before the build, so every binary answers `version` with the tag's number.
# Prints version=<v> for $GITHUB_OUTPUT.
set -eu
case "${GITHUB_REF:-}" in
  refs/tags/v*)
    v=${GITHUB_REF_NAME#v}
    sed "s/const string Version = \"[^\"]*\"/const string Version = \"$v\"/" daemon/Sankofa.Core/Store.cs > Store.cs.tmp
    mv Store.cs.tmp daemon/Sankofa.Core/Store.cs ;;
  *) v=$(sed -n 's/.*const string Version = "\([^"]*\)".*/\1/p' daemon/Sankofa.Core/Store.cs) ;;
esac
echo "version=$v"
