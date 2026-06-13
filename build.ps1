# Legacy C++ build only. Do not use this as the verification command for WinUI changes.

param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$build = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $build | Out-Null

$gxx = (Get-Command g++.exe -ErrorAction Stop).Source
$windres = (Get-Command windres.exe -ErrorAction Stop).Source
$src = Join-Path $root "src\main.cpp"
$testSrc = Join-Path $root "src\parser_tests.cpp"
$resSrc = Join-Path $root "src\resources.rc"
$resOut = Join-Path $build "resources.o"
$out = Join-Path $build "CodexSWBarWindows.exe"
$testOut = Join-Path $build "parser_tests.exe"

$common = @(
    "-std=c++17",
    "-municode",
    "-mwindows",
    "-Wall",
    "-Wextra",
    "-DUNICODE",
    "-D_UNICODE",
    "-I", (Join-Path $root "src"),
    $src,
    $resOut,
    "-o", $out,
    "-lshell32",
    "-luser32",
    "-lgdi32",
    "-lmsimg32",
    "-lole32",
    "-ladvapi32"
)

$testCommon = @(
    "-std=c++17",
    "-Wall",
    "-Wextra",
    "-Wno-unused-function",
    "-DUNICODE",
    "-D_UNICODE",
    "-I", (Join-Path $root "src"),
    $testSrc,
    "-o", $testOut,
    "-lshell32",
    "-luser32",
    "-lgdi32",
    "-lmsimg32",
    "-lole32",
    "-ladvapi32"
)

if ($Release) {
    $common = @("-O2", "-DNDEBUG") + $common
    $testCommon = @("-O2", "-DNDEBUG") + $testCommon
} else {
    $common = @("-g", "-O0") + $common
    $testCommon = @("-g", "-O0") + $testCommon
}

Push-Location $root
try {
    & $windres "--preprocessor=gcc -E -xc -DRC_INVOKED" "-I" (Join-Path $root "src") "-I" $root $resSrc $resOut
    if ($LASTEXITCODE -ne 0) {
        throw "windres failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}

& $gxx @common
if ($LASTEXITCODE -ne 0) {
    throw "g++ failed with exit code $LASTEXITCODE"
}
Write-Host "Built $out"

& $gxx @testCommon
if ($LASTEXITCODE -ne 0) {
    throw "g++ parser tests build failed with exit code $LASTEXITCODE"
}

& $testOut
if ($LASTEXITCODE -ne 0) {
    throw "parser tests failed with exit code $LASTEXITCODE"
}
