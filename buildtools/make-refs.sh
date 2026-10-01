#!/bin/bash
# Regenerate Cecil-normalized reference copies for the net35 build:
#   refs-v4/mscorlib.dll & System*.dll  -> real net35 BCL refs (identity stays 2.0/3.5)
#   everything else                     -> identity rewritten to 4.0.0.0, all BCL refs -> v4
set -e
cd "$(dirname "$0")/.."
rm -rf refs-v4 && mkdir -p refs-v4
cp /tmp/nuget/nfr/build/.NETFramework/v3.5/mscorlib.dll refs-v4/
cp /tmp/nuget/nfr/build/.NETFramework/v3.5/System.dll refs-v4/
cp /tmp/nuget/nfr/build/.NETFramework/v3.5/System.Core.dll refs-v4/
cp /tmp/nuget/nfr/build/.NETFramework/v3.5/System.Xml.dll refs-v4/
for f in refs/*.dll; do
  b=$(basename "$f")
  case "$b" in mscorlib.dll|System.dll|System.Core.dll|System.Xml.dll) continue;; esac
  /tmp/dotnet/dotnet buildtools/rewrite/bin/rewrite.dll "$f" refs-v4
done
echo "OK: $(ls refs-v4 | wc -l) files in refs-v4/"
