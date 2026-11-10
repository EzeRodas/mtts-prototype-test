# Builds the self-contained Windows x64 libespeak-ng.dll with numediart/MBROLA LibOneChannel
# linked in-memory (and static MSVC CRT /MT) and stages espeak-ng-data into Engines/Mbrola/.
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("moontts-mbrola-build-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

try {
    # Locate Visual Studio vcvars64.bat
    $VcVars = $null
    $VsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $VsWhere) {
        $VsInstallDir = & $VsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if ($VsInstallDir) {
            $Candidate = Join-Path $VsInstallDir "VC\Auxiliary\Build\vcvars64.bat"
            if (Test-Path $Candidate) {
                $VcVars = $Candidate
            }
        }
    }
    if (-not $VcVars) {
        $FallbackPaths = @(
            "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat",
            "C:\Program Files\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars64.bat",
            "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
            "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
        )
        foreach ($p in $FallbackPaths) {
            if (Test-Path $p) {
                $VcVars = $p
                break
            }
        }
    }
    if (-not $VcVars) {
        throw "Could not find Visual Studio vcvars64.bat. Please install Visual Studio C++ Build Tools."
    }

    $EspeakVersion = "1.52.0"
    $EspeakUrl = "https://github.com/espeak-ng/espeak-ng/archive/refs/tags/$EspeakVersion.tar.gz"
    $MbrolaUrl = "https://github.com/numediart/MBROLA/archive/refs/heads/master.tar.gz"

    Write-Host "[1/5] Downloading eSpeak NG $EspeakVersion and MBROLA sources..."
    & curl.exe -fsSL $EspeakUrl -o "$WorkDir\espeak-ng.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "Failed to download espeak-ng.tar.gz" }
    & curl.exe -fsSL $MbrolaUrl -o "$WorkDir\mbrola.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw "Failed to download mbrola.tar.gz" }

    # Extract tarballs (exclude Unix symlink src/include/espeak/speak_lib.h and copy header explicitly)
    & tar.exe --exclude="*/src/include/espeak/speak_lib.h" -xzf "$WorkDir\espeak-ng.tar.gz" -C $WorkDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract espeak-ng.tar.gz" }
    & tar.exe -xzf "$WorkDir\mbrola.tar.gz" -C $WorkDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract mbrola.tar.gz" }

    $EspeakSrc = Join-Path $WorkDir "espeak-ng-$EspeakVersion"
    $MbrolaSrc = Join-Path $WorkDir "MBROLA-master"

    New-Item -ItemType Directory -Force -Path "$EspeakSrc\src\include\espeak" | Out-Null
    Copy-Item "$EspeakSrc\src\include\espeak-ng\speak_lib.h" "$EspeakSrc\src\include\espeak\speak_lib.h" -Force

    Write-Host "[2/5] Patching and compiling MBROLA LibOneChannel in-memory engine..."
    $FifoPath = "$MbrolaSrc\Parser\input_fifo.c"
    $FifoContent = [System.IO.File]::ReadAllText($FifoPath)
    $FifoContent = $FifoContent.Replace("static int readline_InputFifo", "static long readline_InputFifo")
    [System.IO.File]::WriteAllText($FifoPath, $FifoContent)

    $OneChannelPath = "$MbrolaSrc\LibOneChannel\onechannel.c"
    $OneContent = [System.IO.File]::ReadAllText($OneChannelPath)
    $OneContent = $OneContent.Replace("#ifdef _MSC_VER", "#if 0")
    $OldClose = @"
void DLL_EXPORT close_MBR(void)
/*	Free all the allocated memory */
{
	close_Mbrola(my_brole);
	my_parse->close_Parser(my_parse);   /* ... the parser   */
	close_InputFifo(my_input);
	close_Fifo(my_fifo);
	my_dba->close_Database(my_dba); /* ... the database */
}
"@
    $NewClose = @"
void DLL_EXPORT close_MBR(void)
/*	Free all the allocated memory */
{
	if (!my_brole) return;
	close_Mbrola(my_brole);
	if (my_parse) my_parse->close_Parser(my_parse);
	if (my_input) close_InputFifo(my_input);
	if (my_fifo) close_Fifo(my_fifo);
	if (my_dba) my_dba->close_Database(my_dba);
	my_brole = NULL;
	my_parse = NULL;
	my_input = NULL;
	my_fifo = NULL;
	my_dba = NULL;
}
"@
    $OneContent = $OneContent.Replace($OldClose.Replace("`r`n", "`n"), $NewClose.Replace("`r`n", "`n")).Replace($OldClose, $NewClose)
    [System.IO.File]::WriteAllText($OneChannelPath, $OneContent)

    $Lib1Obj = "$WorkDir\lib1.obj"
    $ClMbrola = "`"$VcVars`" >nul && cl /nologo /O2 /MT /c /DTARGET_OS_DOS /DLITTLE_ENDIAN /D_CRT_SECURE_NO_WARNINGS /I`"$MbrolaSrc`" /I`"$MbrolaSrc\Misc`" /I`"$MbrolaSrc\Parser`" /I`"$MbrolaSrc\Engine`" /I`"$MbrolaSrc\Database`" /I`"$MbrolaSrc\LibOneChannel`" `"$MbrolaSrc\LibOneChannel\lib1.c`" /Fo`"$Lib1Obj`""
    cmd /c $ClMbrola
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile MBROLA lib1.c" }

    Write-Host "[3/5] Patching and compiling libespeak-ng.dll with direct LibOneChannel linkage..."
    $ConfigH = @"
#define HAVE_MKSTEMP 0
#define USE_ASYNC 0
#define USE_KLATT 0
#define USE_LIBPCAUDIO 0
#define USE_LIBSONIC 0
#define USE_MBROLA 1
#define USE_SPEECHPLAYER 0
#define PACKAGE_VERSION "$EspeakVersion"
"@
    [System.IO.File]::WriteAllText("$EspeakSrc\src\libespeak-ng\config.h", $ConfigH)

    $CompPath = "$EspeakSrc\src\libespeak-ng\compilembrola.c"
    $CompContent = [System.IO.File]::ReadAllText($CompPath)
    $CompContent = $CompContent.Replace("basename(", "mbr_basename(")
    [System.IO.File]::WriteAllText($CompPath, $CompContent)

    $SynthPath = "$EspeakSrc\src\libespeak-ng\synth_mbrola.c"
    $SynthContent = [System.IO.File]::ReadAllText($SynthPath)
    $SynthContent = $SynthContent.Replace("char path[sizeof(path_home)+15];", "char path[4096];")
    $SynthTarget = "`tsprintf(path, `"%s/mbrola/%s`", path_home, mbrola_voice);"
    $SynthReplacement = @"
	sprintf(path, "%s/mbrola/%s", path_home, mbrola_voice);
	const char *moon_mbr = getenv("MOON_MBROLA_DIR");
	if (GetFileLength(path) <= 0 && moon_mbr && moon_mbr[0]) {
		sprintf(path, "%s/%s", moon_mbr, mbrola_voice);
	}
	close_MBR();
"@.Replace("`r`n", "`n")
    $SynthContent = $SynthContent.Replace($SynthTarget, $SynthReplacement)
    [System.IO.File]::WriteAllText($SynthPath, $SynthContent)

    $MbrowrapH = @"
#ifndef MBROWRAP_H
#define MBROWRAP_H

#ifdef __cplusplus
extern "C" {
#endif

int init_MBR(char *voice_path);
void close_MBR(void);
int reset_MBR(void);
int read_MBR(void *buffer, int nb_samples);
int write_MBR(char *data);
int flush_MBR(void);
int getFreq_MBR(void);
void setVolumeRatio_MBR(float value);
int lastErrorStr_MBR(char *buffer, int bufsize);
void setNoError_MBR(int no_error);

int load_MBR(void);
void unload_MBR(void);

#ifdef __cplusplus
}
#endif

#endif
"@
    [System.IO.File]::WriteAllText("$EspeakSrc\src\libespeak-ng\mbrowrap.h", $MbrowrapH)

    $MbrowrapC = @"
#include "config.h"
#include <stdbool.h>
#include "mbrowrap.h"

int load_MBR(void) { return 1; }
void unload_MBR(void) {}
"@
    [System.IO.File]::WriteAllText("$EspeakSrc\src\libespeak-ng\mbrowrap.c", $MbrowrapC)

    $UcdSources = @(
        "$EspeakSrc\src\ucd-tools\src\case.c",
        "$EspeakSrc\src\ucd-tools\src\categories.c",
        "$EspeakSrc\src\ucd-tools\src\ctype.c",
        "$EspeakSrc\src\ucd-tools\src\proplist.c",
        "$EspeakSrc\src\ucd-tools\src\scripts.c",
        "$EspeakSrc\src\ucd-tools\src\tostring.c"
    )
    $EspeakSources = @(
        "$EspeakSrc\src\libespeak-ng\common.c",
        "$EspeakSrc\src\libespeak-ng\mnemonics.c",
        "$EspeakSrc\src\libespeak-ng\error.c",
        "$EspeakSrc\src\libespeak-ng\ieee80.c",
        "$EspeakSrc\src\libespeak-ng\compiledata.c",
        "$EspeakSrc\src\libespeak-ng\compiledict.c",
        "$EspeakSrc\src\libespeak-ng\dictionary.c",
        "$EspeakSrc\src\libespeak-ng\encoding.c",
        "$EspeakSrc\src\libespeak-ng\intonation.c",
        "$EspeakSrc\src\libespeak-ng\langopts.c",
        "$EspeakSrc\src\libespeak-ng\numbers.c",
        "$EspeakSrc\src\libespeak-ng\phoneme.c",
        "$EspeakSrc\src\libespeak-ng\phonemelist.c",
        "$EspeakSrc\src\libespeak-ng\readclause.c",
        "$EspeakSrc\src\libespeak-ng\setlengths.c",
        "$EspeakSrc\src\libespeak-ng\soundicon.c",
        "$EspeakSrc\src\libespeak-ng\spect.c",
        "$EspeakSrc\src\libespeak-ng\ssml.c",
        "$EspeakSrc\src\libespeak-ng\synthdata.c",
        "$EspeakSrc\src\libespeak-ng\synthesize.c",
        "$EspeakSrc\src\libespeak-ng\tr_languages.c",
        "$EspeakSrc\src\libespeak-ng\translate.c",
        "$EspeakSrc\src\libespeak-ng\translateword.c",
        "$EspeakSrc\src\libespeak-ng\voices.c",
        "$EspeakSrc\src\libespeak-ng\wavegen.c",
        "$EspeakSrc\src\libespeak-ng\speech.c",
        "$EspeakSrc\src\libespeak-ng\espeak_api.c",
        "$EspeakSrc\src\libespeak-ng\mbrowrap.c",
        "$EspeakSrc\src\libespeak-ng\compilembrola.c",
        "$EspeakSrc\src\libespeak-ng\synth_mbrola.c"
    )

    New-Item -ItemType Directory -Force -Path "$WorkDir\objs" | Out-Null
    $AllSrcQuoted = ($UcdSources + $EspeakSources | ForEach-Object { "`"$_`"" }) -join " "
    $DllOut = "$WorkDir\libespeak-ng.dll"
    $LibOut = "$WorkDir\libespeak-ng.lib"
    $ExeOut = "$WorkDir\espeak-ng.exe"

    $ClEspeak = "`"$VcVars`" >nul && cl /nologo /O2 /MT /MP /DLIBESPEAK_NG_EXPORT=1 /D_CRT_SECURE_NO_WARNINGS /DPATH_ESPEAK_DATA=\`"/usr/share/espeak-ng-data\`" /I`"$EspeakSrc\src\include\compat`" /I`"$EspeakSrc\src\include`" /I`"$EspeakSrc\src\ucd-tools\src\include`" /I`"$EspeakSrc\src\libespeak-ng`" $AllSrcQuoted `"$Lib1Obj`" /Fo`"$WorkDir\objs\\`" /LD /Fe`"$DllOut`" /link /IMPLIB:`"$LibOut`" /EXPORT:getFreq_MBR advapi32.lib && cl /nologo /O2 /MT /D_CRT_SECURE_NO_WARNINGS /I`"$EspeakSrc\src\include\compat`" /I`"$EspeakSrc\src\include`" /I`"$EspeakSrc\src\libespeak-ng`" `"$EspeakSrc\src\espeak-ng.c`" `"$EspeakSrc\src\compat\getopt.c`" `"$LibOut`" /Fo`"$WorkDir\objs\\`" /Fe`"$ExeOut`""
    cmd /c $ClEspeak
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile libespeak-ng.dll and espeak-ng.exe" }

    Write-Host "[4/5] Compiling phoneme tables, mbrola_ph, and language dictionaries..."
    $env:ESPEAK_DATA_PATH = $EspeakSrc
    New-Item -ItemType Directory -Force -Path "$EspeakSrc\espeak-ng-data\mbrola_ph" | Out-Null

    Push-Location "$EspeakSrc\phsource"
    & $ExeOut --compile-intonations
    if ($LASTEXITCODE -ne 0) { throw "compile-intonations failed" }
    & $ExeOut --compile-phonemes
    if ($LASTEXITCODE -ne 0) { throw "compile-phonemes failed" }
    Pop-Location

    Push-Location $EspeakSrc
    Get-ChildItem "$EspeakSrc\phsource\mbrola\*" | ForEach-Object {
        & $ExeOut "--compile-mbrola=phsource/mbrola/$($_.Name)"
        if ($LASTEXITCODE -ne 0) { throw "compile-mbrola $($_.Name) failed" }
    }
    Pop-Location

    $Langs = @("af","ar","pt","fr","zh","hr","cs","de","et","en","es","el","he","hu","is","id","hi","fa","it","ja","la","lt","ms","nl","mi","pl","ro","ru","sv","te","tr")
    Push-Location "$EspeakSrc\dictsource"
    foreach ($l in $Langs) {
        & $ExeOut "--compile=$l"
        if ($LASTEXITCODE -ne 0) { throw "compile dictionary $l failed" }
    }
    Pop-Location

    Write-Host "[5/5] Staging native library and pruned espeak-ng-data into $ScriptDir..."
    $NativeOut = Join-Path $ScriptDir "runtimes\win-x64\native"
    New-Item -ItemType Directory -Force -Path $NativeOut | Out-Null
    Copy-Item $DllOut (Join-Path $NativeOut "libespeak-ng.dll") -Force

    $DataOut = Join-Path $ScriptDir "espeak-ng-data"
    if (Test-Path $DataOut) {
        Remove-Item -Recurse -Force $DataOut
    }
    New-Item -ItemType Directory -Force -Path "$DataOut\voices\mb" | Out-Null
    New-Item -ItemType Directory -Force -Path "$DataOut\mbrola_ph" | Out-Null
    New-Item -ItemType Directory -Force -Path "$DataOut\mbrola" | Out-Null

    foreach ($coreFile in @("phontab", "phonindex", "phondata", "phondata-manifest", "intonations")) {
        Copy-Item "$EspeakSrc\espeak-ng-data\$coreFile" "$DataOut\$coreFile" -Force
    }
    Copy-Item "$EspeakSrc\espeak-ng-data\*_dict" $DataOut -Force
    Copy-Item "$EspeakSrc\espeak-ng-data\lang" "$DataOut\lang" -Recurse -Force
    if (Test-Path "$EspeakSrc\espeak-ng-data\voices\!v") {
        Copy-Item "$EspeakSrc\espeak-ng-data\voices\!v" "$DataOut\voices\!v" -Recurse -Force
    }
    Copy-Item "$EspeakSrc\espeak-ng-data\voices\mb\*" "$DataOut\voices\mb\" -Force
    Copy-Item "$EspeakSrc\espeak-ng-data\mbrola_ph\*" "$DataOut\mbrola_ph\" -Force
    Copy-Item "$EspeakSrc\COPYING" "$DataOut\COPYING.espeak-ng" -Force
    Copy-Item "$MbrolaSrc\LICENSE" "$DataOut\LICENSE.MBROLA" -Force

    Write-Host "Done! Native library: $NativeOut\libespeak-ng.dll"
    Write-Host "Data directory: $DataOut"
}
finally {
    if (Test-Path $WorkDir) {
        Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
    }
}
