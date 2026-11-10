#!/usr/bin/env bash
# Builds the self-contained libespeak-ng (.so on Linux, .dll on Windows/MSYS2, .dylib on macOS)
# with numediart/MBROLA LibOneChannel linked in-memory and stages espeak-ng-data.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK_DIR="$(mktemp -d -t moontts-mbrola-build-XXXXXX)"
trap 'rm -rf "$WORK_DIR"' EXIT

UNAME_S="$(uname -s)"
UNAME_M="$(uname -m)"
IS_WINDOWS=0
IS_MACOS=0
case "$UNAME_S" in
    MINGW*|MSYS*|CYGWIN*) IS_WINDOWS=1 ;;
    Darwin*) IS_MACOS=1 ;;
esac
NPROC="$(nproc 2>/dev/null || sysctl -n hw.ncpu 2>/dev/null || echo 4)"

ESPEAK_VERSION="1.52.0"
ESPEAK_URL="https://github.com/espeak-ng/espeak-ng/archive/refs/tags/${ESPEAK_VERSION}.tar.gz"
MBROLA_URL="https://github.com/numediart/MBROLA/archive/refs/heads/master.tar.gz"

echo "[1/5] Downloading eSpeak NG ${ESPEAK_VERSION} and MBROLA sources..."
curl -fsSL "$ESPEAK_URL" -o "$WORK_DIR/espeak-ng.tar.gz"
curl -fsSL "$MBROLA_URL" -o "$WORK_DIR/mbrola.tar.gz"

tar -xzf "$WORK_DIR/espeak-ng.tar.gz" -C "$WORK_DIR"
tar -xzf "$WORK_DIR/mbrola.tar.gz" -C "$WORK_DIR"

ESPEAK_SRC="$WORK_DIR/espeak-ng-${ESPEAK_VERSION}"
MBROLA_SRC="$WORK_DIR/MBROLA-master"

echo "[2/5] Patching and compiling MBROLA LibOneChannel in-memory engine..."
python3 - "$MBROLA_SRC/Parser/input_fifo.c" "$MBROLA_SRC/LibOneChannel/onechannel.c" <<'PY'
import sys

fifo_path, onechannel_path = sys.argv[1], sys.argv[2]

with open(fifo_path, encoding="utf-8") as f:
    fifo = f.read()
with open(fifo_path, "w", encoding="utf-8") as f:
    f.write(fifo.replace("static int readline_InputFifo", "static long readline_InputFifo"))

with open(onechannel_path, encoding="utf-8") as f:
    c = f.read()

target = """void DLL_EXPORT close_MBR(void)
/*\tFree all the allocated memory */
{
\tclose_Mbrola(my_brole);
\tmy_parse->close_Parser(my_parse);   /* ... the parser   */
\tclose_InputFifo(my_input);
\tclose_Fifo(my_fifo);
\tmy_dba->close_Database(my_dba); /* ... the database */
}"""

replacement = """void DLL_EXPORT close_MBR(void)
/*\tFree all the allocated memory */
{
\tif (!my_brole) return;
\tclose_Mbrola(my_brole);
\tif (my_parse) my_parse->close_Parser(my_parse);
\tif (my_input) close_InputFifo(my_input);
\tif (my_fifo) close_Fifo(my_fifo);
\tif (my_dba) my_dba->close_Database(my_dba);
\tmy_brole = NULL;
\tmy_parse = NULL;
\tmy_input = NULL;
\tmy_fifo = NULL;
\tmy_dba = NULL;
}"""

assert target in c, "close_MBR target block not found"
with open(onechannel_path, "w", encoding="utf-8") as f:
    f.write(c.replace(target, replacement, 1))
PY

gcc -O3 -fPIC -c \
    -Wno-incompatible-pointer-types -Wno-implicit-function-declaration \
    -D_GNU_SOURCE -DLITTLE_ENDIAN -UWIN32 -UMSDOS \
    -I"$MBROLA_SRC" \
    -I"$MBROLA_SRC/Misc" \
    -I"$MBROLA_SRC/Parser" \
    -I"$MBROLA_SRC/Engine" \
    -I"$MBROLA_SRC/Database" \
    -I"$MBROLA_SRC/LibOneChannel" \
    "$MBROLA_SRC/LibOneChannel/lib1.c" \
    -o "$WORK_DIR/lib1.o"
ar rcs "$WORK_DIR/libmbr.a" "$WORK_DIR/lib1.o"

echo "[3/5] Patching and compiling libespeak-ng with direct LibOneChannel linkage..."
python3 - "$ESPEAK_SRC/src/libespeak-ng/compilembrola.c" "$ESPEAK_SRC/src/libespeak-ng/synth_mbrola.c" \
    "$ESPEAK_SRC/src/include/espeak-ng/speak_lib.h" \
    "$ESPEAK_SRC/src/include/espeak-ng/espeak_ng.h" \
    "$ESPEAK_SRC/src/include/espeak/speak_lib.h" <<'PY'
import sys

compile_path, synth_path = sys.argv[1], sys.argv[2]
header_paths = sys.argv[3:]

with open(compile_path, encoding="utf-8") as f:
    comp = f.read()
with open(compile_path, "w", encoding="utf-8") as f:
    f.write(comp.replace("basename(", "mbr_basename("))

with open(synth_path, encoding="utf-8") as f:
    c = f.read()

c = c.replace("char path[sizeof(path_home)+15];", "char path[4096];", 1)
target = '\tsprintf(path, "%s/mbrola/%s", path_home, mbrola_voice);'
replacement = """\tsprintf(path, "%s/mbrola/%s", path_home, mbrola_voice);
\tconst char *moon_mbr = getenv("MOON_MBROLA_DIR");
\tif (GetFileLength(path) <= 0 && moon_mbr && moon_mbr[0]) {
\t\tsprintf(path, "%s/%s", moon_mbr, mbrola_voice);
\t}"""
assert target in c, "sprintf target in synth_mbrola.c not found"
with open(synth_path, "w", encoding="utf-8") as f:
    f.write(c.replace(target, replacement, 1))

for h_path in header_paths:
    with open(h_path, encoding="utf-8") as f:
        h_text = f.read()
    h_text = h_text.replace("__declspec(dllimport)", "")
    with open(h_path, "w", encoding="utf-8") as f:
        f.write(h_text)
PY

cat << 'EOF' > "$ESPEAK_SRC/src/libespeak-ng/mbrowrap.h"
#ifndef MBROWRAP_H
#define MBROWRAP_H

#ifdef __cplusplus
extern "C" {
#endif

int init_MBR(char *voice_path);
void close_MBR(void);
void reset_MBR(void);
int read_MBR(short *buffer, int nb_samples);
int write_MBR(char *data);
int flush_MBR(void);
int getFreq_MBR(void);
void setVolumeRatio_MBR(float value);
char *lastErrorStr_MBR(char *buffer, int bufsize);
void setNoError_MBR(int no_error);

int load_MBR(void);
void unload_MBR(void);

#ifdef __cplusplus
}
#endif

#endif
EOF

cat << 'EOF' > "$ESPEAK_SRC/src/libespeak-ng/mbrowrap.c"
#include "config.h"
#include <stdbool.h>
#include "mbrowrap.h"

int load_MBR(void) { return 1; }
void unload_MBR(void) {}
EOF

cd "$ESPEAK_SRC"
./autogen.sh

EXTRA_CFLAGS="-O3 -fPIC -D_GNU_SOURCE"
EXTRA_LDFLAGS="-L$WORK_DIR"
EXTRA_LIBS="-lmbr -lm"
SHARED_FLAGS=""
EXE_EXT=""
ESPEAK_DATA_DIR="$ESPEAK_SRC"
if [ "$IS_WINDOWS" -eq 1 ]; then
    EXTRA_CFLAGS="-O3 -D_GNU_SOURCE"
    EXTRA_LDFLAGS="-L$WORK_DIR"
    EXTRA_LIBS="-lmbr -lm -ladvapi32"
    SHARED_FLAGS="--disable-shared --enable-static"
    EXE_EXT=".exe"
    ESPEAK_DATA_DIR="$(cygpath -m "$ESPEAK_SRC" 2>/dev/null || echo "$ESPEAK_SRC")"
fi

./configure \
    --prefix="$WORK_DIR/install" \
    $SHARED_FLAGS \
    --without-async \
    --with-mbrola \
    --without-pcaudiolib \
    --without-sonic \
    --without-klatt \
    --without-speechplayer \
    CFLAGS="$EXTRA_CFLAGS" \
    LDFLAGS="$EXTRA_LDFLAGS" \
    LIBS="$EXTRA_LIBS" || { cat config.log; exit 1; }

make -j"$NPROC" src/libespeak-ng.la "src/espeak-ng${EXE_EXT}"
make -j"$NPROC" espeak-ng-data/phontab espeak-ng-data/en_dict

echo "[4/5] Compiling phoneme translation tables (mbrola_ph) and 36 language dictionaries..."
mkdir -p espeak-ng-data/mbrola_ph
for mbr_src in phsource/mbrola/*; do
    mbr_name="$(basename "$mbr_src")"
    ESPEAK_DATA_PATH="$ESPEAK_DATA_DIR" "src/espeak-ng${EXE_EXT}" --compile-mbrola="phsource/mbrola/${mbr_name}"
done

LANGS="af ar pt fr zh hr cs de et en es el he hu is id hi fa it ja la lt ms nl mi pl ro sv te tr"
for l in $LANGS; do
    (cd dictsource && ESPEAK_DATA_PATH="$ESPEAK_DATA_DIR" "../src/espeak-ng${EXE_EXT}" --compile="$l")
done

echo "[5/5] Staging native library and pruned espeak-ng-data into $SCRIPT_DIR..."
DATA_OUT="$SCRIPT_DIR/espeak-ng-data"
rm -rf "$DATA_OUT"
mkdir -p "$DATA_OUT/voices/mb" "$DATA_OUT/mbrola_ph" "$DATA_OUT/mbrola"

if [ "$IS_WINDOWS" -eq 1 ]; then
    NATIVE_OUT="$SCRIPT_DIR/runtimes/win-x64/native"
    rm -rf "$NATIVE_OUT"
    mkdir -p "$NATIVE_OUT"
    ESPEAK_A=""
    if [ -f "$ESPEAK_SRC/src/.libs/libespeak-ng.a" ]; then
        ESPEAK_A="$ESPEAK_SRC/src/.libs/libespeak-ng.a"
    elif [ -f "$ESPEAK_SRC/src/libespeak-ng.a" ]; then
        ESPEAK_A="$ESPEAK_SRC/src/libespeak-ng.a"
    else
        echo "Error: libespeak-ng.a not found in $ESPEAK_SRC/src"
        exit 1
    fi
    gcc -shared -O3 \
        -o "$NATIVE_OUT/libespeak-ng.dll" \
        -Wl,--whole-archive "$ESPEAK_A" "$WORK_DIR/libmbr.a" -Wl,--no-whole-archive \
        -Wl,--export-all-symbols \
        -static -static-libgcc \
        -lm -ladvapi32
    strip --strip-unneeded "$NATIVE_OUT/libespeak-ng.dll"
    NATIVE_LIB_PATH="$NATIVE_OUT/libespeak-ng.dll"
elif [ "$IS_MACOS" -eq 1 ]; then
    if [ "$UNAME_M" = "arm64" ]; then
        MAC_RID="osx-arm64"
    else
        MAC_RID="osx-x64"
    fi
    NATIVE_OUT="$SCRIPT_DIR/runtimes/${MAC_RID}/native"
    rm -rf "$NATIVE_OUT"
    mkdir -p "$NATIVE_OUT"
    DYLIB_PATH=""
    for f in "$ESPEAK_SRC"/src/.libs/libespeak-ng*.dylib; do
        if [ -f "$f" ]; then
            DYLIB_PATH="$f"
            break
        fi
    done
    if [ -z "$DYLIB_PATH" ]; then
        echo "Error: libespeak-ng dylib not found in $ESPEAK_SRC/src/.libs"
        exit 1
    fi
    cp -L "$DYLIB_PATH" "$NATIVE_OUT/libespeak-ng.dylib"
    strip -x "$NATIVE_OUT/libespeak-ng.dylib"
    NATIVE_LIB_PATH="$NATIVE_OUT/libespeak-ng.dylib"
else
    if [ "$UNAME_M" = "aarch64" ] || [ "$UNAME_M" = "arm64" ]; then
        LINUX_RID="linux-arm64"
    else
        LINUX_RID="linux-x64"
    fi
    NATIVE_OUT="$SCRIPT_DIR/runtimes/${LINUX_RID}/native"
    rm -rf "$NATIVE_OUT"
    mkdir -p "$NATIVE_OUT"
    cp -L "$ESPEAK_SRC/src/.libs/libespeak-ng.so" "$NATIVE_OUT/libespeak-ng.so"
    strip --strip-unneeded "$NATIVE_OUT/libespeak-ng.so"
    NATIVE_LIB_PATH="$NATIVE_OUT/libespeak-ng.so"
fi

for core_file in phontab phonindex phondata phondata-manifest intonations; do
    cp "$ESPEAK_SRC/espeak-ng-data/$core_file" "$DATA_OUT/"
done

cp "$ESPEAK_SRC/espeak-ng-data/"*_dict "$DATA_OUT/"
cp -r "$ESPEAK_SRC/espeak-ng-data/lang" "$DATA_OUT/"
cp "$ESPEAK_SRC/espeak-ng-data/voices/mb/"* "$DATA_OUT/voices/mb/"
cp "$ESPEAK_SRC/espeak-ng-data/mbrola_ph/"* "$DATA_OUT/mbrola_ph/"
cp "$ESPEAK_SRC/COPYING" "$DATA_OUT/LICENSE-espeak-ng.txt"
cp "$MBROLA_SRC/LICENSE" "$DATA_OUT/LICENSE-mbrola.txt"

echo "Done! Native library: $(ls -lh "$NATIVE_LIB_PATH")"
echo "Data size: $(du -sh "$DATA_OUT")"
